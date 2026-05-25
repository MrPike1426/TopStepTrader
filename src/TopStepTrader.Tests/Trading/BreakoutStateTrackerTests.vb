Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Core.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>
    ''' FEAT-62: <see cref="BreakoutStateTracker"/> coverage across forward break,
    ''' counter-breakout invalidation, window-expiry reset, and day-rollover reset.
    ''' </summary>
    Public Class BreakoutStateTrackerTests

        Private Const Symbol As String = "MES"
        Private Const PrevHigh As Decimal = 4500D
        Private Const PrevLow As Decimal = 4480D

        Private Shared Function Bar(close As Decimal) As MarketBar
            Return New MarketBar With {
                .ContractId = Symbol,
                .Open = close,
                .High = close + 1D,
                .Low = close - 1D,
                .Close = close
            }
        End Function

        Private Shared Function DefaultConfig() As BreakAndBounceConfig
            Return New BreakAndBounceConfig() With {
                .InvalidateDirOnCounterBreakout = True,
                .InvalidateDirOnWindowExpiry = True
            }
        End Function

        <Fact>
        Public Sub Update_LongBreakoutSetsDirToOne()
            Dim tracker As New BreakoutStateTracker(DefaultConfig())
            tracker.Update(Symbol, Bar(PrevHigh + 5D), PrevHigh, PrevLow)
            Assert.Equal(1, tracker.GetDirection(Symbol))
        End Sub

        <Fact>
        Public Sub Update_ShortBreakoutSetsDirToMinusOne()
            Dim tracker As New BreakoutStateTracker(DefaultConfig())
            tracker.Update(Symbol, Bar(PrevLow - 5D), PrevHigh, PrevLow)
            Assert.Equal(-1, tracker.GetDirection(Symbol))
        End Sub

        <Fact>
        Public Sub Update_MidRangeCloseLeavesPriorBiasUntouched()
            Dim tracker As New BreakoutStateTracker(DefaultConfig())
            tracker.Update(Symbol, Bar(PrevHigh + 5D), PrevHigh, PrevLow)
            tracker.Update(Symbol, Bar((PrevHigh + PrevLow) / 2D), PrevHigh, PrevLow)
            Assert.Equal(1, tracker.GetDirection(Symbol))
        End Sub

        <Fact>
        Public Sub Update_CounterBreakoutClearsLongBiasToZero()
            Dim tracker As New BreakoutStateTracker(DefaultConfig())
            tracker.Update(Symbol, Bar(PrevHigh + 5D), PrevHigh, PrevLow)
            ' Counter-breakout: bar closes below prev low while bias = long.
            tracker.Update(Symbol, Bar(PrevLow - 5D), PrevHigh, PrevLow)
            Assert.Equal(0, tracker.GetDirection(Symbol))
        End Sub

        <Fact>
        Public Sub Update_CounterBreakoutClearsShortBiasToZero()
            Dim tracker As New BreakoutStateTracker(DefaultConfig())
            tracker.Update(Symbol, Bar(PrevLow - 5D), PrevHigh, PrevLow)
            tracker.Update(Symbol, Bar(PrevHigh + 5D), PrevHigh, PrevLow)
            Assert.Equal(0, tracker.GetDirection(Symbol))
        End Sub

        <Fact>
        Public Sub Update_CounterBreakoutDisabled_FlipsBiasInstead()
            Dim cfg = DefaultConfig()
            cfg.InvalidateDirOnCounterBreakout = False
            Dim tracker As New BreakoutStateTracker(cfg)
            tracker.Update(Symbol, Bar(PrevHigh + 5D), PrevHigh, PrevLow)
            tracker.Update(Symbol, Bar(PrevLow - 5D), PrevHigh, PrevLow)
            ' With invalidation disabled the opposite-side break sets dir to -1.
            Assert.Equal(-1, tracker.GetDirection(Symbol))
        End Sub

        <Fact>
        Public Sub OnEntryWindowExpired_ClearsAllStateWhenEnabled()
            Dim tracker As New BreakoutStateTracker(DefaultConfig())
            tracker.Update(Symbol, Bar(PrevHigh + 5D), PrevHigh, PrevLow)
            tracker.Update("MNQ", Bar(PrevLow - 5D), PrevHigh, PrevLow)
            tracker.OnEntryWindowExpired()
            Assert.Equal(0, tracker.GetDirection(Symbol))
            Assert.Equal(0, tracker.GetDirection("MNQ"))
        End Sub

        <Fact>
        Public Sub OnEntryWindowExpired_NoOpWhenDisabled()
            Dim cfg = DefaultConfig()
            cfg.InvalidateDirOnWindowExpiry = False
            Dim tracker As New BreakoutStateTracker(cfg)
            tracker.Update(Symbol, Bar(PrevHigh + 5D), PrevHigh, PrevLow)
            tracker.OnEntryWindowExpired()
            Assert.Equal(1, tracker.GetDirection(Symbol))
        End Sub

        <Fact>
        Public Sub OnNewTradingDay_ResetsRegardlessOfConfig()
            Dim cfg = DefaultConfig()
            cfg.InvalidateDirOnWindowExpiry = False
            Dim tracker As New BreakoutStateTracker(cfg)
            tracker.Update(Symbol, Bar(PrevHigh + 5D), PrevHigh, PrevLow)
            tracker.OnNewTradingDay()
            Assert.Equal(0, tracker.GetDirection(Symbol))
        End Sub

        <Fact>
        Public Sub Constructor_ThrowsOnNullConfig()
            Dim threw As Boolean = False
            Try
                Dim ignored As New BreakoutStateTracker(CType(Nothing, BreakAndBounceConfig))
            Catch ex As ArgumentNullException
                threw = True
            End Try
            Assert.True(threw, "Constructor should throw ArgumentNullException on null config")
        End Sub

    End Class

End Namespace
