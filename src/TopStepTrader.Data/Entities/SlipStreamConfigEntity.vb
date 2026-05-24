Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    ''' <summary>
    ''' FEAT-70: Singleton row (id=1) persisting SlipStream strategy configuration.
    ''' Per-instrument ATR-multiple overrides are flattened into nullable columns
    ''' (Mes/Mnq/Mgc × {AtrSL, AtrTP1, Trail, TrailOffset}). A null column means
    ''' "use the config-wide default for this knob" — matches the in-memory model's
    ''' nullable override fields.
    ''' </summary>
    <Table("SlipStreamConfig")>
    Public Class SlipStreamConfigEntity

        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.None)>
        Public Property Id As Integer = 1

        ' ── Timeframes ─────────────────────────────────────────────────────
        Public Property HtfTimeframe As String = "60min"
        Public Property UseHtfFilter As Boolean = True
        Public Property HtfEmaLength As Integer = 50

        ' ── Trend (fast/slow EMA bias) ─────────────────────────────────────
        Public Property EmaFastLength As Integer = 21
        Public Property EmaSlowLength As Integer = 200

        ' ── Momentum (RSI + DI direction) ──────────────────────────────────
        Public Property RsiLength As Integer = 14
        Public Property RsiLongMin As Double = 55.0
        Public Property RsiShortMax As Double = 45.0

        ' ── Strength + volatility regime ───────────────────────────────────
        Public Property AdxLength As Integer = 14
        Public Property AdxMin As Double = 22.0
        Public Property AtrLength As Integer = 14
        Public Property AtrPercentLookback As Integer = 100
        Public Property AtrPercentMin As Double = 30.0

        ' ── Pullback shape ─────────────────────────────────────────────────
        Public Property ExtendBars As Integer = 3
        Public Property ExtendAtrMult As Double = 0.5

        ' ── Risk / position sizing ─────────────────────────────────────────
        Public Property RiskPct As Double = 0.5
        Public Property AtrSLmult As Double = 1.5
        Public Property AtrTP1mult As Double = 1.0
        Public Property Tp1Pct As Double = 50.0
        Public Property TrailMult As Double = 1.5
        Public Property TrailOffsetMult As Double = 1.0
        Public Property MaxBarsInTrade As Integer = 40

        ' ── Session / control ──────────────────────────────────────────────
        Public Property UseSession As Boolean = True
        Public Property SessionWindow As String = "0830-1500"
        Public Property FlatWindow As String = "1450-1500"
        Public Property CooldownBars As Integer = 3
        Public Property EnableLong As Boolean = True
        Public Property EnableShort As Boolean = True

        ' ── Broker SL-edit throttle ────────────────────────────────────────
        Public Property MinSlEditStepTicks As Integer = 1
        Public Property MaxSlEditsPerSecond As Integer = 5

        ' ── Forward-compat seam ────────────────────────────────────────────
        Public Property MaxConcurrentPositions As Integer = 1

        ' ── Per-instrument overrides (nullable; null = use defaults) ───────
        Public Property MesAtrSLmultOverride As Double?
        Public Property MesAtrTP1multOverride As Double?
        Public Property MesTrailMultOverride As Double?
        Public Property MesTrailOffsetMultOverride As Double?

        Public Property MnqAtrSLmultOverride As Double?
        Public Property MnqAtrTP1multOverride As Double?
        Public Property MnqTrailMultOverride As Double?
        Public Property MnqTrailOffsetMultOverride As Double?

        Public Property MgcAtrSLmultOverride As Double?
        Public Property MgcAtrTP1multOverride As Double?
        Public Property MgcTrailMultOverride As Double?
        Public Property MgcTrailOffsetMultOverride As Double?

    End Class

End Namespace
