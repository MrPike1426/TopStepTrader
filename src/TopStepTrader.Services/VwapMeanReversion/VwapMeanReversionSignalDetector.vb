Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.ML.Features
Imports TopStepTrader.Services.BreakAndBounce
Imports TopStepTrader.Services.Market
Imports TopStepTrader.Services.SlipStream

Namespace TopStepTrader.Services.VwapMeanReversion

    ''' <summary>
    ''' FEAT-75: Computes the VWAP Mean-Reversion entry chain for a single symbol and
    ''' returns a <see cref="VwapMeanReversionEvaluation"/> for the most recent closed
    ''' 5-minute bar. Entry chain (long; short is the mirror):
    '''
    '''   (a) 5-min close ≥ SdEntryThreshold SDs below the session-anchored VWAP,
    '''   (b) 15-min ADX(14) below the trend-veto threshold,
    '''   (c) 1-min reversal candle (close &gt; open AND close &gt; prior 1-min high,
    '''       or bullish engulfing),
    '''   (d) excursion not beyond the too-far-gone SD veto,
    '''   (e) outside the pre-close entry cutoff.
    '''
    ''' The strategy logic lives in the <c>ComputeFromBars</c> Friend seam so unit tests
    ''' can replay fixture series without bar I/O.
    ''' </summary>
    Public Class VwapMeanReversionSignalDetector
        Implements IVwapMeanReversionSignalDetector

        Private ReadOnly _barIngestion As IBarIngestionService
        Private ReadOnly _barCollection As IBarCollectionService
        Private ReadOnly _config As VwapMeanReversionConfig
        Private ReadOnly _logger As ILogger(Of VwapMeanReversionSignalDetector)

        Public Sub New(barIngestion As IBarIngestionService,
                       barCollection As IBarCollectionService,
                       config As VwapMeanReversionConfig,
                       logger As ILogger(Of VwapMeanReversionSignalDetector))
            _barIngestion = barIngestion
            _barCollection = barCollection
            _config = config
            _logger = logger
        End Sub

        Public Async Function EvaluateAsync(symbol As String,
                                            ct As CancellationToken) _
            As Task(Of VwapMeanReversionEvaluation) Implements IVwapMeanReversionSignalDetector.EvaluateAsync

            Dim eval As New VwapMeanReversionEvaluation With {.Symbol = symbol}

            Dim contract = FavouriteContracts.TryGetBySymbolResolved(symbol)
            If contract Is Nothing Then
                eval.RejectionReason = "Unknown contract"
                Return eval
            End If

            Dim today = DateTime.UtcNow.Date
            Try
                ' Full current session (up to ~276 five-min bars) + previous session for ATR warmup.
                Await _barCollection.EnsureBarsAsync(
                    contract.PxContractId, today.AddDays(-3), today,
                    BarTimeframe.FiveMinute, progress:=Nothing, cancel:=ct)
                Await _barCollection.EnsureBarsAsync(
                    contract.PxContractId, today.AddDays(-3), today,
                    BarTimeframe.FifteenMinute, progress:=Nothing, cancel:=ct)
                Await _barCollection.EnsureBarsAsync(
                    contract.PxContractId, today.AddDays(-1), today,
                    BarTimeframe.OneMinute, progress:=Nothing, cancel:=ct)
            Catch ex As Exception
                _logger?.LogDebug(ex, "VwapMeanReversion bar ensure failed for {Symbol}; continuing with cached", symbol)
            End Try

            Dim fiveBars As IList(Of MarketBar)
            Dim fifteenBars As IList(Of MarketBar)
            Dim oneBars As IList(Of MarketBar)
            Try
                fiveBars = Await _barIngestion.GetLiveBarsAsync(contract.PxContractId,
                                                                 BarTimeframe.FiveMinute, 400,
                                                                 cancel:=ct, live:=False)
                fifteenBars = Await _barIngestion.GetLiveBarsAsync(contract.PxContractId,
                                                                    BarTimeframe.FifteenMinute, 80,
                                                                    cancel:=ct, live:=False)
                oneBars = Await _barIngestion.GetLiveBarsAsync(contract.PxContractId,
                                                                BarTimeframe.OneMinute, 10,
                                                                cancel:=ct, live:=False)
            Catch ex As Exception
                _logger?.LogWarning(ex, "VwapMeanReversion bar fetch failed for {Symbol}", symbol)
                eval.RejectionReason = "Bar fetch failed"
                Return eval
            End Try

            Dim fiveOrdered = OrderedOrEmpty(fiveBars)
            Dim fifteenOrdered = OrderedOrEmpty(fifteenBars)
            Dim oneOrdered = OrderedOrEmpty(oneBars)

            Return ComputeFromBars(symbol, fiveOrdered, fifteenOrdered, oneOrdered)
        End Function

        Private Shared Function OrderedOrEmpty(bars As IList(Of MarketBar)) As List(Of MarketBar)
            If bars Is Nothing Then Return New List(Of MarketBar)()
            Return bars.OrderBy(Function(b) b.Timestamp).ToList()
        End Function

        ''' <summary>
        ''' Pure evaluation given chronologically-ordered 5-min (signal), 15-min (ADX veto)
        ''' and 1-min (confirmation) closed-bar series. <c>Friend</c> so xUnit fixtures can
        ''' drive the chain without mocking bar I/O.
        ''' </summary>
        Friend Function ComputeFromBars(symbol As String,
                                        fiveBars As List(Of MarketBar),
                                        fifteenBars As List(Of MarketBar),
                                        oneBars As List(Of MarketBar)) As VwapMeanReversionEvaluation
            Dim eval As New VwapMeanReversionEvaluation With {.Symbol = symbol}
            eval.BarsAvailable = If(fiveBars Is Nothing, 0, fiveBars.Count)
            eval.AdxBarsAvailable = If(fifteenBars Is Nothing, 0, fifteenBars.Count)
            eval.ConfirmBarsAvailable = If(oneBars Is Nothing, 0, oneBars.Count)

            If fiveBars Is Nothing OrElse fiveBars.Count < _config.AtrLength + 2 Then
                eval.RejectionReason = "5m bars warmup"
                Return eval
            End If
            If fifteenBars Is Nothing OrElse fifteenBars.Count < _config.AdxLength * 2 + 1 Then
                eval.RejectionReason = "15m ADX warmup"
                Return eval
            End If
            If oneBars Is Nothing OrElse oneBars.Count < 2 Then
                eval.RejectionReason = "1m bars unavailable"
                Return eval
            End If

            Dim lastFive = fiveBars(fiveBars.Count - 1)
            eval.AsOf = lastFive.Timestamp
            eval.LastClose = lastFive.Close

            ' ── Session-anchored VWAP + SD (FEAT-75 F2) ─────────────────────
            Dim bands = TechnicalIndicators.VwapStandardDeviationBands(
                fiveBars.Select(Function(b) b.Timestamp).ToList(),
                fiveBars.Select(Function(b) b.High).ToList(),
                fiveBars.Select(Function(b) b.Low).ToList(),
                fiveBars.Select(Function(b) b.Close).ToList(),
                fiveBars.Select(Function(b) b.Volume).ToList())
            Dim n = fiveBars.Count - 1
            Dim vwap As Double = bands.Vwap(n)
            Dim sd As Double = bands.Sd(n)
            eval.Vwap = CDec(vwap)
            eval.Sd = CDec(sd)

            ' ── ATR(14) on the 5-min (stop floor + T2 trail basis) ──────────
            Dim atr As Decimal = BreakAndBounceSignalDetector.ComputeWilderAtr(fiveBars, _config.AtrLength)
            eval.Atr = atr

            ' ── 15-min ADX(14) trend veto ────────────────────────────────────
            Dim adxSeries = SlipStreamSignalDetector.ComputeWilderDmi(
                fifteenBars.Select(Function(b) CDbl(b.High)).ToArray(),
                fifteenBars.Select(Function(b) CDbl(b.Low)).ToArray(),
                fifteenBars.Select(Function(b) CDbl(b.Close)).ToArray(),
                _config.AdxLength)
            Dim adxLast As Double = adxSeries.Adx(fifteenBars.Count - 1)
            eval.Adx = adxLast

            eval.IsWarm = (sd > 0) AndAlso (atr > 0D) AndAlso Not Double.IsNaN(adxLast)
            If Not eval.IsWarm Then
                eval.RejectionReason = "Warmup (SD/ATR/ADX not ready)"
                Return eval
            End If

            ' ── Signed SD deviation of the 5-min close from VWAP ────────────
            Dim dev As Double = (CDbl(lastFive.Close) - vwap) / sd
            eval.DeviationSd = dev

            ' ── Condition flags (always populated for the UI status grid) ───
            eval.InEntryWindow = Not _config.IsInsideEntryCutoff(lastFive.Timestamp.UtcDateTime)
            eval.BeyondEntryBand = Math.Abs(dev) >= _config.SdEntryThreshold
            eval.TooFarGone = Math.Abs(dev) >= _config.SdTooFarThreshold
            eval.AdxVetoPassed = adxLast < _config.AdxVetoThreshold

            Dim dir As Integer = If(dev <= 0, 1, -1)   ' below VWAP → fade long
            Dim lastOne = oneBars(oneBars.Count - 1)
            Dim prevOne = oneBars(oneBars.Count - 2)
            Dim pattern As String = String.Empty
            If dir = 1 Then
                If lastOne.Close > lastOne.Open AndAlso lastOne.Close > prevOne.High Then
                    pattern = "Reversal"
                ElseIf CandlePatternDetector.IsBullishEngulfing(lastOne, prevOne) Then
                    pattern = "BullishEngulfing"
                End If
            Else
                If lastOne.Close < lastOne.Open AndAlso lastOne.Close < prevOne.Low Then
                    pattern = "Reversal"
                ElseIf CandlePatternDetector.IsBearishEngulfing(lastOne, prevOne) Then
                    pattern = "BearishEngulfing"
                End If
            End If
            eval.ConfirmationCandle = Not String.IsNullOrEmpty(pattern)
            eval.ConfirmationPattern = pattern

            ' ── Gate chain (ordered so RejectionReason names the first miss) ─
            If Not eval.InEntryWindow Then
                eval.RejectionReason = "Inside pre-close entry cutoff"
                Return eval
            End If
            If Not eval.BeyondEntryBand Then
                eval.RejectionReason = $"Only {Math.Abs(dev):F1}σ from VWAP (need {_config.SdEntryThreshold:F1}σ)"
                Return eval
            End If
            If eval.TooFarGone Then
                eval.RejectionReason = $"Too far gone: {Math.Abs(dev):F1}σ ≥ {_config.SdTooFarThreshold:F1}σ (breakout, not fade)"
                Return eval
            End If
            If Not eval.AdxVetoPassed Then
                eval.RejectionReason = $"Trend veto: 15m ADX {adxLast:F1} ≥ {_config.AdxVetoThreshold:F0}"
                Return eval
            End If
            If Not eval.ConfirmationCandle Then
                eval.RejectionReason = "No 1m reversal confirmation yet"
                Return eval
            End If
            If dir = 1 AndAlso Not _config.EnableLong Then
                eval.RejectionReason = "Long entries disabled"
                Return eval
            End If
            If dir = -1 AndAlso Not _config.EnableShort Then
                eval.RejectionReason = "Short entries disabled"
                Return eval
            End If

            ' ── Trade plan ───────────────────────────────────────────────────
            Dim entry As Decimal = lastFive.Close
            Dim atrFloor As Decimal = atr * CDec(_config.MinStopAtrMult)
            If dir = 1 Then
                Dim stopDist As Decimal = Math.Max(entry - lastFive.Low, atrFloor)
                eval.SuggestedInitialStopPrice = entry - stopDist
                eval.T1Price = CDec(vwap)
                ' T2: opposite 1 SD band or Tp2StopMultiple × stop distance — the closer wins.
                eval.T2Price = Math.Min(CDec(vwap + sd), entry + CDec(_config.Tp2StopMultiple) * stopDist)
                eval.Signal = VwapMeanReversionSignalSide.Bullish
            Else
                Dim stopDist As Decimal = Math.Max(lastFive.High - entry, atrFloor)
                eval.SuggestedInitialStopPrice = entry + stopDist
                eval.T1Price = CDec(vwap)
                eval.T2Price = Math.Max(CDec(vwap - sd), entry - CDec(_config.Tp2StopMultiple) * stopDist)
                eval.Signal = VwapMeanReversionSignalSide.Bearish
            End If
            Return eval
        End Function

    End Class

End Namespace
