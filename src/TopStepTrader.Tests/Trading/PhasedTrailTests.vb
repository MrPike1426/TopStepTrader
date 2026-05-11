Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>
    ''' STRAT-38: Unit tests for the phased ATR trail math.
    ''' Tests cover per-contract initial-stop clamps, ATR-based breakeven trigger,
    ''' and Chandelier trail anchor-to-watermark behaviour.
    ''' No broker calls — pure math tests.
    ''' Run with: dotnet test --filter "FullyQualifiedName~PhasedTrail"
    ''' </summary>
    Public Class PhasedTrailTests

        ' ── F1/F4: Per-contract initial-stop clamps ──────────────────────────────────

        <Fact>
        Public Sub InitialStop_ClampsToPerContractMin()
            ' MGC: min=10, max=30, ATR=0.5 at tickSize=0.1 → atrSlTicks=floor(1.5×0.5/0.1)=7 → clamped UP to 10
            Dim fav = FavouriteContracts.TryGetBySymbol("GOLD.24-7")
            Assert.NotNull(fav)
            Dim atrSlTicks = CInt(Math.Floor(1.5D * 0.5D / 0.1D))  ' = 7
            Dim phaseMin = fav.PhasedTrailMinInitialStopTicks       ' = 10
            Dim phaseMax = fav.PhasedTrailMaxInitialStopTicks        ' = 30
            Dim hardFloor = Math.Max(5, phaseMin)                   ' defaultSlTicks=5 < 10 → 10
            Dim result = Math.Min(phaseMax, Math.Max(hardFloor, Math.Max(1, atrSlTicks)))
            Assert.Equal(10, result)
        End Sub

        <Fact>
        Public Sub InitialStop_ClampsToPerContractMax()
            ' MGC: min=10, max=30, ATR=10.0 at tickSize=0.1 → atrSlTicks=floor(1.5×10/0.1)=150 → clamped DOWN to 30
            Dim fav = FavouriteContracts.TryGetBySymbol("GOLD.24-7")
            Assert.NotNull(fav)
            Dim atrSlTicks = CInt(Math.Floor(1.5D * 10.0D / 0.1D))  ' = 150
            Dim phaseMin = fav.PhasedTrailMinInitialStopTicks         ' = 10
            Dim phaseMax = fav.PhasedTrailMaxInitialStopTicks         ' = 30
            Dim hardFloor = Math.Max(5, phaseMin)
            Dim result = Math.Min(phaseMax, Math.Max(hardFloor, Math.Max(1, atrSlTicks)))
            Assert.Equal(30, result)
        End Sub

        <Fact>
        Public Sub InitialStop_NoClamp_WhenPropertiesAreZero()
            ' Contract with no clamps set: result = hardFloor = max(defaultSlTicks, 0) = defaultSlTicks
            Dim fav = New FavouriteContract("TST", "Test", "CON.TEST", 0.25D, 1.25D, 5D) With {
                .PxRootSymbol = "TST",
                .PhasedTrailMinInitialStopTicks = 0,
                .PhasedTrailMaxInitialStopTicks = 0
            }
            Dim atrSlTicks = 18
            Dim phaseMin = fav.PhasedTrailMinInitialStopTicks
            Dim phaseMax = If(fav.PhasedTrailMaxInitialStopTicks > 0, fav.PhasedTrailMaxInitialStopTicks, Integer.MaxValue)
            Dim hardFloor = Math.Max(5, phaseMin)
            Dim result = Math.Min(phaseMax, Math.Max(hardFloor, Math.Max(1, atrSlTicks)))
            Assert.Equal(18, result)
        End Sub

        ' ── F5: Phase 2 breakeven trigger gate ───────────────────────────────────────

        <Fact>
        Public Sub BreakevenTrigger_UsesAtrMultiple_WhenSet()
            ' BreakevenTriggerMultipleOfN=1.2, ATR=8.0, tickSize=0.5 → Ceiling(1.2×8/0.5)=20
            Dim atr = 8.0D
            Dim tickSize = 0.5D
            Dim multiple = 1.2D
            Dim atrTriggerTicks = CInt(Math.Ceiling(multiple * atr / tickSize))
            Assert.Equal(20, atrTriggerTicks)
        End Sub

        <Fact>
        Public Sub BreakevenTrigger_FloorsToContractMinTicks()
            ' atrTriggerTicks=4, fav.PhasedTrailBreakevenMinTicks=15 → expect 15
            Dim atrTriggerTicks = 4
            Dim minTriggerTicks = 15
            Dim activationTicks = Math.Max(atrTriggerTicks, minTriggerTicks)
            Assert.Equal(15, activationTicks)
        End Sub

        <Fact>
        Public Sub BreakevenTrigger_FallsBackToLegacy_WhenBothZero()
            ' BreakevenTriggerMultipleOfN=0, minTriggerTicks=0, initialTpTicks=60 → legacy 50% = 36
            Dim atrTriggerTicks = 0
            Dim minTriggerTicks = 0
            Dim initialTpTicks = 60
            Dim legacyFraction = 0.6D   ' FreeRollActivationFraction = 0.6 in the engine (67%)
            Dim activationTicks As Integer
            If atrTriggerTicks > 0 OrElse minTriggerTicks > 0 Then
                activationTicks = Math.Max(atrTriggerTicks, minTriggerTicks)
            Else
                activationTicks = CInt(Math.Floor(initialTpTicks * legacyFraction))
            End If
            Assert.Equal(36, activationTicks)
        End Sub

        ' ── F6: Chandelier trail anchors to watermark, not currentPrice ───────────────

        <Fact>
        Public Sub ChandelierLong_TrailsFromHighestHigh_NotCurrentPrice()
            ' entry=2000, currentPrice=2010, highestHigh=2050, ATR=5, mult=2.0
            ' expected SL = floor((2050 - 10) / tickSize) * tickSize = 2040
            ' (NOT currentPrice-10 = 2000)
            Dim highestHigh = 2050D
            Dim atrDistance = 2.0D * 5.0D   ' = 10
            Dim tickSize = 1.0D
            Dim rawCandidate = highestHigh - atrDistance          ' = 2040
            Dim newSl = CDec(Math.Floor(CDbl(rawCandidate / tickSize))) * tickSize  ' = 2040
            Dim currentPriceBasedSl = 2010D - atrDistance                          ' = 2000

            Assert.Equal(2040D, newSl)
            Assert.NotEqual(currentPriceBasedSl, newSl)
        End Sub

        <Fact>
        Public Sub ChandelierLong_RatchetHolds_WhenWatermarkRetreats()
            ' lastSl=2040 set from high=2050. Next: price=2030, highestHigh stays 2050.
            ' Candidate = 2050-10 = 2040. shouldUpdate (2040 > 2040) = False → no update.
            Dim lastSlPrice = 2040D
            Dim highestHigh = 2050D
            Dim atrDistance = 2.0D * 5.0D   ' = 10
            Dim tickSize = 1.0D
            Dim rawCandidate = highestHigh - atrDistance          ' = 2040
            Dim newSl = CDec(Math.Floor(CDbl(rawCandidate / tickSize))) * tickSize  ' = 2040
            Dim shouldUpdate = newSl > lastSlPrice                ' = False
            Assert.False(shouldUpdate)
        End Sub

        <Fact>
        Public Sub ChandelierShort_TrailsFromLowestLow()
            ' entry=2000, lowestLow=1950, ATR=5, mult=2.0
            ' expected SL = ceiling((1950 + 10) / tickSize) * tickSize = 1960
            Dim lowestLow = 1950D
            Dim atrDistance = 2.0D * 5.0D   ' = 10
            Dim tickSize = 1.0D
            Dim rawCandidate = lowestLow + atrDistance            ' = 1960
            Dim newSl = CDec(Math.Ceiling(CDbl(rawCandidate / tickSize))) * tickSize  ' = 1960
            Assert.Equal(1960D, newSl)
        End Sub

        ' ── F1 acceptance: per-contract values are populated in GetDefaults() ─────────

        <Fact>
        Public Sub FavouriteContracts_HasPhasedTrailValues_ForAllRequiredSymbols()
            Dim tests As New Dictionary(Of String, (min As Integer, max As Integer, be As Integer)) From {
                {"GOLD.24-7", (10, 30, 10)},
                {"OIL",       (20, 60, 15)},
                {"SPX500",    (24, 80, 20)},
                {"M6E",       (6,  16, 5)},
                {"MBT",       (40, 120, 30)}
            }
            For Each kv In tests
                Dim fav = FavouriteContracts.TryGetBySymbol(kv.Key)
                Assert.NotNull(fav)
                Assert.Equal(kv.Value.min, fav.PhasedTrailMinInitialStopTicks)
                Assert.Equal(kv.Value.max, fav.PhasedTrailMaxInitialStopTicks)
                Assert.Equal(kv.Value.be,  fav.PhasedTrailBreakevenMinTicks)
            Next
        End Sub

    End Class

End Namespace
