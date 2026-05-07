Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>
    ''' Unit tests for ScalperExitManager — phased SL ladder for scalping
    ''' strategies (Initial → Breakeven → ProfitLock with the optional
    ''' one-way ScaredyCat tightening). Verifies ratchet semantics, exit
    ''' triggers, and short-side mirroring per QUAL-04.
    ''' </summary>
    Public Class ScalperExitManagerTests

        Private ReadOnly _scalper As ScalperExitManager =
            New ScalperExitManager(NullLogger(Of ScalperExitManager).Instance)

        Private Shared ReadOnly s_baseTime As DateTimeOffset =
            New DateTimeOffset(2025, 1, 1, 14, 0, 0, TimeSpan.Zero)

        ' ── Helpers ──────────────────────────────────────────────────────────

        Private Shared Function MakeSlot(side As String,
                                         Optional entryPrice As Decimal = 100D,
                                         Optional stopPrice As Decimal = 99D,
                                         Optional phase As StopPhase = StopPhase.Initial) As PositionSlot
            Return New PositionSlot With {
                .SlotIndex   = 0,
                .Instrument  = "MES",
                .Side        = side,
                .IsOpen      = True,
                .EntryPrice  = entryPrice,
                .StopPrice   = stopPrice,
                .InitialRisk = Math.Abs(entryPrice - stopPrice),
                .StopPhase   = phase
            }
        End Function

        ''' <summary>
        ''' Build a list of 15-second bars from an array of closes. Highs and
        ''' lows straddle the close by ±0.5 so ATR/BB warm up properly.
        ''' </summary>
        Private Shared Function MakeBars(closes As Decimal()) As List(Of MarketBar)
            Dim list As New List(Of MarketBar)(closes.Length)
            For i = 0 To closes.Length - 1
                list.Add(New MarketBar With {
                    .Timestamp = s_baseTime.AddSeconds(15 * i),
                    .Open  = closes(i),
                    .High  = closes(i) + 0.5D,
                    .Low   = closes(i) - 0.5D,
                    .Close = closes(i),
                    .Volume = 100
                })
            Next
            Return list
        End Function

        ''' <summary>30 bars of mild uptrend from 99.30 → 101.33 (0.07/bar).</summary>
        Private Shared Function RisingBars() As List(Of MarketBar)
            Dim closes(29) As Decimal
            For i = 0 To 29
                closes(i) = 99.3D + CDec(i) * 0.07D
            Next
            Return MakeBars(closes)
        End Function

        ''' <summary>30 bars of mild downtrend from 100.7 → 98.67 (-0.07/bar).</summary>
        Private Shared Function FallingBars() As List(Of MarketBar)
            Dim closes(29) As Decimal
            For i = 0 To 29
                closes(i) = 100.7D - CDec(i) * 0.07D
            Next
            Return MakeBars(closes)
        End Function

        ''' <summary>
        ''' 30 bars: rises bars 0–14 (99.5 → 102.0) then falls bars 15–29 (101.7 → 97.5).
        ''' Final 8+ closes have a declining SMA(10) middle band and the SuperTrend
        ''' direction has flipped negative — required to fire ScaredyCat for a LONG.
        ''' </summary>
        Private Shared Function RiseThenFallBars() As List(Of MarketBar)
            Dim closes(29) As Decimal
            For i = 0 To 14
                closes(i) = 99.5D + CDec(i) * 0.18D
            Next
            For i = 15 To 29
                closes(i) = 102D - CDec(i - 14) * 0.30D
            Next
            Return MakeBars(closes)
        End Function

        Private Shared Function MirroredFallThenRiseBars() As List(Of MarketBar)
            Dim src = RiseThenFallBars()
            Dim mirrored As New List(Of MarketBar)(src.Count)
            For Each b In src
                Dim mid = 100D
                Dim mClose = mid - (b.Close - mid)
                mirrored.Add(New MarketBar With {
                    .Timestamp = b.Timestamp,
                    .Open      = mClose,
                    .High      = mClose + 0.5D,
                    .Low       = mClose - 0.5D,
                    .Close     = mClose,
                    .Volume    = b.Volume
                })
            Next
            Return mirrored
        End Function

        ' ── Initial-phase ratchet ─────────────────────────────────────────────

        <Fact>
        Public Sub Initial_Long_StopRatchetsUpWithSuperTrend()
            Dim slot   = MakeSlot("Buy", entryPrice:=100D, stopPrice:=99D)
            Dim state  = New ScalperState()
            Dim cfg    = New ScalperConfig()
            Dim bars   = RisingBars()
            ' Profit < 0.5R so phase stays Initial.
            Dim price  = 100.3D
            Dim d = _scalper.Evaluate(slot, state, bars, price, cfg)

            Assert.Equal(StopPhase.Initial, d.NewPhase)
            Assert.False(d.ShouldExit)
            Assert.True(d.NewStop >= slot.StopPrice, "SL must ratchet up, never down.")
        End Sub

        <Fact>
        Public Sub Initial_Long_StopHoldsWhenSuperTrendBelowCurrentStop()
            ' Set slot.StopPrice already very high — confirm Evaluate never lowers it.
            Dim slot   = MakeSlot("Buy", entryPrice:=100D, stopPrice:=99D)
            slot.StopPrice = 99.95D
            Dim state  = New ScalperState()
            Dim cfg    = New ScalperConfig()
            Dim bars   = RisingBars()
            Dim d = _scalper.Evaluate(slot, state, bars, 100.3D, cfg)

            Assert.True(d.NewStop >= 99.95D, $"SL retraced from 99.95 to {d.NewStop}.")
        End Sub

        ' ── Breakeven trigger at exactly 0.5R ─────────────────────────────────

        <Fact>
        Public Sub Breakeven_Long_TriggersAtExactlyHalfR()
            Dim slot   = MakeSlot("Buy", entryPrice:=100D, stopPrice:=99D)
            Dim state  = New ScalperState()
            Dim cfg    = New ScalperConfig()
            Dim bars   = RisingBars()
            Dim d = _scalper.Evaluate(slot, state, bars, 100.5D, cfg)

            Assert.True(d.NewPhase >= StopPhase.Breakeven)
            Assert.True(d.NewStop >= 100D, $"BE phase must move SL to entry or higher; got {d.NewStop}.")
        End Sub

        <Fact>
        Public Sub Breakeven_Long_FloorNeverRetraces()
            Dim slot   = MakeSlot("Buy", entryPrice:=100D, stopPrice:=99D)
            Dim state  = New ScalperState()
            Dim cfg    = New ScalperConfig()

            ' First call advances to Breakeven and floors SL at entry.
            Dim d1 = _scalper.Evaluate(slot, state, RisingBars(), 100.5D, cfg)
            Assert.True(d1.NewStop >= 100D)

            ' Apply the decision back to the slot, then call again with a falling
            ' SuperTrend that would normally pull the SL down. The BE floor must hold.
            slot.StopPrice = d1.NewStop
            slot.StopPhase = d1.NewPhase

            Dim d2 = _scalper.Evaluate(slot, state, FallingBars(), 100.5D, cfg)
            Assert.True(d2.NewStop >= d1.NewStop, "BE-phase SL must never retreat below entry.")
        End Sub

        ' ── ProfitLock trigger at 1.5R ───────────────────────────────────────

        <Fact>
        Public Sub ProfitLock_Long_TriggersAt1Point5R_TrailsBBLower()
            Dim slot   = MakeSlot("Buy", entryPrice:=100D, stopPrice:=99D)
            Dim state  = New ScalperState()
            Dim cfg    = New ScalperConfig()
            Dim bars   = RisingBars()
            Dim d = _scalper.Evaluate(slot, state, bars, 101.5D, cfg)

            Assert.Equal(StopPhase.ProfitLock, d.NewPhase)
            Assert.False(d.ShouldExit, "Price 101.5 should still sit comfortably above the BB lower band.")
            Assert.True(d.NewStop > slot.StopPrice, "ProfitLock should ratchet SL up from initial.")
            Assert.True(d.NewStop < 101.5D, "BB-lower trail must remain below current price.")
        End Sub

        <Fact>
        Public Sub ProfitLock_Short_TriggersAt1Point5R_TrailsBBUpper()
            Dim slot   = MakeSlot("Sell", entryPrice:=100D, stopPrice:=101D)
            Dim state  = New ScalperState()
            Dim cfg    = New ScalperConfig()
            Dim bars   = FallingBars()
            ' Short profit at 1.5R when price = 100 - 1.5*1 = 98.5
            Dim d = _scalper.Evaluate(slot, state, bars, 98.5D, cfg)

            Assert.Equal(StopPhase.ProfitLock, d.NewPhase)
            Assert.False(d.ShouldExit)
            Assert.True(d.NewStop < slot.StopPrice, "ProfitLock should ratchet SL down for short.")
            Assert.True(d.NewStop > 98.5D, "BB-upper trail must remain above current price (short).")
        End Sub

        <Fact>
        Public Sub ProfitLock_Long_RatchetOnly_HoldsWhenBandFalls()
            Dim slot   = MakeSlot("Buy", entryPrice:=100D, stopPrice:=99D)
            Dim state  = New ScalperState()
            Dim cfg    = New ScalperConfig()

            Dim d1 = _scalper.Evaluate(slot, state, RisingBars(), 101.5D, cfg)
            Assert.Equal(StopPhase.ProfitLock, d1.NewPhase)

            slot.StopPrice = d1.NewStop
            slot.StopPhase = d1.NewPhase

            ' Now feed FallingBars (BB lower will be lower) but keep current price safely above.
            Dim d2 = _scalper.Evaluate(slot, state, FallingBars(), 101.5D, cfg)

            Assert.True(d2.NewStop >= d1.NewStop, "ProfitLock SL must ratchet only.")
        End Sub

        ' ── ScaredyCat one-way trigger ───────────────────────────────────────

        <Fact>
        Public Sub ScaredyCat_Long_FiresOnDecliningBBMidAndDisagreeingST()
            Dim slot   = MakeSlot("Buy", entryPrice:=100D, stopPrice:=99D)
            Dim state  = New ScalperState()
            Dim cfg    = New ScalperConfig()
            Dim bars   = RiseThenFallBars()

            ' Profit doesn't matter for the trigger evaluation.
            _scalper.Evaluate(slot, state, bars, 99D, cfg)

            Assert.True(state.IsScaredyCatActive, "ScaredyCat should arm on declining BB mid + disagreeing 15s ST.")
        End Sub

        <Fact>
        Public Sub ScaredyCat_DoesNotFireWhenBBMidRisesWithSlot()
            Dim slot   = MakeSlot("Buy", entryPrice:=100D, stopPrice:=99D)
            Dim state  = New ScalperState()
            Dim cfg    = New ScalperConfig()

            _scalper.Evaluate(slot, state, RisingBars(), 100.3D, cfg)

            Assert.False(state.IsScaredyCatActive)
        End Sub

        <Fact>
        Public Sub ScaredyCat_PersistsAcrossSubsequentCalls()
            Dim slot   = MakeSlot("Buy", entryPrice:=100D, stopPrice:=99D)
            Dim state  = New ScalperState()
            Dim cfg    = New ScalperConfig()

            ' Arm on rise-then-fall.
            _scalper.Evaluate(slot, state, RiseThenFallBars(), 99D, cfg)
            Assert.True(state.IsScaredyCatActive)

            ' Now feed clean rising bars — would not have armed on its own.
            _scalper.Evaluate(slot, state, RisingBars(), 100.5D, cfg)
            Assert.True(state.IsScaredyCatActive, "ScaredyCat must persist for the lifetime of the trade.")
        End Sub

        <Fact>
        Public Sub ScaredyCat_TightensBandComparedToNormalMode()
            Dim slot   = MakeSlot("Buy", entryPrice:=100D, stopPrice:=99D)
            Dim cfg    = New ScalperConfig()
            Dim bars   = RisingBars()
            Dim price  = 101.5D                ' Force ProfitLock so trail uses BB lower.

            Dim normalState  As New ScalperState() With {.IsScaredyCatActive = False}
            Dim cautiousState As New ScalperState() With {.IsScaredyCatActive = True}

            Dim dNormal   = _scalper.Evaluate(slot, normalState,   bars, price, cfg)
            Dim dCautious = _scalper.Evaluate(slot, cautiousState, bars, price, cfg)

            ' Cautious BB (mult 1.5) → lower band closer to mid → higher SL for LONG.
            Assert.True(dCautious.NewStop > dNormal.NewStop,
                        $"Cautious SL ({dCautious.NewStop}) should be higher than normal SL ({dNormal.NewStop}).")
        End Sub

        ' ── Exit when 15s close crosses SL ───────────────────────────────────

        <Fact>
        Public Sub Exit_Long_FiresWhenClosedBarCrossesStop()
            Dim slot   = MakeSlot("Buy", entryPrice:=100D, stopPrice:=99D)
            slot.StopPrice = 100.5D                ' Pretend BE is already locked.
            slot.StopPhase = StopPhase.Breakeven
            Dim state  = New ScalperState()
            Dim cfg    = New ScalperConfig()
            Dim bars   = RisingBars()

            ' Current price has drifted back below the locked SL.
            Dim d = _scalper.Evaluate(slot, state, bars, 100.4D, cfg)

            Assert.True(d.ShouldExit)
            Assert.False(String.IsNullOrEmpty(d.Reason))
        End Sub

        <Fact>
        Public Sub Exit_Short_FiresWhenClosedBarCrossesStop()
            Dim slot   = MakeSlot("Sell", entryPrice:=100D, stopPrice:=101D)
            slot.StopPrice = 99.5D                 ' Pretend BE is already locked.
            slot.StopPhase = StopPhase.Breakeven
            Dim state  = New ScalperState()
            Dim cfg    = New ScalperConfig()
            Dim bars   = FallingBars()

            Dim d = _scalper.Evaluate(slot, state, bars, 99.6D, cfg)

            Assert.True(d.ShouldExit)
        End Sub

        ' ── Short-side mirrors ───────────────────────────────────────────────

        <Fact>
        Public Sub Initial_Short_StopRatchetsDownWithSuperTrend()
            Dim slot   = MakeSlot("Sell", entryPrice:=100D, stopPrice:=101D)
            Dim state  = New ScalperState()
            Dim cfg    = New ScalperConfig()
            Dim bars   = FallingBars()
            Dim d = _scalper.Evaluate(slot, state, bars, 99.7D, cfg)

            Assert.Equal(StopPhase.Initial, d.NewPhase)
            Assert.False(d.ShouldExit)
            Assert.True(d.NewStop <= slot.StopPrice, "Short SL must ratchet down, never up.")
        End Sub

        <Fact>
        Public Sub Breakeven_Short_TriggersAtExactlyHalfR()
            Dim slot   = MakeSlot("Sell", entryPrice:=100D, stopPrice:=101D)
            Dim state  = New ScalperState()
            Dim cfg    = New ScalperConfig()
            Dim bars   = FallingBars()
            Dim d = _scalper.Evaluate(slot, state, bars, 99.5D, cfg)

            Assert.True(d.NewPhase >= StopPhase.Breakeven)
            Assert.True(d.NewStop <= 100D, $"Short BE must move SL to entry or lower; got {d.NewStop}.")
        End Sub

        <Fact>
        Public Sub ScaredyCat_Short_FiresOnRisingBBMidAndDisagreeingST()
            Dim slot   = MakeSlot("Sell", entryPrice:=100D, stopPrice:=101D)
            Dim state  = New ScalperState()
            Dim cfg    = New ScalperConfig()

            _scalper.Evaluate(slot, state, MirroredFallThenRiseBars(), 101D, cfg)

            Assert.True(state.IsScaredyCatActive, "ScaredyCat should arm on rising BB mid + disagreeing 15s ST for a SHORT.")
        End Sub

    End Class

End Namespace
