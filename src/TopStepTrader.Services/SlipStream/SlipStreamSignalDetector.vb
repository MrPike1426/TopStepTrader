Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.Market

Namespace TopStepTrader.Services.SlipStream

    ''' <summary>
    ''' FEAT-70: Computes the SlipStream confluence for a single instrument and returns a
    ''' <see cref="SlipStreamEvaluation"/> for the most recent closed signal-timeframe bar.
    '''
    ''' v1 rebuilds the indicators from scratch on every call. At 5m cadence with three
    ''' symbols this is cheap (~250 signal bars + ~720 HTF bars × 3 symbols every 30s);
    ''' incremental caching is a future optimisation.
    '''
    ''' Signal-side timezone note: the session window is interpreted in
    ''' <see cref="SlipStreamConfig.SessionTimeZone"/>. Bar timestamps are stored as UTC;
    ''' the detector resolves the configured tz id via <see cref="TimeZoneInfo.FindSystemTimeZoneById"/>
    ''' (Windows id first, IANA fallback) and respects daylight saving.
    ''' </summary>
    Public Class SlipStreamSignalDetector
        Implements ISlipStreamSignalDetector

        Private ReadOnly _barIngestion As IBarIngestionService
        Private ReadOnly _barCollection As IBarCollectionService
        Private ReadOnly _config As SlipStreamConfig
        Private ReadOnly _logger As ILogger(Of SlipStreamSignalDetector)

        Public Sub New(barIngestion As IBarIngestionService,
                       barCollection As IBarCollectionService,
                       config As SlipStreamConfig,
                       logger As ILogger(Of SlipStreamSignalDetector))
            _barIngestion = barIngestion
            _barCollection = barCollection
            _config = config
            _logger = logger
        End Sub

        Public Async Function EvaluateAsync(symbol As String,
                                            ct As CancellationToken) As Task(Of SlipStreamEvaluation) Implements ISlipStreamSignalDetector.EvaluateAsync

            Dim eval As New SlipStreamEvaluation With {.Symbol = symbol}

            Dim contract = FavouriteContracts.TryGetBySymbolResolved(symbol)
            If contract Is Nothing Then
                eval.RejectionReason = "Unknown contract"
                Return eval
            End If

            ' Need EmaSlowLength + a small buffer for warmup + the AtrPercentLookback so the
            ' regime filter has enough samples.
            Dim signalBarCount As Integer = Math.Max(_config.EmaSlowLength + _config.AtrPercentLookback + 50, 350)
            Dim today = DateTime.UtcNow.Date
            Try
                Await _barCollection.EnsureBarsAsync(
                    contract.PxContractId, today.AddDays(-7), today,
                    BarTimeframe.FiveMinute, progress:=Nothing, cancel:=ct)
            Catch ex As Exception
                _logger?.LogDebug(ex, "SlipStream signal bar ensure failed for {Symbol}; continuing with cached", symbol)
            End Try

            Dim signalBars As IList(Of MarketBar)
            Try
                signalBars = Await _barIngestion.GetLiveBarsAsync(contract.PxContractId,
                                                                   BarTimeframe.FiveMinute,
                                                                   signalBarCount,
                                                                   cancel:=ct,
                                                                   live:=False)
            Catch ex As Exception
                _logger?.LogWarning(ex, "SlipStream signal bar fetch failed for {Symbol}", symbol)
                eval.RejectionReason = "Bar fetch failed"
                Return eval
            End Try

            If signalBars Is Nothing OrElse signalBars.Count = 0 Then
                eval.RejectionReason = "No bars available"
                Return eval
            End If

            ' HTF series for the bias filter — only fetched when the filter is enabled.
            Dim htfBars As IList(Of MarketBar) = Nothing
            If _config.UseHtfFilter Then
                Dim htfTf = ParseTimeframe(_config.HtfTimeframe)
                Dim htfCount As Integer = Math.Max(_config.HtfEmaLength + 50, 200)
                Try
                    Await _barCollection.EnsureBarsAsync(
                        contract.PxContractId, today.AddDays(-30), today,
                        htfTf, progress:=Nothing, cancel:=ct)
                Catch ex As Exception
                    _logger?.LogDebug(ex, "SlipStream HTF bar ensure failed for {Symbol}; continuing with cached", symbol)
                End Try
                Try
                    htfBars = Await _barIngestion.GetLiveBarsAsync(contract.PxContractId,
                                                                    htfTf,
                                                                    htfCount,
                                                                    cancel:=ct,
                                                                    live:=False)
                Catch ex As Exception
                    _logger?.LogWarning(ex, "SlipStream HTF bar fetch failed for {Symbol}", symbol)
                End Try
            End If

            Dim orderedSignal = signalBars.OrderBy(Function(b) b.Timestamp).ToList()
            Dim orderedHtf = If(htfBars Is Nothing, New List(Of MarketBar)(), htfBars.OrderBy(Function(b) b.Timestamp).ToList())
            Dim computed = ComputeFromBars(symbol, orderedSignal, orderedHtf)
            computed.BarsAvailable = orderedSignal.Count
            computed.HtfBarsAvailable = orderedHtf.Count
            Return computed
        End Function

        ''' <summary>
        ''' Pure indicator evaluation given chronologically-ordered closed-bar series.
        ''' Exposed (Friend) so unit tests can replay fixture series without mocking bar I/O.
        ''' Pass <c>Nothing</c> or an empty list for <paramref name="htfBars"/> when the HTF
        ''' filter is disabled.
        ''' </summary>
        Friend Function ComputeFromBars(symbol As String,
                                        signalBars As List(Of MarketBar),
                                        htfBars As List(Of MarketBar)) As SlipStreamEvaluation
            Dim eval As New SlipStreamEvaluation With {.Symbol = symbol}

            If signalBars Is Nothing OrElse signalBars.Count = 0 Then
                eval.RejectionReason = "No bars available"
                Return eval
            End If

            Dim warmupRequired As Integer = Math.Max(_config.EmaSlowLength, _config.AtrPercentLookback) + 1
            If signalBars.Count < warmupRequired Then
                eval.RejectionReason = "Warmup"
                Return eval
            End If

            ' ── Indicator series ──────────────────────────────────────────────
            Dim closes = signalBars.Select(Function(b) CDbl(b.Close)).ToArray()
            Dim highs = signalBars.Select(Function(b) CDbl(b.High)).ToArray()
            Dim lows = signalBars.Select(Function(b) CDbl(b.Low)).ToArray()

            Dim emaFast = ComputeEmaSeries(closes, _config.EmaFastLength)
            Dim emaSlow = ComputeEmaSeries(closes, _config.EmaSlowLength)
            Dim rsi = ComputeWilderRsi(closes, _config.RsiLength)
            Dim atr = ComputeWilderAtr(highs, lows, closes, _config.AtrLength)
            Dim dmi = ComputeWilderDmi(highs, lows, closes, _config.AdxLength)

            Dim n As Integer = signalBars.Count - 1
            Dim lastBar = signalBars(n)

            Dim emaFastLast As Double = emaFast(n)
            Dim emaSlowLast As Double = emaSlow(n)
            Dim rsiLast As Double = rsi(n)
            Dim atrLast As Double = atr(n)
            Dim adxLast As Double = dmi.Adx(n)
            Dim diPlusLast As Double = dmi.DiPlus(n)
            Dim diMinusLast As Double = dmi.DiMinus(n)

            ' HTF EMA — last value on the HTF closes series.
            Dim htfEmaLast As Double = 0.0
            If _config.UseHtfFilter AndAlso htfBars IsNot Nothing AndAlso htfBars.Count >= _config.HtfEmaLength Then
                Dim htfCloses = htfBars.Select(Function(b) CDbl(b.Close)).ToArray()
                Dim htfEma = ComputeEmaSeries(htfCloses, _config.HtfEmaLength)
                htfEmaLast = htfEma(htfEma.Length - 1)
            End If

            ' ATR percentile rank — count of past N ATRs ≤ current, ÷ N × 100.
            Dim atrPctRank As Double = -1.0
            If atrLast > 0 Then
                Dim lookback = Math.Min(_config.AtrPercentLookback, n + 1)
                If lookback > 0 Then
                    Dim atLeq As Integer = 0
                    For i = n - lookback + 1 To n
                        If atr(i) > 0 AndAlso atr(i) <= atrLast Then atLeq += 1
                    Next
                    atrPctRank = (atLeq / lookback) * 100.0
                End If
            End If

            ' Extended-then-pullback gate. Lookback excludes the current bar (Pine "[1]" offset).
            Dim wasExtUp As Boolean = False
            Dim wasExtDown As Boolean = False
            Dim ext = _config.ExtendBars
            If n - ext >= 0 Then
                Dim maxLowMinusEma As Double = Double.MinValue
                Dim minHighMinusEma As Double = Double.MaxValue
                For i = n - ext To n - 1
                    Dim lme = lows(i) - emaFast(i)
                    Dim hme = highs(i) - emaFast(i)
                    If lme > maxLowMinusEma Then maxLowMinusEma = lme
                    If hme < minHighMinusEma Then minHighMinusEma = hme
                Next
                Dim need = _config.ExtendAtrMult * atrLast
                wasExtUp = (maxLowMinusEma >= need)
                wasExtDown = (minHighMinusEma <= -need)
            End If

            ' Pullback shape on the current bar.
            Dim pullbackLong As Boolean = (CDbl(lastBar.Close) >= emaFastLast) AndAlso (CDbl(lastBar.Low) <= emaFastLast)
            Dim pullbackShort As Boolean = (CDbl(lastBar.Close) <= emaFastLast) AndAlso (CDbl(lastBar.High) >= emaFastLast)

            ' Session / flat windows. Resolve the tz once per evaluation.
            Dim sessionTz = ResolveTimeZone(_config.SessionTimeZone)
            Dim inSession = (Not _config.UseSession) OrElse IsTimestampInWindow(lastBar.Timestamp, _config.SessionWindow, sessionTz)
            Dim inFlat = _config.UseSession AndAlso IsTimestampInWindow(lastBar.Timestamp, _config.FlatWindow, sessionTz)

            ' Trend bias.
            Dim trendLong As Boolean = (CDbl(lastBar.Close) > emaSlowLast) AndAlso (emaFastLast > emaSlowLast)
            Dim trendShort As Boolean = (CDbl(lastBar.Close) < emaSlowLast) AndAlso (emaFastLast < emaSlowLast)
            If _config.UseHtfFilter AndAlso htfEmaLast > 0 Then
                trendLong = trendLong AndAlso CDbl(lastBar.Close) > htfEmaLast
                trendShort = trendShort AndAlso CDbl(lastBar.Close) < htfEmaLast
            End If

            ' Strength + regime.
            Dim strengthOk As Boolean = (Not Double.IsNaN(adxLast)) AndAlso (adxLast >= _config.AdxMin)
            Dim regimeOk As Boolean = (atrPctRank >= _config.AtrPercentMin)

            ' Momentum.
            Dim momentumLong As Boolean = (Not Double.IsNaN(rsiLast)) AndAlso (rsiLast >= _config.RsiLongMin) AndAlso (diPlusLast > diMinusLast)
            Dim momentumShort As Boolean = (Not Double.IsNaN(rsiLast)) AndAlso (rsiLast <= _config.RsiShortMax) AndAlso (diMinusLast > diPlusLast)

            ' ── Populate readout fields ───────────────────────────────────────
            eval.AsOf = lastBar.Timestamp
            eval.LastClose = lastBar.Close
            eval.LastBarHigh = lastBar.High
            eval.LastBarLow = lastBar.Low
            eval.EmaFast = CDec(emaFastLast)
            eval.EmaSlow = CDec(emaSlowLast)
            eval.HtfEma = CDec(htfEmaLast)
            eval.Rsi = rsiLast
            eval.Atr = CDec(atrLast)
            eval.AtrPercentRank = atrPctRank
            eval.Adx = adxLast
            eval.DiPlus = diPlusLast
            eval.DiMinus = diMinusLast
            eval.WasExtendedUp = wasExtUp
            eval.WasExtendedDown = wasExtDown
            eval.InSession = inSession
            eval.InFlatWindow = inFlat
            eval.IsWarm = (Not Double.IsNaN(rsiLast)) AndAlso (Not Double.IsNaN(adxLast)) AndAlso (atrLast > 0) AndAlso (atrPctRank >= 0)

            If Not eval.IsWarm Then
                eval.RejectionReason = "Warmup"
                Return eval
            End If

            ' ── Confluence ─────────────────────────────────────────────────────
            If inSession AndAlso Not inFlat AndAlso strengthOk AndAlso regimeOk Then
                If _config.EnableLong AndAlso trendLong AndAlso wasExtUp AndAlso pullbackLong AndAlso momentumLong Then
                    eval.Signal = SlipStreamSignalSide.Bullish
                    Return eval
                End If
                If _config.EnableShort AndAlso trendShort AndAlso wasExtDown AndAlso pullbackShort AndAlso momentumShort Then
                    eval.Signal = SlipStreamSignalSide.Bearish
                    Return eval
                End If
            End If

            eval.RejectionReason = NearestMissReason(eval, trendLong, trendShort, strengthOk, regimeOk,
                                                     momentumLong, momentumShort, pullbackLong, pullbackShort,
                                                     wasExtUp, wasExtDown, inSession, inFlat)
            Return eval
        End Function

        ' ─── Near-miss reason builder ──────────────────────────────────────────

        Private Function NearestMissReason(eval As SlipStreamEvaluation,
                                           trendLong As Boolean, trendShort As Boolean,
                                           strengthOk As Boolean, regimeOk As Boolean,
                                           momLong As Boolean, momShort As Boolean,
                                           pullLong As Boolean, pullShort As Boolean,
                                           extUp As Boolean, extDown As Boolean,
                                           inSession As Boolean, inFlat As Boolean) As String
            If Not inSession Then Return "Outside session window"
            If inFlat Then Return "Inside force-flat window"
            If Not strengthOk Then Return $"ADX {eval.Adx:F1} below {_config.AdxMin:F0}"
            If Not regimeOk Then Return $"ATR percentile {eval.AtrPercentRank:F0} below {_config.AtrPercentMin:F0}"

            ' Bias-aware reason: pick the side that has trend alignment, otherwise pick by EMA bias.
            Dim leanLong As Boolean = trendLong OrElse (CDbl(eval.LastClose) > CDbl(eval.EmaSlow))
            If leanLong Then
                If Not trendLong Then Return "Trend not aligned long (price ≤ EMAslow or EMAfast ≤ EMAslow)"
                If Not extUp Then Return $"Not extended ≥ {_config.ExtendAtrMult:F1}×ATR above EMAfast before pullback"
                If Not pullLong Then Return "No pullback to EMAfast on this bar"
                If Not momLong Then
                    If eval.Rsi < _config.RsiLongMin Then Return $"RSI {eval.Rsi:F1} below {_config.RsiLongMin:F0}"
                    If eval.DiPlus <= eval.DiMinus Then Return $"DI+ {eval.DiPlus:F1} ≤ DI− {eval.DiMinus:F1}"
                End If
                Return "Long confluence near-miss"
            Else
                If Not trendShort Then Return "Trend not aligned short (price ≥ EMAslow or EMAfast ≥ EMAslow)"
                If Not extDown Then Return $"Not extended ≥ {_config.ExtendAtrMult:F1}×ATR below EMAfast before pullback"
                If Not pullShort Then Return "No pullback to EMAfast on this bar"
                If Not momShort Then
                    If eval.Rsi > _config.RsiShortMax Then Return $"RSI {eval.Rsi:F1} above {_config.RsiShortMax:F0}"
                    If eval.DiMinus <= eval.DiPlus Then Return $"DI− {eval.DiMinus:F1} ≤ DI+ {eval.DiPlus:F1}"
                End If
                Return "Short confluence near-miss"
            End If
        End Function

        ' ─── Indicator math (pure helpers) ─────────────────────────────────────

        ''' <summary>Standard EMA: seed = SMA of first <paramref name="length"/>, then recurrence.</summary>
        Private Shared Function ComputeEmaSeries(closes As Double(), length As Integer) As Double()
            Dim n = closes.Length
            Dim ema(n - 1) As Double
            If n = 0 OrElse length < 1 Then Return ema
            Dim k As Double = 2.0 / (length + 1)
            If n < length Then
                ' Not enough data — leave zeros; downstream warmup gate handles this.
                Return ema
            End If
            Dim seedSum As Double = 0
            For i = 0 To length - 1
                seedSum += closes(i)
                ema(i) = 0  ' warmup region; do not consume
            Next
            ema(length - 1) = seedSum / length
            For i = length To n - 1
                ema(i) = (closes(i) - ema(i - 1)) * k + ema(i - 1)
            Next
            Return ema
        End Function

        ''' <summary>Wilder RSI: AvgGain/AvgLoss seeded as simple mean of first <paramref name="length"/> diffs.</summary>
        Private Shared Function ComputeWilderRsi(closes As Double(), length As Integer) As Double()
            Dim n = closes.Length
            Dim rsi(n - 1) As Double
            For i = 0 To n - 1
                rsi(i) = Double.NaN
            Next
            If n <= length Then Return rsi

            Dim gainSum As Double = 0, lossSum As Double = 0
            For i = 1 To length
                Dim diff = closes(i) - closes(i - 1)
                If diff >= 0 Then gainSum += diff Else lossSum += -diff
            Next
            Dim avgGain As Double = gainSum / length
            Dim avgLoss As Double = lossSum / length
            rsi(length) = ComputeRsiValue(avgGain, avgLoss)

            For i = length + 1 To n - 1
                Dim diff = closes(i) - closes(i - 1)
                Dim gain = If(diff > 0, diff, 0.0)
                Dim loss = If(diff < 0, -diff, 0.0)
                avgGain = ((avgGain * (length - 1)) + gain) / length
                avgLoss = ((avgLoss * (length - 1)) + loss) / length
                rsi(i) = ComputeRsiValue(avgGain, avgLoss)
            Next
            Return rsi
        End Function

        Private Shared Function ComputeRsiValue(avgGain As Double, avgLoss As Double) As Double
            If avgLoss = 0 Then Return If(avgGain = 0, 50.0, 100.0)
            Dim rs = avgGain / avgLoss
            Return 100.0 - (100.0 / (1.0 + rs))
        End Function

        ''' <summary>Wilder ATR: seed = mean of first <paramref name="length"/> TRs, then recurrence.</summary>
        Private Shared Function ComputeWilderAtr(highs As Double(), lows As Double(), closes As Double(), length As Integer) As Double()
            Dim n = closes.Length
            Dim atr(n - 1) As Double
            If n <= length Then Return atr

            Dim trSum As Double = 0
            For i = 1 To length
                trSum += TrueRange(highs(i), lows(i), closes(i - 1))
            Next
            atr(length) = trSum / length

            For i = length + 1 To n - 1
                Dim tr = TrueRange(highs(i), lows(i), closes(i - 1))
                atr(i) = ((atr(i - 1) * (length - 1)) + tr) / length
            Next
            Return atr
        End Function

        Private Shared Function TrueRange(high As Double, low As Double, prevClose As Double) As Double
            Dim a = high - low
            Dim b = Math.Abs(high - prevClose)
            Dim c = Math.Abs(low - prevClose)
            Return Math.Max(a, Math.Max(b, c))
        End Function

        ''' <summary>Wilder DMI/ADX: standard +DM/-DM/TR Wilder smoothing then ADX as Wilder smoothing of DX.</summary>
        Friend Shared Function ComputeWilderDmi(highs As Double(), lows As Double(), closes As Double(), length As Integer) As DmiSeries
            Dim n = closes.Length
            Dim result As New DmiSeries(n)
            If n < length + 1 Then Return result

            ' Per-bar +DM, -DM, TR.
            Dim plusDm(n - 1) As Double
            Dim minusDm(n - 1) As Double
            Dim tr(n - 1) As Double
            For i = 1 To n - 1
                Dim upMove = highs(i) - highs(i - 1)
                Dim downMove = lows(i - 1) - lows(i)
                plusDm(i) = If(upMove > downMove AndAlso upMove > 0, upMove, 0.0)
                minusDm(i) = If(downMove > upMove AndAlso downMove > 0, downMove, 0.0)
                tr(i) = TrueRange(highs(i), lows(i), closes(i - 1))
            Next

            ' Wilder smoothed series.
            Dim sPlus As Double = 0, sMinus As Double = 0, sTr As Double = 0
            For i = 1 To length
                sPlus += plusDm(i)
                sMinus += minusDm(i)
                sTr += tr(i)
            Next
            Dim dxSum As Double = 0
            Dim adxStartIdx = length * 2  ' first index with a full ADX (Wilder-on-DX of length samples)

            For i = length To n - 1
                If i > length Then
                    sPlus = sPlus - (sPlus / length) + plusDm(i)
                    sMinus = sMinus - (sMinus / length) + minusDm(i)
                    sTr = sTr - (sTr / length) + tr(i)
                End If
                Dim diPlus As Double = If(sTr > 0, 100.0 * sPlus / sTr, 0.0)
                Dim diMinus As Double = If(sTr > 0, 100.0 * sMinus / sTr, 0.0)
                result.DiPlus(i) = diPlus
                result.DiMinus(i) = diMinus

                Dim diSum = diPlus + diMinus
                Dim dx As Double = If(diSum > 0, 100.0 * Math.Abs(diPlus - diMinus) / diSum, 0.0)

                If i >= length AndAlso i < adxStartIdx Then
                    dxSum += dx
                    If i = adxStartIdx - 1 Then
                        result.Adx(i) = dxSum / length
                    End If
                ElseIf i >= adxStartIdx Then
                    result.Adx(i) = ((result.Adx(i - 1) * (length - 1)) + dx) / length
                End If
            Next

            ' Pre-warmup ADX cells are NaN so the consumer can distinguish "no data" from "0".
            For i = 0 To Math.Min(adxStartIdx - 1, n - 1)
                result.Adx(i) = Double.NaN
            Next
            For i = 0 To length - 1
                result.DiPlus(i) = Double.NaN
                result.DiMinus(i) = Double.NaN
            Next

            Return result
        End Function

        Friend Class DmiSeries
            Public DiPlus() As Double
            Public DiMinus() As Double
            Public Adx() As Double

            Public Sub New(n As Integer)
                ReDim DiPlus(Math.Max(n - 1, 0))
                ReDim DiMinus(Math.Max(n - 1, 0))
                ReDim Adx(Math.Max(n - 1, 0))
            End Sub
        End Class

        ' ─── Session / timezone helpers ────────────────────────────────────────

        ''' <summary>
        ''' Returns True when the UTC timestamp, converted to <paramref name="tz"/>, has an
        ''' HHmm value that falls inside "HHmm-HHmm". Overnight wrap (start &gt; end) supported.
        ''' </summary>
        Friend Shared Function IsTimestampInWindow(utcTs As DateTimeOffset, window As String, tz As TimeZoneInfo) As Boolean
            If String.IsNullOrWhiteSpace(window) Then Return False
            If tz Is Nothing Then tz = TimeZoneInfo.Utc
            Dim parts = window.Split("-"c)
            If parts.Length <> 2 Then Return False
            Dim startHm As Integer, endHm As Integer
            If Not Integer.TryParse(parts(0).Trim(), startHm) Then Return False
            If Not Integer.TryParse(parts(1).Trim(), endHm) Then Return False

            Dim localTs = TimeZoneInfo.ConvertTime(utcTs, tz)
            Dim hm = localTs.Hour * 100 + localTs.Minute
            If startHm <= endHm Then
                Return hm >= startHm AndAlso hm < endHm
            Else
                ' Overnight window (e.g. "2300-0500") — wraps midnight.
                Return hm >= startHm OrElse hm < endHm
            End If
        End Function

        ''' <summary>
        ''' Resolves a Windows or IANA time zone id to a <see cref="TimeZoneInfo"/>. Tries the
        ''' supplied id first, then the same id with Windows↔IANA swap for cross-platform
        ''' portability, and finally falls back to Central time then UTC if nothing resolves.
        ''' Empty input returns the Central default.
        ''' </summary>
        Friend Shared Function ResolveTimeZone(tzId As String) As TimeZoneInfo
            If String.IsNullOrWhiteSpace(tzId) Then tzId = "Central Standard Time"
            Try
                Return TimeZoneInfo.FindSystemTimeZoneById(tzId)
            Catch
            End Try
            ' Cross-platform fallback: common Windows↔IANA aliases for the ids we ship.
            Dim alias_ As String = Nothing
            Select Case tzId
                Case "Central Standard Time" : alias_ = "America/Chicago"
                Case "America/Chicago" : alias_ = "Central Standard Time"
                Case "GMT Standard Time" : alias_ = "Europe/London"
                Case "Europe/London" : alias_ = "GMT Standard Time"
                Case "Eastern Standard Time" : alias_ = "America/New_York"
                Case "America/New_York" : alias_ = "Eastern Standard Time"
            End Select
            If alias_ IsNot Nothing Then
                Try
                    Return TimeZoneInfo.FindSystemTimeZoneById(alias_)
                Catch
                End Try
            End If
            ' Last-resort fallbacks: Central (current default) then UTC.
            Try
                Return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time")
            Catch
                Try
                    Return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago")
                Catch
                    Return TimeZoneInfo.Utc
                End Try
            End Try
        End Function

        ''' <summary>Parses "5min", "60min", "1hour", "15min" etc. into a <see cref="BarTimeframe"/>.</summary>
        Friend Shared Function ParseTimeframe(label As String) As BarTimeframe
            If String.IsNullOrWhiteSpace(label) Then Return BarTimeframe.OneHour
            Dim lower = label.Trim().ToLowerInvariant()
            Select Case lower
                Case "1min", "1minute", "1m" : Return BarTimeframe.OneMinute
                Case "3min", "3minute", "3m" : Return BarTimeframe.ThreeMinute
                Case "5min", "5minute", "5m" : Return BarTimeframe.FiveMinute
                Case "15min", "15minute", "15m" : Return BarTimeframe.FifteenMinute
                Case "30min", "30minute", "30m" : Return BarTimeframe.ThirtyMinute
                Case "60min", "60minute", "60m", "1hour", "1hr", "1h" : Return BarTimeframe.OneHour
                Case "120min", "2hour", "2hr", "2h" : Return BarTimeframe.TwoHour
                Case "240min", "4hour", "4hr", "4h" : Return BarTimeframe.FourHour
                Case "daily", "1d", "1day" : Return BarTimeframe.Daily
                Case Else : Return BarTimeframe.OneHour
            End Select
        End Function

    End Class

End Namespace
