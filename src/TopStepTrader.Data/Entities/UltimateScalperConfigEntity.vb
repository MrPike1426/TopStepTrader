Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    ''' <summary>
    ''' FEAT-64: Singleton row (id=1) persisting Ultimate Scalper strategy configuration.
    ''' Per-instrument risk profiles are flattened into nine columns
    ''' (Mes/Mnq/Mgc × Initial/BeSnap/Trail) to keep the table denormalised and the
    ''' repository round-trip trivial — the scalper always trades exactly these three
    ''' equity-futures micros.
    ''' </summary>
    <Table("UltimateScalperConfig")>
    Public Class UltimateScalperConfigEntity

        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.None)>
        Public Property Id As Integer = 1

        ' ── Indicator tunables ─────────────────────────────────────────────
        Public Property MaLength As Integer = 200
        Public Property RsiLength As Integer = 14
        Public Property RsiOverbought As Double = 70.0
        Public Property RsiOversold As Double = 30.0
        Public Property MaxBarsSinceMidlineCross As Integer = 10

        ' ── Trail engine + broker throttles ────────────────────────────────
        Public Property SafetyCeilingTpDollars As Decimal = 200D
        Public Property MinSlEditStepTicks As Integer = 1
        Public Property MaxSlEditsPerSecond As Integer = 5

        ' ── Position sizing ────────────────────────────────────────────────
        Public Property Leverage As Integer = 1

        ' ── FEAT-69: Pre-staged stop-entry orders ──────────────────────────
        Public Property PreStagedEntriesEnabled As Boolean = True
        Public Property EntryTriggerOffsetTicks As Integer = 1
        Public Property RepriceThresholdTicks As Integer = 2
        Public Property ArmStaleMinutes As Integer = 30
        Public Property ReArmDebounceSeconds As Integer = 30
        Public Property MaxBrokerCallsPerMinute As Integer = 80
        Public Property MaxConcurrentPositions As Integer = 1

        ' ── Per-instrument risk (MES) ──────────────────────────────────────
        Public Property MesInitialStopDollars As Decimal = 20D
        Public Property MesBreakevenSnapDollars As Decimal = 7D
        Public Property MesTrailDistanceDollars As Decimal = 7D

        ' ── Per-instrument risk (MNQ) ──────────────────────────────────────
        Public Property MnqInitialStopDollars As Decimal = 40D
        Public Property MnqBreakevenSnapDollars As Decimal = 12D
        Public Property MnqTrailDistanceDollars As Decimal = 12D

        ' ── Per-instrument risk (MGC) ──────────────────────────────────────
        Public Property MgcInitialStopDollars As Decimal = 30D
        Public Property MgcBreakevenSnapDollars As Decimal = 10D
        Public Property MgcTrailDistanceDollars As Decimal = 10D

    End Class

End Namespace
