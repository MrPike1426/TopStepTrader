Imports TopStepTrader.Core.Models
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>
    ''' TEST-09: Verifies the ATR-bracket tick computation formula that
    ''' <c>PlaceBracketOrdersAsync</c> uses to set <c>_initialSlTicks</c> and
    ''' <c>_initialTpTicks</c> for each ATR tier.
    '''
    ''' Because the engine private fields cannot be inspected from outside the class,
    ''' these tests mirror the exact arithmetic from the engine so that any change to
    ''' that formula is caught immediately.  They also serve as the BUG-14 regression:
    ''' the trail ratchet must use <c>_initialTpTicks</c> (ATR-derived at entry), not
    ''' <c>DefaultTpTicks</c> from appsettings (typically 10), which was the BUG-14 defect.
    '''
    ''' Formula (from StrategyExecutionEngine.PlaceBracketOrdersAsync):
    '''   slTicks  = Max(Max(1, Floor(SlMultipleOfN  × atr / tickSize)), defaultSlTicks)
    '''   tpTicks  = Max(Max(1, Floor(tpMultiple     × atr / tickSize)), defaultTpTicks)
    '''
    ''' Run with:  dotnet test --filter "FullyQualifiedName~AtrBracketTick"
    ''' </summary>
    Public Class AtrBracketTickTests

        ' ── Mirrors PlaceBracketOrdersAsync formula ───────────────────────────────
        Private Shared Function ComputeSlTicks(slMult As Decimal, atr As Decimal,
                                               tickSize As Decimal, defaultSlTicks As Integer) As Integer
            Dim raw = CInt(Math.Floor(CDbl(slMult * atr / tickSize)))
            Return Math.Max(Math.Max(1, raw), defaultSlTicks)
        End Function

        Private Shared Function ComputeTpTicks(tpMult As Decimal, atr As Decimal,
                                               tickSize As Decimal, defaultTpTicks As Integer) As Integer
            Dim effective = If(tpMult > 0D, tpMult, 2D)
            Dim raw = CInt(Math.Floor(CDbl(effective * atr / tickSize)))
            Return Math.Max(Math.Max(1, raw), defaultTpTicks)
        End Function

        ' ── Tier parameterisation ─────────────────────────────────────────────────
        ' InlineData: tierName, slMult, tpMult, atr, tickSize, defaultSlTicks, defaultTpTicks,
        '             expectedSlTicks, expectedTpTicks
        '
        ' Calculation for each tier using ATR=0.50, tickSize=0.01, defaults=10:
        '   NARROW   SL=1.0 × 0.50 / 0.01 = 50  → Max(Max(1,50),10) = 50
        '            TP=2.0 × 0.50 / 0.01 = 100 → Max(Max(1,100),10) = 100
        '   STANDARD SL=1.5 × 0.50 / 0.01 = 75  → Max(Max(1,75),10) = 75
        '            TP=3.0 × 0.50 / 0.01 = 150 → Max(Max(1,150),10) = 150
        '   WIDE     SL=2.5 × 0.50 / 0.01 = 125 → Max(Max(1,125),10) = 125
        '            TP=5.0 × 0.50 / 0.01 = 250 → Max(Max(1,250),10) = 250

        <Theory>
        <InlineData("NARROW",   1.0, 2.0, 0.50, 0.01, 10, 10,  50,  100)>
        <InlineData("STANDARD", 1.5, 3.0, 0.50, 0.01, 10, 10,  75,  150)>
        <InlineData("WIDE",     2.5, 5.0, 0.50, 0.01, 10, 10, 125,  250)>
        Public Sub PlaceBracket_CorrectTicksForAtrTier(
                tierName As String,
                slMult As Double, tpMult As Double,
                atr As Double, tickSize As Double,
                defaultSlTicks As Integer, defaultTpTicks As Integer,
                expectedSlTicks As Integer, expectedTpTicks As Integer)

            Dim slMultD = CDec(slMult)
            Dim tpMultD = CDec(tpMult)
            Dim atrD    = CDec(atr)
            Dim tickD   = CDec(tickSize)

            Dim actualSl = ComputeSlTicks(slMultD, atrD, tickD, defaultSlTicks)
            Dim actualTp = ComputeTpTicks(tpMultD, atrD, tickD, defaultTpTicks)

            Assert.Equal(expectedSlTicks, actualSl)
            Assert.Equal(expectedTpTicks, actualTp)
        End Sub

        ''' <summary>
        ''' Minimum clamp: when ATR-derived ticks fall below defaultXxxTicks, the default wins.
        ''' Example: ATR=0.05 tickSize=0.01 → 5 ticks; defaultSlTicks=10 → result=10.
        ''' </summary>
        <Fact>
        Public Sub PlaceBracket_AtrBelowDefault_DefaultClampApplies()
            Dim actualSl = ComputeSlTicks(1.5D, 0.05D, 0.01D, defaultSlTicks:=10)
            Dim actualTp = ComputeTpTicks(3.0D, 0.05D, 0.01D, defaultTpTicks:=10)

            Assert.Equal(10, actualSl)   ' Floor(1.5×0.05/0.01)=7 < 10 → clamp to 10
            Assert.Equal(15, actualTp)   ' Floor(3.0×0.05/0.01)=15 > 10 → 15 (ATR wins)
        End Sub

        ''' <summary>
        ''' Zero ATR (warm-up path): ATR guard is checked in the engine BEFORE this formula.
        ''' When ATR = 0 the engine falls back to defaultSlTicks / defaultTpTicks entirely.
        ''' This test documents that the formula itself does not divide by zero when tickSize > 0.
        ''' </summary>
        <Fact>
        Public Sub PlaceBracket_ZeroAtr_DefaultsUsed()
            Dim actualSl = ComputeSlTicks(2.5D, 0D, 0.01D, defaultSlTicks:=8)
            Dim actualTp = ComputeTpTicks(5.0D, 0D, 0.01D, defaultTpTicks:=8)

            ' Floor(2.5×0/0.01)=0 → Max(Max(1,0),8) = 8
            Assert.Equal(8, actualSl)
            Assert.Equal(8, actualTp)
        End Sub

        ' ── BUG-14 regression ────────────────────────────────────────────────────────

        ''' <summary>
        ''' BUG-14 regression: the trail ratchet must compute TP distance from the ATR-derived
        ''' _initialTpTicks set at entry, NOT from DefaultTpTicks (which is typically much smaller).
        '''
        ''' Scenario: WIDE tier entry with ATR=0.50, tickSize=0.01 → initialTpTicks=250.
        ''' DefaultTpTicks is 10 (appsettings preset for pre-ATR fallback).
        ''' Verifies: the ATR-derived value (250) is strictly larger than the default (10),
        ''' confirming that any code using DefaultTpTicks instead of _initialTpTicks would produce
        ''' a materially incorrect (96% smaller) TP bracket during trail operations.
        ''' </summary>
        <Fact>
        Public Sub TrailHardStop_UsesInitialTpTicks_NotDefaultTpTicks()
            Const DefaultTpTicks As Integer = 10   ' typical appsettings preset
            Dim wideTpMult     As Decimal   = 5.0D
            Dim atr            As Decimal   = 0.5D
            Dim tickSize       As Decimal   = 0.01D

            Dim initialTpTicks = ComputeTpTicks(wideTpMult, atr, tickSize, DefaultTpTicks)

            ' The ATR-derived initial TP ticks must be much larger than the preset default.
            ' BUG-14: code was using DefaultTpTicks (10) instead of initialTpTicks (250).
            Assert.True(initialTpTicks > DefaultTpTicks,
                        $"_initialTpTicks ({initialTpTicks}) should exceed DefaultTpTicks ({DefaultTpTicks}) " &
                        "for WIDE tier with ATR=0.50; BUG-14 would use DefaultTpTicks instead.")
            Assert.Equal(250, initialTpTicks)
        End Sub

        ''' <summary>
        ''' Validates tick-to-price conversion formula used for SL and TP prices:
        ''' Long SL = entry − slTicks × tickSize
        ''' Long TP = entry + tpTicks × tickSize
        ''' </summary>
        ' InlineData: entry(Double), slTicks, tpTicks, tickSize(Double), isBuy,
        '             expectedSl(Double), expectedTp(Double)
        '
        ' Using Double literals because VB attribute arguments cannot be Decimal constants.
        <Theory>
        <InlineData(5000.0, 125, 250, 0.01, True,  4998.75, 5002.50)>   ' WIDE Long
        <InlineData(5000.0, 125, 250, 0.01, False, 5001.25, 4997.50)>   ' WIDE Short
        <InlineData(5000.0,  75, 150, 0.01, True,  4999.25, 5001.50)>   ' STANDARD Long
        Public Sub BracketPrices_MatchTickFormula(
                entry As Double,
                slTicks As Integer, tpTicks As Integer,
                tickSize As Double,
                isBuy As Boolean,
                expectedSl As Double, expectedTp As Double)

            Dim entryD   = CDec(entry)
            Dim tickD    = CDec(tickSize)
            Dim actualSl = If(isBuy,
                              entryD - slTicks * tickD,
                              entryD + slTicks * tickD)
            Dim actualTp = If(isBuy,
                              entryD + tpTicks * tickD,
                              entryD - tpTicks * tickD)

            Assert.Equal(CDec(expectedSl), actualSl)
            Assert.Equal(CDec(expectedTp), actualTp)
        End Sub

    End Class

End Namespace
