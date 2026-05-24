Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.Market

Namespace TopStepTrader.Services.Scalper

    ''' <summary>
    ''' FEAT-64: Computes the three-element confluence (5m MA200, session VWAP, RSI(14) with
    ''' recency filter) for a single instrument and returns an
    ''' <see cref="UltimateScalperEvaluation"/> for the most recent closed 5-minute bar.
    '''
    ''' v1 rebuilds the indicators from scratch on every call. At 5m cadence with three
    ''' symbols this is cheap (~250 bars × 3 = 750 simple-math iterations every 5 minutes);
    ''' incremental caching is a future optimisation.
    ''' </summary>
    Public Class UltimateScalperSignalDetector
        Implements IUltimateScalperSignalDetector

        Private ReadOnly _barIngestion As IBarIngestionService
        Private ReadOnly _barCollection As IBarCollectionService
        Private ReadOnly _config As UltimateScalperConfig
        Private ReadOnly _logger As ILogger(Of UltimateScalperSignalDetector)

        ''' <summary>Gap (minutes) between consecutive bars that triggers a session-VWAP reset.</summary>
        Private Const SessionGapMinutes As Integer = 30

        Public Sub New(barIngestion As IBarIngestionService,
                       barCollection As IBarCollectionService,
                       config As UltimateScalperConfig,
                       logger As ILogger(Of UltimateScalperSignalDetector))
            _barIngestion = barIngestion
            _barCollection = barCollection
            _config = config
            _logger = logger
        End Sub

        Public Async Function EvaluateAsync(symbol As String,
                                            ct As CancellationToken) As Task(Of UltimateScalperEvaluation) Implements IUltimateScalperSignalDetector.EvaluateAsync

            Dim eval = New UltimateScalperEvaluation With {.Symbol = symbol}

            Dim contract = FavouriteContracts.TryGetBySymbolResolved(symbol)
            If contract Is Nothing Then
                eval.RejectionReason = "Unknown contract"
                Return eval
            End If

            ' Backfill any gaps in the 5m bar store, then pull the latest series.
            Dim barCount = Math.Max(_config.MaLength + 50, 250)
            Try
                Dim today = DateTime.UtcNow.Date
                Await _barCollection.EnsureBarsAsync(
                    contract.PxContractId, today.AddDays(-7), today,
                    BarTimeframe.FiveMinute, progress:=Nothing, cancel:=ct)
            Catch ex As Exception
                _logger?.LogDebug(ex, "5m bar ensure failed for {Symbol}; continuing with what's cached", symbol)
            End Try

            Dim bars As IList(Of MarketBar)
            Try
                bars = Await _barIngestion.GetLiveBarsAsync(contract.PxContractId,
                                                            BarTimeframe.FiveMinute,
                                                            barCount,
                                                            cancel:=ct,
                                                            live:=False)
            Catch ex As Exception
                _logger?.LogWarning(ex, "5m bar fetch failed for {Symbol}", symbol)
                eval.RejectionReason = "Bar fetch failed"
                Return eval
            End Try

            If bars Is Nothing OrElse bars.Count = 0 Then
                eval.RejectionReason = "No bars available"
                Return eval
            End If

            ' Sort chronologically; some sources return descending.
            Dim ordered = bars.OrderBy(Function(b) b.Timestamp).ToList()
            Dim computed = ComputeFromBars(symbol, ordered)
            computed.BarsAvailable = ordered.Count
            Return computed
        End Function

        ''' <summary>
        ''' Pure indicator evaluation given a chronologically-ordered closed-bar series.
        ''' Exposed (Friend) so unit tests can replay fixture series without mocking bar I/O.
        ''' </summary>
        Friend Function ComputeFromBars(symbol As String, bars As List(Of MarketBar)) As UltimateScalperEvaluation
            Dim eval = New UltimateScalperEvaluation With {.Symbol = symbol}

            If bars Is Nothing OrElse bars.Count = 0 Then
                eval.RejectionReason = "No bars available"
                Return eval
            End If

            Dim vwap = New SessionVwapCalculator()
            Dim rsi = New RsiRecencyTracker(_config.RsiLength, _config.RsiMidline)
            Dim previousTs As DateTimeOffset = bars(0).Timestamp
            Dim ma200 As Decimal = 0D

            ' Rolling window for MA200.
            Dim window As New Queue(Of Decimal)(_config.MaLength + 1)
            Dim windowSum As Decimal = 0D

            For i = 0 To bars.Count - 1
                Dim bar = bars(i)

                ' Session-VWAP reset on a gap > SessionGapMinutes.
                If i > 0 Then
                    Dim gap = bar.Timestamp - previousTs
                    If gap.TotalMinutes > SessionGapMinutes Then vwap.Reset()
                End If
                previousTs = bar.Timestamp

                vwap.AddBar(bar)
                rsi.AddClose(bar.Close)

                window.Enqueue(bar.Close)
                windowSum += bar.Close
                If window.Count > _config.MaLength Then
                    windowSum -= window.Dequeue()
                End If

                If window.Count = _config.MaLength Then
                    ma200 = windowSum / _config.MaLength
                End If
            Next

            Dim last = bars(bars.Count - 1)
            eval.AsOf = last.Timestamp
            eval.LastClose = last.Close
            eval.LastBarHigh = last.High
            eval.LastBarLow = last.Low
            eval.Ma200 = ma200
            eval.Vwap = vwap.Vwap
            eval.Rsi = rsi.Rsi
            eval.BarsSinceCrossAbove = rsi.BarsSinceCrossAbove
            eval.BarsSinceCrossBelow = rsi.BarsSinceCrossBelow
            eval.IsWarm = (ma200 > 0D) AndAlso (vwap.Vwap > 0D) AndAlso rsi.IsWarm

            If Not eval.IsWarm Then
                eval.RejectionReason = "Warmup"
                Return eval
            End If

            ' --- Confluence: bullish ---
            If last.Close > ma200 AndAlso
               last.Close > eval.Vwap AndAlso
               eval.Rsi < _config.RsiOversold AndAlso
               eval.BarsSinceCrossAbove <= _config.MaxBarsSinceMidlineCross Then
                eval.Signal = UltimateScalperSignalSide.Bullish
                eval.PrimedSide = UltimateScalperSignalSide.Bullish
                Return eval
            End If

            ' --- Confluence: bearish ---
            If last.Close < ma200 AndAlso
               last.Close < eval.Vwap AndAlso
               eval.Rsi > _config.RsiOverbought AndAlso
               eval.BarsSinceCrossBelow <= _config.MaxBarsSinceMidlineCross Then
                eval.Signal = UltimateScalperSignalSide.Bearish
                eval.PrimedSide = UltimateScalperSignalSide.Bearish
                Return eval
            End If

            ' --- Near-miss reason (first failing gate, bullish-leaning if price above MA) ---
            eval.RejectionReason = NearestMissReason(eval, ma200)
            Return eval
        End Function

        Private Function NearestMissReason(eval As UltimateScalperEvaluation, ma200 As Decimal) As String
            ' Choose the bias to describe based on which side of MA price sits on.
            If eval.LastClose > ma200 Then
                ' Bullish-leaning: explain why we did NOT fire bullish.
                If eval.LastClose <= eval.Vwap Then Return $"Price {eval.LastClose:F2} below VWAP {eval.Vwap:F2}"
                If eval.Rsi >= _config.RsiOversold Then Return $"RSI {eval.Rsi:F1} not oversold (<{_config.RsiOversold:F0})"
                If eval.BarsSinceCrossAbove > _config.MaxBarsSinceMidlineCross Then
                    Return If(eval.BarsSinceCrossAbove = Int32.MaxValue,
                              "No prior RSI cross above 50",
                              $"Last RSI cross above 50 was {eval.BarsSinceCrossAbove} bars ago (>{_config.MaxBarsSinceMidlineCross})")
                End If
                Return "No bullish confluence"
            Else
                ' Bearish-leaning.
                If eval.LastClose >= eval.Vwap Then Return $"Price {eval.LastClose:F2} above VWAP {eval.Vwap:F2}"
                If eval.Rsi <= _config.RsiOverbought Then Return $"RSI {eval.Rsi:F1} not overbought (>{_config.RsiOverbought:F0})"
                If eval.BarsSinceCrossBelow > _config.MaxBarsSinceMidlineCross Then
                    Return If(eval.BarsSinceCrossBelow = Int32.MaxValue,
                              "No prior RSI cross below 50",
                              $"Last RSI cross below 50 was {eval.BarsSinceCrossBelow} bars ago (>{_config.MaxBarsSinceMidlineCross})")
                End If
                Return "No bearish confluence"
            End If
        End Function

    End Class

End Namespace
