Namespace TopStepTrader.Core.Trading

    ''' <summary>
    ''' Defines the risk/reward personality for a trading persona.
    ''' Three built-in profiles cover the full risk spectrum:
    '''   Lewis   — Risk Averse  : small stake, wide N-brackets, high ADX gate
    '''   Damian  — Moderate     : balanced leverage and brackets (application default)
    '''   Joe     — Aggressive   : large stake, tight N-brackets, low ADX gate
    '''
    ''' SL and TP are expressed as multiples of N (= ATR × DollarPerPoint) so bracket
    ''' distances automatically scale with both market volatility and effective position size.
    ''' The bracket step after each advance is always 0.5 × N (core Turtle principle).
    ''' Actual leverage is capped at Min(Leverage, FavouriteContract.MaxLeverage) so
    ''' FCA/ESMA regulatory limits (e.g. OIL = 10×, Crypto = 2×) are always respected.
    ''' </summary>
    Public NotInheritable Class RiskProfile

        ' ── Identity ──────────────────────────────────────────────────────────────
        Public ReadOnly Property Name As String

        ' ── Position sizing ───────────────────────────────────────────────────────
        ''' <summary>Number of contracts per initial trade entry on TopStepX.
        ''' Lewis = 1 (conservative), Damian = 3 (moderate), Joe = 5 (aggressive).</summary>
        Public ReadOnly Property PositionSize As Integer

        ''' <summary>
        ''' Preferred leverage multiplier.
        ''' Actual leverage applied = Min(Leverage, FavouriteContract.MaxLeverage).
        ''' </summary>
        Public ReadOnly Property Leverage As Integer

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
        ''' Falls back to <see cref="StrategyDefinition.SlDollarBracket"/> when ATR = 0.
        ''' </summary>
        Public ReadOnly Property SlMultipleOfN As Decimal

        ''' <summary>
        ''' Wider stop-loss multiple used when trading a leveraged CFD (leverage &gt; 1).
        ''' Not used for TopStepX exchange-margined futures.
        ''' Profile values — Lewis (Averse): 2.5  Damian (Moderate): 2.0  Joe (Aggressive): 1.5
        ''' </summary>
        Public ReadOnly Property LeveragedSlMultipleOfN As Decimal

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
                        leverage As Integer,
                        maxScaleIns As Integer,
                        slMultipleOfN As Decimal,
                        leveragedSlMultipleOfN As Decimal,
                        tpMultipleOfN As Decimal,
                        adxThreshold As Single,
                        defaultConfidencePct As Integer)
            Me.Name = name
            Me.PositionSize = positionSize
            Me.Leverage = leverage
            Me.MaxScaleIns = maxScaleIns
            Me.SlMultipleOfN = slMultipleOfN
            Me.LeveragedSlMultipleOfN = leveragedSlMultipleOfN
            Me.TpMultipleOfN = tpMultipleOfN
            Me.AdxThreshold = adxThreshold
            Me.DefaultConfidencePct = defaultConfidencePct
        End Sub

        ' ── Built-in profiles ─────────────────────────────────────────────────────

        ''' <summary>
        ''' Lewis — Risk Averse.
        ''' SL=1.5N, LeveragedSL=2.5N (legacy CFD, not active on TopStepX).
        ''' TP=3.0N. ADX ≥ 25.  Max 1 scale-in (total 2 positions).
        ''' Patient entries: only trades the strongest, most confirmed trends.
        ''' </summary>
        Public Shared ReadOnly Lewis As New RiskProfile(
            name:="Lewis (Averse)",
            positionSize:=1,
            leverage:=5,
            maxScaleIns:=1,
            slMultipleOfN:=1.5D,
            leveragedSlMultipleOfN:=2.5D,
            tpMultipleOfN:=3.0D,
            adxThreshold:=25.0F,
            defaultConfidencePct:=90)

        ''' <summary>
        ''' Damian — Moderate (application default).
        ''' $500 × 5×. SL=1.0N (non-leveraged), LeveragedSL=2.0N (leveraged CFDs, position
        ''' scaled to 50 % to preserve dollar risk). TP=2.5N. ADX ≥ 20.
        ''' Max 2 scale-ins (total 3 positions). R:R = 1:2.5.
        ''' </summary>
        Public Shared ReadOnly Damian As New RiskProfile(
            name:="Damian (Moderate)",
            positionSize:=3,
            leverage:=5,
            maxScaleIns:=2,
            slMultipleOfN:=1.0D,
            leveragedSlMultipleOfN:=2.0D,
            tpMultipleOfN:=2.5D,
            adxThreshold:=20.0F,
            defaultConfidencePct:=80)

        ''' <summary>
        ''' Joe — Aggressive.
        ''' $1 000 × 10× (capped by per-asset regulatory MaxLeverage). SL=0.75N (non-leveraged,
        ''' tight stop accepts more frequent small losses), LeveragedSL=1.5N (leveraged CFDs).
        ''' TP=2.0N. ADX ≥ 15. Max 3 scale-ins (total 4 positions). R:R = 1:2.67.
        ''' Aggression expressed through larger capital, more scale-ins, and lower gates —
        ''' not wider stops. Tighter SL improves expected value across many trades.
        ''' </summary>
        Public Shared ReadOnly Joe As New RiskProfile(
            name:="Joe (Aggressive)",
            positionSize:=5,
            leverage:=10,
            maxScaleIns:=3,
            slMultipleOfN:=0.75D,
            leveragedSlMultipleOfN:=1.5D,
            tpMultipleOfN:=2.0D,
            adxThreshold:=15.0F,
            defaultConfidencePct:=70)

        ''' <summary>All three built-in profiles in ascending risk order.</summary>
        Public Shared ReadOnly All As IReadOnlyList(Of RiskProfile) = New RiskProfile() {Lewis, Damian, Joe}

    End Class

End Namespace
