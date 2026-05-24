Imports System.Collections.Generic
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Models
Imports TopStepTrader.ML.Features

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' FEAT-61: enriches a <see cref="TradeSetupSnapshot"/> at entry-confirmation time
    ''' with the 12 indicator columns the live SuperTrend+ strategy does not itself
    ''' compute (Ichimoku, EMA21/50, MACD, StochRSI, VIDYA, CMO, ΔVolume). Best-effort —
    ''' fields whose indicator returns NaN (short bar history) are left at their existing
    ''' value and counted as skipped. Each indicator block has its own Try/Catch so a
    ''' bug in one helper cannot poison the rest.
    ''' </summary>
    Public Class TradeSetupSnapshotEnricher

        Private Const TargetFieldCount As Integer = 12
        Private Const DecimalSafeAbsMax As Single = 1.0E+28F

        Private ReadOnly _logger As ILogger(Of TradeSetupSnapshotEnricher)

        Public Sub New(logger As ILogger(Of TradeSetupSnapshotEnricher))
            _logger = logger
        End Sub

        ''' <summary>
        ''' Populate the 12 indicator columns on <paramref name="snapshot"/> that the
        ''' SuperTrend+ live strategy does not compute. Returns (populated, skipped)
        ''' counts for diagnostics. The enricher overwrites its own target fields and
        ''' leaves all other snapshot fields untouched.
        ''' </summary>
        Public Overridable Function PopulateAdditionalIndicators(snapshot As TradeSetupSnapshot,
                                                                 bars As IList(Of MarketBar)) As (Populated As Integer, Skipped As Integer)
            If snapshot Is Nothing Then
                Return (0, TargetFieldCount)
            End If
            If bars Is Nothing OrElse bars.Count = 0 Then
                _logger.LogInformation("TradeSetupSnapshotEnricher: populated 0/12, skipped 12/12 (bars=0)")
                Return (0, TargetFieldCount)
            End If

            Dim populated As Integer = 0
            Dim skipped As Integer = 0
            Dim n As Integer = bars.Count - 1

            Dim highs As New List(Of Decimal)(bars.Count)
            Dim lows As New List(Of Decimal)(bars.Count)
            Dim closes As New List(Of Decimal)(bars.Count)
            For Each b In bars
                highs.Add(b.High)
                lows.Add(b.Low)
                closes.Add(b.Close)
            Next

            ' ── Ichimoku (Tenkan, Kijun, Cloud1=SpanA spot, Cloud2=SpanB spot) ──
            ' Stored as spot values (no forward-projection) — see ticket rationale:
            ' "keeping spot makes the snapshot honest about what was known at signal time."
            Try
                Dim ich = ComputeIchimoku(highs, lows, closes)
                ApplySingleToDecimal(LastValid(ich.Tenkan), Sub(v) snapshot.Tenkan = v, populated, skipped)
                ApplySingleToDecimal(LastValid(ich.Kijun), Sub(v) snapshot.Kijun = v, populated, skipped)

                ' Cloud1 (SpanA spot) = (Tenkan + Kijun) / 2 at the latest bar where both are valid.
                Dim spanASpot = LastValidPair(ich.Tenkan, ich.Kijun, Function(t, k) (t + k) / 2.0F)
                ApplySingleToDecimal(spanASpot, Sub(v) snapshot.Cloud1 = v, populated, skipped)

                ' Cloud2 (SpanB spot) = midrange of last 52 bars' (max high + min low) / 2 at index n.
                Dim spanBSpot = ComputeSpanBSpot(highs, lows, n, 52)
                ApplySingleToDecimal(spanBSpot, Sub(v) snapshot.Cloud2 = v, populated, skipped)
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeSetupSnapshotEnricher: Ichimoku indicator failed")
                skipped += 4
            End Try

            ' ── EMA21 ──
            Try
                ApplySingleToDecimal(LastValid(ComputeEma(closes, 21)),
                                      Sub(v) snapshot.Ema21 = v, populated, skipped)
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeSetupSnapshotEnricher: EMA21 indicator failed")
                skipped += 1
            End Try

            ' ── EMA50 ──
            Try
                ApplySingleToDecimal(LastValid(ComputeEma(closes, 50)),
                                      Sub(v) snapshot.Ema50 = v, populated, skipped)
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeSetupSnapshotEnricher: EMA50 indicator failed")
                skipped += 1
            End Try

            ' ── MACD (Hist, HistPrev) ──
            Try
                Dim macd = ComputeMacd(closes)
                ApplySingleToSingle(LastValid(macd.Histogram),
                                    Sub(v) snapshot.MacdHist = v, populated, skipped)
                ApplySingleToSingle(PreviousValid(macd.Histogram),
                                    Sub(v) snapshot.MacdHistPrev = v, populated, skipped)
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeSetupSnapshotEnricher: MACD indicator failed")
                skipped += 2
            End Try

            ' ── StochRSI %K (signal-smoothed, scaled to [0..100] per ticket contract) ──
            Try
                Dim sr = ComputeStochRsi(closes)
                ' D = signal-period SMA of K (i.e. smoothed K). The underlying helper
                ' returns the [0..1] range; ticket wants [0..100].
                Dim kSmoothed = LastValid(sr.D)
                If Not Single.IsNaN(kSmoothed) AndAlso Not Single.IsInfinity(kSmoothed) Then
                    snapshot.StochRsiK = kSmoothed * 100.0F
                    populated += 1
                Else
                    skipped += 1
                End If
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeSetupSnapshotEnricher: StochRSI indicator failed")
                skipped += 1
            End Try

            ' ── VIDYA ──
            Try
                ApplySingleToDecimal(LastValid(ComputeVidya(closes, 14, 14)),
                                      Sub(v) snapshot.VidyaValue = v, populated, skipped)
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeSetupSnapshotEnricher: VIDYA indicator failed")
                skipped += 1
            End Try

            ' ── CMO ──
            Try
                ApplySingleToSingle(LastValid(ComputeCmo(closes, 14)),
                                    Sub(v) snapshot.CmoValue = v, populated, skipped)
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeSetupSnapshotEnricher: CMO indicator failed")
                skipped += 1
            End Try

            ' ── DeltaVol (latest-bar volume diff) ──
            Try
                If bars.Count >= 2 Then
                    snapshot.DeltaVol = CSng(bars(n).Volume - bars(n - 1).Volume)
                    populated += 1
                Else
                    skipped += 1
                End If
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeSetupSnapshotEnricher: DeltaVol computation failed")
                skipped += 1
            End Try

            _logger.LogInformation("TradeSetupSnapshotEnricher: populated {Populated}/12, skipped {Skipped}/12 (bars={Count})",
                                   populated, skipped, bars.Count)

            Return (populated, skipped)
        End Function

        ' ── Test seams — override in a test subclass to force-throw a single indicator ──

        Protected Friend Overridable Function ComputeIchimoku(highs As IList(Of Decimal),
                                                              lows As IList(Of Decimal),
                                                              closes As IList(Of Decimal)) _
                                                              As (Tenkan As Single(), Kijun As Single(), SpanA As Single(), SpanB As Single())
            Return TechnicalIndicators.IchimokuCloud(highs, lows, closes)
        End Function

        Protected Friend Overridable Function ComputeEma(closes As IList(Of Decimal), period As Integer) As Single()
            Return TechnicalIndicators.EMA(closes, period)
        End Function

        Protected Friend Overridable Function ComputeMacd(closes As IList(Of Decimal)) _
                                                          As (Line As Single(), Signal As Single(), Histogram As Single())
            Return TechnicalIndicators.MACD(closes)
        End Function

        Protected Friend Overridable Function ComputeStochRsi(closes As IList(Of Decimal)) As (K As Single(), D As Single())
            Return TechnicalIndicators.StochasticRSI(closes, 14, 14, 3)
        End Function

        Protected Friend Overridable Function ComputeVidya(closes As IList(Of Decimal),
                                                            vidyaLength As Integer,
                                                            cmoLength As Integer) As Single()
            Return TechnicalIndicators.VIDYA(closes, vidyaLength, cmoLength)
        End Function

        Protected Friend Overridable Function ComputeCmo(closes As IList(Of Decimal), period As Integer) As Single()
            Return TechnicalIndicators.CMO(closes, period)
        End Function

        ' ── Private helpers ───────────────────────────────────────────────────

        Private Shared Sub ApplySingleToDecimal(value As Single,
                                                assign As Action(Of Decimal),
                                                ByRef populated As Integer,
                                                ByRef skipped As Integer)
            If Single.IsNaN(value) OrElse Single.IsInfinity(value) OrElse Math.Abs(value) > DecimalSafeAbsMax Then
                skipped += 1
            Else
                assign(CDec(value))
                populated += 1
            End If
        End Sub

        Private Shared Sub ApplySingleToSingle(value As Single,
                                               assign As Action(Of Single),
                                               ByRef populated As Integer,
                                               ByRef skipped As Integer)
            If Single.IsNaN(value) OrElse Single.IsInfinity(value) Then
                skipped += 1
            Else
                assign(value)
                populated += 1
            End If
        End Sub

        Private Shared Function LastValid(series As Single()) As Single
            If series Is Nothing OrElse series.Length = 0 Then Return Single.NaN
            For i = series.Length - 1 To 0 Step -1
                If Not Single.IsNaN(series(i)) Then Return series(i)
            Next
            Return Single.NaN
        End Function

        Private Shared Function PreviousValid(series As Single()) As Single
            If series Is Nothing OrElse series.Length = 0 Then Return Single.NaN
            Dim found As Integer = 0
            For i = series.Length - 1 To 0 Step -1
                If Not Single.IsNaN(series(i)) Then
                    found += 1
                    If found = 2 Then Return series(i)
                End If
            Next
            Return Single.NaN
        End Function

        Private Shared Function LastValidPair(a As Single(), b As Single(),
                                              combine As Func(Of Single, Single, Single)) As Single
            If a Is Nothing OrElse b Is Nothing Then Return Single.NaN
            Dim len = Math.Min(a.Length, b.Length)
            For i = len - 1 To 0 Step -1
                If Not Single.IsNaN(a(i)) AndAlso Not Single.IsNaN(b(i)) Then
                    Return combine(a(i), b(i))
                End If
            Next
            Return Single.NaN
        End Function

        Private Shared Function ComputeSpanBSpot(highs As IList(Of Decimal),
                                                  lows As IList(Of Decimal),
                                                  idx As Integer,
                                                  period As Integer) As Single
            If idx < period - 1 OrElse idx < 0 OrElse idx >= highs.Count OrElse idx >= lows.Count Then
                Return Single.NaN
            End If
            Dim hh = CDbl(highs(idx))
            Dim ll = CDbl(lows(idx))
            For j = idx - period + 1 To idx
                Dim h = CDbl(highs(j))
                Dim l = CDbl(lows(j))
                If h > hh Then hh = h
                If l < ll Then ll = l
            Next
            Return CSng((hh + ll) / 2.0)
        End Function

    End Class

End Namespace
