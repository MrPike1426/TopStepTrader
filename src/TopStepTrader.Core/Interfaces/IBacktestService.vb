Imports System.Threading
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Core.Interfaces

    Public Interface IBacktestService
        Event ProgressUpdated As EventHandler(Of BacktestProgressEventArgs)
        Function RunBacktestAsync(config As BacktestConfiguration, cancel As CancellationToken) As Task(Of BacktestResult)
        Function GetBacktestRunsAsync() As Task(Of IEnumerable(Of BacktestResult))
    End Interface

    Public Class BacktestConfiguration
        Public Property RunName As String = String.Empty
        Public Property ContractId As String = String.Empty
        Public Property Timeframe As Integer = 5
        Public Property StartDate As Date
        Public Property EndDate As Date
        Public Property InitialCapital As Decimal = 50000D
        Public Property MinSignalConfidence As Single = 0.65F

        ' ── Per-contract execution parameters ──────────────────────────────────
        ''' <summary>Number of contracts per trade entry.</summary>
        Public Property Quantity As Integer = 1

        ''' <summary>
        ''' Price units per tick for the selected contract.
        ''' Used by BacktestMetrics to convert tick counts into price deltas.
        ''' Defaults to 0.25 (MES/MNQ convention — quarter-point ticks).
        ''' Contract overrides: MGC = 0.10, MCL = 0.01.
        ''' </summary>
        Public Property TickSize As Decimal = 0.25D

        ''' <summary>
        ''' Dollar value per one full price-unit (1.0 point) for the selected contract.
        ''' Used by BacktestMetrics.CalculatePnL to convert price movement into dollar P&amp;L.
        ''' Defaults to $5 (MES correct value).
        ''' Contract overrides: MNQ = $2, MGC = $10, MCL = $100.
        ''' </summary>
        Public Property PointValue As Decimal = 5.0D

        ''' <summary>
        ''' Which entry condition to evaluate during backtest replay.
        ''' Defaults to EmaRsiWeightedScore to preserve existing behaviour.
        ''' Set to TripleEmaCascade for Sniper backtests.
        ''' </summary>
        Public Property StrategyCondition As StrategyConditionType = StrategyConditionType.EmaRsiWeightedScore

        ''' <summary>
        ''' Minimum ADX value required before an EmaRsiWeightedScore entry signal is acted on.
        ''' 0 (default) = gate disabled — every bar meeting the confidence threshold is traded,
        '''              regardless of trend strength. Useful for exploring raw signal frequency.
        ''' 25          = matches live StrategyExecutionEngine behaviour (strong-trend-only entries).
        ''' Ignored for TripleEmaCascade (which has no ADX gate).
        ''' </summary>
        Public Property MinAdxThreshold As Single = 0.0F

        ' ── ATR-based SL/TP mode ─────────────────────────────────────────────────

        ''' <summary>
        ''' When True, SL and TP are expressed as multiples of the 14-bar ATR at entry
        ''' rather than fixed dollar amounts.  ATR is in price units; dollar P&amp;L is computed
        ''' via PointValue × Quantity as normal.
        ''' When False (default), SlDollarBracket / TpDollarBracket are used.
        ''' </summary>
        Public Property UseAtrMode As Boolean = True

        ''' <summary>
        ''' Stop-loss distance as a multiple of ATR(14) at entry.
        ''' Only used when <see cref="UseAtrMode"/> is True.  Default 1.5.
        ''' </summary>
        Public Property SlAtrMultiple As Decimal = 1.5D

        ''' <summary>
        ''' Take-profit distance as a multiple of ATR(14) at entry.
        ''' Only used when <see cref="UseAtrMode"/> is True.  Default 3.0 (2:1 R:R vs 1.5× SL).
        ''' </summary>
        Public Property TpAtrMultiple As Decimal = 3.0D

        ' ── Dynamic exit management ──────────────────────────────────────────────

        ''' <summary>
        ''' When True, the stop-loss trails the best bar close by the initial SL distance.
        ''' The stop only moves in the trade's favour — it never widens.
        ''' Applies to all strategies (standard dollar-based and ATR price-level strategies).
        ''' Default False preserves the original fixed-SL behaviour.
        ''' </summary>
        Public Property TrailingStopEnabled As Boolean = False

        ''' <summary>
        ''' When True, once bar close reaches 50% of the initial TP distance the SL
        ''' is advanced to break-even (entry price).  Works independently of TrailingStopEnabled.
        ''' Default False.
        ''' </summary>
        Public Property BreakEvenOnHalfTpEnabled As Boolean = False

        ''' <summary>
        ''' When True, if bar close moves beyond the current TP target the TP is extended
        ''' by one additional TP unit, letting the trade run a further leg.
        ''' Capped at 3× the initial TP delta so positions don't run indefinitely.
        ''' Default False.
        ''' </summary>
        Public Property ExtendTpEnabled As Boolean = False

        ' ── Force Close (profit cap) ─────────────────────────────────────────────

        ''' <summary>
        ''' When True, any open position whose unrealised P&amp;L (sum of all legs)
        ''' reaches or exceeds <see cref="ForceCloseAmount"/> is closed immediately.
        ''' Acts as a per-position profit cap — losses are managed by SL/TP brackets.
        ''' Default False.
        ''' </summary>
        Public Property ForceCloseEnabled As Boolean = False

        ''' <summary>
        ''' Dollar profit threshold for the force-close profit cap.
        ''' When <see cref="ForceCloseEnabled"/> is True and the open position P&amp;L
        ''' ≥ this amount, all legs are closed at the current bar's close.
        ''' Default $50.
        ''' </summary>
        Public Property ForceCloseAmount As Decimal = 50D

        ''' <summary>
        ''' Number of ticks of adverse slippage applied to stop-loss fills.
        ''' Models realistic fill degradation when a SL is triggered by a gap or fast market.
        ''' Long SL fills <c>SlippageTicks × TickSize</c> below the stop price;
        ''' short SL fills the same distance above.
        ''' Default 0 (no slippage — preserves existing behaviour).
        ''' Set to 1 for conservative backtesting; 2 for volatile instruments (MCL, MBT).
        ''' </summary>
        Public Property SlippageTicks As Integer = 0

        ''' <summary>
        ''' Exchange + clearing + platform commission per side per contract, in USD.
        ''' Deducted as a round-trip cost (2×) per closed trade leg in CalculatePnL.
        ''' TopStepX micro futures standard rate: $4.50/side = $9.00 round trip.
        ''' Default 0 preserves existing behaviour for callers that do not set it.
        ''' </summary>
        Public Property CommissionPerSideUsd As Decimal = 0D

        ''' <summary>
        ''' Bid-ask spread applied to entry fills, expressed in ticks.
        ''' Models the half-spread cost: a Buy entry fills <c>SpreadTicks × TickSize</c>
        ''' above bar.Open; a Sell entry fills the same distance below bar.Open.
        ''' This is in addition to the existing 1-tick adverse slippage already applied
        ''' at entry — set SpreadTicks = 0 (default) to preserve the existing behaviour.
        ''' Typical micro futures value: 1 (one tick, e.g. 0.25 pts on MES/MNQ).
        ''' </summary>
        Public Property SpreadTicks As Integer = 0

        ' ── Dollar-based fixed SL/TP bracket ─────────────────────────────────────

        ''' <summary>
        ''' Dollar amount for the initial stop-loss bracket.
        ''' Used by the config-based CheckExit / GetExitPrice overloads when
        ''' <see cref="UseAtrMode"/> is False.  0 = no stop (open-ended).
        ''' </summary>
        Public Property SlDollarBracket As Decimal = 0D

        ''' <summary>
        ''' Dollar amount for the initial take-profit bracket.
        ''' Used by the config-based CheckExit / GetExitPrice overloads when
        ''' <see cref="UseAtrMode"/> is False.  0 = no target (open-ended).
        ''' </summary>
        Public Property TpDollarBracket As Decimal = 0D

        ' ── Indicator period ─────────────────────────────────────────────────────

        ''' <summary>
        ''' RSI period used by EmaRsiWeightedScore backtests.
        ''' Default 14 preserves existing behaviour. Set to 9 for 1-min/5-min scalping.
        ''' Ignored by strategies that do not use a configurable RSI period.
        ''' </summary>
        Public Property IndicatorPeriod As Integer = 14

        ' ── Broker minimum stop ──────────────────────────────────────────────────

        ''' <summary>
        ''' STRAT-26: Minimum stop-loss distance in dollars enforced by the exchange/broker.
        ''' When &gt; 0, backtest SL deltas are clamped so the stop is never tighter than this floor.
        ''' Maps to <see cref="TopStepTrader.Core.Trading.FavouriteContract.PxMinStopDollars"/> at runtime.
        ''' Default 0 = no clamping (preserves existing behaviour).
        ''' Example: M6E minimum = $12.50 (10 ticks × $1.25/tick).
        ''' </summary>
        Public Property MinStopDollars As Decimal = 0D

        ' ── Scale-in cap ─────────────────────────────────────────────────────────

        ''' <summary>
        ''' Maximum additional entries after the initial leg.
        ''' Mirrors <see cref="StrategyDefinition.MaxScaleIns"/>: Lewis=1, Damian=2, Joe=3.
        ''' Default 2 (Damian baseline). Must be ≥ 0.
        ''' </summary>
        Public Property MaxScaleIns As Integer
            Get
                Return _maxScaleIns
            End Get
            Set(value As Integer)
                If value < 0 Then
                    Throw New ArgumentOutOfRangeException(NameOf(MaxScaleIns), "MaxScaleIns must be ≥ 0.")
                End If
                _maxScaleIns = value
            End Set
        End Property
        Private _maxScaleIns As Integer = 2

        ' ── Out-of-sample train/test split ───────────────────────────────────────

        ''' <summary>
        ''' Fraction of bars used as the in-sample training window.
        ''' 0.0 (default) disables the split — preserves current behaviour.
        ''' 0.6 = first 60% are training, last 40% are test.
        ''' When active, <see cref="BacktestResult.OutOfSampleResult"/> carries the test metrics.
        ''' Both subsets must contain at least 50 bars, otherwise the split is skipped.
        ''' </summary>
        Public Property TrainTestSplit As Double = 0.0

    End Class

End Namespace
