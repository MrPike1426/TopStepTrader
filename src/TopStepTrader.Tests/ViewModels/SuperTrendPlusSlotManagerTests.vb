Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.UI.ViewModels
Imports Xunit

Namespace TopStepTrader.Tests.ViewModels

    ''' <summary>
    ''' FEAT-23: Verifies SlotManager slot-open rules:
    '''   - ADX band determines target slot count
    '''   - Two slots cannot open on the same bar timestamp
    '''   - Counter-trend signals are blocked
    '''   - No slot opens when another is Exiting
    ''' </summary>
    Public Class SuperTrendPlusSlotManagerTests

        Private Shared Function MakeConfig() As SuperTrendPlusConfig
            Return New SuperTrendPlusConfig With {
                .MaxSlots             = 3,
                .ContractsPerSlot     = 1,
                .AdxWeakThreshold     = 25.0F,
                .AdxModerateThreshold = 40.0F,
                .AdxStrongThreshold   = 60.0F
            }
        End Function

        Private Shared ReadOnly Bar1 As DateTimeOffset = New DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero)
        Private Shared ReadOnly Bar2 As DateTimeOffset = Bar1.AddMinutes(5)
        Private Shared ReadOnly Bar3 As DateTimeOffset = Bar2.AddMinutes(5)

        ' ── ADX band target ─────────────────────────────────────────────────

        <Fact>
        Public Sub TargetSlotCount_BelowWeak_Returns0()
            Dim sm = New SlotManager(MakeConfig())
            Assert.Equal(0, sm.TargetSlotCount(24.9F))
        End Sub

        <Fact>
        Public Sub TargetSlotCount_Adx25_Returns1()
            Dim sm = New SlotManager(MakeConfig())
            Assert.Equal(1, sm.TargetSlotCount(25.0F))
        End Sub

        <Fact>
        Public Sub TargetSlotCount_Adx40_Returns2()
            Dim sm = New SlotManager(MakeConfig())
            Assert.Equal(2, sm.TargetSlotCount(40.0F))
        End Sub

        <Fact>
        Public Sub TargetSlotCount_Adx60_Returns3()
            Dim sm = New SlotManager(MakeConfig())
            Assert.Equal(3, sm.TargetSlotCount(60.0F))
        End Sub

        ' ── Slot 1 opens on ADX 25 ──────────────────────────────────────────

        <Fact>
        Public Sub TryOpenSlot_Adx25_OpensOneSlot()
            Dim sm = New SlotManager(MakeConfig())
            Dim slot = sm.TryOpenSlot("MCLE", "Buy", 25.0F, Bar1, 65.0D, 65.5D)
            Assert.NotNull(slot)
            Assert.Equal(0, slot.SlotIndex)
            Assert.True(slot.IsOpen)
            Assert.Equal(1, sm.OpenSlotCount)
        End Sub

        ' ── Slot 2 opens on ADX 45, next bar ───────────────────────────────

        <Fact>
        Public Sub TryOpenSlot_Adx45_NextBar_OpensSlot2()
            Dim sm = New SlotManager(MakeConfig())
            sm.TryOpenSlot("MCLE", "Buy", 25.0F, Bar1, 65.0D, 65.5D)   ' slot 1

            Dim slot2 = sm.TryOpenSlot("MCLE", "Buy", 45.0F, Bar2, 65.1D, 65.6D)
            Assert.NotNull(slot2)
            Assert.Equal(2, sm.OpenSlotCount)
        End Sub

        ' ── Slot 3 opens on ADX 65, bar after slot 2 ───────────────────────

        <Fact>
        Public Sub TryOpenSlot_Adx65_ThirdBar_OpensSlot3()
            Dim sm = New SlotManager(MakeConfig())
            sm.TryOpenSlot("MCLE", "Buy", 25.0F, Bar1, 65.0D, 65.5D)
            sm.TryOpenSlot("MCLE", "Buy", 45.0F, Bar2, 65.1D, 65.6D)

            Dim slot3 = sm.TryOpenSlot("MCLE", "Buy", 65.0F, Bar3, 65.2D, 65.7D)
            Assert.NotNull(slot3)
            Assert.Equal(3, sm.OpenSlotCount)
        End Sub

        ' ── Two slots cannot open on the same bar timestamp ─────────────────

        <Fact>
        Public Sub TryOpenSlot_SameBarTimestamp_SecondBlocked()
            Dim sm = New SlotManager(MakeConfig())
            sm.TryOpenSlot("MCLE", "Buy", 45.0F, Bar1, 65.0D, 65.5D)  ' opens slot 0

            Dim second = sm.TryOpenSlot("MCLE", "Buy", 45.0F, Bar1, 65.0D, 65.5D)  ' same bar
            Assert.Null(second)
            Assert.Equal(1, sm.OpenSlotCount)
        End Sub

        ' ── Counter-trend blocked ─────────────────────────────────────────────

        <Fact>
        Public Sub TryOpenSlot_CounterTrend_Blocked()
            Dim sm = New SlotManager(MakeConfig())
            sm.TryOpenSlot("MCLE", "Buy", 45.0F, Bar1, 65.0D, 65.5D)  ' long slot open

            Dim counter = sm.TryOpenSlot("MCLE", "Sell", 45.0F, Bar2, 65.1D, 65.6D)
            Assert.Null(counter)
        End Sub

        ' ── No slot opens when another is Exiting ────────────────────────────

        <Fact>
        Public Sub TryOpenSlot_ExitingSlotPresent_Blocked()
            Dim sm = New SlotManager(MakeConfig())
            Dim s1 = sm.TryOpenSlot("MCLE", "Buy", 25.0F, Bar1, 65.0D, 65.5D)
            s1.Health = SlotHealth.Exiting

            Dim s2 = sm.TryOpenSlot("MCLE", "Buy", 45.0F, Bar2, 65.1D, 65.6D)
            Assert.Null(s2)
        End Sub

        ' ── CloseSlot resets state ────────────────────────────────────────────

        <Fact>
        Public Sub CloseSlot_ResetsSlotState()
            Dim sm = New SlotManager(MakeConfig())
            Dim s = sm.TryOpenSlot("MCLE", "Buy", 25.0F, Bar1, 65.0D, 65.5D)
            Assert.NotNull(s)
            sm.CloseSlot(s.SlotIndex)
            Assert.Equal(0, sm.OpenSlotCount)
            Assert.False(sm.Slots(s.SlotIndex).IsOpen)
        End Sub

        ' ── ResetAll clears all slots ─────────────────────────────────────────

        <Fact>
        Public Sub ResetAll_ClearsAllSlots()
            Dim sm = New SlotManager(MakeConfig())
            sm.TryOpenSlot("MCLE", "Buy", 65.0F, Bar1, 65.0D, 65.5D)
            sm.TryOpenSlot("MCLE", "Buy", 65.0F, Bar2, 65.1D, 65.6D)
            sm.ResetAll()
            Assert.Equal(0, sm.OpenSlotCount)
        End Sub

        ' ── BUG-49: IsEarlyModeEntry ─────────────────────────────────────────

        <Fact>
        Public Sub TryOpenSlot_Normal_DoesNotSetEarlyMode()
            Dim sm = New SlotManager(MakeConfig())
            Dim slot = sm.TryOpenSlot("MCLE", "Buy", 25.0F, Bar1, 65.0D, 65.5D)
            Assert.False(slot.IsEarlyModeEntry)
        End Sub

        <Fact>
        Public Sub CloseSlot_ResetsIsEarlyModeEntry()
            Dim sm = New SlotManager(MakeConfig())
            Dim slot = sm.TryOpenSlot("MCLE", "Buy", 25.0F, Bar1, 65.0D, 65.5D)
            slot.IsEarlyModeEntry = True
            sm.CloseSlot(slot.SlotIndex)
            Assert.False(sm.Slots(slot.SlotIndex).IsEarlyModeEntry)
        End Sub

        ' ── STRAT-31: LastAdxBand and BandForAdx ─────────────────────────────

        <Fact>
        Public Sub BandForAdx_BelowWeak_Returns0()
            Dim sm = New SlotManager(MakeConfig())
            Assert.Equal(0, sm.BandForAdx(24.9F))
        End Sub

        <Fact>
        Public Sub BandForAdx_AtWeak_Returns1()
            Dim sm = New SlotManager(MakeConfig())
            Assert.Equal(1, sm.BandForAdx(25.0F))
        End Sub

        <Fact>
        Public Sub BandForAdx_AtModerate_Returns2()
            Dim sm = New SlotManager(MakeConfig())
            Assert.Equal(2, sm.BandForAdx(40.0F))
        End Sub

        <Fact>
        Public Sub BandForAdx_AtStrong_Returns3()
            Dim sm = New SlotManager(MakeConfig())
            Assert.Equal(3, sm.BandForAdx(60.0F))
        End Sub

        <Fact>
        Public Sub TryOpenSlot_Adx25_SetsLastAdxBand1()
            Dim sm = New SlotManager(MakeConfig())
            Dim slot = sm.TryOpenSlot("MCLE", "Buy", 25.0F, Bar1, 65.0D, 65.5D)
            Assert.Equal(1, slot.LastAdxBand)
        End Sub

        <Fact>
        Public Sub TryOpenSlot_Adx45_SetsLastAdxBand2()
            Dim sm = New SlotManager(MakeConfig())
            Dim slot = sm.TryOpenSlot("MCLE", "Buy", 45.0F, Bar1, 65.0D, 65.5D)
            Assert.Equal(2, slot.LastAdxBand)
        End Sub

        <Fact>
        Public Sub TryOpenSlot_Adx65_SetsLastAdxBand3()
            Dim sm = New SlotManager(MakeConfig())
            Dim slot = sm.TryOpenSlot("MCLE", "Buy", 65.0F, Bar1, 65.0D, 65.5D)
            Assert.Equal(3, slot.LastAdxBand)
        End Sub

        <Fact>
        Public Sub CloseSlot_ResetsLastAdxBand()
            Dim sm = New SlotManager(MakeConfig())
            Dim slot = sm.TryOpenSlot("MCLE", "Buy", 65.0F, Bar1, 65.0D, 65.5D)
            sm.CloseSlot(slot.SlotIndex)
            Assert.Equal(0, sm.Slots(slot.SlotIndex).LastAdxBand)
        End Sub

    End Class

End Namespace
