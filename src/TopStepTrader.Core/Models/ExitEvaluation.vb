Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' Result of evaluating one bar's exit signals for a single open slot.
    ''' Produced by ExitSignalEngine; consumed by the slot management loop.
    ''' </summary>
    Public Class ExitEvaluation

        ''' <summary>Composite degradation score for this bar.</summary>
        Public Property Score As Integer

        ''' <summary>
        ''' Names of signals that fired this bar, together with their weights.
        ''' e.g. {"E2:3", "E4:2"}
        ''' </summary>
        Public Property ContributingSignals As New List(Of String)

        ''' <summary>
        ''' True when E1 (SuperTrend flip) fired — slot must close immediately
        ''' regardless of score.
        ''' </summary>
        Public Property ImmediateExit As Boolean

        ''' <summary>
        ''' Recommended health state derived from Score: 0–2 = Healthy (Green), 3–6 = Warning (Amber),
        ''' 7+ = Exiting (Red). The Exiting cut-off matches
        ''' <c>SuperTrendPlusConfig.ExitScoreThreshold</c>; a single bar above this threshold shows
        ''' the slot in Exiting but the management loop's two-bar gate
        ''' (<see cref="PositionSlot.ConsecutiveExitBars"/>) must reach 2 before the position
        ''' actually closes. Overridden to Exiting when <see cref="ImmediateExit"/> is True (E1
        ''' SuperTrend flip).
        ''' </summary>
        Public ReadOnly Property RecommendedHealth As Enums.SlotHealth
            Get
                If ImmediateExit Then Return Enums.SlotHealth.Exiting
                If Score >= 7 Then Return Enums.SlotHealth.Exiting  ' BUG-88: matches Config.ExitScoreThreshold
                If Score >= 3 Then Return Enums.SlotHealth.Warning  ' WarningScoreThreshold
                Return Enums.SlotHealth.Healthy
            End Get
        End Property

        ''' <summary>Desired stop price after applying phased stop logic (ratchet-only).</summary>
        Public Property PhasedStopPrice As Decimal

        ''' <summary>Stop phase assigned this bar.</summary>
        Public Property StopPhase As Enums.StopPhase

    End Class

End Namespace
