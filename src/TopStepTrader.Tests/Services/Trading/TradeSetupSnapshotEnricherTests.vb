Imports System.Collections.Generic
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Services.Trading

    ''' <summary>
    ''' FEAT-61: round-trip tests for <see cref="TradeSetupSnapshotEnricher"/> —
    ''' verifies the 12 forward-compat indicator columns are populated from a bar
    ''' series, the field-overwrite contract (target fields overwritten, non-target
    ''' fields preserved), and per-indicator exception isolation.
    ''' </summary>
    Public Class TradeSetupSnapshotEnricherTests

        Private Shared Function MakeBars(count As Integer) As List(Of MarketBar)
            ' Sine-wave price series on top of a slow upward drift so every indicator
            ' has meaningful variation. Volumes step + cycle so DeltaVol is never zero.
            Dim bars As New List(Of MarketBar)(count)
            For i = 0 To count - 1
                Dim baseP As Decimal = 100D + CDec(Math.Sin(i / 5.0) * 5.0) + CDec(i) * 0.1D
                Dim open_ As Decimal = baseP - 0.5D
                Dim close_ As Decimal = baseP + 0.5D
                Dim high As Decimal = baseP + 1.0D
                Dim low As Decimal = baseP - 1.0D
                Dim vol As Long = 1000L + CLng(i) * 5L + CLng(i Mod 7) * 50L
                bars.Add(New MarketBar With {
                    .Open = open_,
                    .High = high,
                    .Low = low,
                    .Close = close_,
                    .Volume = vol
                })
            Next
            Return bars
        End Function

        Private Shared Function MakeEnricher() As TradeSetupSnapshotEnricher
            Return New TradeSetupSnapshotEnricher(NullLogger(Of TradeSetupSnapshotEnricher).Instance)
        End Function

        <Fact>
        Public Sub PopulateAdditionalIndicators_HappyPath_All12FieldsNonZero()
            Dim enricher = MakeEnricher()
            Dim bars = MakeBars(100)
            Dim snapshot As New TradeSetupSnapshot()

            Dim result = enricher.PopulateAdditionalIndicators(snapshot, bars)

            Assert.Equal(12, result.Populated)
            Assert.Equal(0, result.Skipped)

            Assert.NotEqual(0D, snapshot.Tenkan)
            Assert.NotEqual(0D, snapshot.Kijun)
            Assert.NotEqual(0D, snapshot.Cloud1)
            Assert.NotEqual(0D, snapshot.Cloud2)
            Assert.NotEqual(0D, snapshot.Ema21)
            Assert.NotEqual(0D, snapshot.Ema50)
            Assert.NotEqual(0F, snapshot.MacdHist)
            Assert.NotEqual(0F, snapshot.MacdHistPrev)
            Assert.NotEqual(0F, snapshot.StochRsiK)
            Assert.NotEqual(0D, snapshot.VidyaValue)
            Assert.NotEqual(0F, snapshot.CmoValue)
            Assert.NotEqual(0F, snapshot.DeltaVol)

            ' StochRsiK contract: returned in [0..100] after the *100 rescale.
            Assert.InRange(snapshot.StochRsiK, 0.0F, 100.0F)
        End Sub

        <Fact>
        Public Sub PopulateAdditionalIndicators_ShortHistory_LeavesLongWindowFieldsAtZero()
            Dim enricher = MakeEnricher()
            ' 20 bars: enough for Tenkan (9), VIDYA (15), CMO (15), DeltaVol (2),
            ' but not enough for Kijun (26), Cloud1 (needs Kijun), Cloud2 (52),
            ' Ema21 (21), Ema50 (50), MACD (26+9), StochRSI (14+14).
            Dim bars = MakeBars(20)
            Dim snapshot As New TradeSetupSnapshot()

            Dim result = enricher.PopulateAdditionalIndicators(snapshot, bars)

            Assert.True(result.Populated < 12,
                        $"Expected Populated < 12 on short history, got {result.Populated}")
            Assert.True(result.Skipped > 0,
                        $"Expected Skipped > 0 on short history, got {result.Skipped}")
            Assert.Equal(12, result.Populated + result.Skipped)

            ' Ticket-specified fields that must stay at default 0 with 20 bars.
            Assert.Equal(0D, snapshot.Cloud1)
            Assert.Equal(0D, snapshot.Cloud2)
            Assert.Equal(0D, snapshot.Ema50)
            Assert.Equal(0F, snapshot.StochRsiK)
        End Sub

        <Fact>
        Public Sub PopulateAdditionalIndicators_PreservesNonTargetFields()
            Dim enricher = MakeEnricher()
            Dim bars = MakeBars(100)
            Dim snapshot As New TradeSetupSnapshot() With {
                .AdxValue = 42.0F,
                .Rsi14 = 55.0F,
                .AtrValue = 12.5D
            }

            enricher.PopulateAdditionalIndicators(snapshot, bars)

            ' Non-target fields must not be touched by the enricher.
            Assert.Equal(42.0F, snapshot.AdxValue)
            Assert.Equal(55.0F, snapshot.Rsi14)
            Assert.Equal(12.5D, snapshot.AtrValue)
        End Sub

        <Fact>
        Public Sub PopulateAdditionalIndicators_OverwritesPrePopulatedTargetField()
            Dim enricher = MakeEnricher()
            Dim bars = MakeBars(100)
            Dim snapshot As New TradeSetupSnapshot() With {
                .Ema21 = 999.0D
            }

            enricher.PopulateAdditionalIndicators(snapshot, bars)

            ' Ema21 is a target field — the enricher overwrites it with the freshly
            ' computed value (here, a number near recent close price ≈ 100s, not 999).
            Assert.NotEqual(999.0D, snapshot.Ema21)
            Assert.True(snapshot.Ema21 > 0D)
        End Sub

        <Fact>
        Public Sub PopulateAdditionalIndicators_IndicatorException_IsolatedToThatIndicator()
            Dim enricher As New ThrowingEma21Enricher(NullLogger(Of TradeSetupSnapshotEnricher).Instance)
            Dim bars = MakeBars(100)
            Dim snapshot As New TradeSetupSnapshot()

            Dim result = enricher.PopulateAdditionalIndicators(snapshot, bars)

            ' EMA21 throws → 1 skipped; remaining 11 succeed.
            Assert.Equal(11, result.Populated)
            Assert.Equal(1, result.Skipped)
            Assert.Equal(0D, snapshot.Ema21)
            Assert.NotEqual(0D, snapshot.Ema50)
            Assert.NotEqual(0D, snapshot.Tenkan)
            Assert.NotEqual(0F, snapshot.MacdHist)
        End Sub

        ' ── Test subclass: forces ComputeEma(period=21) to throw so we can assert
        '    that the per-indicator Try/Catch isolates the failure ──
        Private NotInheritable Class ThrowingEma21Enricher
            Inherits TradeSetupSnapshotEnricher

            Public Sub New(logger As ILogger(Of TradeSetupSnapshotEnricher))
                MyBase.New(logger)
            End Sub

            Protected Overrides Function ComputeEma(closes As IList(Of Decimal),
                                                    period As Integer) As Single()
                If period = 21 Then
                    Throw New InvalidOperationException("simulated EMA21 failure for test")
                End If
                Return MyBase.ComputeEma(closes, period)
            End Function
        End Class

    End Class

End Namespace
