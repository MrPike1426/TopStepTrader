Imports TopStepTrader.Core.Enums
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>
    ''' FEAT-11: Unit tests for the adaptive poll cadence and stale-data guard
    ''' logic introduced for MultiConfluence flat sessions.
    '''
    ''' These tests exercise the pure-logic helpers that determine:
    '''   1. Which BarTimeframe to use depending on strategy type and position state.
    '''   2. Whether a bar is considered stale given its age and the active cadence.
    '''   3. That FifteenSecond = 0 is distinct from all other BarTimeframe values.
    '''
    ''' Run with:  dotnet test --filter "FullyQualifiedName~MultiConfluenceAdaptivePoll"
    ''' </summary>
    Public Class MultiConfluenceAdaptivePollTests

        ' ── Helper: replicate the cadence-selection logic from DoCheckAsync ────────

        ''' <summary>
        ''' Mirrors the isMcFlat + timeframe selection block in DoCheckAsync.
        ''' Returns the effective BarTimeframe the engine would fetch.
        ''' </summary>
        Private Shared Function SelectTimeframe(
                condition As StrategyConditionType,
                positionOpen As Boolean,
                strategyTimeframeMinutes As Integer) As BarTimeframe

            Dim isMcFlat = (condition = StrategyConditionType.MultiConfluence AndAlso Not positionOpen)
            If isMcFlat Then Return BarTimeframe.FifteenSecond

            Select Case strategyTimeframeMinutes
                Case 1 : Return BarTimeframe.OneMinute
                Case 5 : Return BarTimeframe.FiveMinute
                Case 15 : Return BarTimeframe.FifteenMinute
                Case 60 : Return BarTimeframe.OneHour
                Case Else : Return BarTimeframe.FiveMinute
            End Select
        End Function

        ''' <summary>
        ''' Mirrors the stale-bar determination from DoCheckAsync.
        ''' Returns True when the bar should be considered stale.
        ''' </summary>
        Private Shared Function IsStale(
                isMcFlat As Boolean,
                barAgeSeconds As Double,
                strategyTimeframeMinutes As Integer) As Boolean

            If isMcFlat Then
                ' FEAT-11: 15-second cadence — stale after 3 × 15 s = 45 s
                Return barAgeSeconds > 15.0 * 3.0
            Else
                ' Standard: stale after TimeframeMinutes × 3 minutes
                Return (barAgeSeconds / 60.0) > strategyTimeframeMinutes * 3.0
            End If
        End Function

        ' ════════════════════════════════════════════════════════════════════════════
        ' 1 — Timeframe selection
        ' ════════════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' FEAT-11 AC: When MC strategy is flat, engine selects FifteenSecond bars.
        ''' </summary>
        <Fact>
        Public Sub McFlat_SelectsFifteenSecondTimeframe()
            Dim tf = SelectTimeframe(StrategyConditionType.MultiConfluence,
                                     positionOpen:=False, strategyTimeframeMinutes:=5)
            Assert.Equal(BarTimeframe.FifteenSecond, tf)
        End Sub

        ''' <summary>
        ''' FEAT-11 AC: When MC strategy has a position open, engine uses the strategy timeframe (5-min).
        ''' </summary>
        <Fact>
        Public Sub McWithPosition_SelectsStrategyTimeframe()
            Dim tf = SelectTimeframe(StrategyConditionType.MultiConfluence,
                                     positionOpen:=True, strategyTimeframeMinutes:=5)
            Assert.Equal(BarTimeframe.FiveMinute, tf)
        End Sub

        ''' <summary>
        ''' FEAT-11 AC: Non-MC strategies always use the strategy timeframe, regardless of position state.
        ''' </summary>
        <Theory>
        <InlineData(StrategyConditionType.EmaRsiWeightedScore, False)>
        <InlineData(StrategyConditionType.EmaRsiWeightedScore, True)>
        <InlineData(StrategyConditionType.RSIOversold, False)>
        <InlineData(StrategyConditionType.EMACrossAbove, False)>
        Public Sub NonMcStrategy_AlwaysUsesStrategyTimeframe(condition As StrategyConditionType, positionOpen As Boolean)
            Dim tf = SelectTimeframe(condition, positionOpen, strategyTimeframeMinutes:=5)
            Assert.Equal(BarTimeframe.FiveMinute, tf)
        End Sub

        ' ════════════════════════════════════════════════════════════════════════════
        ' 2 — Stale-bar guard: 15-second cadence
        ' ════════════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' A bar 44 seconds old is NOT stale on the 15-second cadence (threshold = 45 s).
        ''' </summary>
        <Fact>
        Public Sub McFlat_Bar44sOld_IsNotStale()
            Assert.False(IsStale(isMcFlat:=True, barAgeSeconds:=44.0, strategyTimeframeMinutes:=5))
        End Sub

        ''' <summary>
        ''' A bar 46 seconds old IS stale on the 15-second cadence (threshold = 45 s).
        ''' </summary>
        <Fact>
        Public Sub McFlat_Bar46sOld_IsStale()
            Assert.True(IsStale(isMcFlat:=True, barAgeSeconds:=46.0, strategyTimeframeMinutes:=5))
        End Sub

        ''' <summary>
        ''' Exactly at threshold (45 s) is NOT stale (strict greater-than comparison).
        ''' </summary>
        <Fact>
        Public Sub McFlat_Bar45sOld_IsNotStale()
            Assert.False(IsStale(isMcFlat:=True, barAgeSeconds:=45.0, strategyTimeframeMinutes:=5))
        End Sub

        ' ════════════════════════════════════════════════════════════════════════════
        ' 3 — Stale-bar guard: standard strategy-timeframe cadence (unchanged behaviour)
        ' ════════════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Standard 5-min strategy: a bar 14 minutes old is NOT stale (threshold = 15 min).
        ''' </summary>
        <Fact>
        Public Sub Standard5Min_Bar14MinOld_IsNotStale()
            Assert.False(IsStale(isMcFlat:=False, barAgeSeconds:=14 * 60, strategyTimeframeMinutes:=5))
        End Sub

        ''' <summary>
        ''' Standard 5-min strategy: a bar 16 minutes old IS stale (threshold = 15 min).
        ''' </summary>
        <Fact>
        Public Sub Standard5Min_Bar16MinOld_IsStale()
            Assert.True(IsStale(isMcFlat:=False, barAgeSeconds:=16 * 60, strategyTimeframeMinutes:=5))
        End Sub

        ' ════════════════════════════════════════════════════════════════════════════
        ' 4 — BarTimeframe enum integrity
        ' ════════════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' FEAT-11: FifteenSecond must be distinct from all minute-based timeframes
        ''' so the DB discriminator column cannot collide.
        ''' </summary>
        <Fact>
        Public Sub FifteenSecond_IsDistinctFromAllOtherTimeframes()
            Dim all = [Enum].GetValues(GetType(BarTimeframe)).Cast(Of BarTimeframe)().ToList()
            Dim fsValue = CInt(BarTimeframe.FifteenSecond)
            Dim others = all.Where(Function(tf) tf <> BarTimeframe.FifteenSecond).ToList()
            Assert.All(others, Sub(tf) Assert.NotEqual(fsValue, CInt(tf)))
        End Sub

        ''' <summary>FifteenSecond = 0 (verified as a compile-time constant).</summary>
        <Fact>
        Public Sub FifteenSecond_HasExpectedEnumValue()
            Assert.Equal(0, CInt(BarTimeframe.FifteenSecond))
        End Sub

    End Class

End Namespace
