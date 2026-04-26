Namespace TopStepTrader.Core.Trading

    ''' <summary>
    ''' Defines the risk/reward personality for a trading persona.
    ''' Three built-in profiles cover the full risk spectrum:
    '''   Lewis   — Risk Averse  : small stake, wide N-brackets, high ADX gate
    '''   Damian  — Moderate     : balanced brackets (application default)
    '''   Joe     — Aggressive   : large stake, tight N-brackets, low ADX gate
    '''
    ''' SL and TP are expressed as multiples of N (= ATR × DollarPerPoint) so bracket
    ''' distances automatically scale with both market volatility and effective position size.
    ''' The bracket step after each advance is always 0.5 × N (core Turtle principle).
    ''' </summary>
    Public NotInheritable Class RiskProfile

        ' ── Identity ──────────────────────────────────────────────────────────────
        Public ReadOnly Property Name As String

        ' ── Position sizing ───────────────────────────────────────────────────────
        ''' <summary>Number of contracts per initial trade entry on TopStepX.
        ''' Lewis = 1 (conservative), Damian = 3 (moderate), Joe = 5 (aggressive).</summary>
        Public ReadOnly Property PositionSize As Integer

        ' ── Scale-in ──────────────────────────────────────────────────────────────
        ''' <summary>
        ''' Maximum number of additional positions after the initial entry.
        ''' Lewis = 1.  Damian = 2.  Joe = 3.
        ''' </summary>
        Public ReadOnly Property MaxScaleIns As Integer

        ' ── Turtle bracket N-multiples ────────────────────────────────────────────
        ''' <summary>
        ''' Initial stop-loss expressed as a multiple of N (ATR in dollar terms).
        ''' SL dollars = SlMultipleOfN × ATR × DollarPerPoint.
        ''' </summary>
        Public ReadOnly Property SlMultipleOfN As Decimal

        ''' <summary>
        ''' Initial take-profit expressed as a multiple of N.
        ''' TP dollars = TpMultipleOfN × ATR × DollarPerPoint.
        ''' Subsequent advances always step by +0.5 × N (fixed Turtle increment).
        ''' </summary>
        Public ReadOnly Property TpMultipleOfN As Decimal

        ' ── ADX trend-strength gate ────────────────────────────────────────────────
        ''' <summary>
        ''' Minimum ADX(14) value required for the trend-strength gate to pass.
        ''' Higher = entry only during stronger, more decisive trends.
        ''' </summary>
        Public ReadOnly Property AdxThreshold As Single

        ' ── Signal confidence ──────────────────────────────────────────────────────
        ''' <summary>
        ''' Recommended minimum weighted confidence score (0–100) to fire a trade.
        ''' Can be overridden by the user via the confidence slider.
        ''' </summary>
        Public ReadOnly Property DefaultConfidencePct As Integer

        Private Sub New(name As String,
                        positionSize As Integer,
                        maxScaleIns As Integer,
                        slMultipleOfN As Decimal,
                        tpMultipleOfN As Decimal,
                        adxThreshold As Single,
                        defaultConfidencePct As Integer)
            Me.Name = name
            Me.PositionSize = positionSize
            Me.MaxScaleIns = maxScaleIns
            Me.SlMultipleOfN = slMultipleOfN
            Me.TpMultipleOfN = tpMultipleOfN
            Me.AdxThreshold = adxThreshold
            Me.DefaultConfidencePct = defaultConfidencePct
        End Sub

        ' ── Built-in profiles ─────────────────────────────────────────────────────

        ''' <summary>Lewis — Risk Averse. SL=1.5N, TP=3.0N. ADX ≥ 25. Max 1 scale-in.</summary>
        Public Shared ReadOnly Lewis As New RiskProfile(
            name:="Lewis (Averse)",
            positionSize:=1,
            maxScaleIns:=1,
            slMultipleOfN:=1.5D,
            tpMultipleOfN:=3.0D,
            adxThreshold:=25.0F,
            defaultConfidencePct:=90)

        ''' <summary>Damian — Moderate (application default). SL=1.0N, TP=2.5N. ADX ≥ 20. Max 2 scale-ins. R:R = 1:2.5.</summary>
        Public Shared ReadOnly Damian As New RiskProfile(
            name:="Damian (Moderate)",
            positionSize:=3,
            maxScaleIns:=2,
            slMultipleOfN:=1.0D,
            tpMultipleOfN:=2.5D,
            adxThreshold:=20.0F,
            defaultConfidencePct:=80)

        ''' <summary>Joe — Aggressive. SL=0.75N, TP=2.0N. ADX ≥ 15. Max 3 scale-ins. R:R = 1:2.67.</summary>
        Public Shared ReadOnly Joe As New RiskProfile(
            name:="Joe (Aggressive)",
            positionSize:=5,
            maxScaleIns:=3,
            slMultipleOfN:=0.75D,
            tpMultipleOfN:=2.0D,
            adxThreshold:=15.0F,
            defaultConfidencePct:=70)

        ''' <summary>All three built-in profiles in ascending risk order.</summary>
        Public Shared ReadOnly All As IReadOnlyList(Of RiskProfile) = New RiskProfile() {Lewis, Damian, Joe}

    End Class

End Namespace
