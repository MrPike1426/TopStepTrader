Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    ''' <summary>
    ''' Singleton row (id=1) persisting SuperTrend+ Autopilot configuration across restarts.
    ''' Stores both the SuperTrendPlusConfig POCO properties and the ViewModel UI selections.
    ''' </summary>
    <Table("SuperTrendPlusConfig")>
    Public Class SuperTrendPlusConfigEntity

        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.None)>
        Public Property Id As Integer = 1

        ' ── ViewModel UI dropdown selections ───────────────────────────────
        Public Property SelectedTpMultiple As String = "2×"
        Public Property StMultiplier As Double = 3.0
        Public Property SelectedTimeframe As String = "15min"

        ' ── Slot / instrument settings ──────────────────────────────────────
        Public Property MaxSlots As Integer = 3
        Public Property ContractsPerSlot As Integer = 1

        ' ── ADX entry thresholds ────────────────────────────────────────────
        Public Property AdxWeakThreshold As Single = 25.0F
        Public Property AdxModerateThreshold As Single = 40.0F
        Public Property AdxStrongThreshold As Single = 60.0F

        ' ── Phased stop R multiples ─────────────────────────────────────────
        <Column(TypeName:="decimal(18,4)")>
        Public Property BreakevenTriggerR As Decimal = 0.5D

        <Column(TypeName:="decimal(18,4)")>
        Public Property ProfitLockTriggerR As Decimal = 1.0D

        <Column(TypeName:="decimal(18,4)")>
        Public Property ProfitLockOffsetR As Decimal = 0.3D

        <Column(TypeName:="decimal(18,4)")>
        Public Property TrailAtrMultiple As Decimal = 2.0D

        <Column(TypeName:="decimal(18,4)")>
        Public Property ProfitTrailTriggerR As Decimal = 1.5D

        <Column(TypeName:="decimal(18,4)")>
        Public Property HarvestTriggerR As Decimal = 2.0D

        <Column(TypeName:="decimal(18,4)")>
        Public Property HarvestLockR As Decimal = 1.5D

        <Column(TypeName:="decimal(18,4)")>
        Public Property FreeRideTriggerR As Decimal = 3.0D

        <Column(TypeName:="decimal(18,4)")>
        Public Property FreeRideLockR As Decimal = 2.0D

        ' ── Degradation score thresholds ────────────────────────────────────
        Public Property WarningScoreThreshold As Integer = 3
        Public Property ExitingScoreThreshold As Integer = 6

    End Class

End Namespace
