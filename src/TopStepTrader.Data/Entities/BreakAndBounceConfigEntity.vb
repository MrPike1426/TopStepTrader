Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    ''' <summary>
    ''' FEAT-62: Singleton row (id=1) persisting <c>BreakAndBounceConfig</c>. Mirrors
    ''' <c>SlipStreamConfigEntity</c> in shape — flat columns; defaults applied at the
    ''' repository's <c>ToConfig</c> mapper.
    ''' </summary>
    <Table("BreakAndBounceConfig")>
    Public Class BreakAndBounceConfigEntity

        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.None)>
        Public Property Id As Integer = 1

        Public Property EntryWindow As String = "0830-1100"
        Public Property FlatWindow As String = "1450-1500"
        Public Property BreakoutTimeframe As String = "15min"
        Public Property RetestTimeframe As String = "5min"

        Public Property MinimumStopDistanceTicks As Integer = 8
        Public Property MinimumStopAtrFraction As Double = 0.5
        Public Property AtrLength As Integer = 14

        Public Property InvalidateDirOnCounterBreakout As Boolean = True
        Public Property InvalidateDirOnWindowExpiry As Boolean = True

        Public Property ContractsPerEntry As Integer = 1

        Public Property AiVetoEnabled As Boolean = True
        Public Property EnableLong As Boolean = True
        Public Property EnableShort As Boolean = True

        Public Property MinSlEditStepTicks As Integer = 1
        Public Property MaxConcurrentPositions As Integer = 1

    End Class

End Namespace
