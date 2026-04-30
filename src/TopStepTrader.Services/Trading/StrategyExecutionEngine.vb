Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports TopStepTrader.API.Hubs
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Models.Diagnostics
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Data.Entities
Imports TopStepTrader.Data.Repositories
Imports TopStepTrader.ML.Features
Imports TopStepTrader.Services.Diagnostics
Imports TopStepTrader.Services.Market

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' Manually-started execution engine that monitors live bars every 30 seconds,
    ''' evaluates the strategy condition, and places entry + bracket orders when triggered.
    ''' Raises LogMessage events that the UI subscribes to for real-time feedback.
    ''' Register as Transient — one instance per strategy session.
    ''' </summary>
    Public Class StrategyExecutionEngine
        Implements IDisposable

        ' ── Trade phase state machine ─────────────────────────────────────────────
        Private Enum TradePhase
            Idle        ' no position
            Entering    ' order submitted, waiting for broker propagation
            HardStop    ' position confirmed; initial hard SL + TP in place; no Free Roll yet
            FreeRoll    ' activation price crossed; SL at BE+buffer; TP cancelled; ATR trail active
            Closing     ' flatten in progress
        End Enum

        ' ── Dependencies ──────────────────────────────────────────────────────────
        Private ReadOnly _ingestionService As IBarIngestionService
        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _logger As ILogger(Of StrategyExecutionEngine)
        Private ReadOnly _claudeService As IClaudeReviewService
        Private ReadOnly _pxSettings As ProjectXSettings
        Private ReadOnly _marketHub As MarketHubClient
        Private ReadOnly _outcomeRepo As TradeOutcomeRepository
        Private ReadOnly _snapshotRepo As ITradeSetupSnapshotRepository
        Private ReadOnly _lifespanRepo As ITradeLifespanRepository

        ' ── Real-time price (MarketHub) ───────────────────────────────────────────
        ' Updated by OnMarketQuoteReceived on every GatewayQuote push.
        ' Double so Volatile.Read/Write are valid (Decimal is not atomic on 64-bit CLR).
        ' 0D = no quote received yet; fall back to 15-second history bar.
        Private _lastQuotePrice As Double = 0D
        ' PX contract ID currently subscribed; Nothing = not subscribed.
        Private _subscribedPxContractId As String = Nothing

        ' ── State ─────────────────────────────────────────────────────────────────
        Private _strategy As StrategyDefinition
        ' SuperTrendAdx: direction on the previous bar check (+1.0, -1.0, or 0.0)
        Private _stPrevDirection As Single = 0.0F
        Private _timer As System.Threading.Timer
        Private _positionTimer As System.Threading.Timer  ' adaptive bracket trail (5s open / 60s idle)
        Private _cts As CancellationTokenSource       ' cancelled by Stop() / Dispose()
        Private _callbackRunning As Integer = 0       ' Interlocked reentrancy guard
        Private _positionCallbackRunning As Integer = 0  ' reentrancy guard for trail timer
        ' Protects shared position state written by the 30-second REST timer and read/written
        ' by the SignalR OnLivePositionUpdated handler (different thread-pool thread).
        Private ReadOnly _stateLock As New Object()
        Private _tradePhase As TradePhase = TradePhase.Idle
        ''' <summary>True when a position is open (any phase except Idle/Entering).</summary>
        Private ReadOnly Property _positionOpen As Boolean
            Get
                Return _tradePhase <> TradePhase.Idle AndAlso _tradePhase <> TradePhase.Entering
            End Get
        End Property
        Private _disposed As Boolean = False
        Private _running As Boolean = False
        Private _lastCheckedBarCount As Integer = 0  ' tracks when to re-log bar-window / gap info
        Private _openPositionId As Long? = Nothing     ' broker positionId of the live trade; Nothing = no open position

        ' ── Events
        ''' <summary>Raised on the thread-pool whenever a log line is produced.</summary>
        Public Event LogMessage As EventHandler(Of String)
        ''' <summary>Raised when the engine stops (expired, stopped, or errored).</summary>
        Public Event ExecutionStopped As EventHandler(Of String)
        ''' <summary>Raised when an entry order is placed (trade opened).</summary>
        Public Event TradeOpened As EventHandler(Of TradeOpenedEventArgs)
        ''' <summary>Raised when the bracket position closes (TP or SL filled).</summary>
        Public Event TradeClosed As EventHandler(Of TradeClosedEventArgs)
        ''' <summary>Raised each bar-check cycle with the latest bar close price, for live P&amp;L updates.</summary>
        Public Event BarPriceUpdated As EventHandler(Of Decimal)
        ''' <summary>Raised every 30-second tick while a position is open with API-authoritative P&amp;L and positionId.</summary>
        Public Event PositionSynced As EventHandler(Of PositionSyncedEventArgs)
        ''' <summary>Raised after every bar check with the live EMA/RSI confidence score (0–100), even when no signal fires.</summary>
        Public Event ConfidenceUpdated As EventHandler(Of ConfidenceUpdatedEventArgs)
        ''' <summary>Raised when the turtle bracket changes.</summary>
        Public Event TurtleBracketChanged As EventHandler(Of TurtleBracketChangedEventArgs)

        ' ── Market-open guard ─────────────────────────────────────────────────────
        ''' <summary>
        ''' Optional predicate evaluated immediately before any entry or scale-in order is
        ''' submitted.  Return False to suppress order placement while keeping the bar-check
        ''' loop and confidence telemetry fully active (used by Hydra market-hours guard).
        ''' Defaults to Function() True so all non-Hydra callers are unaffected.
        ''' </summary>
        Public Property IsOrderingAllowed As Func(Of Boolean) = Function() True

        ''' <summary>
        ''' When True the SL is snapped to entry price (breakeven) on the next trail tick.
        ''' Set by the Asset Bassett coordinator when an opposing strategy fires a signal.
        ''' Cleared when the position closes or the coordinator detects a reversal.
        ''' Normal ATR trail is suspended while this flag is True.
        ''' </summary>
        Public Property DangerZoneActive As Boolean = False

        ''' <summary>Read-only access to the confirmed entry price for the current position.
        ''' Used by the Asset Bassett coordinator to verify the breakeven level.</summary>
        Public ReadOnly Property LastEntryPrice As Decimal
            Get
                Return _lastEntryPrice
            End Get
        End Property

        ' ── Trade-tracking state (for performance panel) ──────────────────────────
        Private _lastEntryPrice As Decimal = 0D
        Private _lastEntrySide As OrderSide = OrderSide.Buy
        Private ReadOnly _lastConfidencePct As Integer = 0
        Private _lastTpPrice As Decimal = 0D
        Private _lastSlPrice As Decimal = 0D
        ' ATR-derived initial SL price (before minSlPoints clamp).  The trail is blocked
        ' until the new SL candidate moves strictly beyond this level — preventing the
        ' minSlPoints-clamp → first-bar jump → premature-stop failure mode.
        Private _initialSlPrice As Decimal = 0D
        ' Tick distance used when placing the initial SL bracket (ATR-derived or DefaultSlTicks).
        ' TrailBracketAsync uses this so the Wide/Standard/Tight tier is preserved throughout
        ' the trade — the trail anchors at the same tick distance, not the fixed DefaultSlTicks.
        Private _initialSlTicks As Integer = 0
        Private ReadOnly _lastTpExternalId As Long? = Nothing
        Private _pendingConfidencePct As Integer = 0
        Private _lastFinalAmount As Decimal = 0D   ' contract count (TopStepX) or cash amount submitted to broker
        ' Running sum of DollarPerPoint across ALL open positions (initial + scale-ins).
        ' DollarPerPoint (TopStepX) = tickValue / tickSize per contract.
        ' The bracket is rescaled after each scale-in so SL/TP advancement reflects
        ' the TOTAL portfolio P&L, not just the initial single position.
        Private _totalDollarPerPoint As Decimal = 0D
        ' Timestamp recorded when a position is confirmed open (after PlaceOrderAsync succeeds).
        ' Used to skip the portfolio close-check for the first 60 s so the broker API has time
        ' to reflect the new position before we would mistakenly declare it closed.
        Private _positionOpenedAt As DateTimeOffset = DateTimeOffset.MinValue
        ' Timestamp of the most recent broker-confirmed position close (SL/TP, flatten, or trail).
        ' Enforces a re-entry cooldown so the engine cannot place a new order in the same
        ' 30-second tick that detected the close — preventing instant re-entry cascades.
        Private _lastPositionClosedAt As DateTimeOffset = DateTimeOffset.MinValue
        ''' <summary>2 full bars in seconds — scales with timeframe so confluent conditions clear before re-entry is allowed.</summary>
        Private ReadOnly Property ReEntryCooldownSeconds As Integer
            Get
                Return _strategy.TimeframeMinutes * 2 * 60
            End Get
        End Property
        Private _lastApiPnl As Decimal = 0D     ' last broker-reported unrealised P&L; used as final P&L on close
        Private _lastBarClose As Decimal = 0D   ' freshest price: 15-second bar when position open, strategy-tf bar otherwise; used for P&L calculation
        ' Cloud-edge SL price set by the MultiConfluence case; consumed once by PlaceBracketOrdersAsync.
        ' Nothing for all other strategy types.
        Private _mcCloudSlPrice As Decimal? = Nothing
        ' STRAT-16: True when the pending MultiConfluence signal is a partial 8/9 conviction signal.
        ' Causes quantity to be halved for the entry order.
        Private _mcPartialSignal As Boolean = False
        ' Absolute SL price for LULT Divergence — trigger wave extreme ± ATR-scaled tick buffer.
        ' Set when the 6-step LULT signal fires; Nothing for all other strategy types.
        Private _lultTriggerExtreme As Decimal? = Nothing
        ' Set to True when a startup-detected (or orphan-detected) position is attached but the
        ' Turtle bracket could not be initialized because ATR = 0 at that moment.
        ' Cleared on the first DoCheckAsync tick where ATR is available, and on every position close/reset.
        Private _bracketInitPending As Boolean = False
        ' ── Free Roll state ─────────────────────────────────────────────────────
        ' Activation price = entry ± (initialTpTicks × 67% × tickSize).
        Private Const FreeRollActivationFraction As Decimal = 0.67D
        ' When price crosses this level the engine moves SL to BE+CommissionTickBuffer,
        ' cancels the TP, and enters pure ATR trailing mode (TradePhase.FreeRoll).
        Private _freeRollActivationPrice As Decimal = 0D
        ' TP tick distance stored at bracket placement so the 50% activation threshold
        ' can be computed from the same value used for the original broker order.
        Private _initialTpTicks As Integer = 0
        ' Confirmed reversal requires ReversalConfirmBars consecutive NEW bars each
        ' producing an opposite-direction signal.  Bar-timestamp de-duplication prevents
        ' the 30-second timer from counting multiple checks of the same last bar as
        ' separate confirmation steps — only a genuine new completed bar advances the counter.
        Private Const ReversalConfirmBars As Integer = 2
        Private _currentTrendSide As OrderSide?          ' direction we are currently trading
        Private _reversalCandidateSide As OrderSide?     ' opposite side being confirmed
        Private _reversalConfirmCount As Integer = 0     ' consecutive new-bar opposite signals seen
        ' Consecutive ticks on which GetLivePositionSnapshotAsync returned Nothing while we
        ' believe a position is open.  A single Nothing may be a transient API fault, not a
        ' genuine close.  Only after SyncMissThreshold consecutive misses do we declare closed.
        Private _syncMissCount As Integer = 0
        Private Const SyncMissThreshold As Integer = 3  ' 3 × 30s = 90s before declaring position closed externally
        Private _lastBarTimestamp As DateTimeOffset = DateTimeOffset.MinValue
        ' Tracks whether the previous polling tick saw a stale bar so the market-closed
        ' ConfidenceUpdated event is fired exactly once on the fresh→stale transition.
        Private _lastBarWasStale As Boolean = False
        ' Number of times TP has been extended beyond the initial target (max 3 per trade).
        Private _tpAdvanceCount As Integer = 0
        ' ── Session P&L tracking (for AI pre-trade context) ─────────────────────
        ' Accumulated realised P&L across all closed trades since Start().
        ' Used by the AI pre-check so it can judge whether session drawdown warrants a VETO.
        Private _sessionPnl As Decimal = 0D
        Private _sessionTradeCount As Integer = 0
        ' Consecutive-loss and total-trades counters — enriches PreTradeContext for Phases 4–5.
        Private _consecutiveLosses As Integer = 0
        Private _totalTradesThisSession As Integer = 0
        ' Last AI pre-trade verdict — stored for capture into TradeSetupSnapshot.
        Private _lastAiVerdict As String = String.Empty
        Private _lastAiReasoning As String = String.Empty

        ' Turtle bracket instance for managing SL/TP levels
        Private ReadOnly _turtleBracket As Object = Nothing

        ' ── High-fidelity diagnostic logging (8-hour test session) ───────────────
        Private ReadOnly _diagLogger As DiagnosticLogger
        ' Pending entry built in the signal-evaluation branch; logged in the dispatch block.
        ' Nothing = current strategy does not build diagnostic entries this tick.
        Private _pendingDiagEntry As DiagnosticLogEntry = Nothing
        ' Complete TRADE record held in memory until the position closes; written once
        ' as a single JSON line with Outcome fully populated (MFE, MAE, P&L, status).
        Private _openTradeDiagEntry As DiagnosticLogEntry = Nothing

        ' ── Confidence-driven scale-in state ──────────────────────────────────────
        ' Used exclusively by the EmaRsiWeightedScore strategy condition.
        ' Scale-in fires when confidence extreme persists for ScaleInRequiredTicks consecutive ticks.
        Private Const ScaleInRequiredTicks As Integer = 3      ' consecutive extreme ticks before scale-in fires
        ''' <summary>
        ''' Maximum additional positions after the initial entry.
        ''' Reads <see cref="StrategyDefinition.MaxScaleIns"/> from the active profile;
        ''' falls back to 3 (Joe-level) if the strategy is not yet initialised.
        ''' </summary>
        Private ReadOnly Property MaxScaleInTrades As Integer
            Get
                Return If(_strategy IsNot Nothing, _strategy.MaxScaleIns, 3)
            End Get
        End Property
        Private Const ExtremeConfidenceHighThreshold As Integer = 85  ' bullish extreme (upPct ≥ this)
        Private Const ExtremeConfidenceLowThreshold As Integer = 25   ' bearish extreme (upPct ≤ this)
        Private Const NeutralConfidenceLow As Integer = 40     ' neutral band lower bound (upPct)
        Private Const NeutralConfidenceHigh As Integer = 60    ' neutral band upper bound (upPct)
        Private _extremeConfidenceDurationCount As Integer = 0  ' consecutive extreme-confidence ticks
        Private _scaleInTradeCount As Integer = 0               ' scale-in trades placed this session
        ' Count of all open trades tracked this session (initial + scale-ins).
        ' Incremented on each successful PlaceOrder; used to fire the correct number
        ' of TradeClosed events when the broker reports no open positions.
        Private _openTradeCount As Integer = 0
        Private _currentAtrValue As Decimal = 0D      ' ATR(14) from latest bar — drives dynamic SL/TP levels
        Private _lastAdxValue As Single = 0F          ' ADX(14) from latest bar — passed to pre-trade AI check
        Private _currentEma21 As Decimal = 0D          ' EMA21 from latest bar — logged as quality metric alongside scale-in
        Private Const ScaleInBullThreshold As Integer = 80   ' bull score > this required for scale-in (UP ≥ 81%)
        Private Const ScaleInBearThreshold As Integer = 20   ' bull score < this required for bear scale-in (DOWN ≥ 81%)

        ' ── Mid-confidence adverse exit (EmaRsiWeightedScore only) ──────────────────
        ' When a position is open and confidence has clearly shifted into the opposite
        ' direction (above NeutralConfidenceHigh but below the full reversal threshold)
        ' for this many consecutive NEW bars, the position is flattened immediately.
        ' This closes the "61–84% purgatory zone" where neither neutral exit (≤60%)
        ' nor reversal exit (≥85% for 2 bars) fires, allowing large losses to accumulate.
        Private Const AdverseConfidenceBars As Integer = 3     ' 3 new bars = 3 × timeframe minutes of confirmation
        Private _adverseConfidenceCount As Integer = 0         ' consecutive new-bar adverse ticks

        ' ── FEAT-01: outcome / lifespan tracking ─────────────────────────────────
        Private _openTradeOutcomeId As Long? = Nothing
        Private _openLifespanId As Integer? = Nothing
        Private _runningMaeDollars As Decimal = 0D
        Private _runningMfeDollars As Decimal = 0D
        Private _slRatchetCount As Integer = 0
        Private _freeRideActivatedAt As DateTimeOffset? = Nothing
        Private _tradeEntrySessionWindow As String = String.Empty
        Private _barsInTrade As Integer = 0
        Private _lastSignalArgs As ConfidenceUpdatedEventArgs = Nothing
        Private _lastSignalBar As MarketBar = Nothing
        ' Stores the most recent ConfidenceUpdatedEventArgs raised this tick (any strategy).
        ' Written just before RaiseEvent ConfidenceUpdated so it is always current when
        ' PlaceBracketOrdersAsync is called in the same tick.
        Private _latestConfidenceArgs As ConfidenceUpdatedEventArgs = Nothing

        Public Sub New(ingestionService As IBarIngestionService,
                       orderService As IOrderService,
                       session As ITradingSessionContext,
                       logger As ILogger(Of StrategyExecutionEngine),
                       diagLogger As DiagnosticLogger,
                       claudeService As IClaudeReviewService,
                       pxOptions As IOptions(Of ProjectXSettings),
                       marketHub As MarketHubClient,
                       Optional outcomeRepo As TradeOutcomeRepository = Nothing,
                       Optional snapshotRepo As ITradeSetupSnapshotRepository = Nothing,
                       Optional lifespanRepo As ITradeLifespanRepository = Nothing)
            _ingestionService = ingestionService
            _orderService = orderService
            _session = session
            _logger = logger
            _diagLogger = diagLogger
            _claudeService = claudeService
            _pxSettings = pxOptions.Value
            _marketHub = marketHub
            _outcomeRepo = outcomeRepo
            _snapshotRepo = snapshotRepo
            _lifespanRepo = lifespanRepo
        End Sub

        ' ── Public API ────────────────────────────────────────────────────────────

        Public ReadOnly Property IsRunning As Boolean
            Get
                Return _running
            End Get
        End Property

        ''' <summary>
        ''' Start monitoring. Sets ExpiresAt on the strategy and begins the 30-second polling loop.
        ''' </summary>
        Public Sub Start(strategy As StrategyDefinition)
            If _running Then Return
            _strategy = strategy
            _strategy.ExpiresAt = DateTimeOffset.UtcNow.AddHours(strategy.DurationHours)
            _tradePhase = TradePhase.Idle
            _openPositionId = Nothing
            _openTradeCount = 0
            _positionOpenedAt = DateTimeOffset.MinValue
            _lastPositionClosedAt = DateTimeOffset.MinValue
            _lastApiPnl = 0D
            _mcCloudSlPrice = Nothing
            _mcPartialSignal = False
            _lultTriggerExtreme = Nothing
            _currentTrendSide = Nothing
            _reversalCandidateSide = Nothing
            _reversalConfirmCount = 0
            _lastBarTimestamp = DateTimeOffset.MinValue
            _lastBarWasStale = False
            _extremeConfidenceDurationCount = 0
            _scaleInTradeCount = 0
            _adverseConfidenceCount = 0
            _syncMissCount = 0
            _sessionPnl = 0D
            _sessionTradeCount = 0
            _consecutiveLosses = 0
            _totalTradesThisSession = 0
            _lastAiVerdict = String.Empty
            _lastAiReasoning = String.Empty
            _currentAtrValue = 0D
            _currentEma21 = 0D
            _totalDollarPerPoint = 0D
            _lastBarClose = 0D
            _freeRollActivationPrice = 0D
            _initialTpTicks = 0
            ResetTrailState()
            _running = True
            _lastCheckedBarCount = 0   ' reset so bar-window is logged on first tick of this session
            _cts = New CancellationTokenSource()
            Interlocked.Exchange(_callbackRunning, 0)
            AddHandler _orderService.PositionUpdated, AddressOf OnLivePositionUpdated
            AddHandler _marketHub.QuoteReceived, AddressOf OnMarketQuoteReceived

            ' ── Diagnostic session reset ───────────────────────────────────────────
            _openTradeDiagEntry = Nothing
            _pendingDiagEntry = Nothing
            _diagLogger?.StartSession(strategy.ContractId, strategy.Name)

            Log($"Strategy started — {strategy.ContractId} | {strategy.Name}")
            Log($"Duration: {strategy.DurationHours}hrs | Expires: {strategy.ExpiresAt:HH:mm} UTC")
            Log($"Checking bars every 30 seconds...")
            Log("── Strategy Config ─────────────────────────────")
            Log($"  Condition       : {strategy.Condition}")
            Log($"  Instrument      : {strategy.ContractId}  TF: {strategy.TimeframeMinutes}m")
            Log($"  Persona         : {If(String.IsNullOrEmpty(strategy.PersonaName), "(none)", strategy.PersonaName)}  ADX≥{strategy.AdxThreshold}")
            Log($"  SL×{strategy.SlMultipleOfN}N  TP×{strategy.TpMultipleOfN}N")
            Log($"  MaxScaleIns     : {strategy.MaxScaleIns}")
            Log($"  Trading Hours   : {strategy.TradingStartHourUtc:D2}:00–{strategy.TradingEndHourUtc:D2}:00 UTC")
            Log($"  Pre-trade AI    : {If(strategy.UseAiPreTradeGate AndAlso strategy.UsePreTradeAiCheck, "ON", "OFF")}")
            Log($"  Re-entry cool   : {strategy.TimeframeMinutes * 2 * 60}s")
            Log("────────────────────────────────────────────────")

            ' ── Existing-position check on startup ──────────────────────────────
            ' Query the broker immediately so the engine knows about any open
            ' positions left by a previous session.  If found, set _positionOpen = True
            ' so the engine skips the initial-entry path and goes straight to monitoring.
            ' This prevents piling new orders on top of already-open ones.
            Task.Run(Async Function() As Task
                         Try
                             Dim snapshot = Await _orderService.GetLivePositionSnapshotAsync(
                                 strategy.AccountId, strategy.ContractId, Nothing, _cts.Token)
                             If snapshot IsNot Nothing Then
                                 _tradePhase = TradePhase.HardStop
                                 SetTrailTimerInterval(True)
                                 SetBarCheckInterval(True)   ' FEAT-11: position detected — revert to 30s poll
                                 _openPositionId = snapshot.PositionId
                                 _positionOpenedAt = DateTimeOffset.UtcNow.AddSeconds(-61) ' skip propagation guard
                                 If snapshot.OpenRate > 0D Then
                                     _lastEntryPrice = snapshot.OpenRate
                                     _lastEntrySide = If(snapshot.IsBuy, OrderSide.Buy, OrderSide.Sell)
                                 End If
                                 _currentTrendSide = If(snapshot.IsBuy, OrderSide.Buy, OrderSide.Sell)
                                 _lastFinalAmount = snapshot.Amount
                                 _openTradeCount = snapshot.PositionCount
                                 ' Infer how many scale-ins have already been placed so the cap
                                 ' is enforced correctly when the engine restarts with positions open.
                                 ' initial trade = 1; every additional position = one scale-in.
                                 _scaleInTradeCount = Math.Min(MaxScaleInTrades,
                                                               Math.Max(0, snapshot.PositionCount - 1))
                                 ' Seed total DPP from the aggregate units the broker reports so
                                 ' the deferred bracket init uses the correct combined sensitivity.
                                 ' DPP = contracts × (tickValue / tickSize), NOT raw contract count.
                                 ' E.g. MBT 10 contracts: 10 × ($0.50 / 5) = $1.00 per point.
                                 _totalDollarPerPoint = If(snapshot.Units > 0D AndAlso strategy.TickSize > 0D,
                                     snapshot.Units * strategy.TickValue / strategy.TickSize, 0D)
                                 Dim startupFav = FavouriteContracts.TryGetBySymbolResolved(strategy.ContractId)
                                 If startupFav IsNot Nothing AndAlso Not String.IsNullOrEmpty(startupFav.PxContractId) Then
                                     SubscribeMarketQuotes(startupFav.PxContractId)
                                 End If
                                 Dim startupSide = If(snapshot.IsBuy, OrderSide.Buy, OrderSide.Sell)

                                 ' Always attach to the existing position — regardless of current P&L.
                                 ' The engine cannot know the intended risk sizing of a position that was
                                 ' opened at a different notional or placed manually, so
                                 ' rescue-closing it on startup based on the current strategy's SL
                                 ' dollar amount is inappropriate.  The turtle bracket will establish
                                 ' SL/TP protection once ATR is available on the first bar-check tick.
                                 RaiseEvent TradeOpened(Me, New TradeOpenedEventArgs(startupSide, strategy.ContractId, 100,
                                         snapshot.OpenedAtUtc, Nothing, snapshot.PositionId,
                                         snapshot.OpenedAtUtc, snapshot.Amount, snapshot.OpenRate))
                                 Dim pnlStr = If(snapshot.UnrealizedPnlUsd <> 0D,
                                     $" P&L=${snapshot.UnrealizedPnlUsd:F2}", String.Empty)
                                 Dim capStr = If(_scaleInTradeCount >= MaxScaleInTrades,
                                                 $"scale-in cap REACHED ({_scaleInTradeCount}/{MaxScaleInTrades})",
                                                 $"scale-in {_scaleInTradeCount}/{MaxScaleInTrades} used")
                                 Log($"⚠️  Existing {snapshot.PositionCount} position(s) detected on startup " &
                                     $"(positionId={snapshot.PositionId}, entry={snapshot.OpenRate:F4}, " &
                                     $"units={snapshot.Units:F3}{pnlStr}, {capStr}) — attaching and applying turtle bracket.")
                                 ' Turtle bracket cannot be initialized here because ATR = 0 before the
                                 ' first bar check.  Set the pending flag so DoCheckAsync creates the
                                 ' bracket on the first tick where ATR is available.
                                 _bracketInitPending = True
                             Else
                                 Log($"✓ No existing positions for {strategy.ContractId} — ready to trade.")
                             End If
                         Catch ex As Exception
                             Log($"⚠️  Startup position check failed: {ex.Message} — assuming no open positions.")
                         End Try
                     End Function)

            ' 3-second initial delay gives the startup position-check Task.Run time to complete
            ' before the first bar-check tick fires, eliminating a race where _positionOpen
            ' could still be False when the timer's first callback runs.
            ' FEAT-11: MultiConfluence starts at 15-second cadence when flat.
            ' SetBarCheckInterval(positionOpen:=False) below fires after the timer is created
            ' so that non-MC strategies remain at 30s (SetBarCheckInterval is a no-op until
            ' the timer object exists).
            _timer = New System.Threading.Timer(AddressOf TimerCallback, Nothing,
                                                TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30))
            ' FEAT-11: switch to 15s immediately for flat MultiConfluence sessions
            SetBarCheckInterval(False)

            ' Bracket trail timer — fires independently of the 30-second bar-check.
            ' Period adapts: 1s while a position is open, 60s when idle.
            ' SetTrailTimerInterval() switches the period at each position open/close.
            Interlocked.Exchange(_positionCallbackRunning, 0)
            _positionTimer = New System.Threading.Timer(AddressOf PositionTimerCallback, Nothing,
                                                        TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60))
        End Sub

        ''' <summary>Stop the engine and raise ExecutionStopped event.</summary>
        Public Sub [Stop](Optional reason As String = "Stopped by user")
            If Not _running Then Return
            _running = False
            _cts?.Cancel()                          ' cancel any in-flight API call immediately
            _timer?.Change(Timeout.Infinite, 0)     ' prevent future timer ticks
            _positionTimer?.Change(Timeout.Infinite, 0)

            ' RC-3: warn the user if position
            ' exposed between sessions.  Neutral-exit logic requires the engine to be running;
            ' on next Start() the startup position check (RC-2) will re-attach and the first
            ' tick will evaluate confidence and flatten if the band is neutral.
            If _positionOpen Then
                Log($"⚠️  POSITIONS STILL OPEN — {_strategy?.ContractId} has active positions. " &
                    $"Monitor manually or restart the engine to resume automated management.")
            End If
            ' Flush any in-memory TRADE record as ENGINE_STOPPED before closing the log
            If _openTradeDiagEntry IsNot Nothing Then
                If _openTradeDiagEntry.Outcome Is Nothing Then
                    _openTradeDiagEntry.Outcome = New DiagOutcome()
                End If
                _openTradeDiagEntry.Outcome.Status = "ENGINE_STOPPED"
                _openTradeDiagEntry.Outcome.TradeLifetimeSeconds =
                    If(_positionOpenedAt > DateTimeOffset.MinValue,
                       CLng((DateTimeOffset.UtcNow - _positionOpenedAt).TotalSeconds), 0L)
                _openTradeDiagEntry.Timestamp = DateTimeOffset.UtcNow.ToString("o")
                _diagLogger?.WriteEntry(_openTradeDiagEntry)
                _openTradeDiagEntry = Nothing
            End If
            _diagLogger?.CloseSession()
            RemoveHandler _orderService.PositionUpdated, AddressOf OnLivePositionUpdated
            RemoveHandler _marketHub.QuoteReceived, AddressOf OnMarketQuoteReceived
            Log($"■ Strategy stopped — {reason}")
            RaiseEvent ExecutionStopped(Me, reason)
        End Sub

        ' ── Timer callback ────────────────────────────────────────────────────────

        Private Sub TimerCallback(state As Object)
            If Not _running Then Return

            ' Reentrancy guard: skip this tick if the previous check hasn't finished yet.
            ' Prevents API calls from piling up when the network is slow.
            If Interlocked.CompareExchange(_callbackRunning, 1, 0) <> 0 Then
                Log("⏭  Previous bar check still running — skipping this tick")
                Return
            End If

            Task.Run(Async Function() As Task
                         Try
                             Await DoCheckAsync()
                         Catch ex As OperationCanceledException
                             ' Normal: Stop() was called while an API request was in flight.
                         Catch ex As Exception
                             _logger.LogError(ex, "StrategyExecutionEngine unhandled error")
                             Log($"⚠️  Error during bar check: {ex.Message}")
                         Finally
                             Interlocked.Exchange(_callbackRunning, 0)
                         End Try
                     End Function)
        End Sub

        Private Async Function DoCheckAsync() As Task
            If Not _running Then Return
            Dim ct = If(_cts IsNot Nothing, _cts.Token, CancellationToken.None)

            ' ── Check expiry ──────────────────────────────────────────────────────
            If DateTimeOffset.UtcNow > _strategy.ExpiresAt Then
                [Stop]("Strategy duration expired")
                Return
            End If

            Dim remaining = _strategy.ExpiresAt - DateTimeOffset.UtcNow
            Dim remStr = $"{CInt(remaining.TotalHours)}h {remaining.Minutes}m remaining"

            ' ── Fetch bars ────────────────────────────────────────────────────────
            ' Indicators are always computed from strategy-native timeframe bars
            ' (5/10/15-min depending on the contract).  The timer fires every 15 seconds
            ' for MultiConfluence flat sessions (SetBarCheckInterval) so conditions are
            ' re-evaluated on the most recently closed strategy bar without waiting for the
            ' next full bar close.  This avoids the unreliable 15-second bar feed entirely.
            Dim isMcFlat = (_strategy.Condition = StrategyConditionType.MultiConfluence AndAlso
                            Not _positionOpen)
            Dim timeframe As BarTimeframe = CType(
                If(_strategy.TimeframeMinutes = 1, BarTimeframe.OneMinute,
                If(_strategy.TimeframeMinutes = 5, BarTimeframe.FiveMinute,
                If(_strategy.TimeframeMinutes = 15, BarTimeframe.FifteenMinute,
                If(_strategy.TimeframeMinutes = 60, BarTimeframe.OneHour,
                   BarTimeframe.FiveMinute)))), BarTimeframe)

            ' Compute minimum bars required by the strategy and request a generous buffer
            ' above that threshold so the DB is fully populated on the very first tick.
            ' BUG-12: MultiConfluence requires 80 bars (Ichimoku Span B warm-up) — use its own
            ' MinBarsRequired constant instead of the generic IndicatorPeriod+5 formula.
            Dim minBars = If(_strategy.Indicator = StrategyIndicatorType.MultiConfluence,
                             MultiConfluenceStrategy.MinBarsRequired,
                             _strategy.IndicatorPeriod + 5)
            Dim fetchCount = Math.Max(minBars + 15, 70)  ' buffer above minBars guard

            ' Ingest fresh bars — on the very first call this populates the DB with fetchCount
            ' bars so the strategy can evaluate immediately.
            Dim bars As IList(Of MarketBar)
            Await _ingestionService.IngestAsync(_strategy.ContractId, timeframe, fetchCount, ct)
            bars = Await _ingestionService.GetBarsForMLAsync(_strategy.ContractId, timeframe, fetchCount, ct)

            ' ── Partial-bar guard ────────────────────────────────────────────────────
            ' Drop the last bar when its timestamp falls inside the still-open period.
            ' E.g. on a 15-min TF, a bar timestamped 14:07 UTC is forming until 14:15.
            ' Indicator arrays must only contain closed bars to prevent repaint.
            Dim periodTicksGuard As Long = TimeSpan.FromMinutes(_strategy.TimeframeMinutes).Ticks
            Dim currentPeriodStart As DateTimeOffset = New DateTimeOffset(
                DateTimeOffset.UtcNow.Ticks - (DateTimeOffset.UtcNow.Ticks Mod periodTicksGuard),
                DateTimeOffset.UtcNow.Offset)
            If bars IsNot Nothing AndAlso bars.Count > 0 Then
                Dim lastBarPeriodStart As DateTimeOffset = New DateTimeOffset(
                    bars.Last().Timestamp.Ticks - (bars.Last().Timestamp.Ticks Mod periodTicksGuard),
                    bars.Last().Timestamp.Offset)
                If lastBarPeriodStart >= currentPeriodStart Then
                    bars = bars.Take(bars.Count - 1).ToList()
                End If
            End If

            If bars Is Nothing OrElse bars.Count < minBars Then
                Dim barCount = If(bars Is Nothing, 0, bars.Count)
                If barCount = 0 Then
                    Log($"No bars returned for '{_strategy.ContractId}' (tf={timeframe}, fetch={fetchCount}) — " &
                        $"market may be closed or ingestion failed. Retrying… ({remStr})")
                    ' Surface on the asset card so SummaryLine doesn't stay at "Awaiting first bar check…" forever.
                    ' Reuses _lastBarWasStale to avoid spamming ConfidenceUpdated on every tick.
                    If Not _lastBarWasStale Then
                        RaiseEvent ConfidenceUpdated(Me, New ConfidenceUpdatedEventArgs(0, 0) With {.IsMarketClosed = True})
                        _lastBarWasStale = True
                    End If
                Else
                    Log($"Waiting for bars — have {barCount}/{minBars} needed ({remStr})")
                End If
                Return
            End If

            ' ── Bar-window and gap-continuity logging ────────────────────────────
            ' Fires on startup and whenever a new bar arrives (bar count increases).
            ' Surfaces any timestamp gaps (market closures, missing data) that would
            ' cause EMA/RSI warmup to span non-contiguous sessions.
            If bars.Count > _lastCheckedBarCount Then
                _lastCheckedBarCount = bars.Count
                Dim tfMin = _strategy.TimeframeMinutes
                Dim gapThresholdMin = tfMin * 2.5
                Log($"📊 Bar window: {bars.First().Timestamp:yyyy-MM-dd HH:mm} UTC → {bars.Last().Timestamp:yyyy-MM-dd HH:mm} UTC ({bars.Count} bars, {tfMin}-min tf)")
                Dim gapCount As Integer = 0
                For i = 1 To bars.Count - 1
                    Dim gapMin = (bars(i).Timestamp - bars(i - 1).Timestamp).TotalMinutes
                    If gapMin > gapThresholdMin Then
                        Log($"⚠️  Gap at bar {i}: {bars(i - 1).Timestamp:HH:mm} UTC → {bars(i).Timestamp:HH:mm} UTC ({gapMin:F0} min, expected {tfMin} min)")
                        gapCount += 1
                    End If
                Next
                If gapCount = 0 Then
                    Log($"✓ Bar series contiguous — {bars.Count} bars, no gaps > {CInt(gapThresholdMin)} min")
                Else
                    Log($"⚠️  {gapCount} gap(s) detected — EMA/RSI warmup spans a market closure; indicators may be unreliable across the gap")
                End If
            End If

            ' ── Evaluate condition ────────────────────────────────────────────────
            Dim closes = bars.Select(Function(b) b.Close).ToList()
            Dim opens = bars.Select(Function(b) b.Open).ToList()
            Dim highs = bars.Select(Function(b) b.High).ToList()
            Dim lows = bars.Select(Function(b) b.Low).ToList()
            Dim volumes = bars.Select(Function(b) CDec(b.Volume)).ToList()

            Dim lastBar = bars.Last()
            _lastBarClose = CDec(lastBar.Close)
            RaiseEvent BarPriceUpdated(Me, CDec(lastBar.Close))
            If _positionOpen Then
                Dim livePrice = Await _ingestionService.GetLatestPriceAsync(_strategy.ContractId, ct)
                If livePrice > 0D Then
                    _lastBarClose = livePrice
                    RaiseEvent BarPriceUpdated(Me, livePrice)
                End If
            End If
            Dim side As OrderSide? = Nothing
            Dim rawUpPct As Integer = 0    ' captured from EmaRsiWeightedScore for confidence actions
            Dim rawDownPct As Integer = 0

            ' ── Bar-period de-duplication guard ─────────────────────────────────
            ' The BarIngestionService may store a partially-formed "current" bar whose
            ' sub-minute timestamp advances on every poll.  Without snapping to the
            ' canonical period boundary, isNewBar would be True on every tick.
            Dim periodTicks As Long = TimeSpan.FromMinutes(_strategy.TimeframeMinutes).Ticks
            Dim barPeriodStart = New DateTimeOffset(
                lastBar.Timestamp.Ticks - (lastBar.Timestamp.Ticks Mod periodTicks),
                lastBar.Timestamp.Offset)
            Dim isNewBar = (barPeriodStart > _lastBarTimestamp)
            If isNewBar Then _lastBarTimestamp = barPeriodStart

            ' ── Stale bar guard ──────────────────────────────────────────────────────
            ' When the market is closed the ingestion service stops receiving new bars
            ' but the DB retains the last session's history.  barIsStale = True when the
            ' most recent bar is older than 3× the strategy timeframe cadence.
            ' Entry signals and order placement are suppressed; position monitoring
            ' (broker sync + trailing bracket) continues normally.
            Dim barAgeSeconds = (DateTimeOffset.UtcNow - lastBar.Timestamp).TotalSeconds
            Dim barAgeMins = barAgeSeconds / 60.0
            Dim barStaleThresholdMins = _strategy.TimeframeMinutes * 3.0
            Dim barIsStale As Boolean = barAgeMins > barStaleThresholdMins
            Dim staleDesc As String = $"{barAgeMins:F0}m old (threshold {CInt(barStaleThresholdMins)}m)"

            ' On the fresh→stale transition: fire one ConfidenceUpdated with IsMarketClosed=True
            ' so every tile immediately shows a neutral "closed" state instead of frozen indicator values.
            If barIsStale AndAlso Not _lastBarWasStale Then
                RaiseEvent ConfidenceUpdated(Me, New ConfidenceUpdatedEventArgs(0, 0) With {.IsMarketClosed = True})
                Log($"⏸ Market closed — last bar {staleDesc}. Entry signals suppressed.")
            End If
            ' On the stale→fresh recovery: clear the market-closed state so the tile and engine log
            ' reflect that the market has reopened.  Without this the UI stays dimmed forever.
            If Not barIsStale AndAlso _lastBarWasStale Then
                RaiseEvent ConfidenceUpdated(Me, New ConfidenceUpdatedEventArgs(0, 0) With {.IsMarketClosed = False})
                Log($"▶ Market open — fresh bar received. Resuming signal evaluation.")
            End If
            _lastBarWasStale = barIsStale

            ' ── STRAT-37: TopStepX pre-close adverse-exit check ──────────────────
            ' When a position is open and we are inside the pre-close blackout window
            ' (19:50–20:10 UTC), close any losing position immediately so it cannot
            ' deteriorate further before the 20:10 UTC hard cut.
            ' Winning or flat positions are left open — they may still reach TP.
            If Not barIsStale AndAlso _positionOpen AndAlso IsInTopStepXBlackout() Then
                Dim nowUtcMins = DateTimeOffset.UtcNow.Hour * 60 + DateTimeOffset.UtcNow.Minute
                Dim hardCutMins = TopStepXCloseHourUtc * 60 + TopStepXCloseMinuteUtc  ' 20:10
                If nowUtcMins < hardCutMins AndAlso _lastApiPnl < 0D Then
                    Log($"⚠️  Pre-close adverse flatten — position closed before TopStepX 20:10 UTC cut | P&L=${_lastApiPnl:F2}")
                    Await DoNeutralFlattenAsync(ct)
                    Return
                End If
            End If

            If Not barIsStale Then

                ' ── STRAT-30: Regime filter — classify market and route to override strategy ──
                ' Both TrendingStrategyOverride and RangingStrategyOverride must be set to activate.
                ' When disabled (either is Nothing), activeCondition = base Condition (no-op, backwards-compatible).
                Dim activeCondition = _strategy.Condition
                If _strategy.TrendingStrategyOverride.HasValue AndAlso _strategy.RangingStrategyOverride.HasValue Then
                    Dim regimeAtr = TechnicalIndicators.ATR(highs, lows, closes, 14)
                    Dim regimeAdx = TechnicalIndicators.LastValid(TechnicalIndicators.DMI(highs, lows, closes).ADX)
                    Dim regime = TopStepTrader.Services.Backtest.RegimeClassifier.Classify(regimeAtr, regimeAdx, _strategy.AdxThreshold)
                    activeCondition = If(regime = TopStepTrader.Services.Backtest.RegimeType.Trending,
                                         _strategy.TrendingStrategyOverride.Value,
                                         _strategy.RangingStrategyOverride.Value)
                    Log($"Regime: {regime} → {activeCondition} | ATR={TechnicalIndicators.LastValid(regimeAtr):F4} ADX={regimeAdx:F1} threshold={_strategy.AdxThreshold:F0}")
                End If

                Select Case activeCondition
                    Case StrategyConditionType.FullCandleOutsideBands,
                     StrategyConditionType.CloseOutsideBands
                        Dim bands = TechnicalIndicators.BollingerBands(closes,
                                                                   _strategy.IndicatorPeriod,
                                                                   _strategy.IndicatorMultiplier)
                        Dim upper = CDec(TechnicalIndicators.LastValid(bands.Upper))
                        Dim lower = CDec(TechnicalIndicators.LastValid(bands.Lower))
                        Dim middle = CDec(TechnicalIndicators.LastValid(bands.Middle))
                        Dim bbAtrVals = TechnicalIndicators.ATR(highs, lows, closes, 14)
                        _currentAtrValue = CDec(TechnicalIndicators.LastValid(bbAtrVals))
                        Dim bbDmi = TechnicalIndicators.DMI(highs, lows, closes)
                        Dim bbAdx = TechnicalIndicators.LastValid(bbDmi.ADX)

                        If _currentAtrValue <= 0 Then
                            Log($"Bar checked — BB: ATR={_currentAtrValue:F6} too low (degenerate bar) | {remStr}")
                        ElseIf bbAdx < _strategy.AdxThreshold Then
                            Log($"Bar checked — BB: ADX={bbAdx:F1} < {_strategy.AdxThreshold:F0} (ranging market) | Close={lastBar.Close:F2} BB=[{lower:F2}—{upper:F2}] | {remStr}")
                        ElseIf _strategy.Condition = StrategyConditionType.FullCandleOutsideBands Then
                            ' Entire candle must be outside the band with ≥0.25% penetration depth
                            If _strategy.GoLongWhenBelowBands AndAlso lastBar.High < lower * 0.9975D Then
                                Log($"✅ Full candle below lower band! High={lastBar.High:F2} < Lower={lower:F2}×0.9975 | ADX={bbAdx:F1}")
                                side = OrderSide.Buy
                            ElseIf _strategy.GoShortWhenAboveBands AndAlso lastBar.Low > upper * 1.0025D Then
                                Log($"✅ Full candle above upper band! Low={lastBar.Low:F2} > Upper={upper:F2}×1.0025 | ADX={bbAdx:F1}")
                                side = OrderSide.Sell
                            Else
                                Log($"Bar checked — Close={lastBar.Close:F2} | BB [{lower:F2} — {middle:F2} — {upper:F2}] ADX={bbAdx:F1} | no signal ({remStr})")
                            End If
                        Else
                            ' Close outside band with ≥0.25% penetration depth
                            If _strategy.GoLongWhenBelowBands AndAlso lastBar.Close < lower * 0.9975D Then
                                Log($"✅ Close below lower band! Close={lastBar.Close:F2} < Lower={lower:F2}×0.9975 | ADX={bbAdx:F1}")
                                side = OrderSide.Buy
                            ElseIf _strategy.GoShortWhenAboveBands AndAlso lastBar.Close > upper * 1.0025D Then
                                Log($"✅ Close above upper band! Close={lastBar.Close:F2} > Upper={upper:F2}×1.0025 | ADX={bbAdx:F1}")
                                side = OrderSide.Sell
                            Else
                                Log($"Bar checked — Close={lastBar.Close:F2} | BB [{lower:F2}—{upper:F2}] ADX={bbAdx:F1} | no signal ({remStr})")
                            End If
                        End If

                    Case StrategyConditionType.RSIOversold, StrategyConditionType.RSIOverbought
                        Dim rsi = TechnicalIndicators.RSI(closes, _strategy.IndicatorPeriod)
                        Dim rsiVal = TechnicalIndicators.LastValid(rsi)
                        Dim rsiDmi = TechnicalIndicators.DMI(highs, lows, closes)
                        Dim rsiAdx = TechnicalIndicators.LastValid(rsiDmi.ADX)
                        Dim rsiAtrVals = TechnicalIndicators.ATR(highs, lows, closes, 14)
                        _currentAtrValue = CDec(TechnicalIndicators.LastValid(rsiAtrVals))

                        If _currentAtrValue <= 0 Then
                            Log($"Bar checked — RSI: ATR too low (degenerate bar) | {remStr}")
                        ElseIf rsiAdx < _strategy.AdxThreshold Then
                            Log($"Bar checked — RSI={rsiVal:F1} | ADX={rsiAdx:F1} < {_strategy.AdxThreshold:F0} (ranging market — oversold/overbought unreliable) | {remStr}")
                        ElseIf _strategy.GoLongWhenBelowBands AndAlso rsiVal < 25 Then
                            Log($"✅ RSI oversold! RSI={rsiVal:F1} < 25 | ADX={rsiAdx:F1}")
                            side = OrderSide.Buy
                        ElseIf _strategy.GoShortWhenAboveBands AndAlso rsiVal > 75 Then
                            Log($"✅ RSI overbought! RSI={rsiVal:F1} > 75 | ADX={rsiAdx:F1}")
                            side = OrderSide.Sell
                        Else
                            Log($"Bar checked — RSI={rsiVal:F1} ADX={rsiAdx:F1} | no signal ({remStr})")
                        End If

                    Case StrategyConditionType.EMACrossAbove, StrategyConditionType.EMACrossBelow
                        Dim fastEma = TechnicalIndicators.EMA(closes, _strategy.IndicatorPeriod)
                        Dim slowEma = TechnicalIndicators.EMA(closes, _strategy.SecondaryPeriod)
                        Dim fastNow = TechnicalIndicators.LastValid(fastEma)
                        Dim fastPrev = TechnicalIndicators.PreviousValid(fastEma)
                        Dim slowNow = TechnicalIndicators.LastValid(slowEma)
                        Dim slowPrev = TechnicalIndicators.PreviousValid(slowEma)
                        Dim emaDmi = TechnicalIndicators.DMI(highs, lows, closes)
                        Dim emaAdx = TechnicalIndicators.LastValid(emaDmi.ADX)
                        Dim emaAtrVals = TechnicalIndicators.ATR(highs, lows, closes, 14)
                        _currentAtrValue = CDec(TechnicalIndicators.LastValid(emaAtrVals))

                        ' Minimum gap = 5% of ATR(14) — prevents single-tick whipsaw fires
                        Dim emaMinGap = CSng(_currentAtrValue * 0.05D)
                        Dim crossedAbove = fastPrev < slowPrev AndAlso fastNow > slowNow AndAlso
                                           (fastNow - slowNow) >= emaMinGap AndAlso
                                           fastNow > fastPrev  ' fast EMA must be rising after cross
                        Dim crossedBelow = fastPrev > slowPrev AndAlso fastNow < slowNow AndAlso
                                           (slowNow - fastNow) >= emaMinGap AndAlso
                                           fastNow < fastPrev  ' fast EMA must be falling after cross

                        If _currentAtrValue <= 0 Then
                            Log($"Bar checked — EMA Cross: ATR too low (degenerate bar) | {remStr}")
                        ElseIf emaAdx < _strategy.AdxThreshold Then
                            Log($"Bar checked — EMA{_strategy.IndicatorPeriod}/{_strategy.SecondaryPeriod}: ADX={emaAdx:F1} < {_strategy.AdxThreshold:F0} (ranging) | {remStr}")
                        ElseIf _strategy.GoLongWhenBelowBands AndAlso crossedAbove Then
                            Log($"✅ EMA{_strategy.IndicatorPeriod} crossed above EMA{_strategy.SecondaryPeriod}! " &
                                $"Gap={fastNow - slowNow:F4} ≥ {emaMinGap:F4} | ADX={emaAdx:F1}")
                            side = OrderSide.Buy
                        ElseIf _strategy.GoShortWhenAboveBands AndAlso crossedBelow Then
                            Log($"✅ EMA{_strategy.IndicatorPeriod} crossed below EMA{_strategy.SecondaryPeriod}! " &
                                $"Gap={slowNow - fastNow:F4} ≥ {emaMinGap:F4} | ADX={emaAdx:F1}")
                            side = OrderSide.Sell
                        Else
                            Log($"Bar checked — EMA{_strategy.IndicatorPeriod}={fastNow:F2} EMA{_strategy.SecondaryPeriod}={slowNow:F2} ADX={emaAdx:F1} minGap={emaMinGap:F4} | no signal ({remStr})")
                        End If

                    Case StrategyConditionType.EmaRsiWeightedScore
                        ' Seven-signal weighted scoring — mirrors EmaRsiSignalProvider in backtest.
                        ' EMA periods fixed (EMA21/EMA50); RSI period from _strategy.IndicatorPeriod (default 14).
                        Dim ema21Vals = TechnicalIndicators.EMA(closes, 21)
                        Dim ema50Vals = TechnicalIndicators.EMA(closes, 50)
                        Dim rsiPeriod = If(_strategy.IndicatorPeriod > 0, _strategy.IndicatorPeriod, 14)
                        Dim rsi14Vals = TechnicalIndicators.RSI(closes, rsiPeriod)

                        Dim ema21Now = TechnicalIndicators.LastValid(ema21Vals)
                        Dim ema21Prev = TechnicalIndicators.PreviousValid(ema21Vals)
                        Dim ema50Now = TechnicalIndicators.LastValid(ema50Vals)
                        Dim rsiVal = TechnicalIndicators.LastValid(rsi14Vals)
                        Dim lastClose = CDec(lastBar.Close)
                        _currentEma21 = CDec(ema21Now)    ' snapshot for pullback scale-in guard

                        ' 3-bar EMA21 slope for Signal 5
                        Dim ema21ThreeBack = If(ema21Vals.Length >= 4, ema21Vals(ema21Vals.Length - 4), Single.NaN)
                        Dim ema21SlopeValid = Not Single.IsNaN(ema21ThreeBack) AndAlso ema21ThreeBack > 0.0F
                        Dim ema21Slope = If(ema21SlopeValid, (ema21Now - ema21ThreeBack) / ema21ThreeBack, 0.0F)

                        ' Volume ratio for Signal 7 (0 when volume absent/zero for futures bars)
                        Dim volRatioVal As Single = 0F
                        If bars.Count >= 20 Then
                            Dim avgVol = bars.Skip(bars.Count - 20).Average(Function(b) CDbl(b.Volume))
                            If avgVol > 0 AndAlso lastBar.Volume > 0 Then
                                volRatioVal = CSng(lastBar.Volume / avgVol)
                            End If
                        End If

                        ' Accumulate bull score
                        Dim bullScore As Double = 0

                        ' 1. EMA21 vs EMA50 crossover — 25 pts (requires ≥0.05% separation)
                        If ema21Now > ema50Now * 1.0005 Then bullScore += 25
                        ' 2. Price vs EMA21 — 20 pts
                        If lastClose > CDec(ema21Now) Then bullScore += 20
                        ' 3. Price vs EMA50 — 15 pts
                        If lastClose > CDec(ema50Now) Then bullScore += 15
                        ' 4. RSI zone scoring — 3 tiers
                        If rsiVal >= 55 AndAlso rsiVal < 70 Then
                            bullScore += 20    ' confirmed bullish trend zone
                        ElseIf rsiVal >= 70 Then
                            bullScore += 12    ' extended/overbought — still bullish, reduced weight
                        ElseIf rsiVal >= 50 Then
                            bullScore += 8     ' mildly bullish, above midline
                        End If
                        bullScore = Math.Max(0.0, Math.Min(100.0, bullScore))
                        ' 5. EMA21 slope over 3 bars — 10 pts
                        If ema21SlopeValid AndAlso ema21Slope > 0.0003F Then bullScore += 10
                        ' 6. Recent 3 candles — 10 pts  (majority green = bullish)
                        Dim lastThree = bars.Skip(bars.Count - 3).ToList()
                        Dim bullCandles = lastThree.Where(Function(b) b.Close >= b.Open).Count()
                        If bullCandles >= 2 Then bullScore += 10
                        ' 7. Volume — 10 pts
                        If volRatioVal > 1.1F Then bullScore += 10

                        ' ── Bear score (independent — not 100 − bullScore) ──────────────
                        Dim bearScore As Double = 0
                        ' 1. EMA21 < EMA50 × 0.9995 — 25 pts
                        If ema21Now < ema50Now * 0.9995 Then bearScore += 25
                        ' 2. Close < EMA21 — 20 pts
                        If lastClose < CDec(ema21Now) Then bearScore += 20
                        ' 3. Close < EMA50 — 15 pts
                        If lastClose < CDec(ema50Now) Then bearScore += 15
                        ' 4. RSI zone scoring — 3 tiers
                        If rsiVal >= 30 AndAlso rsiVal <= 45 Then
                            bearScore += 20    ' confirmed bearish trend zone
                        ElseIf rsiVal < 30 Then
                            bearScore += 12    ' oversold — still bearish, reduced weight
                        ElseIf rsiVal <= 50 Then
                            bearScore += 8     ' mildly bearish, below midline
                        End If
                        bearScore = Math.Max(0.0, Math.Min(100.0, bearScore))
                        ' 5. EMA21 slope falling over 3 bars — 10 pts
                        If ema21SlopeValid AndAlso ema21Slope < -0.0003F Then bearScore += 10
                        ' 6. ≥ 2 of last 3 candles bearish — 10 pts
                        Dim bearCandles = lastThree.Where(Function(b) b.Close < b.Open).Count()
                        If bearCandles >= 2 Then bearScore += 10
                        ' 7. Volume — 10 pts
                        If volRatioVal > 1.1F Then bearScore += 10

                        Dim upPct As Double = bullScore
                        Dim downPct As Double = bearScore
                        rawUpPct = CInt(upPct)
                        rawDownPct = CInt(downPct)
                        Dim minPct As Integer = _strategy.MinConfidencePct  ' user-set threshold (default 85)

                        ' ── ADX trend-strength gate (TICKET-019) ────────────────────────
                        ' ADX trend-strength gate — threshold driven by the active risk profile.
                        ' Lewis (Averse) ≥ 25, Damian (Moderate) ≥ 20, Joe (Aggressive) ≥ 15.
                        ' A signal is only acted on when the market is in a trending phase;
                        ' ADX below the threshold indicates a ranging/consolidating market.
                        Dim dmiResult = TechnicalIndicators.DMI(highs, lows, closes)
                        Dim adxNow = TechnicalIndicators.LastValid(dmiResult.ADX)
                        _lastAdxValue = CSng(adxNow)
                        Dim adxGatePassed = (adxNow >= _strategy.AdxThreshold)

                        Dim atrVals = TechnicalIndicators.ATR(highs, lows, closes, 14)
                        _currentAtrValue = CDec(TechnicalIndicators.LastValid(atrVals))

                        ' Raise ConfidenceUpdated AFTER the ADX gate is known so the UI can
                        ' display the suppressed state (amber ⊘) instead of a misleading green arrow.
                        Dim emaRsiArgs As New ConfidenceUpdatedEventArgs(CInt(upPct), CInt(downPct), adxGatePassed, CSng(adxNow), lastClose) With {
                            .Ema21 = CDec(ema21Now),
                            .Ema50 = CDec(ema50Now),
                            .Rsi14 = CSng(rsiVal),
                            .VolumeRatio = volRatioVal,
                            .Ema21Rising = (ema21Now > ema21Prev),
                            .RecentCandlesBullish = (bullCandles >= 2),
                            .PlusDI = TechnicalIndicators.LastValid(dmiResult.PlusDI),
                            .MinusDI = TechnicalIndicators.LastValid(dmiResult.MinusDI),
                            .TotalConditions = 6,
                            .AdxThreshold = _strategy.AdxThreshold,
                            .MinConfidencePct = _strategy.MinConfidencePct
                        }
                        _latestConfidenceArgs = emaRsiArgs   ' FEAT-01
                        RaiseEvent ConfidenceUpdated(Me, emaRsiArgs)

                        If _currentAtrValue <= 0 Then
                            Log($"Bar checked — EMA/RSI: ATR too low (degenerate bar) — signal suppressed | {remStr}")
                        ElseIf Not adxGatePassed Then
                            Log($"Bar checked — ADX={adxNow:F1} < {_strategy.AdxThreshold:F0} (ranging market) — signal suppressed | EMA/RSI: UP={upPct:F0}% DOWN={downPct:F0}% | ATR={_currentAtrValue:F4} | {remStr}")
                        ElseIf upPct >= minPct Then
                            _pendingConfidencePct = CInt(upPct)
                            Log($"✅ EMA/RSI weighted: UP={upPct:F0}% ≥ {minPct}% — LONG signal! Close={lastClose:F2} EMA21={ema21Now:F2} EMA50={ema50Now:F2} RSI={rsiVal:F1} ADX={adxNow:F1}")
                            side = OrderSide.Buy
                        ElseIf downPct >= minPct Then
                            _pendingConfidencePct = CInt(downPct)
                            Log($"✅ EMA/RSI weighted: DOWN={downPct:F0}% ≥ {minPct}% — SHORT signal! Close={lastClose:F2} EMA21={ema21Now:F2} EMA50={ema50Now:F2} RSI={rsiVal:F1} ADX={adxNow:F1}")
                            side = OrderSide.Sell
                        Else
                            Log($"Bar checked — EMA/RSI: UP={upPct:F0}% DOWN={downPct:F0}% | no signal (need ≥{minPct}%) | EMA21={ema21Now:F2} EMA50={ema50Now:F2} RSI={rsiVal:F1} ADX={adxNow:F1} | {remStr}")
                        End If

                    Case StrategyConditionType.MultiConfluence
                        ' STRAT-23: TOD gate — suppress new entries during CME daily maintenance (17:00–18:00 ET).
                        ' MultiConfluenceStrategy.Evaluate() has no session awareness.  This upstream check
                        ' in the engine is the single TOD gate for all MC signals.
                        ' For news/roll blackouts, see STRAT-23 open items in Open_TICKETS.md.
                        Dim nowEt As DateTimeOffset
                        Try
                            Dim etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")
                            nowEt = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, etZone)
                        Catch
                            nowEt = DateTimeOffset.UtcNow.AddHours(-5)  ' UTC-5 fallback
                        End Try
                        Dim todHour = nowEt.Hour
                        Dim isCmeMaintenance = (todHour = 17)   ' 17:00–17:59 ET = CME daily maintenance window
                        If isCmeMaintenance AndAlso Not _positionOpen Then
                            Log($"⏸  TOD gate: CME daily maintenance window (17:xx ET) — new entries suppressed | {remStr}")
                        End If

                        Dim mcResult = MultiConfluenceStrategy.Evaluate(highs, lows, closes, volumes, adxThreshold:=_strategy.AdxThreshold, macdHistMinAtrFraction:=_strategy.MacdHistMinAtrFraction)
                        _currentAtrValue = mcResult.AtrValue
                        _lastAdxValue = CSng(mcResult.AdxValue)
                        _mcCloudSlPrice = Nothing   ' reset; will be set only when a signal fires
                        _mcPartialSignal = False    ' reset each bar

                        ' Raise live confidence telemetry every bar regardless of signal state
                        Dim mcArgs As New ConfidenceUpdatedEventArgs(mcResult.BullScore, mcResult.BearScore, adxValue:=mcResult.AdxValue, lastClose:=CDec(lastBar.Close)) With {
                            .Cloud1 = mcResult.Cloud1,
                            .Cloud2 = mcResult.Cloud2,
                            .Tenkan = mcResult.Tenkan,
                            .Kijun = mcResult.Kijun,
                            .Ema21 = mcResult.Ema21,
                            .Ema50 = mcResult.Ema50,
                            .PlusDI = mcResult.PlusDI,
                            .MinusDI = mcResult.MinusDI,
                            .MacdHist = mcResult.MacdHist,
                            .MacdHistPrev = mcResult.MacdHistPrev,
                            .StochRsiK = mcResult.StochRsiK,
                            .LongCount = mcResult.LongCount,
                            .ShortCount = mcResult.ShortCount,
                            .TotalConditions = 7,
                            .AdxThreshold = _strategy.AdxThreshold,
                            .MinConfidencePct = _strategy.MinConfidencePct
                        }
                        RaiseEvent ConfidenceUpdated(Me, mcArgs)
                        _latestConfidenceArgs = mcArgs   ' FEAT-01

                        ' ── Live 15-second display refresh ───────────────────────────────────
                        ' Fetch the latest 15-second bar close and substitute it as the last
                        ' bar in the series so Close, Tenkan, Kijun, EMA21/50, ADX and Cloud
                        ' columns in the Hydra grid update intra-bar rather than waiting for
                        ' the next strategy-timeframe bar close.
                        ' Signal evaluation always uses the unmodified strategy-timeframe bars
                        ' above — this refresh is display-only and does not affect entry logic.
                        If isMcFlat Then
                            Try
                                Dim live15sBars = Await _ingestionService.GetLiveBarsAsync(
                                    _strategy.ContractId, BarTimeframe.FifteenSecond, 3, ct)
                                If live15sBars IsNot Nothing AndAlso live15sBars.Count > 0 Then
                                    Dim liveClose = live15sBars.Last().Close
                                    If liveClose > 0D Then
                                        ' Build a working copy of the bar arrays with the live close
                                        ' substituted for the last element.
                                        Dim liveCloses = New List(Of Decimal)(closes)
                                        liveCloses(liveCloses.Count - 1) = liveClose
                                        Dim liveResult = MultiConfluenceStrategy.Evaluate(
                                            highs, lows, liveCloses, volumes,
                                            adxThreshold:=_strategy.AdxThreshold,
                                            macdHistMinAtrFraction:=_strategy.MacdHistMinAtrFraction)
                                        Dim displayArgs As New ConfidenceUpdatedEventArgs(
                                            mcArgs.UpPct, mcArgs.DownPct,
                                            adxValue:=liveResult.AdxValue,
                                            lastClose:=liveClose) With {
                                            .Cloud1 = liveResult.Cloud1,
                                            .Cloud2 = liveResult.Cloud2,
                                            .Tenkan = liveResult.Tenkan,
                                            .Kijun = liveResult.Kijun,
                                            .Ema21 = liveResult.Ema21,
                                            .Ema50 = liveResult.Ema50,
                                            .PlusDI = liveResult.PlusDI,
                                            .MinusDI = liveResult.MinusDI,
                                            .MacdHist = mcArgs.MacdHist,
                                            .MacdHistPrev = mcArgs.MacdHistPrev,
                                            .StochRsiK = mcArgs.StochRsiK,
                                            .LongCount = mcArgs.LongCount,
                                            .ShortCount = mcArgs.ShortCount,
                                            .TotalConditions = 7,
                                            .AdxThreshold = _strategy.AdxThreshold,
                                            .MinConfidencePct = _strategy.MinConfidencePct,
                                            .IsDisplayOnly = True
                                        }
                                        RaiseEvent ConfidenceUpdated(Me, displayArgs)
                                    End If
                                End If
                            Catch ex As Exception
                                ' Non-fatal: live display refresh is best-effort.
                                _logger.LogDebug(ex, "MC live display refresh failed for {Id}", _strategy.ContractId)
                            End Try
                        End If

                        ' ── STRAT-05: Confluence dissolution exit
                        ' On each new bar while a position is open, check how many conditions
                        ' remain active in the entry direction.  If conviction has decayed below
                        ' the threshold, flatten the position before checking for a new signal.
                        Const DissolutionThreshold As Integer = 3
                        If _positionOpen AndAlso isNewBar Then
                            Dim activeCount = If(_lastEntrySide = OrderSide.Buy, mcResult.LongCount, mcResult.ShortCount)
                            If activeCount < DissolutionThreshold Then
                                Log($"⚠️  Confluence dissolved ({activeCount}/9 conditions active) — flattening position")
                                Await DoNeutralFlattenAsync(ct)
                                Return
                            End If
                        End If

                        If mcResult.Side.HasValue Then
                            Dim mcSide = mcResult.Side.Value
                            _pendingConfidencePct = 100
                            _mcCloudSlPrice = mcResult.CloudEdgeSl
                            _mcPartialSignal = mcResult.IsPartialSignal
                            If mcResult.IsPartialSignal Then
                                Dim partialCount = If(mcSide = OrderSide.Buy, mcResult.LongCount, mcResult.ShortCount)
                                If mcSide = OrderSide.Buy Then
                                    Log($"⚡ Multi-Confluence LONG — partial {partialCount}/9 conviction (half-size) | {mcResult.StatusLine} | {remStr}")
                                Else
                                    Log($"⚡ Multi-Confluence SHORT — partial {partialCount}/9 conviction (half-size) | {mcResult.StatusLine} | {remStr}")
                                End If
                            Else
                                If mcSide = OrderSide.Buy Then
                                    Log($"✅ Multi-Confluence LONG — all 9 conditions met! {mcResult.StatusLine} | {remStr}")
                                Else
                                    Log($"✅ Multi-Confluence SHORT — all 9 conditions met! {mcResult.StatusLine} | {remStr}")
                                End If
                            End If
                            side = mcSide
                            ' STRAT-23: suppress new entry (not position monitoring) during CME maintenance
                            If isCmeMaintenance Then side = Nothing
                        Else
                            If mcResult.StatusLine.Contains("Warming up") Then
                                Log($"⏳ Multi-Confluence warming up — {mcResult.StatusLine} | {remStr}")
                            End If
                        End If

                    Case StrategyConditionType.LultDivergence
                        _lultTriggerExtreme = Nothing   ' reset each tick; set only when signal fires
                        Dim lultOpens = bars.Select(Function(b) b.Open).ToList()
                        Dim lultAtrVals = TechnicalIndicators.ATR(highs, lows, closes, 14)
                        _currentAtrValue = CDec(TechnicalIndicators.LastValid(lultAtrVals))
                        Dim lultResult = LultDivergenceStrategy.Evaluate(highs, lows, closes, lultOpens)
                        ' Raise live confidence telemetry every bar regardless of signal state
                        RaiseEvent ConfidenceUpdated(Me, New ConfidenceUpdatedEventArgs(lultResult.BullScore, lultResult.BearScore, lastClose:=CDec(lastBar.Close)))
                        If Not lultResult.IsInTradingWindow Then
                            Log($"Bar checked — LULT (OUT of EST window): {lultResult.StatusLine} | {remStr}")
                        ElseIf lultResult.Side.HasValue Then
                            Dim lultSide = lultResult.Side.Value
                            _pendingConfidencePct = 100
                            ' SL absolute price: trigger wave extreme ± max(3 NQ ticks = 0.75 pts, 25 % of ATR)
                            Dim tickBuf = If(_currentAtrValue > 0D,
                                         Math.Max(_currentAtrValue * 0.25D, 0.75D), 0.75D)
                            _lultTriggerExtreme = If(lultSide = OrderSide.Buy,
                                                  lultResult.TriggerWaveExtreme - tickBuf,
                                                  lultResult.TriggerWaveExtreme + tickBuf)
                            Dim partialMsg = If(lultResult.PartialTpSwingLevel.HasValue,
                                            $" | Partial TP swing={lultResult.PartialTpSwingLevel.Value:F4}",
                                            String.Empty)
                            If lultSide = OrderSide.Buy Then
                                Log($"✅ LULT LONG — 6/6 steps confirmed! {lultResult.StatusLine} | " &
                                $"AnchorWT1={lultResult.AnchorWt1:F1} TriggerWT1={lultResult.TriggerWt1:F1} " &
                                $"TriggerLow={lultResult.TriggerWaveExtreme:F4} SL≈{_lultTriggerExtreme:F4}{partialMsg} | {remStr}")
                            Else
                                Log($"✅ LULT SHORT — 6/6 steps confirmed! {lultResult.StatusLine} | " &
                                $"AnchorWT1={lultResult.AnchorWt1:F1} TriggerWT1={lultResult.TriggerWt1:F1} " &
                                $"TriggerHigh={lultResult.TriggerWaveExtreme:F4} SL≈{_lultTriggerExtreme:F4}{partialMsg} | {remStr}")
                            End If
                            side = lultSide
                        Else
                            Log($"Bar checked — LULT: {lultResult.StatusLine} | {remStr}")
                        End If

                    Case StrategyConditionType.BbSqueezeScalper
                        ' ── BB Squeeze Scalper ────────────────────────────────────────────
                        ' Dual-mode: Squeeze Breakout (momentum) or Band Bounce (mean-reversion).
                        ' Indicators: BB(12,2.0), BBW, %B, RSI(7), EMA(5), ATR(10).
                        ' Mode A fires when bands are squeezing; Mode B when bands are wide.

                        Const BbPeriod As Integer = 12
                        Const BbMult As Double = 2.0
                        Const BbwSmaPeriod As Integer = 20
                        Const SqueezeConsecutiveBars As Integer = 3

                        Dim bbBands = TechnicalIndicators.BollingerBands(closes, BbPeriod, BbMult)
                        Dim bbwArr = TechnicalIndicators.BollingerBandWidth(closes, BbPeriod, BbMult)
                        Dim pctBArr = TechnicalIndicators.BollingerPercentB(closes, BbPeriod, BbMult)
                        Dim rsi7Arr = TechnicalIndicators.RSI(closes, 7)
                        Dim ema5Arr = TechnicalIndicators.EMA(closes, 5)
                        Dim atr10Arr = TechnicalIndicators.ATR(highs, lows, closes, 10)
                        Dim bbwSmaArr = TechnicalIndicators.SMA(
                        bbwArr.Select(Function(v) If(Single.IsNaN(v), 0D, CDec(v))).ToList(),
                        BbwSmaPeriod)

                        _currentAtrValue = CDec(TechnicalIndicators.LastValid(atr10Arr))

                        Dim bbUpper = CDec(TechnicalIndicators.LastValid(bbBands.Upper))
                        Dim bbLower = CDec(TechnicalIndicators.LastValid(bbBands.Lower))
                        Dim bbMiddle = CDec(TechnicalIndicators.LastValid(bbBands.Middle))
                        Dim pctBNow = CDbl(TechnicalIndicators.LastValid(pctBArr))
                        Dim rsi7Now = CDbl(TechnicalIndicators.LastValid(rsi7Arr))
                        Dim ema5Now = CDbl(TechnicalIndicators.LastValid(ema5Arr))
                        Dim ema5Prev = CDbl(TechnicalIndicators.PreviousValid(ema5Arr))
                        Dim bbwNow = CDbl(TechnicalIndicators.LastValid(bbwArr))
                        Dim bbwSma = CDbl(TechnicalIndicators.LastValid(bbwSmaArr))
                        Dim bbLastClose = CDec(lastBar.Close)

                        ' Count consecutive bars where BBW < SMA(BBW) — squeeze detection
                        Dim squeezeCount As Integer = 0
                        For si = bars.Count - 1 To Math.Max(0, bars.Count - 10) Step -1
                            Dim bwVal = CDbl(bbwArr(si))
                            Dim smaIdx = Math.Min(si, bbwSmaArr.Length - 1)
                            Dim smaVal = CDbl(bbwSmaArr(smaIdx))
                            If Not Double.IsNaN(bwVal) AndAlso Not Double.IsNaN(smaVal) AndAlso
                           smaVal > 0 AndAlso bwVal < smaVal Then
                                squeezeCount += 1
                            Else
                                Exit For
                            End If
                        Next
                        Dim squeezeActive = squeezeCount >= SqueezeConsecutiveBars

                        Dim ema5Rising = ema5Now > ema5Prev

                        ' Bar range metrics for wick filter (Mode B)
                        Dim bbBarRange = CDbl(lastBar.High - lastBar.Low)
                        Dim lowerWick = CDbl(Math.Min(lastBar.Open, lastBar.Close) - lastBar.Low)
                        Dim upperWick = CDbl(lastBar.High - Math.Max(lastBar.Open, lastBar.Close))
                        Dim lowerWickPct = If(bbBarRange > 0, lowerWick / bbBarRange, 0.0)
                        Dim upperWickPct = If(bbBarRange > 0, upperWick / bbBarRange, 0.0)

                        ' ══════════════════════════════════════════════════════════════════
                        ' DIAGNOSTIC SNAPSHOT — built every tick, logged after signal decision.
                        ' Captures indicator state, market micro-structure, and bar noise
                        ' regardless of whether a signal fires or is suppressed.
                        ' ══════════════════════════════════════════════════════════════════
                        _pendingDiagEntry = Nothing
                        Dim diagQuote As Quote = Nothing

                        ' Previous 3 bars noise floor (the "noise" price must overcome to be profitable)
                        Dim diagPrev3 As New List(Of DiagBarSnapshot)
                        For pi As Integer = Math.Max(0, bars.Count - 4) To bars.Count - 2
                            Dim pb = bars(pi)
                            diagPrev3.Add(New DiagBarSnapshot With {
                            .Timestamp = pb.Timestamp.ToString("o"),
                            .Open = pb.Open,
                            .High = pb.High,
                            .Low = pb.Low,
                            .Close = pb.Close,
                            .Range = pb.High - pb.Low,
                            .Body = Math.Abs(pb.Close - pb.Open),
                            .IsBullish = pb.Close >= pb.Open
                        })
                        Next
                        Dim diagAvg3Range As Decimal = If(diagPrev3.Count > 0,
                        diagPrev3.Average(Function(b) b.Range), 0D)

                        Dim diagSpread As Decimal = 0D
                        Dim diagSpreadPct As Decimal = 0D
                        If diagQuote IsNot Nothing AndAlso diagQuote.AskPrice > 0D Then
                            diagSpread = diagQuote.Spread
                            Dim mid = diagQuote.MidPrice
                            If mid > 0D Then diagSpreadPct = Math.Round(diagSpread / mid * 100D, 5)
                        End If

                        Dim diagMid As Decimal = If(diagQuote IsNot Nothing AndAlso diagQuote.MidPrice > 0D,
                                                diagQuote.MidPrice, bbLastClose)
                        Dim diagBid As Decimal = If(diagQuote IsNot Nothing, diagQuote.BidPrice, 0D)
                        Dim diagAsk As Decimal = If(diagQuote IsNot Nothing, diagQuote.AskPrice, 0D)

                        _pendingDiagEntry = New DiagnosticLogEntry With {
                        .TradeId = Guid.NewGuid().ToString("N"),
                        .EventType = "NO_SIGNAL",
                        .Timestamp = DateTimeOffset.UtcNow.ToString("o"),
                        .Symbol = _strategy.ContractId,
                        .Strategy = _strategy.Name,
                        .Action = "NONE",
                        .MetricsAtEntry = New DiagMetricsAtEntry With {
                            .Rsi7 = CDec(rsi7Now),
                            .BbUpper = bbUpper,
                            .BbMiddle = bbMiddle,
                            .BbLower = bbLower,
                            .BbPercentB = CDec(pctBNow),
                            .BbWidth = CDec(bbwNow),
                            .BbWidthSma20 = CDec(bbwSma),
                            .BbSqueezeCount = squeezeCount,
                            .BbSqueezeActive = squeezeActive,
                            .Ema5Now = CDec(ema5Now),
                            .Ema5Prev = CDec(ema5Prev),
                            .Ema5Rising = ema5Rising,
                            .Atr10 = _currentAtrValue,
                            .PriceEntry = bbLastClose,
                            .SpreadBps = If(diagMid > 0D, Math.Round(diagSpread / diagMid * 10000D, 1), 0D),
                            .Bid = diagBid,
                            .Ask = diagAsk,
                            .BarTimestamp = lastBar.Timestamp.ToString("o"),
                            .BarOpen = lastBar.Open,
                            .BarHigh = lastBar.High,
                            .BarLow = lastBar.Low,
                            .BarClose = lastBar.Close,
                            .BarRange = CDec(bbBarRange),
                            .BarLowerWickPct = CDec(lowerWickPct),
                            .BarUpperWickPct = CDec(upperWickPct)
                        },
                        .NoiseCheck = New DiagNoiseCheck With {
                            .Prev3BarAvgRange = diagAvg3Range,
                            .PrevBars = diagPrev3
                        }
                    }
                        ' ── end diagnostic setup ───────────────────────────────────────────

                        If _currentAtrValue <= 0 Then
                            Log($"Bar checked — BB Squeeze: ATR too low (degenerate bar) | {remStr}")
                        ElseIf squeezeActive Then
                            ' ── Mode A: Squeeze Breakout (momentum entry in breakout direction) ──
                            ' RSI gates tightened: ≥60 for long, ≤40 for short (was 50/50 — too loose).
                            ' Band breach requires ≥0.25% penetration beyond band edge (prevents 1-tick fires).
                            Dim modeALongBreak = bbLastClose > bbUpper * 1.0025D
                            Dim modeAShortBreak = bbLastClose < bbLower * 0.9975D
                            If modeALongBreak AndAlso ema5Rising AndAlso rsi7Now >= 60 Then
                                Log($"✅ BB SQUEEZE BREAKOUT LONG! Close={bbLastClose:F4} > Upper={bbUpper:F4}×1.0025 " &
                                $"| BBW={bbwNow:F3} < SMA={bbwSma:F3} ({squeezeCount} bars) " &
                                $"| EMA5↑ RSI7={rsi7Now:F1}≥60")
                                If _pendingDiagEntry IsNot Nothing Then
                                    _pendingDiagEntry.Strategy = $"{_strategy.Name} (Mode A)"
                                    _pendingDiagEntry.Why = $"Mode A LONG ✓ | squeeze={squeezeCount}bars | Close={bbLastClose:F4}>Upper×1.0025={bbUpper * 1.0025D:F4} | EMA5↑({ema5Now:F4}>{ema5Prev:F4}) | RSI7={rsi7Now:F1}≥60"
                                End If
                                side = OrderSide.Buy
                            ElseIf modeAShortBreak AndAlso Not ema5Rising AndAlso rsi7Now <= 40 Then
                                Log($"✅ BB SQUEEZE BREAKOUT SHORT! Close={bbLastClose:F4} < Lower={bbLower:F4}×0.9975 " &
                                $"| BBW={bbwNow:F3} < SMA={bbwSma:F3} ({squeezeCount} bars) " &
                                $"| EMA5↓ RSI7={rsi7Now:F1}≤40")
                                If _pendingDiagEntry IsNot Nothing Then
                                    _pendingDiagEntry.Strategy = $"{_strategy.Name} (Mode A)"
                                    _pendingDiagEntry.Why = $"Mode A SHORT ✓ | squeeze={squeezeCount}bars | Close={bbLastClose:F4}<Lower×0.9975={bbLower * 0.9975D:F4} | EMA5↓({ema5Now:F4}<{ema5Prev:F4}) | RSI7={rsi7Now:F1}≤40"
                                End If
                                side = OrderSide.Sell
                            Else
                                Log($"BB Squeeze ({squeezeCount} bars) — waiting for breakout | " &
                                $"Close={bbLastClose:F4} BB=[{bbLower:F4}—{bbUpper:F4}] " &
                                $"RSI7={rsi7Now:F1} EMA5={ema5Now:F4} | {remStr}")
                                If _pendingDiagEntry IsNot Nothing Then
                                    _pendingDiagEntry.Strategy = $"{_strategy.Name} (Mode A)"
                                    Dim aParts = New List(Of String) From {$"squeeze={squeezeCount}bars"}
                                    If modeALongBreak Then
                                        aParts.Add($"Close={bbLastClose:F4}>Upper×1.0025={bbUpper * 1.0025D:F4}✓")
                                    ElseIf modeAShortBreak Then
                                        aParts.Add($"Close={bbLastClose:F4}<Lower×0.9975={bbLower * 0.9975D:F4}✓")
                                    Else
                                        aParts.Add($"Close={bbLastClose:F4} no ≥0.25% breakout✗")
                                    End If
                                    If ema5Rising Then aParts.Add("EMA5rising✓") Else aParts.Add("EMA5flat/falling✗")
                                    If rsi7Now >= 60 Then
                                        aParts.Add($"RSI7={rsi7Now:F1}≥60✓")
                                    ElseIf rsi7Now <= 40 Then
                                        aParts.Add($"RSI7={rsi7Now:F1}≤40✓")
                                    Else
                                        aParts.Add($"RSI7={rsi7Now:F1} neutral(40-60)✗")
                                    End If
                                    _pendingDiagEntry.Why = "Mode A no-signal | " & String.Join(" | ", aParts)
                                End If
                            End If
                        Else
                            ' ── Mode B: Band Bounce (mean-reversion fade at extremes) ──────────
                            ' %B thresholds tightened: ≤-0.1 for long, ≥1.1 for short (was 0/-1 — fired at band edge).
                            ' Requires price to be meaningfully beyond the band, not just touching it.
                            If pctBNow <= -0.1 AndAlso rsi7Now < 25 AndAlso lowerWickPct >= 0.6 Then
                                Log($"✅ BB BAND BOUNCE LONG! %B={pctBNow:F3}≤-0.1 | RSI7={rsi7Now:F1} < 25 " &
                                $"| Lower wick={lowerWickPct:P0} | BBW={bbwNow:F3} ≥ SMA={bbwSma:F3}")
                                If _pendingDiagEntry IsNot Nothing Then
                                    _pendingDiagEntry.Strategy = $"{_strategy.Name} (Mode B)"
                                    _pendingDiagEntry.Why = $"Mode B LONG ✓ | %B={pctBNow:F3}≤-0.1 | RSI7={rsi7Now:F1}<25 | LowerWick={lowerWickPct:P0}≥60% | BBW={bbwNow:F3}≥SMA={bbwSma:F3}"
                                End If
                                side = OrderSide.Buy
                            ElseIf pctBNow >= 1.1 AndAlso rsi7Now > 75 AndAlso upperWickPct >= 0.6 Then
                                Log($"✅ BB BAND BOUNCE SHORT! %B={pctBNow:F3}≥1.1 | RSI7={rsi7Now:F1} > 75 " &
                                $"| Upper wick={upperWickPct:P0} | BBW={bbwNow:F3} ≥ SMA={bbwSma:F3}")
                                If _pendingDiagEntry IsNot Nothing Then
                                    _pendingDiagEntry.Strategy = $"{_strategy.Name} (Mode B)"
                                    _pendingDiagEntry.Why = $"Mode B SHORT ✓ | %B={pctBNow:F3}≥1.1 | RSI7={rsi7Now:F1}>75 | UpperWick={upperWickPct:P0}≥60% | BBW={bbwNow:F3}≥SMA={bbwSma:F3}"
                                End If
                                side = OrderSide.Sell
                            Else
                                Log($"BB no signal — %B={pctBNow:F3} RSI7={rsi7Now:F1} | " &
                                $"BB=[{bbLower:F4}—{bbMiddle:F4}—{bbUpper:F4}] " &
                                $"BBW={bbwNow:F3} SMA={bbwSma:F3} | {remStr}")
                                If _pendingDiagEntry IsNot Nothing Then
                                    _pendingDiagEntry.Strategy = $"{_strategy.Name} (Mode B)"
                                    Dim bParts = New List(Of String)
                                    If pctBNow <= -0.1 Then
                                        bParts.Add($"pctB={pctBNow:F3}≤-0.1✓")
                                    ElseIf pctBNow >= 1.1 Then
                                        bParts.Add($"pctB={pctBNow:F3}≥1.1✓")
                                    ElseIf pctBNow < 0.0 Then
                                        bParts.Add($"pctB={pctBNow:F3}<0 but >-0.1(insufficient penetration)✗")
                                    ElseIf pctBNow > 1.0 Then
                                        bParts.Add($"pctB={pctBNow:F3}>1 but <1.1(insufficient penetration)✗")
                                    Else
                                        bParts.Add($"pctB={pctBNow:F3} in-band✗")
                                    End If
                                    If rsi7Now < 25 Then
                                        bParts.Add($"RSI7={rsi7Now:F1}<25✓")
                                    ElseIf rsi7Now > 75 Then
                                        bParts.Add($"RSI7={rsi7Now:F1}>75✓")
                                    Else
                                        bParts.Add($"RSI7={rsi7Now:F1} neutral✗")
                                    End If
                                    If lowerWickPct >= 0.6 Then
                                        bParts.Add($"LowerWick={lowerWickPct:P1}>=60pct✓")
                                    ElseIf upperWickPct >= 0.6 Then
                                        bParts.Add($"UpperWick={upperWickPct:P1}>=60pct✓")
                                    Else
                                        bParts.Add($"lo-wick={lowerWickPct:P1} up-wick={upperWickPct:P1} neither>=60pct✗")
                                    End If
                                    _pendingDiagEntry.Why = "Mode B no-signal | " & String.Join(" | ", bParts)
                                End If
                            End If
                        End If

                        ' Emit %B as confidence signal (scaled to 0–100; 50 = middle band)
                        Dim bbPctScaled = CInt(Math.Max(0, Math.Min(100, pctBNow * 100.0)))
                        RaiseEvent ConfidenceUpdated(Me, New ConfidenceUpdatedEventArgs(
                        If(pctBNow >= 0.5, bbPctScaled, 100 - bbPctScaled),
                        If(pctBNow < 0.5, bbPctScaled, 100 - bbPctScaled),
                        adxValue:=CSng(bbwNow),
                        lastClose:=bbLastClose))

                    Case StrategyConditionType.VidyaCross
                        ' ── VIDYA Cross ─────────────────────────────────────────────────────
                        ' Long  when close crosses ABOVE the VIDYA line AND 6-bar ΔVol ≥ +20%
                        ' Short when close crosses BELOW the VIDYA line AND 6-bar ΔVol ≤ −20%
                        ' Confidence = |ΔVol| × 100  (30% delta → confidence 30, 75% → 75)
                        ' The ±20% hard gate ensures only meaningful buying/selling conviction fires.

                        Dim vidyaLength = If(_strategy.IndicatorPeriod > 0, _strategy.IndicatorPeriod, 14)
                        Dim cmoLen = If(_strategy.SecondaryPeriod > 0, _strategy.SecondaryPeriod, 9)
                        Dim vidyaArr = TechnicalIndicators.VIDYA(closes, vidyaLength, cmoLen)
                        Dim cmoArr = TechnicalIndicators.CMO(closes, cmoLen)   ' retained for diagnostics
                        Dim deltaArr = TechnicalIndicators.DeltaVolume(closes, opens, volumes)

                        Dim vidyaNow = CDec(TechnicalIndicators.LastValid(vidyaArr))
                        Dim vidyaPrev = CDec(TechnicalIndicators.PreviousValid(vidyaArr))
                        Dim closeNow = CDec(lastBar.Close)
                        Dim closePrev = If(bars.Count >= 2, CDec(bars(bars.Count - 2).Close), closeNow)
                        Dim cmoNow = CDbl(TechnicalIndicators.LastValid(cmoArr))
                        Dim deltaNow = CDbl(TechnicalIndicators.LastValid(deltaArr))
                        _currentAtrValue = CDec(TechnicalIndicators.LastValid(TechnicalIndicators.ATR(highs, lows, closes, 14)))

                        ' Confidence = |ΔVol| × 100 (volume conviction, not CMO momentum)
                        Const VolThreshold As Double = 0.2   ' ±20% delta required to fire
                        Dim vidyaConfidence = CInt(Math.Min(100, Math.Abs(deltaNow) * 100))
                        RaiseEvent ConfidenceUpdated(Me, New ConfidenceUpdatedEventArgs(
                            If(deltaNow >= VolThreshold, vidyaConfidence, 0),
                            If(deltaNow <= -VolThreshold, vidyaConfidence, 0),
                            adxValue:=CSng(deltaNow),
                            lastClose:=closeNow) With {
                            .TotalConditions = -1,
                            .VidyaValue = vidyaNow,
                            .CmoValue = CSng(cmoNow),
                            .DeltaVol = CSng(deltaNow)
                        })

                        ' Compute ADX for VIDYA trend-strength gate
                        Dim vidyaDmi = TechnicalIndicators.DMI(highs, lows, closes)
                        Dim vidyaAdx = TechnicalIndicators.LastValid(vidyaDmi.ADX)

                        ' VIDYA gap: require close to clear VIDYA by ≥0.1% — prevents single-tick crossover fires
                        Dim vidyaLongOk = closeNow > vidyaNow * 1.001D AndAlso closePrev <= vidyaPrev * 1.001D
                        Dim vidyaShortOk = closeNow < vidyaNow * 0.999D AndAlso closePrev >= vidyaPrev * 0.999D

                        If vidyaNow = 0 OrElse vidyaPrev = 0 Then
                            Log($"Bar checked — VIDYA: insufficient data | {remStr}")
                        ElseIf vidyaAdx < _strategy.AdxThreshold Then
                            Log($"Bar checked — VIDYA: ADX={vidyaAdx:F1} < {_strategy.AdxThreshold:F0} (ranging market) | Close={closeNow:F4} VIDYA={vidyaNow:F4} | {remStr}")
                        ElseIf vidyaLongOk Then
                            ' Price crossed above VIDYA with ≥0.1% gap — require ≥ +20% volume delta
                            If deltaNow >= VolThreshold Then
                                _pendingConfidencePct = vidyaConfidence
                                Log($"✅ VIDYA CROSS LONG! Close={closeNow:F4} > VIDYA={vidyaNow:F4}×1.001 " &
                                    $"| ΔVol={deltaNow:P0}✓ (conf={vidyaConfidence}) | ADX={vidyaAdx:F1} | CMO={cmoNow:F3} | ATR={_currentAtrValue:F4} | {remStr}")
                                side = OrderSide.Buy
                            Else
                                Log($"⚠ VIDYA cross long filtered — ΔVol={deltaNow:P0} < +20% threshold " &
                                    $"| Close={closeNow:F4} VIDYA={vidyaNow:F4} | {remStr}")
                            End If
                        ElseIf vidyaShortOk Then
                            ' Price crossed below VIDYA with ≥0.1% gap — require ≤ −20% volume delta
                            If deltaNow <= -VolThreshold Then
                                _pendingConfidencePct = vidyaConfidence
                                Log($"✅ VIDYA CROSS SHORT! Close={closeNow:F4} < VIDYA={vidyaNow:F4}×0.999 " &
                                    $"| ΔVol={deltaNow:P0}✓ (conf={vidyaConfidence}) | ADX={vidyaAdx:F1} | CMO={cmoNow:F3} | ATR={_currentAtrValue:F4} | {remStr}")
                                side = OrderSide.Sell
                            Else
                                Log($"⚠ VIDYA cross short filtered — ΔVol={deltaNow:P0} > −20% threshold " &
                                    $"| Close={closeNow:F4} VIDYA={vidyaNow:F4} | {remStr}")
                            End If
                        Else
                            Log($"Bar checked — VIDYA={vidyaNow:F4} Close={closeNow:F4} ΔVol={deltaNow:P0} ADX={vidyaAdx:F1} CMO={cmoNow:F3} | {remStr}")
                        End If

                    Case StrategyConditionType.NakedTrader
                        ' ── Naked Trader ────────────────────────────────────────────────────
                        ' 4-vote consensus: EMA(9/21), MACD(8,17,9), DMI/ADX(14), VWAP on 5-min bars.                        ' High confidence → signal fires (confidence = 90)
                        ' Medium confidence → signal fires with reduced confidence (confidence = 60)
                        ' Low confidence or tie → no signal this bar

                        If bars.Count < NakedTraderAnalyser.MIN_BARS Then
                            Log($"Bar checked — Naked Trader: insufficient bars ({bars.Count}/{NakedTraderAnalyser.MIN_BARS}) | {remStr}")
                        Else
                            If bars.Count < NakedTraderAnalyser.RECOMMENDED_BARS Then
                                Log($"⚠ Naked Trader: only {bars.Count} bars (recommend {NakedTraderAnalyser.RECOMMENDED_BARS}+) — ADX may be unstable")
                            End If

                            Dim snap = NakedTraderAnalyser.Analyse(bars)
                            _currentAtrValue = CDec(TechnicalIndicators.LastValid(TechnicalIndicators.ATR(highs, lows, closes, 14)))

                            Dim ntConfidence As Integer = 0
                            If snap.Confidence = TrendConfidence.High Then
                                ntConfidence = 90
                            ElseIf snap.Confidence = TrendConfidence.Medium Then
                                ntConfidence = 60
                            End If

                            ' Emit confidence so coordinator / Hydra can react even when no trade fires
                            Dim longConf = If(snap.Confidence >= TrendConfidence.Medium AndAlso snap.Direction = TrendDirection.Up, ntConfidence, 0)
                            Dim shortConf = If(snap.Confidence >= TrendConfidence.Medium AndAlso snap.Direction = TrendDirection.Down, ntConfidence, 0)
                            RaiseEvent ConfidenceUpdated(Me, New ConfidenceUpdatedEventArgs(
                                longConf, shortConf,
                                adxValue:=snap.Adx,
                                lastClose:=snap.LastClose))

                            If snap.Confidence = TrendConfidence.Low Then
                                Log($"Bar checked — Naked Trader: LOW conf | {snap.Summary} | ATR={_currentAtrValue:F4} | {remStr}")
                            ElseIf snap.Direction = TrendDirection.Up Then
                                _pendingConfidencePct = ntConfidence
                                Log($"✅ Naked Trader LONG! {snap.Summary} | conf={ntConfidence} ATR={_currentAtrValue:F4} | {remStr}")
                                side = OrderSide.Buy
                            Else
                                _pendingConfidencePct = ntConfidence
                                Log($"✅ Naked Trader SHORT! {snap.Summary} | conf={ntConfidence} ATR={_currentAtrValue:F4} | {remStr}")
                                side = OrderSide.Sell
                            End If
                        End If

                    Case StrategyConditionType.DoubleBubbleButt
                        ' ── Double Bubble Butt ──────────────────────────────────────────────
                        ' Two BB sets over SMA(20): inner ±1.0 SD, outer ±2.0 SD.
                        ' Long  when close enters Buy Zone  (close > upper inner 1.0 SD band).
                        ' Short when close enters Sell Zone (close < lower inner 1.0 SD band).
                        ' Neutral Zone (between inner bands) → no signal / stay flat.
                        Dim dbbInner = TechnicalIndicators.BollingerBands(closes, 20, 1.0)
                        Dim dbbInnerUp = CDec(TechnicalIndicators.LastValid(dbbInner.Upper))
                        Dim dbbInnerLow = CDec(TechnicalIndicators.LastValid(dbbInner.Lower))
                        Dim dbbOuter = TechnicalIndicators.BollingerBands(closes, 20, 2.0)
                        Dim dbbOuterUp = CDec(TechnicalIndicators.LastValid(dbbOuter.Upper))
                        Dim dbbOuterLow = CDec(TechnicalIndicators.LastValid(dbbOuter.Lower))
                        Dim dbbAtrVals = TechnicalIndicators.ATR(highs, lows, closes, 20)
                        _currentAtrValue = CDec(TechnicalIndicators.LastValid(dbbAtrVals))

                        Dim dbbClose = CDec(lastBar.Close)
                        If dbbInnerUp = 0D OrElse dbbInnerLow = 0D Then
                            Log($"Bar checked — DBB: bands not ready | {remStr}")
                        ElseIf dbbClose > dbbInnerUp Then
                            ' Price is in Buy Zone
                            Log($"✅ DBB LONG! Close={dbbClose:F4} > upper 1-SD={dbbInnerUp:F4} (Buy Zone) | outer=[{dbbOuterLow:F4}–{dbbOuterUp:F4}] | ATR={_currentAtrValue:F4} | {remStr}")
                            side = OrderSide.Buy
                        ElseIf dbbClose < dbbInnerLow Then
                            ' Price is in Sell Zone
                            Log($"✅ DBB SHORT! Close={dbbClose:F4} < lower 1-SD={dbbInnerLow:F4} (Sell Zone) | outer=[{dbbOuterLow:F4}–{dbbOuterUp:F4}] | ATR={_currentAtrValue:F4} | {remStr}")
                            side = OrderSide.Sell
                        Else
                            ' Price is in Neutral Zone — no trade
                            Log($"Bar checked — DBB: Neutral Zone | Close={dbbClose:F4} inner=[{dbbInnerLow:F4}–{dbbInnerUp:F4}] | {remStr}")
                        End If

                    Case StrategyConditionType.OpeningRangeBreakout
                        ' ── Opening Range Breakout ────────────────────────────────────────────
                        ' The first 30 minutes of the session establish OR high/low.
                        ' Entry: close breaks OR with volume ≥ 1.2× 20-bar avg; SL = opposite OR extreme;
                        ' TP = 1.5× OR width.  No-trade: OR > 2× ATR, or past session midpoint.
                        Dim orbMinutes = _strategy.TimeframeMinutes
                        Dim orbBarCount = Math.Max(1, If(orbMinutes > 0, 30 \ orbMinutes, 6))
                        Dim orbAtrVals = TechnicalIndicators.ATR(highs, lows, closes, 14)
                        _currentAtrValue = CDec(TechnicalIndicators.LastValid(orbAtrVals))

                        Dim orbDate = lastBar.Timestamp.Date
                        Dim orbSessionStart = bars.Count - 1
                        Do While orbSessionStart > 0 AndAlso bars(orbSessionStart - 1).Timestamp.Date = orbDate
                            orbSessionStart -= 1
                        Loop
                        Dim orbSessionBarCount = bars.Count - orbSessionStart
                        Dim orbEndBarIdx = orbSessionStart + orbBarCount - 1  ' index within bars list

                        If bars.Count - 1 <= orbEndBarIdx Then
                            Log($"Bar checked — ORB: building opening range ({orbSessionBarCount}/{orbBarCount} bars in session) | {remStr}")
                        ElseIf orbSessionBarCount > orbBarCount * 4 Then
                            ' Past session midpoint approximation: more than 4× the OR duration has elapsed
                            Log($"Bar checked — ORB: past session midpoint ({orbSessionBarCount} bars into session) — entries suppressed | {remStr}")
                        Else
                            ' Compute OR high/low from session-start bars
                            Dim orbHigh As Decimal = Decimal.MinValue
                            Dim orbLow As Decimal = Decimal.MaxValue
                            For oi = orbSessionStart To Math.Min(orbEndBarIdx, bars.Count - 1)
                                orbHigh = Math.Max(orbHigh, bars(oi).High)
                                orbLow = Math.Min(orbLow, bars(oi).Low)
                            Next
                            Dim orbWidth = orbHigh - orbLow

                            If _currentAtrValue > 0D AndAlso orbWidth > _currentAtrValue * 2D Then
                                Log($"Bar checked — ORB: range too wide (OR={orbWidth:F4} > 2×ATR={_currentAtrValue * 2D:F4}) | {remStr}")
                            Else
                                ' Volume gate
                                Dim orbVolGate = True
                                If bars.Count >= 20 Then
                                    Dim avgVol = bars.Skip(bars.Count - 20).Average(Function(b) CDbl(b.Volume))
                                    If avgVol > 0 AndAlso lastBar.Volume > 0 Then
                                        orbVolGate = (lastBar.Volume >= avgVol * 1.2)
                                    End If
                                End If
                                Dim orbClose = CDec(lastBar.Close)
                                If Not orbVolGate Then
                                    Log($"Bar checked — ORB: volume gate failed | Close={orbClose:F4} OR=[{orbLow:F4}–{orbHigh:F4}] | {remStr}")
                                ElseIf orbClose > orbHigh Then
                                    Log($"✅ ORB LONG! Close={orbClose:F4} > OR High={orbHigh:F4} | OR=[{orbLow:F4}–{orbHigh:F4}] Width={orbWidth:F4} ATR={_currentAtrValue:F4} | {remStr}")
                                    side = OrderSide.Buy
                                ElseIf orbClose < orbLow Then
                                    Log($"✅ ORB SHORT! Close={orbClose:F4} < OR Low={orbLow:F4} | OR=[{orbLow:F4}–{orbHigh:F4}] Width={orbWidth:F4} ATR={_currentAtrValue:F4} | {remStr}")
                                    side = OrderSide.Sell
                                Else
                                    Log($"Bar checked — ORB: no breakout | Close={orbClose:F4} OR=[{orbLow:F4}–{orbHigh:F4}] ATR={_currentAtrValue:F4} | {remStr}")
                                End If
                            End If
                        End If

                    Case StrategyConditionType.VwapMeanReversion
                        ' ── VWAP Mean Reversion ──────────────────────────────────────────────
                        ' Institutional midday strategy (10am–2pm ET).
                        ' Session-anchored VWAP ± rolling 20-bar stddev bands.
                        ' Long  when close ≤ VWAP − 1.5 SD; Short when close ≥ VWAP + 1.5 SD.
                        ' Exit at VWAP (mean reversion target); SL beyond the 2.0 SD band.
                        Const VmrWindowStartUtcHour As Integer = 15   ' 10:00 ET = 15:00 UTC
                        Const VmrWindowEndUtcHour As Integer = 19     ' 14:00 ET = 19:00 UTC
                        Dim vmrHour = lastBar.Timestamp.ToUniversalTime().Hour
                        If vmrHour < VmrWindowStartUtcHour OrElse vmrHour >= VmrWindowEndUtcHour Then
                            Log($"Bar checked — VWAP MR: outside 10am–2pm ET window | {remStr}")
                        ElseIf bars.Count < 20 Then
                            Log($"Bar checked — VWAP MR: insufficient bars ({bars.Count}/20) | {remStr}")
                        Else
                            ' Session-anchored VWAP
                            Dim vmrDate = lastBar.Timestamp.Date
                            Dim cumPV As Double = 0
                            Dim cumVol As Double = 0
                            For Each b In bars.Where(Function(x) x.Timestamp.Date = vmrDate)
                                Dim tp = (CDbl(b.High) + CDbl(b.Low) + CDbl(b.Close)) / 3.0
                                cumPV += tp * CDbl(b.Volume)
                                cumVol += CDbl(b.Volume)
                            Next
                            Dim vwapVal As Decimal = If(cumVol > 0, CDec(cumPV / cumVol), CDec(lastBar.Close))

                            ' Rolling 20-bar standard deviation of (close − VWAP)
                            Dim recent = bars.Skip(Math.Max(0, bars.Count - 20)).ToList()
                            Dim variance As Double = 0
                            For Each b In recent
                                Dim diff = CDbl(b.Close) - CDbl(vwapVal)
                                variance += diff * diff
                            Next
                            Dim sd = CDec(Math.Sqrt(variance / recent.Count))

                            Dim vmrLo15 = vwapVal - 1.5D * sd
                            Dim vmrHi15 = vwapVal + 1.5D * sd
                            Dim vmrLo2  = vwapVal - 2.0D * sd
                            Dim vmrHi2  = vwapVal + 2.0D * sd
                            Dim vmrClose = CDec(lastBar.Close)

                            _currentAtrValue = CDec(TechnicalIndicators.LastValid(TechnicalIndicators.ATR(highs, lows, closes, 14)))

                            If vmrClose <= vmrLo15 Then
                                Log($"✅ VWAP MR LONG! Close={vmrClose:F4} ≤ VWAP−1.5SD={vmrLo15:F4} | VWAP={vwapVal:F4} SD={sd:F4} | ATR={_currentAtrValue:F4} | {remStr}")
                                side = OrderSide.Buy
                            ElseIf vmrClose >= vmrHi15 Then
                                Log($"✅ VWAP MR SHORT! Close={vmrClose:F4} ≥ VWAP+1.5SD={vmrHi15:F4} | VWAP={vwapVal:F4} SD={sd:F4} | ATR={_currentAtrValue:F4} | {remStr}")
                                side = OrderSide.Sell
                            Else
                                Log($"Bar checked — VWAP MR: inside bands | Close={vmrClose:F4} bands=[{vmrLo15:F4}–{vmrHi15:F4}] VWAP={vwapVal:F4} | {remStr}")
                            End If
                        End If

                    Case StrategyConditionType.SuperTrendAdx
                        ' ── SuperTrend + ADX ─────────────────────────────────────────────────
                        ' Long  when SuperTrend direction = +1, ADX ≥ AdxThreshold, and +DI > -DI.
                        ' Short when SuperTrend direction = -1, ADX ≥ AdxThreshold, and -DI > +DI.
                        ' SL = SuperTrend line at entry (ATR bracket applied by PlaceBracketOrdersAsync).
                        Dim stResult = TechnicalIndicators.SuperTrend(highs, lows, closes, period:=10, multiplier:=3.0)
                        Dim stDir = stResult.Direction(stResult.Direction.Length - 1)
                        Dim stLine = CDec(stResult.Line(stResult.Line.Length - 1))
                        Dim stDmi = TechnicalIndicators.DMI(highs, lows, closes)
                        Dim stAdx = TechnicalIndicators.LastValid(stDmi.ADX)
                        Dim stPlusDi = TechnicalIndicators.LastValid(stDmi.PlusDI)
                        Dim stMinusDi = TechnicalIndicators.LastValid(stDmi.MinusDI)
                        Dim stAtrVals = TechnicalIndicators.ATR(highs, lows, closes, 14)
                        _currentAtrValue = CDec(TechnicalIndicators.LastValid(stAtrVals))
                        _lastAdxValue = CSng(stAdx)

                        Dim stLongConf = If(stDir > 0.0F AndAlso stPlusDi > stMinusDi, 100, 0)
                        Dim stShortConf = If(stDir < 0.0F AndAlso stMinusDi > stPlusDi, 100, 0)
                        RaiseEvent ConfidenceUpdated(Me, New ConfidenceUpdatedEventArgs(
                            stLongConf, stShortConf,
                            adxGatePassed:=(stAdx >= _strategy.AdxThreshold),
                            adxValue:=CSng(stAdx),
                            lastClose:=CDec(lastBar.Close)) With {
                            .PlusDI = CSng(stPlusDi),
                            .MinusDI = CSng(stMinusDi),
                            .AdxThreshold = _strategy.AdxThreshold,
                            .MinConfidencePct = _strategy.MinConfidencePct
                        })

                        Dim stIsFlip As Boolean = (stDir <> _stPrevDirection AndAlso _stPrevDirection <> 0.0F)
                        _stPrevDirection = stDir

                        If _currentAtrValue <= 0 Then
                            Log($"Bar checked — SuperTrend+: ATR too low (degenerate bar) | {remStr}")
                        ElseIf stDir = 0.0F OrElse Single.IsNaN(stDir) Then
                            Log($"Bar checked — SuperTrend+: direction warming up | ADX={stAdx:F1} | {remStr}")
                        ElseIf stAdx < _strategy.AdxThreshold Then
                            Log($"Bar checked — SuperTrend+: ADX={stAdx:F1} < {_strategy.AdxThreshold:F0} (ranging market) | " &
                                $"dir={If(stDir > 0, "UP", "DOWN")} ST={stLine:F4} +DI={stPlusDi:F1} -DI={stMinusDi:F1} | {remStr}")
                        ElseIf stDir > 0.0F AndAlso stPlusDi > stMinusDi Then
                            _pendingConfidencePct = 100
                            Dim prefix = If(stIsFlip, "✅", "→")
                            Log($"{prefix} SuperTrend+ LONG! dir=UP ST={stLine:F4} | ADX={stAdx:F1} +DI={stPlusDi:F1} > -DI={stMinusDi:F1} | ATR={_currentAtrValue:F4} | {remStr}")
                            side = OrderSide.Buy
                        ElseIf stDir < 0.0F AndAlso stMinusDi > stPlusDi Then
                            _pendingConfidencePct = 100
                            Dim prefix2 = If(stIsFlip, "✅", "→")
                            Log($"{prefix2} SuperTrend+ SHORT! dir=DOWN ST={stLine:F4} | ADX={stAdx:F1} -DI={stMinusDi:F1} > +DI={stPlusDi:F1} | ATR={_currentAtrValue:F4} | {remStr}")
                            side = OrderSide.Sell
                        Else
                            Log($"Bar checked — SuperTrend+: dir={If(stDir > 0, "UP", "DOWN")} ST={stLine:F4} " &
                                $"ADX={stAdx:F1} +DI={stPlusDi:F1} -DI={stMinusDi:F1} | DI confirmation missing | {remStr}")
                        End If

                    Case Else
                        Log($"Condition '{activeCondition}' not yet implemented")

                End Select

                If side.HasValue Then
                    If _currentTrendSide Is Nothing Then
                        ' First signal of this session — establish the trend direction.
                        _currentTrendSide = side
                        _reversalCandidateSide = Nothing
                        _reversalConfirmCount = 0
                    ElseIf side.Value = _currentTrendSide.Value Then
                        ' Continuing the same direction — cancel any pending reversal candidate.
                        If _reversalCandidateSide.HasValue Then
                            Log($"↩  Reversal candidate cleared — {side.Value} signal confirms existing trend")
                        End If
                        _reversalCandidateSide = Nothing
                        _reversalConfirmCount = 0
                    Else
                        ' Opposite signal — advance confirmation counter on new bars only.
                        If isNewBar Then
                            If Not _reversalCandidateSide.HasValue OrElse _reversalCandidateSide.Value <> side.Value Then
                                _reversalCandidateSide = side
                                _reversalConfirmCount = 1
                            Else
                                _reversalConfirmCount += 1
                            End If
                            Log($"↔  Reversal candidate: was {_currentTrendSide.Value}, now {side.Value} " &
                            $"({_reversalConfirmCount}/{ReversalConfirmBars} confirmations)")
                        End If

                        If _reversalConfirmCount >= ReversalConfirmBars Then
                            Await DoReversalFlushAsync(side.Value, CDec(lastBar.Close), ct)
                        End If
                    End If
                End If

            End If ' Not barIsStale

            ' ── API-authoritative position reconciliation
            ' Queries the broker for ANY open position on this contract every tick (after
            ' the 60-s propagation guard).  Broker state is always authoritative.
            ' If the API reports no positions, ALL locally-tracked UI rows are force-closed
            ' regardless of how many are shown as "In Progress" — this correctly handles
            ' the multi-position (scale-in) scenario where the engine tracks only one
            ' _openPositionId but multiple rows may be open in the UI.
            ' This reconciliation runs BEFORE any confidence-driven action below.
            If _positionOpen Then
                Dim secondsSinceEntry = (DateTimeOffset.UtcNow - _positionOpenedAt).TotalSeconds
                If secondsSinceEntry < 60 Then
                    Log($"⏳ Sync skipped ({CInt(secondsSinceEntry)}s since entry — waiting 60 s for portfolio to reflect new position)")
                Else
                    ' Pass Nothing so the call finds ANY live position for this contract,
                    ' not just _openPositionId.  This detects SL/TP closures on scale-in
                    ' positions even when the initial position ID has changed.
                    Dim snapshot = Await _orderService.GetLivePositionSnapshotAsync(
                        _strategy.AccountId, _strategy.ContractId, Nothing, ct)

                    If snapshot IsNot Nothing Then
                        ' Position confirmed open at broker.
                        ' _stateLock: guards writes shared with OnLivePositionUpdated (SignalR thread).
                        Dim positionSyncedArgs As PositionSyncedEventArgs = Nothing
                        SyncLock _stateLock
                            _syncMissCount = 0
                            If Not _openPositionId.HasValue Then
                                _openPositionId = snapshot.PositionId
                                Log($"🔗 Position ID resolved: {snapshot.PositionId}")
                                ' Correct the entry price from the broker's confirmed fill rate.
                                ' PlaceBracketOrdersAsync stores lastClose as _lastEntryPrice (an estimate).
                                ' ProjectX does not return the fill price from PlaceOrder, so the first
                                ' REST sync is the earliest point we can get the actual fill rate.
                                ' Without this fix, M6E P&L is off by (fill − barClose) × DPP for the
                                ' entire life of the trade — typically 3-5 ticks on fast bars.
                                If snapshot.OpenRate > 0D AndAlso _lastEntryPrice > 0D Then
                                    Dim fillDiff = Math.Abs(snapshot.OpenRate - _lastEntryPrice)
                                    If fillDiff > If(_strategy.TickSize > 0D, _strategy.TickSize, 0.0001D) Then
                                        Log($"📌 Entry corrected {_lastEntryPrice:F5} → {snapshot.OpenRate:F5} (fill delta={fillDiff:F5})")
                                        _lastEntryPrice = snapshot.OpenRate
                                    End If
                                End If
                            End If

                            ' Keep the aggregate contract count / leverage in sync with the
                            ' live broker state so the tile always reflects the real position
                            ' (handles scale-ins, partial closes, and manual adjustments).
                            _lastFinalAmount = snapshot.Amount

                            ' Self-heal _totalDollarPerPoint if the startup Task.Run raced
                            ' against the first timer tick and lost (i.e. it was still 0).
                            ' This ensures the fallback DPP is correct even on the very first
                            ' PositionSynced tick after orphan detection.
                            If _totalDollarPerPoint <= 0D AndAlso _strategy.TickSize > 0D AndAlso snapshot.Units > 0D Then
                                _totalDollarPerPoint = snapshot.Units * _strategy.TickValue / _strategy.TickSize
                            End If
                            ' Use the broker's reported P&L directly; fall back to a price-based
                            ' estimate when openPnl=0 (TopStepX REST always returns 0 for futures).
                            ' Price priority: (1) _lastQuotePrice — sub-second MarketHub tick;
                            '                 (2) _lastBarClose   — set by 5-second timer, up to 5s stale.
                            ' Fallback MUST use tick-adjusted DPP, NOT raw Units.
                            ' Raw Units = 1 contract of M6E, but DPP = 1 × 1.25/0.0001 = $12,500/pt.
                            ' Using Units=1 gives $0.0002 per pip → rounds to $0.00 (the reported bug).
                            Dim derivedDpp = If(_strategy.TickSize > 0D,
                                                snapshot.Units * _strategy.TickValue / _strategy.TickSize, 0D)
                            Dim dpp = If(_totalDollarPerPoint > 0D, _totalDollarPerPoint,
                                         If(derivedDpp > 0D, derivedDpp, snapshot.Units))
                            Dim rawQuoteRest = Volatile.Read(_lastQuotePrice)
                            Dim priceForRest = If(rawQuoteRest > 0D, CDec(rawQuoteRest), _lastBarClose)
                            Dim livePnl = If(snapshot.UnrealizedPnlUsd <> 0D, snapshot.UnrealizedPnlUsd,
                                If(priceForRest > 0D, ComputeLivePnl(priceForRest), 0D))
                            _lastApiPnl = livePnl
                            positionSyncedArgs = New PositionSyncedEventArgs(
                                snapshot.PositionId, livePnl, snapshot.OpenedAtUtc,
                                snapshot.Amount, snapshot.IsBuy, snapshot.PositionCount)
                        End SyncLock
                        ' Raise event outside the lock — event handlers may be slow.
                        RaiseEvent PositionSynced(Me, positionSyncedArgs)

                    Else
                        ' Snapshot returned Nothing — could be a genuine close or a transient API fault.
                        ' Require SyncMissThreshold consecutive misses before declaring the position closed.
                        ' This prevents a single network blip or a slow-to-reflect fill from falsely
                        ' resetting the tile and letting the engine place duplicate orders.
                        Dim shouldDeclareClose As Boolean = False
                        Dim closedCount As Integer = 0
                        Dim closePnl As Decimal = 0D
                        SyncLock _stateLock
                            _syncMissCount += 1
                            If _syncMissCount < SyncMissThreshold Then
                                Log($"⚠️  Sync miss {_syncMissCount}/{SyncMissThreshold} for {_strategy.ContractId} — position not visible in API yet; will retry.")
                                ' Don't declare closed yet — leave _positionOpen True and skip this tick.
                            Else
                                ' SyncMissThreshold consecutive misses — position genuinely closed externally.
                                _syncMissCount = 0
                                closedCount = Math.Max(1, _openTradeCount)
                                closePnl = _lastApiPnl
                                Log($"⚠️  API reconciliation: {SyncMissThreshold} consecutive sync misses for {_strategy.ContractId} — " &
                                    $"declaring closed (SL/TP/external). " &
                                    $"Final P&L={If(_lastApiPnl >= 0, "+", "")}$({_lastApiPnl:F2}). Ready for next signal.")
                                _tradePhase = TradePhase.Idle
                                _openPositionId = Nothing
                                _openTradeCount = 0
                                _scaleInTradeCount = 0
                                _lastApiPnl = 0D
                                shouldDeclareClose = True
                            End If
                        End SyncLock
                        ' Raise events and call non-thread-safe helpers outside the lock.
                        If shouldDeclareClose Then
                            WriteDiagPostMortem("SL/TP", closePnl)
                            _diagLogger?.WriteBracketEvent("POSITION_CLOSED",
                                If(_currentTrendSide.HasValue, _currentTrendSide.Value.ToString(), "UNKNOWN"),
                                _lastBarClose, _lastSlPrice, _lastTpPrice,
                                $"Position closed (SL/TP/external) | P&L={If(closePnl >= 0, "+", "")}${closePnl:F2}")
                            SetTrailTimerInterval(False)
                            SetBarCheckInterval(False)   ' FEAT-11: position closed — switch to 15s flat cadence
                            ' FEAT-01: infer exit price from entry ± PnL
                            Dim slTpExitPrice As Decimal = 0D
                            If _totalDollarPerPoint > 0D Then
                                Dim direction = If(_lastEntrySide = OrderSide.Buy, 1D, -1D)
                                slTpExitPrice = _lastEntryPrice + direction * closePnl / _totalDollarPerPoint
                            End If
                            PersistTradeClose("SL/TP", closePnl, slTpExitPrice)
                            For i As Integer = 1 To closedCount
                                RaiseEvent TradeClosed(Me, New TradeClosedEventArgs("SL/TP", closePnl, slTpExitPrice))
                                closePnl = 0D   ' P&L only available for the first (most-recently synced) close
                            Next
                            _sessionPnl += closePnl
                            _sessionTradeCount += 1
                            _totalTradesThisSession += 1
                            If closePnl < 0D Then
                                _consecutiveLosses += 1
                            Else
                                _consecutiveLosses = 0
                            End If
                            ResetTrailState()
                            _lastPositionClosedAt = DateTimeOffset.UtcNow  ' start re-entry cooldown
                        End If
                    End If   ' snapshot IsNot Nothing
                End If   ' secondsSinceEntry
            Else
                ' ── Orphan-position detection ──────────────────────────────────────────
                ' When the engine has no known open position, query the broker each tick to
                ' catch positions opened externally (manually on the platform or by a concurrent
                ' session) that the startup check may have missed (async race or API hiccup).
                ' Guard: skip during the re-entry cooldown window — a position we just closed
                ' can still appear briefly in the portfolio API before the broker reconciles.
                Dim cooldownElapsed = (DateTimeOffset.UtcNow - _lastPositionClosedAt).TotalSeconds
                If cooldownElapsed >= ReEntryCooldownSeconds Then
                    Try
                        Dim orphan = Await _orderService.GetLivePositionSnapshotAsync(
                            _strategy.AccountId, _strategy.ContractId, Nothing, ct)
                        If orphan IsNot Nothing Then
                            _tradePhase = TradePhase.HardStop
                            SetTrailTimerInterval(True)
                            SetBarCheckInterval(True)   ' FEAT-11: orphan position — revert to 30s poll
                            _openPositionId = orphan.PositionId
                            _positionOpenedAt = DateTimeOffset.UtcNow.AddSeconds(-61) ' skip propagation guard
                            If orphan.OpenRate > 0D Then
                                _lastEntryPrice = orphan.OpenRate
                                _lastEntrySide = If(orphan.IsBuy, OrderSide.Buy, OrderSide.Sell)
                            End If
                            _currentTrendSide = If(orphan.IsBuy, OrderSide.Buy, OrderSide.Sell)
                            _lastFinalAmount = orphan.Amount
                            _openTradeCount = Math.Max(1, orphan.PositionCount)
                            _scaleInTradeCount = Math.Min(MaxScaleInTrades,
                                                          Math.Max(0, orphan.PositionCount - 1))
                            _totalDollarPerPoint = If(orphan.Units > 0D AndAlso _strategy.TickSize > 0D,
                                orphan.Units * _strategy.TickValue / _strategy.TickSize, 0D)
                            Dim orphanFav = FavouriteContracts.TryGetBySymbolResolved(_strategy.ContractId)
                            If orphanFav IsNot Nothing AndAlso Not String.IsNullOrEmpty(orphanFav.PxContractId) Then
                                SubscribeMarketQuotes(orphanFav.PxContractId)
                            End If
                            _bracketInitPending = True ' Turtle bracket deferred until ATR is ready
                            Dim orphanSide = If(orphan.IsBuy, OrderSide.Buy, OrderSide.Sell)
                            RaiseEvent TradeOpened(Me, New TradeOpenedEventArgs(
                                orphanSide, _strategy.ContractId, 100,
                                orphan.OpenedAtUtc, Nothing, orphan.PositionId,
                                orphan.OpenedAtUtc, orphan.Amount, orphan.OpenRate))
                            Dim orphanCapStr = If(_scaleInTradeCount >= MaxScaleInTrades,
                                $"scale-in cap REACHED ({_scaleInTradeCount}/{MaxScaleInTrades})",
                                $"scale-in {_scaleInTradeCount}/{MaxScaleInTrades} used")
                            Log($"🔍 Orphan position attached — positionId={orphan.PositionId} " &
                                $"entry={orphan.OpenRate:F4} side={orphanSide} " &
                                $"({orphan.PositionCount} position(s), {orphanCapStr}) — Turtle bracket pending ATR.")
                        End If
                    Catch ex As Exception
                        ' Non-fatal — orphan check retries on next tick.
                        Log($"⚠️  Orphan position check failed: {ex.Message}")
                    End Try
                End If
            End If

            ' ── Deferred Turtle bracket init for startup / orphan-detected positions ─────
            ' _turtleBracket cannot be initialised during Start() or the orphan-detection
            ' path because ATR = 0 before the first bar check.  Once ATR is available
            ' (computed above in the strategy-condition block) we create the bracket here
            ' so all subsequent ticks get full SL/TP stepped trail management.
            '
            ' Guard: if the position's current P&L is already beyond the configured SL
            ' threshold (e.g. a manually-opened position that is deep in loss), initialising
            ' the bracket would cause ApplySteppedTrailAsync to breach the SL on this very
            ' tick, flatten the position, and raise TradeClosed.  In that case skip the
            ' bracket and let the broker's native SL/TP manage the position; the engine
            ' will still track P&L via broker sync and surface it on the tile.
            If _positionOpen AndAlso _bracketInitPending AndAlso
               True AndAlso ' _turtleBracket Is Nothing AndAlso
                _lastEntryPrice > 0D AndAlso _currentAtrValue > 0D Then
                ' TopStepX futures DPP = tickValue / tickSize × contracts
                ' TopStepX DPP is tick-value-based (not CFD dollar-per-unit)
                Dim dppDeferred As Decimal = _totalDollarPerPoint
                If dppDeferred <= 0D Then
                    Dim tickSzDef = If(_strategy.TickSize > 0D, _strategy.TickSize, 0.01D)
                    Dim tickValDef = If(_strategy.TickValue > 0D, _strategy.TickValue, 1D)
                    Dim contractsDef = CDec(If(_strategy.Contracts > 0, _strategy.Contracts, 1))
                    dppDeferred = If(tickSzDef > 0D, (tickValDef / tickSzDef) * contractsDef, 0D)
                End If
                Dim sideStrDeferred = If(_lastEntrySide = OrderSide.Buy, "BUY", "SELL")

                ' Estimate current P&L: prefer the broker-reported value from the last
                ' sync tick; fall back to a price-based estimate when not yet available.
                ' SL threshold uses N-based formula when ATR is available; fixed dollar fallback otherwise.
                Dim slThreshold As Decimal =
                    If(_currentAtrValue > 0D AndAlso _totalDollarPerPoint > 0D,
                       -Math.Abs(_strategy.SlMultipleOfN * _currentAtrValue * _totalDollarPerPoint),
                       0D)
                Dim estimatedPnl As Decimal
                If _lastApiPnl <> 0D Then
                    estimatedPnl = _lastApiPnl
                ElseIf dppDeferred > 0D Then
                    Dim dirMult = If(_lastEntrySide = OrderSide.Buy, 1D, -1D)
                    estimatedPnl = Math.Round(dirMult * (_lastBarClose - _lastEntryPrice) * dppDeferred, 2)
                Else
                    estimatedPnl = 0D
                End If

                If estimatedPnl <= slThreshold Then
                    ' Position already in a loss that exceeds the engine SL — skip bracket.
                    ' ApplySteppedTrailAsync would fire the SL immediately if the bracket were
                    ' created, closing a position the user wants the engine to monitor.
                    _bracketInitPending = False
                    Log($"⚠️  Attached position P&L≈${estimatedPnl:F2} already exceeds SL threshold " &
                        $"${slThreshold:F2} — turtle bracket not applied; monitoring via broker sync only.")
                Else
                    ' N-based bracket: SL = SlMultipleOfN × ATR × DPP, TP = TpMultipleOfN × ATR × DPP.
                    ' Falls back to fixed dollar amounts when ATR is unavailable (indicator warm-up).
                    Dim slDollarsDeferred As Decimal
                    Dim tpDollarsDeferred As Decimal
                    If _currentAtrValue > 0D AndAlso dppDeferred > 0D Then
                        slDollarsDeferred = Math.Round(_strategy.SlMultipleOfN * _currentAtrValue * dppDeferred, 2)
                        tpDollarsDeferred = Math.Round(_strategy.TpMultipleOfN * _currentAtrValue * dppDeferred, 2)
                    Else
                        slDollarsDeferred = 0D
                        tpDollarsDeferred = 0D
                    End If
                    ' Seed _lastSlPrice and _lastTpPrice from ATR so the trail ratchet has a
                    ' starting point, then IMMEDIATELY push both levels to the broker.
                    ' Without the push, the broker keeps whatever SL/TP the user placed manually
                    ' until the ratchet first advances — which never happens if price stays flat.
                    Dim isBuyDeferred = (_lastEntrySide = OrderSide.Buy)
                    Dim tickSzDeferred = If(_strategy.TickSize > 0D, _strategy.TickSize, 0.01D)
                    If _lastSlPrice <= 0D AndAlso _currentAtrValue > 0D Then
                        Dim rawSl = If(isBuyDeferred,
                            _lastEntryPrice - _strategy.SlMultipleOfN * _currentAtrValue,
                            _lastEntryPrice + _strategy.SlMultipleOfN * _currentAtrValue)
                        _lastSlPrice = If(isBuyDeferred,
                            CDec(Math.Floor(CDbl(rawSl / tickSzDeferred))) * tickSzDeferred,
                            CDec(Math.Ceiling(CDbl(rawSl / tickSzDeferred))) * tickSzDeferred)
                        ' For reattached positions use the ATR-derived level as both the seed
                        ' and the trail guard (we don't know what the live SL was clamped to).
                        If _initialSlPrice <= 0D Then _initialSlPrice = _lastSlPrice
                    End If
                    ' Compute ATR-based TP for orphan positions (_lastTpPrice starts at 0).
                    If _lastTpPrice <= 0D AndAlso _currentAtrValue > 0D Then
                        Dim rawTp = If(isBuyDeferred,
                            _lastEntryPrice + _strategy.TpMultipleOfN * _currentAtrValue,
                            _lastEntryPrice - _strategy.TpMultipleOfN * _currentAtrValue)
                        _lastTpPrice = If(isBuyDeferred,
                            CDec(Math.Ceiling(CDbl(rawTp / tickSzDeferred))) * tickSzDeferred,
                            CDec(Math.Floor(CDbl(rawTp / tickSzDeferred))) * tickSzDeferred)
                    End If
                    ' Compute Free Roll activation price for reattached positions
                    If _freeRollActivationPrice = 0D AndAlso _currentAtrValue > 0D AndAlso tickSzDeferred > 0D Then
                        Dim tpMultDeferred = If(_strategy.TpMultipleOfN > 0D, _strategy.TpMultipleOfN, 2D)
                        _initialTpTicks = CInt(Math.Floor(tpMultDeferred * _currentAtrValue / tickSzDeferred))
                        Dim actTks = CInt(Math.Floor(_initialTpTicks * FreeRollActivationFraction))
                        _freeRollActivationPrice = TickMath.PriceFromTicks(
                            _lastEntryPrice, actTks, tickSzDeferred, isBuyDeferred, isStop:=False)
                        Log($"🎯 Free Roll gate (deferred): activation @ {_freeRollActivationPrice:F4} ({actTks}t = 67% of {_initialTpTicks}t TP)")
                    End If
                    _bracketInitPending = False
                    Log($"🎯 ATR bracket init for detected position — " &
                        $"entry={_lastEntryPrice:F4} side={sideStrDeferred} " &
                        $"ATR={_currentAtrValue:F4} → SL={_lastSlPrice:F4} ({_strategy.SlMultipleOfN:F2}N)" &
                        $" TP={_lastTpPrice:F4} ({_strategy.TpMultipleOfN:F2}N) — pushing to broker...")
                    ' Push ATR-based bracket to broker immediately (don't wait for first ratchet).
                    If _openPositionId.HasValue Then
                        Dim pushOk = Await _orderService.EditPositionSlTpAsync(
                            _openPositionId.Value,
                            If(_lastSlPrice > 0D, CType(_lastSlPrice, Decimal?), Nothing),
                            If(_lastTpPrice > 0D, CType(_lastTpPrice, Decimal?), Nothing),
                            cancel:=ct)
                        If pushOk Then
                            Log($"✅ Broker bracket updated — SL={_lastSlPrice:F4} TP={_lastTpPrice:F4}")
                        Else
                            Log($"⚠️  Broker bracket push failed — will retry via ATR trail on next tick")
                        End If
                    End If
                End If
            End If

            ' ── SL/TP management delegated to TopStepX ClaudeTrader OCO bracket ───────
            ' The broker's native OCO handles both SL and TP fills. When the SL fires,
            ' the TP is automatically cancelled, and vice versa. No app-side trailing or
            ' TP extension is needed. FlattenContractAsync still cancels all resting orders
            ' as a safety net on manual "Close All" requests.
            ' (ApplyAtrTrailAsync and ExtendTpIfClosedBeyondTargetAsync are disabled)

            ' ── Place orders / confidence-driven scale-in ────────────────────────
            ' EmaRsiWeightedScore uses the confidence model (scale-in + neutral exit).
            ' All other strategies retain the single-trade-at-a-time guardrail.

            If barIsStale AndAlso Not _lastBarWasStale Then
                Log($"⏸  Market closed — last bar {staleDesc} — monitoring positions only. ({remStr})")
            ElseIf _strategy.Condition = StrategyConditionType.EmaRsiWeightedScore Then
                Await EvaluateConfidenceActionsAsync(rawUpPct, rawDownPct, side, CDec(lastBar.Close), isNewBar, ct)
            Else
                If side.HasValue Then
                    ' BUG-13: MC same-direction signal should scale into an open position rather
                    ' than being blocked.  All other strategies retain the single-trade guardrail.
                    Dim isSameDirection = _currentTrendSide.HasValue AndAlso side.Value = _currentTrendSide.Value
                    Dim isMcScaleIn = _strategy.Condition = StrategyConditionType.MultiConfluence AndAlso
                                      _positionOpen AndAlso
                                      isSameDirection AndAlso
                                      _scaleInTradeCount < MaxScaleInTrades
                    If _positionOpen AndAlso Not isMcScaleIn Then
                        ' MC same-direction cap reached
                        If _strategy.Condition = StrategyConditionType.MultiConfluence AndAlso isSameDirection Then
                            Log($"⛔ Scale-in cap reached ({_scaleInTradeCount}/{MaxScaleInTrades}) — signal: {side.Value}")
                        ElseIf _strategy.Condition = StrategyConditionType.MultiConfluence AndAlso Not isSameDirection AndAlso _currentTrendSide.HasValue Then
                            Log($"⛔ Opposite-direction signal ({side.Value}) blocked — position open in {_currentTrendSide.Value} direction")
                        Else
                            Log($"⛔ Signal ({side.Value}) blocked — position already open (positionId={If(_openPositionId.HasValue, _openPositionId.Value.ToString(), "pending")}). Waiting for close before next entry.")
                        End If
                        If _pendingDiagEntry IsNot Nothing Then
                            _pendingDiagEntry.EventType = "REJECT"
                            _pendingDiagEntry.RejectionReason = $"Position open — {If(isSameDirection, "scale-in cap reached", "opposite-direction blocked")}"
                            ' FinalizeDiagEntry(side.Value, CDec(lastBar.Close))
                            _diagLogger?.WriteEntry(_pendingDiagEntry)
                            _pendingDiagEntry = Nothing
                        End If
                    ElseIf isMcScaleIn AndAlso _lastApiPnl < 0D Then
                        Log($"📊 Scale-in suppressed — position not profitable (P&L=${_lastApiPnl:F2})")
                        If _pendingDiagEntry IsNot Nothing Then
                            _pendingDiagEntry.EventType = "REJECT"
                            _pendingDiagEntry.RejectionReason = $"Scale-in suppressed — position not profitable (P&L=${_lastApiPnl:F2})"
                            _diagLogger?.WriteEntry(_pendingDiagEntry)
                            _pendingDiagEntry = Nothing
                        End If
                    ElseIf Not IsOrderingAllowed.Invoke() Then
                        Log($"⏸  {_strategy.ContractId} market CLOSED — monitoring only (no orders) | signal: {side.Value}")
                        If _pendingDiagEntry IsNot Nothing Then
                            _pendingDiagEntry.EventType = "REJECT"
                            _pendingDiagEntry.RejectionReason = "Market closed — IsOrderingAllowed returned False"
                            ' FinalizeDiagEntry(side.Value, CDec(lastBar.Close))
                            _diagLogger?.WriteEntry(_pendingDiagEntry)
                            _pendingDiagEntry = Nothing
                        End If
                    ElseIf (DateTimeOffset.UtcNow - _lastPositionClosedAt).TotalSeconds < ReEntryCooldownSeconds Then
                        Dim cooldownLeft = CInt(ReEntryCooldownSeconds - (DateTimeOffset.UtcNow - _lastPositionClosedAt).TotalSeconds)
                        Log($"⏸  Re-entry cooldown — {cooldownLeft}s remaining after last close | signal: {side.Value}")
                        If _pendingDiagEntry IsNot Nothing Then
                            _pendingDiagEntry.EventType = "REJECT"
                            _pendingDiagEntry.RejectionReason = $"Re-entry cooldown ({cooldownLeft}s remaining)"
                            ' FinalizeDiagEntry(side.Value, CDec(lastBar.Close))
                            _diagLogger?.WriteEntry(_pendingDiagEntry)
                            _pendingDiagEntry = Nothing
                        End If
                    ElseIf IsDailyLossLimitHit() Then
                        Log($"🛑 Daily loss limit hit (session P&L=${_sessionPnl:F2}, limit=-${_strategy.MaxDailyLossUsd:F0}) — no new entries for {_strategy.ContractId}")
                        If _pendingDiagEntry IsNot Nothing Then
                            _pendingDiagEntry.EventType = "REJECT"
                            _pendingDiagEntry.RejectionReason = $"Daily loss limit hit (session P&L=${_sessionPnl:F2})"
                            _diagLogger?.WriteEntry(_pendingDiagEntry)
                            _pendingDiagEntry = Nothing
                        End If
                    ElseIf IsInTopStepXBlackout() Then
                        Log($"⏸  Entry blocked — TopStepX pre-close window (gate opens again 22:00 UTC) | signal: {side.Value}")
                        If _pendingDiagEntry IsNot Nothing Then
                            _pendingDiagEntry.EventType = "REJECT"
                            _pendingDiagEntry.RejectionReason = "TopStepX pre-close blackout (19:50–22:00 UTC)"
                            _diagLogger?.WriteEntry(_pendingDiagEntry)
                            _pendingDiagEntry = Nothing
                        End If
                    ElseIf Not IsInsideTradingHours() Then
                        Log($"⏸  Outside trading hours (UTC {DateTimeOffset.UtcNow.Hour:00}:xx, window={_strategy.TradingStartHourUtc:00}–{_strategy.TradingEndHourUtc:00}h) — no new entries | signal: {side.Value}")
                        If _pendingDiagEntry IsNot Nothing Then
                            _pendingDiagEntry.EventType = "REJECT"
                            _pendingDiagEntry.RejectionReason = $"Outside trading hours (UTC {DateTimeOffset.UtcNow.Hour:00}:xx, window={_strategy.TradingStartHourUtc:00}–{_strategy.TradingEndHourUtc:00}h)"
                            _diagLogger?.WriteEntry(_pendingDiagEntry)
                            _pendingDiagEntry = Nothing
                        End If
                    ElseIf Not IsInsideTradingWindow() Then
                        Log($"⏰ Outside trading window ({_strategy.TradingWindowUtcStart.Value}–{_strategy.TradingWindowUtcEnd.Value} UTC) — entry suppressed | signal: {side.Value}")
                        If _pendingDiagEntry IsNot Nothing Then
                            _pendingDiagEntry.EventType = "REJECT"
                            _pendingDiagEntry.RejectionReason = $"Outside trading window ({_strategy.TradingWindowUtcStart.Value}–{_strategy.TradingWindowUtcEnd.Value} UTC)"
                            _diagLogger?.WriteEntry(_pendingDiagEntry)
                            _pendingDiagEntry = Nothing
                        End If
                    Else
                        Dim cloudSlArg As Decimal? = _mcCloudSlPrice

                        ' ── Pre-trade AI sanity check ───────────────────────────────────────
                        ' Calls Claude Haiku (~$0.01) for a macro/session filter before entry.
                        ' On API failure or missing key the result defaults to PROCEED so
                        ' a connectivity issue never silently blocks all trading.
                        If (_strategy.UseAiPreTradeGate AndAlso _strategy.UsePreTradeAiCheck) AndAlso _claudeService IsNot Nothing Then
                            Dim favForAi = FavouriteContracts.TryGetBySymbolResolved(_strategy.ContractId)
                            Dim aiCtx As New PreTradeContext With {
                                .ContractId = _strategy.ContractId,
                                .ContractDescription = If(favForAi IsNot Nothing, favForAi.Name, _strategy.ContractId),
                                .Side = If(side.Value = OrderSide.Buy, "BUY", "SELL"),
                                .Price = CDec(lastBar.Close),
                                .AtrValue = _currentAtrValue,
                                .SlMultiple = If(_strategy.SlMultipleOfN > 0D, _strategy.SlMultipleOfN, 1D),
                                .TpMultiple = If(_strategy.TpMultipleOfN > 0D, _strategy.TpMultipleOfN, 2D),
                                .AdxValue = _lastAdxValue,
                                .AdxThreshold = _strategy.AdxThreshold,
                                .ConfidencePct = _pendingConfidencePct,
                                .MinConfidencePct = _strategy.MinConfidencePct,
                                .TimeframeMinutes = _strategy.TimeframeMinutes,
                                .StrategyName = _strategy.Name,
                                .PersonaName = _strategy.PersonaName,
                                .UtcNow = DateTimeOffset.UtcNow,
                                .SessionPnlUsd = _sessionPnl,
                                .SessionTradeCount = _sessionTradeCount,
                                .ConsecutiveLosses = _consecutiveLosses,
                                .TotalTradesThisSession = _totalTradesThisSession,
                                .EffectiveMinConfidence = _strategy.MinConfidencePct
                            }
                            Dim aiResult = Await _claudeService.PreTradeCheckAsync(aiCtx, ct)
                            _lastAiVerdict = If(aiResult.Proceed, "PROCEED", "VETO")
                            _lastAiReasoning = aiResult.Reasoning
                            If aiResult.Proceed Then
                                Log($"↳ AI: PROCEED — ""{aiResult.Reasoning}""")
                                _diagLogger?.WriteEntry(New DiagnosticLogEntry With {
                                    .EventType = "AI_PASS",
                                    .Action = If(side.Value = OrderSide.Buy, "BUY", "SELL"),
                                    .Symbol = _strategy.ContractId,
                                    .Strategy = _strategy.Name,
                                    .Why = $"AI approved {If(side.Value = OrderSide.Buy, "BUY", "SELL")} entry — {aiResult.Reasoning}"
                                })
                            Else
                                Log($"↳ AI: VETO — ""{aiResult.Reasoning}""")
                                _diagLogger?.WriteEntry(New DiagnosticLogEntry With {
                                    .EventType = "AI_VETO",
                                    .Action = If(side.Value = OrderSide.Buy, "BUY", "SELL"),
                                    .Symbol = _strategy.ContractId,
                                    .Strategy = _strategy.Name,
                                    .RejectionReason = $"AI pre-check VETO: {aiResult.Reasoning}"
                                })
                                If _pendingDiagEntry IsNot Nothing Then
                                    _pendingDiagEntry.EventType = "REJECT"
                                    _pendingDiagEntry.RejectionReason = $"AI pre-check VETO: {aiResult.Reasoning}"
                                    _diagLogger?.WriteEntry(_pendingDiagEntry)
                                    _pendingDiagEntry = Nothing
                                End If
                                Return
                            End If
                        End If

                        ' FEAT-01: capture signal context for setup snapshot
                        _lastSignalBar = lastBar
                        _lastSignalArgs = _latestConfidenceArgs
                        Await PlaceBracketOrdersAsync(side.Value, CDec(lastBar.Close), cloudSlArg)
                        If isMcScaleIn Then
                            _scaleInTradeCount += 1
                            Log($"📈 MC SCALE-IN #{_scaleInTradeCount}/{MaxScaleInTrades} — {side.Value} signal added to {_currentTrendSide.Value} position.")
                        End If
                    End If
                Else
                    ' No signal this tick — log the diagnostic snapshot if one was built
                    If _pendingDiagEntry IsNot Nothing Then
                        _diagLogger?.WriteEntry(_pendingDiagEntry)
                        _pendingDiagEntry = Nothing
                    End If
                End If
            End If
        End Function

        Private Async Function PlaceBracketOrdersAsync(side As OrderSide, lastClose As Decimal,
                                                         Optional cloudSlPrice As Decimal? = Nothing) As Task

            Dim fav = FavouriteContracts.TryGetBySymbolResolved(_strategy.ContractId)
            Dim priceUsed = lastClose
            Dim sideStr = If(side = OrderSide.Buy, "BUY", "SELL")

            ' TopStepX is the only broker — always use the futures path.
            If fav Is Nothing OrElse String.IsNullOrEmpty(fav.PxContractId) Then
                Log($"⚠️  '{_strategy.ContractId}' has no TopStepX (PX) contract ID — order aborted. " &
                    $"Add a PxContractId to FavouriteContracts.")
                _tradePhase = TradePhase.Idle
                SetTrailTimerInterval(False)
                SetBarCheckInterval(False)   ' FEAT-11: order aborted — keep 15s flat cadence
                Return
            End If

            Dim contracts = If(_strategy.Contracts > 0, _strategy.Contracts, 1)
            ' STRAT-16: halve size for partial 8/9 MultiConfluence signals
            If _mcPartialSignal Then
                contracts = Math.Max(1, contracts \ 2)
                Log($"⚡ Partial-conviction entry: quantity halved to {contracts} contract(s)")
            End If
            _mcPartialSignal = False   ' consume; cleared after order is placed
            Dim tickSize = If(_strategy.TickSize > 0D, _strategy.TickSize, fav.PxTickSize)
            Dim tickValue = If(_strategy.TickValue > 0D, _strategy.TickValue, fav.PxTickValue)

            ' ── OCO bracket: ATR-derived ticks when available, fixed preset as fallback ─
            ' When the strategy carries non-zero ATR multiples and ATR(14) has been computed
            ' from at least one bar check, derive tick counts from ATR so the initial bracket
            ' reflects the configured Wide/Standard/Tight tier (e.g. Wide SL = 2.5 × ATR).
            ' Falls back to DefaultSlTicks/DefaultTpTicks from appsettings.json on the very
            ' first entry before any bar has been analysed (ATR = 0).
            ' TopStepXInstrumentCatalog.ClampStopTicksAsync enforces the per-instrument minimum.
            Dim initialStopTicks As Integer
            Dim initialTpTicks As Integer
            If _strategy.SlMultipleOfN > 0D AndAlso _currentAtrValue > 0D AndAlso tickSize > 0D Then
                Dim atrSlTicks = CInt(Math.Floor(_strategy.SlMultipleOfN * _currentAtrValue / tickSize))
                Dim tpMultiple = If(_strategy.TpMultipleOfN > 0D, _strategy.TpMultipleOfN, _strategy.SlMultipleOfN * 2D)
                Dim atrTpTicks = CInt(Math.Floor(tpMultiple * _currentAtrValue / tickSize))
                initialStopTicks = Math.Max(Math.Max(1, atrSlTicks), _pxSettings.DefaultSlTicks)
                initialTpTicks = Math.Max(Math.Max(1, atrTpTicks), _pxSettings.DefaultTpTicks)
                Log($"📐 ATR bracket: SL={initialStopTicks}t ({_strategy.SlMultipleOfN:F2}×N) " &
                    $"TP={initialTpTicks}t ({tpMultiple:F2}×N) [ATR={_currentAtrValue:F4} tickSz={tickSize}]")
            Else
                initialStopTicks = Math.Max(1, _pxSettings.DefaultSlTicks)
                initialTpTicks = Math.Max(1, _pxSettings.DefaultTpTicks)
                Log($"📐 Fixed bracket: SL={initialStopTicks}t TP={initialTpTicks}t " &
                    $"(${initialStopTicks * tickValue * contracts:F0} / ${initialTpTicks * tickValue * contracts:F0})" &
                    If(_currentAtrValue <= 0D, " [ATR not ready — using preset]", String.Empty))
            End If

            ' ── Free Roll: store TP ticks and compute activation price (67% of TP distance) ─
            _initialTpTicks = initialTpTicks
            Dim activationTicks = CInt(Math.Floor(initialTpTicks * FreeRollActivationFraction))
            Dim isBuyFutForActivation = (side = OrderSide.Buy)

            Log($"📊 Placing TopStepX {sideStr} {contracts} contract(s) {fav.PxContractId} | " &
                $"SL={initialStopTicks}t TP={initialTpTicks}t [tickValue=${tickValue:F2}/contract]")

            Dim order As New Order With {
                .AccountId = If(_session.SelectedAccount IsNot Nothing, _session.SelectedAccount.Id, _strategy.AccountId),
                .Broker = BrokerType.TopStepX,
                .ContractId = fav.PxContractId,
                .Side = side,
                .Quantity = contracts,
                .OrderType = OrderType.Market,
                .InitialStopTicks = initialStopTicks,
                .InitialTakeProfitTicks = initialTpTicks,
                .EstimatedEntryPrice = priceUsed
            }   ' No StopLossRate, no TakeProfitRate for futures (tick-based bracket only)

            Dim placedOrder = Await _orderService.PlaceOrderAsync(order)

            If placedOrder IsNot Nothing AndAlso placedOrder.Status = OrderStatus.Working Then
                _tradePhase = TradePhase.HardStop
                SetTrailTimerInterval(True)
                SetBarCheckInterval(True)   ' FEAT-11: entry confirmed — revert to 30s poll while position is open
                SubscribeMarketQuotes(fav.PxContractId)
                _openPositionId = placedOrder.ExternalPositionId
                _lastEntryPrice = priceUsed
                _lastEntrySide = side
                Dim isBuyFut = (side = OrderSide.Buy)
                _lastSlPrice = TickMath.PriceFromTicks(priceUsed, initialStopTicks, tickSize,
                                                       isBuy:=isBuyFut, isStop:=True)
                _initialSlPrice = _lastSlPrice   ' tick-derived SL = initial ATR SL for TopStepX (no spread clamp)
                _initialSlTicks = initialStopTicks ' preserve Wide/Standard/Tight tier for trail anchor
                _lastTpPrice = TickMath.PriceFromTicks(priceUsed, initialTpTicks, tickSize,
                                                       isBuy:=isBuyFut, isStop:=False)
                ' Free Roll activation at 50% of TP tick distance from entry
                _freeRollActivationPrice = TickMath.PriceFromTicks(priceUsed, activationTicks, tickSize,
                                                                    isBuy:=isBuyFut, isStop:=False)
                Log($"🎯 Free Roll gate: activation @ {_freeRollActivationPrice:F4} ({activationTicks}t = 67% of TP {initialTpTicks}t)")
                _totalDollarPerPoint += CDec(contracts) * tickValue / tickSize
                _positionOpenedAt = DateTimeOffset.UtcNow
                _lastApiPnl = 0D
                _lastFinalAmount += CDec(contracts)  ' += accumulates scale-ins; ResetTrailState zeros on close

                RaiseEvent TradeOpened(Me, New TradeOpenedEventArgs(
                    side, fav.PxContractId, _pendingConfidencePct,
                    DateTimeOffset.UtcNow,
                    placedOrder.ExternalOrderId,
                    placedOrder.ExternalPositionId,
                    DateTimeOffset.UtcNow,
                    CDec(contracts),
                    priceUsed))
                _openTradeCount += 1

                ' ── FEAT-01: persist open trade outcome + setup snapshot + lifespan ──
                _tradeEntrySessionWindow = GetSessionWindow(DateTimeOffset.UtcNow)
                If _outcomeRepo IsNot Nothing Then
                    Try
                        Dim outcome As New Core.Models.TradeOutcome With {
                            .ContractId = fav.PxContractId,
                            .Timeframe = _strategy.TimeframeMinutes,
                            .SignalType = side.ToString(),
                            .SignalConfidence = CSng(_pendingConfidencePct),
                            .ModelVersion = _strategy.Name,
                            .EntryTime = DateTimeOffset.UtcNow,
                            .EntryPrice = priceUsed,
                            .IsOpen = True
                        }
                        _openTradeOutcomeId = Await _outcomeRepo.SaveOutcomeAsync(outcome)
                    Catch ex As Exception
                        _logger.LogDebug(ex, "FEAT-01 SaveOutcomeAsync failed: {Msg}", ex.Message)
                    End Try
                End If
                If _snapshotRepo IsNot Nothing AndAlso _openTradeOutcomeId.HasValue Then
                    Try
                        Dim snap As New TradeSetupSnapshotEntity With {
                            .TradeOutcomeId = _openTradeOutcomeId.Value,
                            .CapturedAt = DateTimeOffset.UtcNow,
                            .StrategyName = _strategy.Name,
                            .PersonaName = If(_strategy.PersonaName, String.Empty),
                            .SlMultiple = CSng(_strategy.SlMultipleOfN),
                            .TpMultiple = CSng(_strategy.TpMultipleOfN),
                            .TimeframeMinutes = _strategy.TimeframeMinutes,
                            .AtrValue = _currentAtrValue,
                            .AdxValue = _lastAdxValue,
                            .SessionWindow = _tradeEntrySessionWindow,
                            .DayOfWeek = CInt(DateTimeOffset.UtcNow.DayOfWeek),
                            .HourOfDay = DateTimeOffset.UtcNow.Hour
                        }
                        If _lastSignalArgs IsNot Nothing Then
                            snap.UpPct = _lastSignalArgs.UpPct
                            snap.DownPct = _lastSignalArgs.DownPct
                            snap.Tenkan = _lastSignalArgs.Tenkan
                            snap.Kijun = _lastSignalArgs.Kijun
                            snap.Cloud1 = _lastSignalArgs.Cloud1
                            snap.Cloud2 = _lastSignalArgs.Cloud2
                            snap.Ema21 = _lastSignalArgs.Ema21
                            snap.Ema50 = _lastSignalArgs.Ema50
                            snap.MacdHist = _lastSignalArgs.MacdHist
                            snap.MacdHistPrev = _lastSignalArgs.MacdHistPrev
                            snap.StochRsiK = _lastSignalArgs.StochRsiK
                            snap.PlusDI = _lastSignalArgs.PlusDI
                            snap.MinusDI = _lastSignalArgs.MinusDI
                            snap.LongCount = _lastSignalArgs.LongCount
                            snap.ShortCount = _lastSignalArgs.ShortCount
                            snap.TotalConditions = _lastSignalArgs.TotalConditions
                            snap.Rsi14 = _lastSignalArgs.Rsi14
                            snap.VidyaValue = _lastSignalArgs.VidyaValue
                            snap.CmoValue = _lastSignalArgs.CmoValue
                            snap.DeltaVol = _lastSignalArgs.DeltaVol
                        End If
                        If _lastSignalBar IsNot Nothing Then
                            snap.SignalBarOpen = CDec(_lastSignalBar.Open)
                            snap.SignalBarHigh = CDec(_lastSignalBar.High)
                            snap.SignalBarLow = CDec(_lastSignalBar.Low)
                            snap.SignalBarClose = CDec(_lastSignalBar.Close)
                            snap.SignalBarVolume = _lastSignalBar.Volume
                        End If
                        Await _snapshotRepo.SaveAsync(snap)
                    Catch ex As Exception
                        _logger.LogDebug(ex, "FEAT-01 SaveAsync(snapshot) failed: {Msg}", ex.Message)
                    End Try
                End If
                If _lifespanRepo IsNot Nothing AndAlso _openTradeOutcomeId.HasValue Then
                    Try
                        Dim lifespan As New TradeLifespanRecordEntity With {
                            .TradeOutcomeId = _openTradeOutcomeId.Value,
                            .EntrySessionWindow = _tradeEntrySessionWindow,
                            .CreatedAt = DateTimeOffset.UtcNow,
                            .UpdatedAt = DateTimeOffset.UtcNow
                        }
                        _openLifespanId = Await _lifespanRepo.SaveAsync(lifespan)
                    Catch ex As Exception
                        _logger.LogDebug(ex, "FEAT-01 SaveAsync(lifespan) failed: {Msg}", ex.Message)
                    End Try
                End If

            End If

            ' Resolve positionId immediately after placement.
            ' ProjectX PlaceOrder only returns orderId — positionId is never populated.
            ' Without it, ApplyAtrTrailAsync is gated by `If Not _openPositionId.HasValue Then Return`
            ' and SL/TP bracket management cannot start until the 60s reconciliation fires.
            ' Retry up to 8×750ms (6s total) to give the exchange time to propagate the fill.
            ' If still unresolved, a background Task retries every 5s so the trail is never
            ' blocked for a full 60-second reconciliation cycle.
            If _positionOpen AndAlso Not _openPositionId.HasValue Then
                Dim posEntryAccountId = If(_session.SelectedAccount IsNot Nothing,
                                           _session.SelectedAccount.Id, _strategy.AccountId)
                Dim posSnapshot As LivePositionSnapshot = Nothing
                For attempt As Integer = 1 To 8
                    Await Task.Delay(750, _cts.Token)
                    posSnapshot = Await _orderService.GetLivePositionSnapshotAsync(
                        posEntryAccountId, fav.PxContractId, Nothing, _cts.Token)
                    If posSnapshot IsNot Nothing AndAlso posSnapshot.PositionId <> 0 Then Exit For
                    If attempt < 8 Then Log($"⏳ {fav.Name} position ID not visible yet (attempt {attempt}/8), retrying...")
                Next
                If posSnapshot IsNot Nothing AndAlso posSnapshot.PositionId <> 0 Then
                    SyncLock _stateLock
                        _openPositionId = posSnapshot.PositionId
                    End SyncLock
                    Log($"🔗 {fav.Name} position ID resolved at entry: {posSnapshot.PositionId} — ATR trail active")
                Else
                    Log($"⚠️  {fav.Name} position ID not resolved in 6s — starting background resolver (5s retry)")
                    Dim bgContractId = fav.PxContractId
                    Dim bgAccountId = posEntryAccountId
                    Dim bgCts = _cts
                    Dim _bgTask = Task.Run(Async Function() As Task
                                               Try
                                                   While Not bgCts.Token.IsCancellationRequested
                                                       Await Task.Delay(5000, bgCts.Token)
                                                       Dim bg = Await _orderService.GetLivePositionSnapshotAsync(
                                             bgAccountId, bgContractId, Nothing, bgCts.Token)
                                                       If bg IsNot Nothing AndAlso bg.PositionId <> 0 Then
                                                           SyncLock _stateLock
                                                               If Not _openPositionId.HasValue Then
                                                                   _openPositionId = bg.PositionId
                                                               End If
                                                           End SyncLock
                                                           Log($"🔗 {fav.Name} position ID resolved by background resolver: {bg.PositionId} — ATR trail active")
                                                           Return
                                                       End If
                                                       ' Stop if position is no longer open (SL/TP hit before we resolved)
                                                       SyncLock _stateLock
                                                           If Not _positionOpen Then Return
                                                       End SyncLock
                                                   End While
                                               Catch ex As OperationCanceledException
                                                   ' Engine stopped — exit silently
                                               Catch ex As Exception
                                                   Log($"⚠️  Background positionId resolver error: {ex.Message}")
                                               End Try
                                           End Function)
                End If
            End If
        End Function

        ''' <summary>
        ''' Closes all open positions for the current contract when confidence enters the
        ''' neutral band (40–60%).  Resets all position and scale-in state.
        ''' Works regardless of whether positions were opened by the app or manually on the broker.
        ''' </summary>
        Private Async Function DoNeutralFlattenAsync(ct As CancellationToken) As Task
            Log($"🔴 NEUTRAL EXIT — Closing ALL positions for {_strategy.ContractId} via API flatten...")
            Dim ok = Await _orderService.FlattenContractAsync(_strategy.AccountId, _strategy.ContractId, ct)
            If ok Then
                Log($"✅ Neutral flatten complete — {_strategy.ContractId} closed. " &
                    $"Confidence returned to neutral; re-entry requires a new extreme signal.")
            Else
                Log($"⚠️  Neutral flatten partially failed for {_strategy.ContractId} — check positions manually.")
            End If

            ' Fire TradeClosed for every locally-tracked open trade so all "In Progress" UI
            ' rows are reconciled — not just the initial position row.
            Dim closedCount = If(_positionOpen, Math.Max(1, _openTradeCount), 0)
            If closedCount > 0 Then
                If closedCount > 1 Then
                    Log($"⚠️  Closing {closedCount} UI trade row(s) for {_strategy.ContractId} — all positions flattened")
                End If
                Dim closePnl = _lastApiPnl
                ' FEAT-01: use last bar close as best available exit price on neutral flatten
                Dim neutralExitPrice = If(_lastBarClose > 0D, _lastBarClose, _lastEntryPrice)
                PersistTradeClose("Neutral", closePnl, neutralExitPrice)
                For i As Integer = 1 To closedCount
                    RaiseEvent TradeClosed(Me, New TradeClosedEventArgs("Neutral", closePnl, neutralExitPrice))
                    closePnl = 0D   ' P&L only known for the most-recently synced position
                Next
            End If

            WriteDiagPostMortem("Neutral", _lastApiPnl)
            _diagLogger?.WriteBracketEvent("POSITION_CLOSED",
                If(_currentTrendSide.HasValue, _currentTrendSide.Value.ToString(), "UNKNOWN"),
                _lastBarClose, _lastSlPrice, _lastTpPrice,
                $"Position closed (Neutral) | P&L={If(_lastApiPnl >= 0, "+", "")}${_lastApiPnl:F2}")
            _sessionPnl += _lastApiPnl
            _sessionTradeCount += 1
            _totalTradesThisSession += 1
            If _lastApiPnl < 0D Then
                _consecutiveLosses += 1
            Else
                _consecutiveLosses = 0
            End If
            _tradePhase = TradePhase.Idle
            SetTrailTimerInterval(False)
            SetBarCheckInterval(False)   ' FEAT-11: neutral close — switch to 15s flat cadence
            _openPositionId = Nothing
            _openTradeCount = 0
            _positionOpenedAt = DateTimeOffset.MinValue
            _lastPositionClosedAt = DateTimeOffset.UtcNow
            _lastApiPnl = 0D
            _extremeConfidenceDurationCount = 0
            _scaleInTradeCount = 0
            _syncMissCount = 0
            _currentTrendSide = Nothing
            _reversalCandidateSide = Nothing
            _reversalConfirmCount = 0
            ResetTrailState()
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            _cts?.Cancel()
            RemoveHandler _orderService.PositionUpdated, AddressOf OnLivePositionUpdated
            RemoveHandler _marketHub.QuoteReceived, AddressOf OnMarketQuoteReceived
            _timer?.Dispose()
            _positionTimer?.Dispose()
            _cts?.Dispose()
        End Sub

        Private Sub Log(message As String)
            RaiseEvent LogMessage(Me, message)
        End Sub

        ''' <summary>
        ''' FEAT-01: Persists the resolved outcome and finalised lifespan record when a trade closes.
        ''' Fire-and-forget safe — errors are caught and logged without bubbling.
        ''' </summary>
        Private Sub PersistTradeClose(exitReason As String, pnl As Decimal, exitPrice As Decimal)
            If Not _openTradeOutcomeId.HasValue Then Return
            Dim outcomeId = _openTradeOutcomeId.Value
            Dim lifespanId = _openLifespanId
            Dim entryTime = _positionOpenedAt
            Dim slCount = _slRatchetCount
            Dim tpCount = _tpAdvanceCount
            Dim freeRide = _freeRideActivatedAt
            Dim mae = _runningMaeDollars
            Dim mfe = _runningMfeDollars
            Dim initialSl = _initialSlPrice
            Dim entryPrice = _lastEntryPrice
            Dim entrySess = _tradeEntrySessionWindow
            Dim exitSess = GetSessionWindow(DateTimeOffset.UtcNow)
            Dim durationMin = CSng((DateTimeOffset.UtcNow - entryTime).TotalMinutes)

            Dim rMultiple As Single = 0F
            If initialSl <> 0D AndAlso entryPrice <> 0D Then
                Dim riskDollars = Math.Abs(entryPrice - initialSl) * _totalDollarPerPoint
                If riskDollars > 0D Then rMultiple = CSng(pnl / riskDollars)
            End If

            Task.Run(Async Function() As Task
                         Try
                             If _outcomeRepo IsNot Nothing Then
                                 Await _outcomeRepo.ResolveOutcomeAsync(
                                     outcomeId,
                                     DateTimeOffset.UtcNow,
                                     exitPrice,
                                     pnl,
                                     pnl >= 0D,
                                     exitReason)
                             End If
                         Catch ex As Exception
                             _logger.LogDebug(ex, "FEAT-01 ResolveOutcomeAsync failed: {Msg}", ex.Message)
                         End Try
                         If _lifespanRepo IsNot Nothing AndAlso lifespanId.HasValue Then
                             Try
                                 Dim lifespan = Await _lifespanRepo.GetByTradeOutcomeIdAsync(outcomeId)
                                 If lifespan IsNot Nothing Then
                                     lifespan.MaxAdverseExcursionDollars = mae
                                     lifespan.MaxFavorableExcursionDollars = mfe
                                     lifespan.SlRatchetCount = slCount
                                     lifespan.TpAdvanceCount = tpCount
                                     lifespan.FreeRideActivated = freeRide.HasValue
                                     lifespan.FreeRideActivatedAtMinutes = If(freeRide.HasValue,
                                         CSng((freeRide.Value - entryTime).TotalMinutes), 0F)
                                     lifespan.DurationMinutes = durationMin
                                     lifespan.ExitSessionWindow = exitSess
                                     lifespan.CrossedSessionBoundary = (entrySess <> exitSess)
                                     lifespan.RMultiple = rMultiple
                                     Await _lifespanRepo.UpdateAsync(lifespan)
                                 End If
                             Catch ex As Exception
                                 _logger.LogDebug(ex, "FEAT-01 UpdateAsync(lifespan) failed: {Msg}", ex.Message)
                             End Try
                         End If
                     End Function)
        End Sub

        ''' <summary>FEAT-01: Returns a session-window label for the given UTC time.</summary>
        Private Shared Function GetSessionWindow(utc As DateTimeOffset) As String
            Dim h = utc.Hour
            If h >= 8 AndAlso h < 12 Then Return "London"
            If h >= 12 AndAlso h < 13 Then Return "London-US Overlap"
            If h >= 13 AndAlso h < 17 Then Return "US Session"
            If h >= 17 AndAlso h < 21 Then Return "US Afternoon"
            Return "Off Hours"
        End Function

        Private _positionLogTickCount As Integer = 0  ' counts ManagePositionAsync ticks; drives diag snapshot cadence
        ' BUG-22: timestamp of the most recent 2-second bar used for P&L; DateTimeOffset.MinValue = not yet set.
        Private _lastPnlBarTimestamp As DateTimeOffset = DateTimeOffset.MinValue

        Private Sub ResetTrailState()
            _lastSlPrice = 0D
            _lastTpPrice = 0D
            _initialSlPrice = 0D
            _initialSlTicks = 0
            _tpAdvanceCount = 0
            _positionLogTickCount = 0
            _lastPnlBarTimestamp = DateTimeOffset.MinValue
            _lastFinalAmount = 0D       ' reset so += accumulation starts fresh on next entry
            _totalDollarPerPoint = 0D   ' reset so += accumulation starts fresh on next entry
            _freeRollActivationPrice = 0D
            _initialTpTicks = 0
            ' FEAT-01
            _openTradeOutcomeId = Nothing
            _openLifespanId = Nothing
            _runningMaeDollars = 0D
            _runningMfeDollars = 0D
            _slRatchetCount = 0
            _freeRideActivatedAt = Nothing
            _tradeEntrySessionWindow = String.Empty
            _barsInTrade = 0
            _lastSignalArgs = Nothing
            _lastSignalBar = Nothing
            _stPrevDirection = 0.0F
        End Sub

        ' ── 15-second bracket trail ───────────────────────────────────────────────

        ''' <summary>
        ''' Timer callback for the 2-second position management loop.
        ''' Handles P&amp;L updates, Free Roll activation, and ATR trailing while a position is open.
        ''' The 2-second period matches the 2-second bar-close cadence used by GetLatestPriceAsync.
        ''' Reentrancy guard prevents overlapping calls when the API is slow.
        ''' </summary>
        Private Sub PositionTimerCallback(state As Object)
            If Not _running Then Return
            If Not _positionOpen Then Return   ' fast-path: nothing to manage
            If Interlocked.CompareExchange(_positionCallbackRunning, 1, 0) <> 0 Then Return

            Task.Run(Async Function() As Task
                         Try
                             Dim ct = If(_cts IsNot Nothing, _cts.Token, CancellationToken.None)
                             Await ManagePositionAsync(ct)
                         Catch ex As OperationCanceledException
                             ' Stop() was called mid-flight — normal shutdown
                         Catch ex As Exception
                             _logger.LogDebug(ex, "ManagePositionAsync error: {Msg}", ex.Message)
                         Finally
                             Interlocked.Exchange(_positionCallbackRunning, 0)
                         End Try
                     End Function)
        End Sub

        ''' <summary>
        ''' Switches the management timer between active (2 s) and idle (60 s) intervals.
        ''' 2-second interval: P&amp;L updates, Free Roll activation check, and ATR trail while open.
        ''' The 2-second period matches the 2-second bar-close cadence so P&amp;L advances once
        ''' per bar close rather than on arbitrary quote ticks.
        ''' Call immediately after every position open/close transition.
        ''' </summary>
        Private Sub SetTrailTimerInterval(positionOpen As Boolean)
            Dim period = If(positionOpen, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60))
            _positionTimer?.Change(period, period)
        End Sub

        ''' <summary>
        ''' FEAT-11: Adapts the bar-check timer cadence to position state.
        '''
        ''' Flat (no position open), MultiConfluence strategy:
        '''   Period = 15 s — the engine fetches 15-second bars so indicator values
        '''   (Cloud, Tenkan, Kijun, EMA, MACD, StochRSI…) advance on every 15-second bar
        '''   close instead of the 5-minute bar close, reducing maximum staleness from
        '''   ~4 m 30 s down to ~30 s (2 missed ticks at the 15-second boundary).
        '''
        ''' All other strategies / position open:
        '''   Period = 30 s — unchanged default cadence. Strategy-timeframe bars are used
        '''   for position management (confluence dissolution, reversal confirmation). The
        '''   30-second cadence matches the existing position-monitoring REST budget.
        '''
        ''' Call at every position open/close transition, mirroring SetTrailTimerInterval.
        ''' </summary>
        Private Sub SetBarCheckInterval(positionOpen As Boolean)
            ' Only MultiConfluence benefits from the 15-second flat cadence.
            ' All other strategy types remain at the standard 30-second interval.
            Dim isMc = (_strategy IsNot Nothing AndAlso
                        _strategy.Condition = StrategyConditionType.MultiConfluence)
            Dim flatSeconds = If(isMc, 15, 30)
            Dim period = If(positionOpen, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(flatSeconds))
            _timer?.Change(period, period)
        End Sub

        ''' <summary>
        ''' Subscribes to real-time GatewayQuote events for the specified PX contract so
        ''' TrailBracketAsync can use last-trade price instead of the 15-second history bar.
        ''' Fire-and-forget — subscription runs in background; engine continues immediately.
        ''' </summary>
        Private Sub SubscribeMarketQuotes(pxContractId As String)
            If String.IsNullOrEmpty(pxContractId) Then Return
            If String.Equals(_subscribedPxContractId, pxContractId, StringComparison.OrdinalIgnoreCase) Then Return
            _subscribedPxContractId = pxContractId
            Volatile.Write(_lastQuotePrice, 0D)
            Task.Run(Async Function() As Task
                         Try
                             Await _marketHub.SubscribeContractAsync(pxContractId, _cts.Token)
                         Catch ex As Exception
                             _logger.LogDebug(ex, "MarketHub subscribe failed for {Id}", pxContractId)
                         End Try
                     End Function)
        End Sub

        ''' <summary>
        ''' Caches the most recent last-trade price from the MarketHub GatewayQuote stream.
        ''' Only runs while a position is open; filters to the engine's own contract.
        ''' Uses Volatile.Write so TrailBracketAsync (different thread) always reads the
        ''' latest value without a memory barrier penalty on the hot path.
        ''' </summary>
        Private Sub OnMarketQuoteReceived(sender As Object, e As MarketQuoteEventArgs)
            If Not _positionOpen Then Return
            If _strategy Is Nothing Then Return
            Dim fav = FavouriteContracts.TryGetBySymbolResolved(_strategy.ContractId)
            Dim expectedId = If(fav IsNot Nothing AndAlso Not String.IsNullOrEmpty(fav.PxContractId),
                                fav.PxContractId, _strategy.ContractId)
            ' Primary match: exact contract ID (e.g. "CON.F.US.M6E.U26").
            ' Fallback: root-symbol match so quotes survive quarterly rolls without a code change
            ' (e.g. MarketHub sends "CON.F.US.M6E.M26" but hardcoded PxContractId is "CON.F.US.M6E.U26").
            Dim exactMatch = String.Equals(e.Quote.ContractId, expectedId, StringComparison.OrdinalIgnoreCase)
            Dim rootMatch = Not String.IsNullOrEmpty(fav?.PxRootSymbol) AndAlso
                            e.Quote.ContractId.IndexOf(fav.PxRootSymbol, StringComparison.OrdinalIgnoreCase) >= 0
            If Not exactMatch AndAlso Not rootMatch Then Return
            ' Prefer last-trade price; fall back to bid/ask mid when no trade has printed yet.
            Dim price As Double = CDbl(e.Quote.LastPrice)
            If price <= 0D AndAlso e.Quote.BidPrice > 0D AndAlso e.Quote.AskPrice > 0D Then
                price = CDbl((e.Quote.BidPrice + e.Quote.AskPrice) / 2D)
            End If
            If price > 0D Then Volatile.Write(_lastQuotePrice, price)
        End Sub

        ' ══════════════════════════════════════════════════════════════════════════
        ' P&L helper
        ' ══════════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Computes live unrealised P&amp;L from cached price.
        ''' Uses tick-adjusted DollarPerPoint so all instruments (including M6E) are correct.
        ''' Returns 0 when any prerequisite is missing.
        ''' </summary>
        Private Function ComputeLivePnl(currentPrice As Decimal) As Decimal
            If currentPrice = 0D OrElse _lastEntryPrice = 0D OrElse _totalDollarPerPoint = 0D Then Return 0D
            Dim direction = If(_lastEntrySide = OrderSide.Buy, 1D, -1D)
            Return Math.Round((currentPrice - _lastEntryPrice) * _totalDollarPerPoint * direction, 2)
        End Function

        ' ══════════════════════════════════════════════════════════════════════════
        ' 1-second position management loop (replaces 5s TrailBracketAsync)
        ' ══════════════════════════════════════════════════════════════════════════

        ''' <summary>
        ''' Runs every 1 second while a position is open.
        ''' Responsibilities: P&amp;L publish · Free Roll activation check · phase-specific trail.
        ''' Fetches the latest 2-second bar close from TopStepX on every tick to ensure P&amp;L
        ''' advances at the 2-second bar-close cadence.  Falls back to the cached quote price
        ''' when the bar fetch fails (network error, rate limit).
        ''' </summary>
        Private Async Function ManagePositionAsync(ct As CancellationToken) As Task
            If Not _openPositionId.HasValue Then Return
            If _lastEntryPrice <= 0D Then Return

            Dim isBuy = (_lastEntrySide = OrderSide.Buy)
            Dim tickSize = If(_strategy.TickSize > 0D, _strategy.TickSize, 0.01D)

            ' BUG-22: Refresh _lastBarClose from the latest 2-second bar close so P&L
            ' advances on 2-second bar closes rather than on sub-second quote ticks.
            ' Falls back to the cached quote (sub-second) when the bar fetch returns 0,
            ' then further falls back to the last known _lastBarClose.
            Dim barClosePrice As Decimal = 0D
            Dim barTs As DateTimeOffset = DateTimeOffset.MinValue
            Try
                barClosePrice = Await _ingestionService.GetLatestPriceAsync(_strategy.ContractId, ct)
                If barClosePrice > 0D Then
                    _lastBarClose = barClosePrice
                    _lastPnlBarTimestamp = DateTimeOffset.UtcNow  ' approximate: bar close ≈ now
                End If
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                _logger.LogDebug(ex, "ManagePositionAsync: 2-s bar fetch failed for {Id}", _strategy.ContractId)
            End Try

            Dim rawQuote = Volatile.Read(_lastQuotePrice)
            Dim currentPrice As Decimal = If(barClosePrice > 0D, barClosePrice,
                                             If(rawQuote > 0D, CDec(rawQuote), _lastBarClose))
            If currentPrice <= 0D Then Return

            ' Keep _lastBarClose in sync regardless of which source was used.
            If barClosePrice <= 0D Then _lastBarClose = currentPrice

            ' Compute and publish live P&L
            Dim livePnl = ComputeLivePnl(currentPrice)

            ' Sanity: log unexpectedly large single-tick P&L jumps (bad quote filter)
            If _strategy IsNot Nothing AndAlso _strategy.TickValue > 0D Then
                Dim pnlJump = Math.Abs(livePnl - _lastApiPnl)
                If pnlJump > 5D * _strategy.TickValue Then
                    _logger.LogDebug("[WARN] {Contract} PnL jump Δ=${Delta:F2} (prev={Prev:F2} new={New:F2})",
                                     _strategy.ContractId, pnlJump, _lastApiPnl, livePnl)
                End If
            End If
            _lastApiPnl = livePnl

            ' FEAT-01: update running MAE/MFE
            If livePnl < _runningMaeDollars Then _runningMaeDollars = livePnl
            If livePnl > _runningMfeDollars Then _runningMfeDollars = livePnl

            RaiseEvent PositionSynced(Me, New PositionSyncedEventArgs(
                _openPositionId.GetValueOrDefault(0L), livePnl, _positionOpenedAt,
                If(_lastFinalAmount > 0D, _lastFinalAmount, 1D),
                isBuy, Math.Max(1, _openTradeCount)))

            ' BUG-22: diagnostics — log bar timestamp and age so staleness is observable.
            ' First tick: always log. Then every 30 ticks (≈ 60 s at 2-second period).
            Dim priceSource = If(barClosePrice > 0D, "2s-bar",
                                 If(rawQuote > 0D, "quote", "cached-bar"))
            _positionLogTickCount += 1
            Dim isDiagTick = (_positionLogTickCount = 1 OrElse _positionLogTickCount Mod 30 = 0)
            If isDiagTick Then
                Dim barAgeMs = If(_lastPnlBarTimestamp > DateTimeOffset.MinValue,
                                  (DateTimeOffset.UtcNow - _lastPnlBarTimestamp).TotalMilliseconds,
                                  Double.NaN)
                Dim ageStr = If(Double.IsNaN(barAgeMs), "age=unknown", $"age={barAgeMs:F0} ms")
                Log($"📈 P&L bar [{priceSource}]: close={currentPrice:F4}  pnl=${livePnl:F2}  {ageStr}")
                Dim slTks = If(_initialSlTicks > 0, _initialSlTicks, Math.Max(1, _pxSettings.DefaultSlTicks))
                _diagLogger?.WritePositionSnapshot(
                    _strategy.ContractId, If(isBuy, "LONG", "SHORT"),
                    _lastEntryPrice, currentPrice, priceSource, livePnl,
                    _lastSlPrice, _lastTpPrice, _openPositionId, slTks, _initialTpTicks)
            End If

            ' Phase-specific bracket management
            Select Case _tradePhase
                Case TradePhase.HardStop
                    If _lastSlPrice <= 0D Then Return  ' bracket not placed yet (warm-up)
                    ' Check Free Roll activation
                    If _freeRollActivationPrice > 0D Then
                        Dim activated = If(isBuy,
                                          currentPrice >= _freeRollActivationPrice,
                                          currentPrice <= _freeRollActivationPrice)
                        If activated Then
                            Await ActivateFreeRollAsync(currentPrice, isBuy, ct)
                            Return  ' trail on next 2s tick
                        End If
                    End If
                    ' Tick-based trail (preserves Wide/Standard/Tight tier, SL + TP together)
                    Await TrailHardStopBracketAsync(currentPrice, isBuy, tickSize, ct)

                Case TradePhase.FreeRoll
                    ' ATR-based trail, TP cancelled — SL is what closes the trade
                    Await TrailFreeRollBracketAsync(currentPrice, isBuy, tickSize, ct)
            End Select
        End Function

        ''' <summary>
        ''' Activates the Free Roll: moves SL to breakeven + CommissionTickBuffer ticks,
        ''' cancels the TP, and transitions to TradePhase.FreeRoll.
        ''' The ratchet guard ensures the new SL is strictly better than the current one
        ''' (handles edge case where price reverses immediately after triggering activation).
        ''' </summary>
        Private Async Function ActivateFreeRollAsync(currentPrice As Decimal, isBuy As Boolean, ct As CancellationToken) As Task
            Dim contract = FavouriteContracts.TryGetBySymbolResolved(_strategy.ContractId)
            Dim commBuffer = If(contract IsNot Nothing, contract.GetCommissionTickBuffer(), 2)
            Dim tickSize = If(_strategy.TickSize > 0D, _strategy.TickSize, 0.01D)

            ' SL at entry ± commBuffer ticks (be = breakeven + fee cover)
            Dim newSl = TickMath.PriceFromTicks(_lastEntryPrice, commBuffer, tickSize, isBuy, isStop:=True)

            ' Ratchet guard: new SL must be strictly better than current
            Dim slImproved = If(isBuy, newSl > _lastSlPrice, newSl < _lastSlPrice)
            If Not slImproved Then newSl = _lastSlPrice  ' keep current if already past BE

            ' Pass Nothing for TP to cancel the resting TP bracket
            Dim ok = Await _orderService.EditPositionSlTpAsync(_openPositionId.Value, newSl, Nothing, cancel:=ct)
            If ok Then
                _lastSlPrice = newSl
                _lastTpPrice = 0D   ' TP cancelled — SL will close the trade in profit
                _tradePhase = TradePhase.FreeRoll
                RaiseEvent TurtleBracketChanged(Me, New TurtleBracketChangedEventArgs(
                    0, newSl, 0D, isAdvance:=False, isFreeRide:=True))
                _diagLogger?.WriteBracketEvent("FREE_ROLL_ON", If(isBuy, "BUY", "SELL"),
                    currentPrice, newSl, 0D,
                    $"Free Roll activated — SL moved to BE+{commBuffer}t @ {newSl:F4} | TP cancelled | ATR trail engaged")
                Log($"[{_strategy.ContractId}] 🎯 Free Roll activated — " &
                    $"SL moved to BE+{commBuffer}t @ {newSl:F4} | TP cancelled | ATR trail engaged")
                _freeRideActivatedAt = DateTimeOffset.UtcNow   ' FEAT-01
            Else
                Log($"⚠️  Free Roll activation failed for {_strategy.ContractId} — will retry next tick")
            End If
        End Function

        ''' <summary>
        ''' Tick-based SL+TP trail used during TradePhase.HardStop.
        ''' Anchors both brackets to current price at the original tier tick distances.
        ''' Ratchet: SL only advances toward profit by ≥ 1 tick.
        ''' </summary>
        Private Async Function TrailHardStopBracketAsync(currentPrice As Decimal, isBuy As Boolean,
                                                          tickSize As Decimal, ct As CancellationToken) As Task
            Dim slTicks = If(_initialSlTicks > 0, _initialSlTicks, Math.Max(1, _pxSettings.DefaultSlTicks))
            ' BUG-14: use the tier-correct TP tick distance stored at entry, not the config default
            Dim tpTicks = If(_initialTpTicks > 0, _initialTpTicks, Math.Max(1, _pxSettings.DefaultTpTicks))

            Dim profitable = If(isBuy, currentPrice > _lastEntryPrice, currentPrice < _lastEntryPrice)
            If Not profitable Then Return

            Dim newSl = TickMath.PriceFromTicks(currentPrice, slTicks, tickSize, isBuy:=isBuy, isStop:=True)
            Dim newTp = TickMath.PriceFromTicks(currentPrice, tpTicks, tickSize, isBuy:=isBuy, isStop:=False)

            Dim slAdvanced = If(isBuy, newSl > _lastSlPrice, newSl < _lastSlPrice)
            If Not slAdvanced Then Return

            Dim ticks = Math.Abs(TickMath.TicksBetween(_lastSlPrice, newSl, tickSize))
            If ticks < 1 Then Return

            Dim isFreeRide = If(isBuy, newSl >= _lastEntryPrice, newSl <= _lastEntryPrice)

            Log($"🔄 Trail [HardStop | {If(isBuy, "BUY", "SELL")}]: " &
                $"SL {_lastSlPrice:F4}→{newSl:F4}  TP {_lastTpPrice:F4}→{newTp:F4} " &
                $"+{ticks}t | price={currentPrice:F4}" &
                If(isFreeRide, " 🔒 free-ride", String.Empty))

            Dim ok = Await _orderService.EditPositionSlTpAsync(_openPositionId.Value, newSl, newTp, cancel:=ct)
            If ok Then
                _lastSlPrice = newSl
                _lastTpPrice = newTp
                _slRatchetCount += 1   ' FEAT-01
                RaiseEvent TurtleBracketChanged(Me, New TurtleBracketChangedEventArgs(
                    0, newSl, newTp, isAdvance:=True, isFreeRide:=isFreeRide))
                If _diagLogger IsNot Nothing Then
                    _diagLogger.WriteBracketEvent("BRACKET_TRAIL", If(isBuy, "BUY", "SELL"),
                        currentPrice, newSl, newTp,
                        $"SL trailed to {newSl:F4}  TP at {newTp:F4} (+{ticks}t)")
                End If
            Else
                Log($"⚠️  Trail API call failed — will retry next tick (1s)")
            End If
        End Function

        ''' <summary>
        ''' ATR-based SL trail used during TradePhase.FreeRoll.
        ''' No TP is sent (cancelled at Free Roll activation); the trailing SL closes the trade.
        ''' Same ratchet logic as ApplyAtrTrailAsync but operates at 1-second resolution.
        ''' </summary>
        Private Async Function TrailFreeRollBracketAsync(currentPrice As Decimal, isBuy As Boolean,
                                                          tickSize As Decimal, ct As CancellationToken) As Task
            If _currentAtrValue <= 0D Then Return  ' ATR not ready (bar warmup)

            Dim slMultiple = If(_strategy.SlMultipleOfN > 0D, _strategy.SlMultipleOfN, 1D)
            Dim atrDistance = slMultiple * _currentAtrValue
            Dim rawCandidate = If(isBuy, currentPrice - atrDistance, currentPrice + atrDistance)

            ' Snap to tick boundary (conservative: floor for longs, ceiling for shorts)
            Dim newSlCandidate As Decimal
            If isBuy Then
                newSlCandidate = CDec(Math.Floor(CDbl(rawCandidate / tickSize))) * tickSize
            Else
                newSlCandidate = CDec(Math.Ceiling(CDbl(rawCandidate / tickSize))) * tickSize
            End If

            ' Ratchet: only advance toward profit
            Dim shouldUpdate = If(isBuy, newSlCandidate > _lastSlPrice, newSlCandidate < _lastSlPrice)
            If Not shouldUpdate Then Return

            Dim improveTicks = Math.Abs(TickMath.TicksBetween(_lastSlPrice, newSlCandidate, tickSize))
            If improveTicks < 1 Then Return

            Dim isFreeRide = If(isBuy, newSlCandidate >= _lastEntryPrice, newSlCandidate <= _lastEntryPrice)

            Log($"🏃 Free Roll trail [{If(isBuy, "BUY", "SELL")}]: " &
                $"SL {_lastSlPrice:F4}→{newSlCandidate:F4} (+{improveTicks}t) " &
                $"ATR={_currentAtrValue:F4}×{slMultiple:F2}N price={currentPrice:F4}" &
                If(isFreeRide, " 🔒 profit-locked", String.Empty))

            ' TP = Nothing: keep the TP cancelled (broker ignores Nothing for unchanged brackets)
            Dim ok = Await _orderService.EditPositionSlTpAsync(_openPositionId.Value, newSlCandidate, Nothing, cancel:=ct)
            If ok Then
                _lastSlPrice = newSlCandidate
                _slRatchetCount += 1   ' FEAT-01
                RaiseEvent TurtleBracketChanged(Me, New TurtleBracketChangedEventArgs(
                    0, newSlCandidate, 0D, isAdvance:=True, isFreeRide:=isFreeRide))
            Else
                Log($"⚠️  Free Roll trail update failed for {_strategy.ContractId} — will retry next tick")
            End If
        End Function

        ''' <summary>
        ''' Handles real-time position updates pushed by the SignalR UserHub (GatewayUserPosition).
        ''' Updates _lastApiPnl and fires PositionSynced immediately so the tile reflects live P&L
        ''' without waiting for the 30-second REST poll tick.
        ''' </summary>
        Private Sub OnLivePositionUpdated(sender As Object, e As PositionUpdateEventArgs)
            ' _stateLock: this handler fires on a SignalR thread-pool thread and shares position
            ' state with the 30-second REST timer.  Lock for the full body — it is synchronous
            ' and holds the lock only for microseconds, so there is no deadlock risk.
            SyncLock _stateLock
                If Not _positionOpen Then Return
                If _strategy Is Nothing Then Return
                ' Match the SignalR full PX contract ID (e.g. "CON.F.US.MES.H26")
                ' against the strategy's friendly symbol ("MES") via FavouriteContracts lookup.
                Dim fav = FavouriteContracts.TryGetBySymbolResolved(_strategy.ContractId)
                Dim expectedId = If(fav IsNot Nothing AndAlso Not String.IsNullOrEmpty(fav.PxContractId),
                                    fav.PxContractId, _strategy.ContractId)
                If Not String.Equals(e.ContractId, expectedId, StringComparison.OrdinalIgnoreCase) Then Return
                If e.NetPosition = 0 Then
                    ' SignalR reports the position is flat.  Pre-arm the miss counter to SyncMissThreshold - 1
                    ' so the very next 30-second REST poll (which will return Nothing) immediately crosses the
                    ' threshold and declares the position closed — eliminating up to 60 s of stale-tile time.
                    ' Guard: skip pre-arm when multiple scale-in positions are tracked — a NetPos=0 push
                    ' during a multi-position trade likely reflects one OCO bracket triggering, not all
                    ' positions closing simultaneously.  REST reconciliation handles the real close.
                    If _openTradeCount <= 1 AndAlso _syncMissCount < SyncMissThreshold - 1 Then
                        _syncMissCount = SyncMissThreshold - 1
                        Log($"⚡ SignalR: NetPos=0 for {e.ContractId} — miss counter pre-armed ({_syncMissCount}/{SyncMissThreshold}). Close will be confirmed on next REST poll.")
                    End If
                    Return
                End If
                ' TopStepX SignalR openPnL is always 0 for futures — compute from price delta.
                ' Price priority: (1) _lastQuotePrice — sub-second MarketHub tick, always preferred;
                '                 (2) _lastBarClose   — set by the 1-second management timer;
                '                 (3) skip update     — no usable price available yet.
                Dim livePnl As Decimal = e.OpenPnL
                If livePnl = 0D Then
                    Dim rawQuote = Volatile.Read(_lastQuotePrice)
                    Dim priceForPnl = If(rawQuote > 0D, CDec(rawQuote), _lastBarClose)
                    If priceForPnl > 0D Then livePnl = ComputeLivePnl(priceForPnl)
                End If
                _lastApiPnl = livePnl
                RaiseEvent PositionSynced(Me, New PositionSyncedEventArgs(
                    _openPositionId.GetValueOrDefault(0L),
                    livePnl,
                    _positionOpenedAt,
                    If(_lastFinalAmount > 0D, _lastFinalAmount, CDec(Math.Abs(e.NetPosition))),
                    _lastEntrySide = OrderSide.Buy,
                    Math.Max(1, _openTradeCount)))
            End SyncLock
        End Sub

        ''' <summary>
        ''' ATR-based trailing SL. Called each bar-check tick while a position is open.
        ''' Computes a new SL at SlMultipleOfN × ATR(14) distance behind the current bar close,
        ''' then advances the broker SL if and only if the new level is strictly better than the
        ''' current one (ratchet — never loosens).
        '''
        ''' Requires at least 1-tick improvement to avoid no-op API calls on quiet bars.
        ''' TopStepX: finds the resting stop bracket and modifies its stop price via EditPositionSlTpAsync.
        ''' </summary>
        Private Async Function ApplyAtrTrailAsync(currentPrice As Decimal, ct As CancellationToken) As Task
            If Not _openPositionId.HasValue Then
                Log($"📊 Trail skip — no positionId resolved yet (waiting for broker sync)")
                Return
            End If
            If _currentAtrValue <= 0D Then
                Log($"📊 Trail skip — ATR=0 (bars not yet analysed or indicator warming up)")
                Return
            End If
            If _lastEntryPrice <= 0D Then
                Log($"📊 Trail skip — entry price unknown")
                Return
            End If

            Dim isBuy = (_lastEntrySide = OrderSide.Buy)
            Dim slMultiple = If(_strategy.SlMultipleOfN > 0D, _strategy.SlMultipleOfN, 1D)
            Dim tickSize = If(_strategy.TickSize > 0D, _strategy.TickSize, 0.01D)

            ' SL candidate: SlMultipleOfN × ATR behind current price
            Dim atrDistance = slMultiple * _currentAtrValue
            Dim rawCandidate = If(isBuy, currentPrice - atrDistance, currentPrice + atrDistance)

            ' Snap to tick boundary (floor for longs = conservative; ceiling for shorts = conservative)
            Dim newSlCandidate As Decimal
            If isBuy Then
                newSlCandidate = CDec(Math.Floor(CDbl(rawCandidate / tickSize))) * tickSize
            Else
                newSlCandidate = CDec(Math.Ceiling(CDbl(rawCandidate / tickSize))) * tickSize
            End If

            ' Seed _lastSlPrice on first tick after entry (in case PlaceBracketOrdersAsync set it to 0)
            If _lastSlPrice <= 0D Then
                _lastSlPrice = newSlCandidate
                Log($"📊 Trail baseline seeded — SL={newSlCandidate:F4} (ATR={_currentAtrValue:F4} × {slMultiple:F2}N) entry={_lastEntryPrice:F4}")
                Return   ' nothing to ratchet yet; establish baseline
            End If

            ' ── Danger Zone: snap SL to breakeven ─────────────────────────────────
            ' Set by the Asset Bassett coordinator when an opposing strategy fires.
            ' Move SL to _lastEntryPrice once and hold; normal ATR trail is suspended
            ' while this flag is True so the SL never retreats from breakeven.
            If DangerZoneActive Then
                Dim beCandidate = _lastEntryPrice
                Dim beBetter = If(isBuy, beCandidate > _lastSlPrice, beCandidate < _lastSlPrice)
                If beBetter Then
                    Dim beTicks = Math.Abs(TickMath.TicksBetween(_lastSlPrice, beCandidate, tickSize))
                    If beTicks >= 1 Then
                        Log($"⚠️ DANGER ZONE: Snapping SL to breakeven {_lastEntryPrice:F4} (was {_lastSlPrice:F4})")
                        Dim beOk = Await _orderService.EditPositionSlTpAsync(
                            _openPositionId.Value, beCandidate, Nothing, cancel:=ct)
                        If beOk Then
                            _lastSlPrice = beCandidate
                            RaiseEvent TurtleBracketChanged(Me, New TurtleBracketChangedEventArgs(
                                0, beCandidate, _lastTpPrice, isAdvance:=True, isFreeRide:=True))
                        End If
                    End If
                End If
                Return   ' danger zone active — no further ATR trail this tick
            End If

            ' Ratchet guard: only advance in the profitable direction
            Dim shouldUpdate = If(isBuy, newSlCandidate > _lastSlPrice, newSlCandidate < _lastSlPrice)
            If Not shouldUpdate Then
                Log($"📊 Trail hold [{If(isBuy, "BUY", "SELL")}]: price={currentPrice:F4} candidate SL={newSlCandidate:F4} ≤ current SL={_lastSlPrice:F4} — no advance | ATR={_currentAtrValue:F4} TP={_lastTpPrice:F4} entry={_lastEntryPrice:F4}")
                Return
            End If

            ' Initial-SL guard: block trail until the candidate has cleared the ATR-derived
            ' initial SL level.  When minSlPoints clamped the broker SL wider than ATR
            ' suggested, the first favourable bar could otherwise jump the resting stop from
            ' the clamped level to a tight ATR level — causing a premature close on any
            ' normal pullback.  Only once the candidate strictly exceeds _initialSlPrice do
            ' we know the trade has genuine profit beyond the natural entry risk.
            If _initialSlPrice > 0D Then
                Dim clearedInitial = If(isBuy,
                    newSlCandidate > _initialSlPrice,
                    newSlCandidate < _initialSlPrice)
                If Not clearedInitial Then
                    Log($"📊 Trail hold [{If(isBuy, "BUY", "SELL")}]: candidate SL={newSlCandidate:F4} hasn't cleared initial SL={_initialSlPrice:F4} — waiting for profit to extend | ATR={_currentAtrValue:F4}")
                    Return
                End If
            End If

            ' Require at least 1 tick improvement to avoid trivial API calls
            Dim improveTicks = Math.Abs(TickMath.TicksBetween(_lastSlPrice, newSlCandidate, tickSize))
            If improveTicks < 1 Then
                Log($"📊 Trail hold [{If(isBuy, "BUY", "SELL")}]: candidate SL={newSlCandidate:F4} vs current={_lastSlPrice:F4} — <1 tick improvement ({improveTicks:F1}t) | ATR={_currentAtrValue:F4}")
                Return
            End If

            Dim isFreeRide = If(isBuy, newSlCandidate >= _lastEntryPrice, newSlCandidate <= _lastEntryPrice)

            Log($"🎯 ATR trail SL [{If(isBuy, "BUY", "SELL")}]: {_lastSlPrice:F4} → {newSlCandidate:F4} " &
                $"(+{improveTicks}t) ATR={_currentAtrValue:F4} × {slMultiple:F2}N" &
                If(isFreeRide, " 🔒 FREE RIDE", String.Empty))

            ' TP is NOT trailed — it is fixed at placement time and only advanced by
            ' ExtendTpIfClosedBeyondTargetAsync (on a bar-close beyond target) or by the
            ' cloud-edge SL override at entry.  Chasing TP by currentPrice + N×ATR every
            ' tick causes the target to recede as fast as price moves — it can never be hit.
            ' Only the SL ratchet is updated here.
            Dim ok = Await _orderService.EditPositionSlTpAsync(
                _openPositionId.Value, newSlCandidate, Nothing, cancel:=ct)

            If ok Then
                _lastSlPrice = newSlCandidate
                RaiseEvent TurtleBracketChanged(Me, New TurtleBracketChangedEventArgs(
                    0, newSlCandidate, _lastTpPrice,
                    isAdvance:=True, isFreeRide:=isFreeRide))
            Else
                Log($"⚠️  ATR trail update failed for {_strategy.ContractId} — will retry next tick")
            End If
        End Function

        ''' <summary>
        ''' Extend TP on close: when a bar closes at or beyond the current TP price, advance
        ''' the TP by one TpMultipleOfN × ATR unit in the trade direction (up to 3 advances).
        ''' Only fires when <see cref="StrategyDefinition.ExtendTpOnClose"/> = True.
        ''' This is independent of the ATR trailing SL — it lets winning trades run further
        ''' without touching the SL ratchet.
        ''' </summary>
        Private Async Function ExtendTpIfClosedBeyondTargetAsync(lastClose As Decimal, ct As CancellationToken) As Task
            If Not _strategy.ExtendTpOnClose Then Return
            If Not _openPositionId.HasValue Then Return
            If _lastTpPrice <= 0D OrElse _lastEntryPrice <= 0D Then Return
            If _tpAdvanceCount >= 3 Then Return
            If _currentAtrValue <= 0D Then Return

            Dim isBuy = (_lastEntrySide = OrderSide.Buy)
            Dim closedBeyond = If(isBuy, lastClose >= _lastTpPrice, lastClose <= _lastTpPrice)
            If Not closedBeyond Then Return

            Dim tpMultiple = If(_strategy.TpMultipleOfN > 0D, _strategy.TpMultipleOfN, 2D)
            Dim advance = tpMultiple * _currentAtrValue
            Dim tickSize = If(_strategy.TickSize > 0D, _strategy.TickSize, 0.01D)

            Dim newTp As Decimal
            If isBuy Then
                newTp = CDec(Math.Ceiling(CDbl((_lastTpPrice + advance) / tickSize))) * tickSize
            Else
                newTp = CDec(Math.Floor(CDbl((_lastTpPrice - advance) / tickSize))) * tickSize
            End If

            Dim ok = Await _orderService.EditPositionSlTpAsync(_openPositionId.Value, Nothing, newTp, cancel:=ct)
            If ok Then
                _tpAdvanceCount += 1
                Log($"🏃 Extend TP [{If(isBuy, "BUY", "SELL")}] advance {_tpAdvanceCount}/3: {_lastTpPrice:F4} → {newTp:F4} (bar closed at {lastClose:F4})")
                _lastTpPrice = newTp
                RaiseEvent TurtleBracketChanged(Me, New TurtleBracketChangedEventArgs(
                    0, _lastSlPrice, newTp, isAdvance:=True, isFreeRide:=False))
            Else
                Log($"⚠️  Extend TP update failed for {_strategy.ContractId} — will retry if bar stays beyond target")
            End If
        End Function

        Private Async Function PushTrailToAllPositionsAsync(ct As CancellationToken) As Task(Of Boolean)
            Await Task.CompletedTask
            Return True
        End Function

        Private Sub WriteDiagPostMortem(reason As String, pnl As Decimal)
        End Sub

        Private Async Function DoReversalFlushAsync(side As OrderSide, price As Decimal, ct As CancellationToken) As Task
            Await Task.CompletedTask
        End Function

        ' ── STRAT-37: TopStepX session boundary constants (UTC) ──────────────────
        ' TopStepX hard daily close: 20:10 UTC (21:10 UK / 15:10 US Central).
        ' Pre-close blackout starts 20 minutes before close so adverse positions can be
        ' exited before the hard cut.  Market re-opens at 22:00 UTC (23:00 UK).
        ' No lower bound is applied — overnight trading (22:00–06:00 UTC) is permitted.
        Private Const TopStepXCloseHourUtc   As Integer = 20  ' 20:10 UTC hard close
        Private Const TopStepXCloseMinuteUtc As Integer = 10
        Private Const PreCloseBlackoutMinutes As Integer = 20 ' entry gate closes this many minutes before the hard cut
        Private Const MarketReopenHourUtc    As Integer = 22  ' 22:00 UTC CME re-open
        Private Const MarketReopenMinuteUtc  As Integer = 0

        ''' <summary>
        ''' Returns True when the current UTC time is inside the TopStepX pre-close blackout
        ''' or CME maintenance window (19:50–22:00 UTC).  New entry orders are suppressed;
        ''' position management continues normally throughout.
        ''' </summary>
        Private Shared Function IsInTopStepXBlackout() As Boolean
            Dim nowUtc = DateTimeOffset.UtcNow
            Dim totalMins = nowUtc.Hour * 60 + nowUtc.Minute
            ' Blackout start = TopStepX close − PreCloseBlackoutMinutes
            Dim blackoutStart = TopStepXCloseHourUtc * 60 + TopStepXCloseMinuteUtc - PreCloseBlackoutMinutes  ' = 19:50
            Dim reopenMins    = MarketReopenHourUtc * 60 + MarketReopenMinuteUtc                              ' = 22:00
            Return totalMins >= blackoutStart AndAlso totalMins < reopenMins
        End Function

        ''' <summary>
        ''' Returns True when the current UTC time falls within the configured legacy integer-hour
        ''' trading window.  When both TradingStartHourUtc and TradingEndHourUtc are 0 the filter
        ''' is disabled (always returns True).  Only new entries are blocked.
        ''' </summary>
        Private Function IsInsideTradingHours() As Boolean
            If _strategy.TradingStartHourUtc = 0 AndAlso _strategy.TradingEndHourUtc = 0 Then Return True
            Dim h = DateTimeOffset.UtcNow.Hour
            Return h >= _strategy.TradingStartHourUtc AndAlso h < _strategy.TradingEndHourUtc
        End Function

        ''' <summary>
        ''' Returns True when the current UTC time falls within the nullable minute-precise window.
        ''' When either property is Nothing the filter is disabled (always returns True).
        ''' </summary>
        Private Function IsInsideTradingWindow() As Boolean
            If Not _strategy.TradingWindowUtcStart.HasValue OrElse Not _strategy.TradingWindowUtcEnd.HasValue Then Return True
            Dim nowUtc = TimeOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime)
            Return nowUtc >= _strategy.TradingWindowUtcStart.Value AndAlso nowUtc <= _strategy.TradingWindowUtcEnd.Value
        End Function

        ''' <summary>
        ''' Returns True when the session P&amp;L has hit or exceeded the MaxDailyLossUsd circuit
        ''' breaker.  Always returns False when MaxDailyLossUsd is 0 (disabled).
        ''' </summary>
        Private Function IsDailyLossLimitHit() As Boolean
            If _strategy.MaxDailyLossUsd <= 0D Then Return False
            Return _sessionPnl <= -_strategy.MaxDailyLossUsd
        End Function

        Private Async Function EvaluateConfidenceActionsAsync(upPct As Double, downPct As Double, side As OrderSide?, price As Decimal, isNewBar As Boolean, ct As CancellationToken) As Task
            ' ── 1. New-entry gate ──────────────────────────────────────────────────────
            If Not _positionOpen Then
                If Not side.HasValue Then
                    ' No signal — flush any no-signal diag entry
                    If _pendingDiagEntry IsNot Nothing Then
                        _diagLogger?.WriteEntry(_pendingDiagEntry)
                        _pendingDiagEntry = Nothing
                    End If
                    Return
                End If
                If Not IsOrderingAllowed.Invoke() Then
                    Log($"⏸  {_strategy.ContractId} market CLOSED — monitoring only (no orders) | signal: {side.Value} up={upPct:F0}%")
                    If _pendingDiagEntry IsNot Nothing Then
                        _pendingDiagEntry.EventType = "REJECT"
                        _pendingDiagEntry.RejectionReason = "Market closed — IsOrderingAllowed returned False"
                        _diagLogger?.WriteEntry(_pendingDiagEntry)
                        _pendingDiagEntry = Nothing
                    End If
                    Return
                End If
                Dim cooldownSecs = (DateTimeOffset.UtcNow - _lastPositionClosedAt).TotalSeconds
                If cooldownSecs < ReEntryCooldownSeconds Then
                    Dim remaining = CInt(ReEntryCooldownSeconds - cooldownSecs)
                    Log($"⏸  Re-entry cooldown — {remaining}s remaining after last close | signal: {side.Value} up={upPct:F0}%")
                    If _pendingDiagEntry IsNot Nothing Then
                        _pendingDiagEntry.EventType = "REJECT"
                        _pendingDiagEntry.RejectionReason = $"Re-entry cooldown ({remaining}s remaining)"
                        _diagLogger?.WriteEntry(_pendingDiagEntry)
                        _pendingDiagEntry = Nothing
                    End If
                    Return
                End If
                If IsDailyLossLimitHit() Then
                    Log($"🛑 Daily loss limit hit (session P&L=${_sessionPnl:F2}, limit=-${_strategy.MaxDailyLossUsd:F0}) — no new entries for {_strategy.ContractId}")
                    If _pendingDiagEntry IsNot Nothing Then
                        _pendingDiagEntry.EventType = "REJECT"
                        _pendingDiagEntry.RejectionReason = $"Daily loss limit hit (session P&L=${_sessionPnl:F2})"
                        _diagLogger?.WriteEntry(_pendingDiagEntry)
                        _pendingDiagEntry = Nothing
                    End If
                    Return
                End If
                If IsInTopStepXBlackout() Then
                    Log($"⏸  Entry blocked — TopStepX pre-close window (gate opens again 22:00 UTC) | signal: {side.Value} up={upPct:F0}%")
                    If _pendingDiagEntry IsNot Nothing Then
                        _pendingDiagEntry.EventType = "REJECT"
                        _pendingDiagEntry.RejectionReason = "TopStepX pre-close blackout (19:50–22:00 UTC)"
                        _diagLogger?.WriteEntry(_pendingDiagEntry)
                        _pendingDiagEntry = Nothing
                    End If
                    Return
                End If
                If Not IsInsideTradingHours() Then
                    Log($"⏸  Outside trading hours (UTC {DateTimeOffset.UtcNow.Hour:00}:xx, window={_strategy.TradingStartHourUtc:00}–{_strategy.TradingEndHourUtc:00}h) — no new entries | signal: {side.Value} up={upPct:F0}%")
                    If _pendingDiagEntry IsNot Nothing Then
                        _pendingDiagEntry.EventType = "REJECT"
                        _pendingDiagEntry.RejectionReason = $"Outside trading hours (UTC {DateTimeOffset.UtcNow.Hour:00}:xx)"
                        _diagLogger?.WriteEntry(_pendingDiagEntry)
                        _pendingDiagEntry = Nothing
                    End If
                    Return
                End If
                If Not IsInsideTradingWindow() Then
                    Log($"⏰ Outside trading window ({_strategy.TradingWindowUtcStart.Value}–{_strategy.TradingWindowUtcEnd.Value} UTC) — entry suppressed | signal: {side.Value} up={upPct:F0}%")
                    If _pendingDiagEntry IsNot Nothing Then
                        _pendingDiagEntry.EventType = "REJECT"
                        _pendingDiagEntry.RejectionReason = $"Outside trading window ({_strategy.TradingWindowUtcStart.Value}–{_strategy.TradingWindowUtcEnd.Value} UTC)"
                        _diagLogger?.WriteEntry(_pendingDiagEntry)
                        _pendingDiagEntry = Nothing
                    End If
                    Return
                End If
                ' All guards passed — place the entry
                Await PlaceBracketOrdersAsync(side.Value, price, Nothing)
                Return
            End If

            ' ── Position is open from here ──────────────────────────────────────────

            ' ── 2. Neutral-band exit (only on new bar to avoid noise) ──────────────
            If isNewBar Then
                If upPct >= NeutralConfidenceLow AndAlso upPct <= NeutralConfidenceHigh Then
                    _adverseConfidenceCount = 0
                    Log($"🟡 Neutral confidence (up={upPct:F0}%) on new bar — flattening position.")
                    Await DoNeutralFlattenAsync(ct)
                    Return
                End If

                ' ── 3. Adverse-confidence exit — N consecutive new bars holding opposite signal ──
                Dim isAdverse = (_currentTrendSide = OrderSide.Buy AndAlso upPct <= ExtremeConfidenceLowThreshold) OrElse
                                (_currentTrendSide = OrderSide.Sell AndAlso upPct >= ExtremeConfidenceHighThreshold)
                If isAdverse Then
                    _adverseConfidenceCount += 1
                    Log($"⚠️  Adverse confidence tick {_adverseConfidenceCount}/{AdverseConfidenceBars} — up={upPct:F0}% against {_currentTrendSide}")
                    If _adverseConfidenceCount >= AdverseConfidenceBars Then
                        Log($"🔴 ADVERSE EXIT — {_adverseConfidenceCount} consecutive adverse bars. Flattening {_strategy.ContractId}.")
                        _adverseConfidenceCount = 0
                        Await DoNeutralFlattenAsync(ct)
                        Return
                    End If
                Else
                    _adverseConfidenceCount = 0
                End If
            End If

            ' ── 4. Scale-in on sustained extreme confidence ────────────────────────
            ' Scale-ins are suppressed once Free Roll is active: the TP is cancelled and
            ' a new OCO bracket would conflict with the pure trailing-SL regime.
            If _tradePhase = TradePhase.FreeRoll Then Return
            If _scaleInTradeCount >= MaxScaleInTrades Then Return
            If Not IsOrderingAllowed.Invoke() Then Return
            ' Profitability gate: only scale into a winning position.
            ' _lastApiPnl < 0 means the REST sync has confirmed a negative P&L — adding size
            ' into a losing trade multiplies the loss on an adverse macro move.
            If _lastApiPnl < 0D Then
                Log($"📊 Scale-in suppressed — position not profitable (P&L=${_lastApiPnl:F2}), waiting for recovery")
                _extremeConfidenceDurationCount = 0
                Return
            End If

            Dim isExtremeBull = (_currentTrendSide = OrderSide.Buy AndAlso upPct >= ScaleInBullThreshold)
            Dim isExtremeBear = (_currentTrendSide = OrderSide.Sell AndAlso downPct >= (100 - ScaleInBearThreshold))
            If isExtremeBull OrElse isExtremeBear Then
                _extremeConfidenceDurationCount += 1
                If _extremeConfidenceDurationCount >= ScaleInRequiredTicks Then
                    _extremeConfidenceDurationCount = 0
                    Dim scaleSide = If(_currentTrendSide = OrderSide.Buy, OrderSide.Buy, OrderSide.Sell)
                    Log($"📈 SCALE-IN #{_scaleInTradeCount + 1}/{MaxScaleInTrades} — {ScaleInRequiredTicks} extreme ticks reached " &
                        $"(up={upPct:F0}%, down={downPct:F0}%) — adding to {scaleSide} position.")
                    Await PlaceBracketOrdersAsync(scaleSide, price, Nothing)
                    _scaleInTradeCount += 1   ' enforce MaxScaleInTrades cap on subsequent ticks
                End If
            Else
                _extremeConfidenceDurationCount = 0
            End If
        End Function

    End Class

End Namespace
