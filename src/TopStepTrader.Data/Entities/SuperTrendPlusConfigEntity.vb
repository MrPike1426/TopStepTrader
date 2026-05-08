Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    ''' <summary>
    ''' Singleton row (id=1) persisting SuperTrend+ Autopilot configuration across restarts.
    ''' </summary>
    <Table("SuperTrendPlusConfig")>
    Public Class SuperTrendPlusConfigEntity

        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.None)>
        Public Property Id As Integer = 1

        ' ── Persona + timeframe ───────────────────────────────────��─────────
        Public Property ActivePersona As String = "Damian"
        Public Property SelectedTimeframe As String = "15min"

        ' ── Slot settings ────────────────────────────��───────────────────────
        Public Property MaxSlots As Integer = 3

        ' ── ADX entry thresholds ────────────────────────────────────────────
        Public Property AdxWeakThreshold As Single = 25.0F
        Public Property AdxModerateThreshold As Single = 40.0F
        Public Property AdxStrongThreshold As Single = 60.0F

        ' ── Degradation score thresholds ────────────────────────────────────
        Public Property WarningScoreThreshold As Integer = 3
        Public Property ExitingScoreThreshold As Integer = 6

        ' ── FEAT-46: Pre-entry exit-signal gate ─────────────────────────────
        Public Property EntryExitScoreBlockThreshold As Integer = 4

    End Class

End Namespace
