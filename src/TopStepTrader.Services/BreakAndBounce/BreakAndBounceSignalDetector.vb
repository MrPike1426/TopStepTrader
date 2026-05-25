Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.Market

Namespace TopStepTrader.Services.BreakAndBounce

    ''' <summary>
    ''' FEAT-62: Computes the Break and Bounce entry chain for a single symbol and
    ''' returns a <see cref="BreakAndBounceEvaluation"/> for the most recent closed
    ''' 5-minute bar. The strategy logic lives in the <c>ComputeFromBars</c> Friend
    ''' seam so unit tests can replay fixture series without bar I/O.
    ''' </summary>
    Public Class BreakAndBounceSignalDetector
        Implements IBreakAndBounceSignalDetector

        Private ReadOnly _barIngestion As IBarIngestionService
        Private ReadOnly _barCollection As IBarCollectionService
        Private ReadOnly _dailyRange As IDailyRangeService
        Private ReadOnly _config As BreakAndBounceConfig
        Private ReadOnly _tracker As BreakoutStateTracker
        Private ReadOnly _logger As ILogger(Of BreakAndBounceSignalDetector)

        Private Shared ReadOnly s_exchangeTz As TimeZoneInfo = ResolveExchangeTz()

        Public Sub New(barIngestion As IBarIngestionService,
                       barCollection As IBarCollectionService,
                       dailyRange As IDailyRangeService,
                       config As BreakAndBounceConfig,
                       tracker As BreakoutStateTracker,
                       logger As ILogger(Of BreakAndBounceSignalDetector))
            _barIngestion = barIngestion
            _barCollection = barCollection
            _dailyRange = dailyRange
            _config = config
            _tracker = tracker
            _logger = logger
        End Sub

        Public Async Function EvaluateAsync(symbol As String,
                                            ct As CancellationToken) _
            As Task(Of BreakAndBounceEvaluation) Implements IBreakAndBounceSignalDetector.EvaluateAsync

            Dim eval As New BreakAndBounceEvaluation With {.Symbol = symbol}

            Dim contract = FavouriteContracts.TryGetBySymbolResolved(symbol)
            If contract Is Nothing Then
                eval.RejectionReason = "Unknown contract"
                Return eval
            End If

            ' ── Previous-day reference range ──────────────────────────────
            Dim range As DailyRange? = Nothing
            Try
                range = Await _dailyRange.GetPreviousDayRangeAsync(contract.PxContractId, ct)
            Catch ex As Exception
                _logger?.LogWarning(ex, "BreakAndBounce daily range fetch failed for {Symbol}", symbol)
            End Try
            If Not range.HasValue Then
                eval.RejectionReason = "Previous-day range unavailable"
                Return eval
            End If
            eval.PrevHigh = range.Value.PrevHigh
            eval.PrevLow = range.Value.PrevLow
            eval.HasPreviousRange = True

            ' ── Fetch retest-TF (5m) bars and breakout-TF (15m) bars ──────
            Dim today = DateTime.UtcNow.Date
            Dim retestTf As BarTimeframe = ParseTimeframe(_config.RetestTimeframe, BarTimeframe.FiveMinute)
            Dim breakoutTf As BarTimeframe = ParseTimeframe(_config.BreakoutTimeframe, BarTimeframe.FifteenMinute)

            Try
                Await _barCollection.EnsureBarsAsync(
                    contract.PxContractId, today.AddDays(-3), today,
                    retestTf, progress:=Nothing, cancel:=ct)
                Await _barCollection.EnsureBarsAsync(
                    contract.PxContractId, today.AddDays(-3), today,
                    breakoutTf, progress:=Nothing, cancel:=ct)
            Catch ex As Exception
                _logger?.LogDebug(ex, "BreakAndBounce bar ensure failed for {Symbol}", symbol)
            End Try

            Dim retestBars As IList(Of MarketBar)
            Dim breakoutBars As IList(Of MarketBar)
            Try
                Dim retestNeeded As Integer = Math.Max(_config.AtrLength + 5, 30)
                retestBars = Await _barIngestion.GetLiveBarsAsync(contract.PxContractId,
                                                                   retestTf, retestNeeded,
                                                                   cancel:=ct, live:=False)
                breakoutBars = Await _barIngestion.GetLiveBarsAsync(contract.PxContractId,
                                                                     breakoutTf, 50,
                                                                     cancel:=ct, live:=False)
            Catch ex As Exception
                _logger?.LogWarning(ex, "BreakAndBounce bar fetch failed for {Symbol}", symbol)
                eval.RejectionReason = "Bar fetch failed"
                Return eval
            End Try

            Dim retestOrdered = If(retestBars Is Nothing,
                                    New List(Of MarketBar)(),
                                    retestBars.OrderBy(Function(b) b.Timestamp).ToList())
            Dim breakoutOrdered = If(breakoutBars Is Nothing,
                                       New List(Of MarketBar)(),
                                       breakoutBars.OrderBy(Function(b) b.Timestamp).ToList())

            Return ComputeFromBars(symbol, contract.PxTickSize,
                                    range.Value, retestOrdered, breakoutOrdered)
        End Function

        ''' <summary>
        ''' Pure evaluation given the previous-day range and chronologically-ordered
        ''' retest-TF + breakout-TF bar series. <c>Friend</c> so xUnit fixtures can
        ''' drive the chain without mocking bar I/O.
        ''' </summary>
        Friend Function ComputeFromBars(symbol As String,
                                        tickSize As Decimal,
                                        range As DailyRange,
                                        retestBars As List(Of MarketBar),
                                        breakoutBars As List(Of MarketBar)) As BreakAndBounceEvaluation
            Dim eval As New BreakAndBounceEvaluation With {.Symbol = symbol}
            eval.PrevHigh = range.PrevHigh
            eval.PrevLow = range.PrevLow
            eval.HasPreviousRange = True
            eval.RetestBarsAvailable = If(retestBars Is Nothing, 0, retestBars.Count)
            eval.BreakoutBarsAvailable = If(breakoutBars Is Nothing, 0, breakoutBars.Count)

            If retestBars Is Nothing OrElse retestBars.Count < 2 Then
                eval.RejectionReason = "Retest bars unavailable"
                Return eval
            End If
            If breakoutBars Is Nothing OrElse breakoutBars.Count < 1 Then
                eval.RejectionReason = "Breakout bars unavailable"
                Return eval
            End If

            Dim lastRetest = retestBars(retestBars.Count - 1)
            Dim prevRetest = retestBars(retestBars.Count - 2)
            Dim lastBreakout = breakoutBars(breakoutBars.Count - 1)

            eval.AsOf = lastRetest.Timestamp
            eval.LastFiveClose = lastRetest.Close
            eval.LastFiveHigh = lastRetest.High
            eval.LastFiveLow = lastRetest.Low
            eval.LastFifteenClose = lastBreakout.Close

            ' ── Session window gate ───────────────────────────────────────
            Dim inEntry As Boolean = IsTimestampInWindow(lastRetest.Timestamp, _config.EntryWindow)
            Dim inFlat As Boolean = IsTimestampInWindow(lastRetest.Timestamp, _config.FlatWindow)
            eval.InEntryWindow = inEntry
            eval.InFlatWindow = inFlat

            ' ── 15m breakout state ────────────────────────────────────────
            _tracker.Update(symbol, lastBreakout, range.PrevHigh, range.PrevLow)
            Dim dir = _tracker.GetDirection(symbol)
            eval.Direction = dir

            If Not inEntry Then
                eval.RejectionReason = "Outside entry window"
                Return eval
            End If
            If inFlat Then
                eval.RejectionReason = "Inside force-flat window"
                Return eval
            End If
            If dir = 0 Then
                eval.RejectionReason = "No 15m breakout bias yet"
                Return eval
            End If

            ' ── 5m retest + candle pattern ────────────────────────────────
            Dim retestLong As Boolean = (dir = 1) AndAlso (lastRetest.Low <= range.PrevHigh) AndAlso (lastRetest.Close > range.PrevHigh)
            Dim retestShort As Boolean = (dir = -1) AndAlso (lastRetest.High >= range.PrevLow) AndAlso (lastRetest.Close < range.PrevLow)
            eval.RetestTagged = retestLong OrElse retestShort

            If dir = 1 AndAlso Not retestLong Then
                eval.RejectionReason = "No 5m retest of prev high"
                Return eval
            End If
            If dir = -1 AndAlso Not retestShort Then
                eval.RejectionReason = "No 5m retest of prev low"
                Return eval
            End If

            Dim patternHit As String = String.Empty
            If dir = 1 Then
                If Not _config.EnableLong Then
                    eval.RejectionReason = "Long entries disabled"
                    Return eval
                End If
                If CandlePatternDetector.IsHammer(lastRetest) Then
                    patternHit = "Hammer"
                ElseIf CandlePatternDetector.IsBullishEngulfing(lastRetest, prevRetest) Then
                    patternHit = "BullishEngulfing"
                End If
            ElseIf dir = -1 Then
                If Not _config.EnableShort Then
                    eval.RejectionReason = "Short entries disabled"
                    Return eval
                End If
                If CandlePatternDetector.IsInvertedHammer(lastRetest) Then
                    patternHit = "InvertedHammer"
                ElseIf CandlePatternDetector.IsBearishEngulfing(lastRetest, prevRetest) Then
                    patternHit = "BearishEngulfing"
                End If
            End If

            If String.IsNullOrEmpty(patternHit) Then
                eval.RejectionReason = "No qualifying candle pattern"
                Return eval
            End If
            eval.PatternHit = patternHit

            ' ── SL floor (furthest from entry of: PDF stop, ticks floor, ATR floor) ──
            Dim atr14 As Decimal = ComputeWilderAtr(retestBars, _config.AtrLength)
            eval.Atr = atr14

            Dim stopResult = ComputeInitialStop(dir, lastRetest, range, atr14, tickSize, _config)
            eval.SuggestedInitialStopPrice = stopResult.StopPrice
            eval.StopFloorSource = stopResult.Source

            eval.Signal = If(dir = 1, BreakAndBounceSignalSide.Bullish, BreakAndBounceSignalSide.Bearish)
            Return eval
        End Function

        ''' <summary>
        ''' Computes the initial stop and returns which floor won. Public-ish via Friend
        ''' so tests can pin behaviour per ticket acceptance.
        ''' </summary>
        Friend Shared Function ComputeInitialStop(dir As Integer,
                                                   retestBar As MarketBar,
                                                   range As DailyRange,
                                                   atr14 As Decimal,
                                                   tickSize As Decimal,
                                                   config As BreakAndBounceConfig) As (StopPrice As Decimal, Source As String)
            Dim entry As Decimal = retestBar.Close
            Dim ticksFloor As Decimal = config.MinimumStopDistanceTicks * tickSize
            Dim atrFloor As Decimal = atr14 * CDec(config.MinimumStopAtrFraction)

            If dir = 1 Then
                Dim stopPdf As Decimal = range.PrevHigh - retestBar.Range * 0.2D
                Dim stopMinTicks As Decimal = entry - ticksFloor
                Dim stopMinAtr As Decimal = If(atr14 > 0D, entry - atrFloor, stopMinTicks)
                Dim final As Decimal = Math.Min(stopPdf, Math.Min(stopMinTicks, stopMinAtr))
                Dim source = "PDF"
                If final = stopMinTicks Then source = "TicksFloor"
                If final = stopMinAtr Then source = "AtrFloor"
                Return (final, source)
            Else
                Dim stopPdf As Decimal = range.PrevLow + retestBar.Range * 0.2D
                Dim stopMinTicks As Decimal = entry + ticksFloor
                Dim stopMinAtr As Decimal = If(atr14 > 0D, entry + atrFloor, stopMinTicks)
                Dim final As Decimal = Math.Max(stopPdf, Math.Max(stopMinTicks, stopMinAtr))
                Dim source = "PDF"
                If final = stopMinTicks Then source = "TicksFloor"
                If final = stopMinAtr Then source = "AtrFloor"
                Return (final, source)
            End If
        End Function

        ''' <summary>Wilder ATR over the closed bar series. Returns 0 when insufficient data.</summary>
        Friend Shared Function ComputeWilderAtr(bars As IList(Of MarketBar), length As Integer) As Decimal
            If bars Is Nothing OrElse bars.Count <= length OrElse length < 1 Then Return 0D
            Dim trSum As Decimal = 0D
            For i = 1 To length
                trSum += TrueRange(bars(i), bars(i - 1))
            Next
            Dim atr As Decimal = trSum / length
            For i = length + 1 To bars.Count - 1
                Dim tr = TrueRange(bars(i), bars(i - 1))
                atr = ((atr * (length - 1)) + tr) / length
            Next
            Return atr
        End Function

        Private Shared Function TrueRange(curr As MarketBar, prev As MarketBar) As Decimal
            Dim a As Decimal = curr.High - curr.Low
            Dim b As Decimal = Math.Abs(curr.High - prev.Close)
            Dim c As Decimal = Math.Abs(curr.Low - prev.Close)
            Return Math.Max(a, Math.Max(b, c))
        End Function

        ' ─── Session / timezone helpers (mirror SlipStreamSignalDetector) ──

        Friend Shared Function IsTimestampInWindow(utcTs As DateTimeOffset, window As String) As Boolean
            If String.IsNullOrWhiteSpace(window) Then Return False
            Dim parts = window.Split("-"c)
            If parts.Length <> 2 Then Return False
            Dim startHm As Integer, endHm As Integer
            If Not Integer.TryParse(parts(0).Trim(), startHm) Then Return False
            If Not Integer.TryParse(parts(1).Trim(), endHm) Then Return False
            Dim exchangeLocal = TimeZoneInfo.ConvertTime(utcTs, s_exchangeTz)
            Dim hm = exchangeLocal.Hour * 100 + exchangeLocal.Minute
            If startHm <= endHm Then
                Return hm >= startHm AndAlso hm < endHm
            Else
                Return hm >= startHm OrElse hm < endHm
            End If
        End Function

        Private Shared Function ResolveExchangeTz() As TimeZoneInfo
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

        Friend Shared Function ParseTimeframe(label As String, fallback As BarTimeframe) As BarTimeframe
            If String.IsNullOrWhiteSpace(label) Then Return fallback
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
                Case Else : Return fallback
            End Select
        End Function

    End Class

End Namespace
