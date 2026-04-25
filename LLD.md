# TopStepTrader — Low-Level Design (LLD)

**Version:** 1.5
**Date:** 2026-04-09
**Status:** Current

---

## 1. Project Structure

```
src/
├── TopStepTrader.Core/
│   ├── Enums/               BrokerType, OrderSide, OrderType, OrderStatus,
│   │                        BarTimeframe, SignalType, StrategyConditionType,
│   │                        StrategyIndicatorType
│   ├── Interfaces/          IOrderService, IAccountService,
│   │                        IBarCollectionService, IContractMetadataService,
│   │                        IBacktestService, ITradingSessionContext, IApiKeyStore
│   ├── Models/              Order, Account, Contract, TradeSignal, MarketBar,
│   │                        BacktestResult, BacktestTrade, LivePositionSnapshot
│   └── Trading/             StrategyDefinition, FavouriteContracts, TickMath,
│                            RiskProfile, StrategyDefaults, TradingSessionContext
│
├── TopStepTrader.API/
│   ├── Http/                PXAccountClient, PXContractClient,
│   │                        PXOrderClient, PXHistoryClient       (ProjectX)
│   │                        YahooFinanceHistoryClient
│   │                        PXHttpClientBase (shared base for all PX clients)
│   ├── SignalR/             MarketHubClient, UserHubClient
│   ├── TokenManagers/       ProjectXTokenManager
│   └── ApiServiceExtensions.vb  (DI registration)
│
├── TopStepTrader.Data/
│   ├── AppDbContext.vb
│   ├── Repositories/        BarRepository, OrderRepository, SignalRepository,
│   │                        BacktestRepository, ContractCacheRepository,
│   │                        TradeOutcomeRepository
│   └── DataServiceExtensions.vb
│
├── TopStepTrader.ML/
│   ├── ModelManager.vb      FileSystemWatcher hot-reload
│   └── TechnicalIndicators.vb  EMA, SMA, RSI, ATR, MACD, BollingerBands,
│                               Ichimoku, StochRSI, ADX, VIDYA, WaveTrend,
│                               SuperTrend, DonchianChannel, ConnorsRSI
│
├── TopStepTrader.Services/
│   ├── Trading/
│   │   ├── StrategyExecutionEngine.vb       Hydra engine
│   │   ├── SniperExecutionEngine.vb         Sniper engine
│   │   ├── CryptoStrategyExecutionEngine.vb CryptoJoe engine
│   │   ├── PumpNDumpExecutionEngine.vb      PumpNDump engine
│   │   ├── ProjectXOrderService.vb          IOrderService implementation
│   │   ├── TopStepXInstrumentCatalog.vb     Cached tick specs
│   │   └── TrendAnalysisService.vb
│   ├── BacktestEngine.vb / BacktestService.vb
│   ├── BarIngestionService.vb
│   ├── Workers/
│   │   ├── BarIngestionWorker.vb
│   │   └── TokenRefreshWorker.vb
│   └── ServicesExtensions.vb  (DI registration)
│
├── TopStepTrader.UI/
│   ├── Views/               DashboardView, HydraView, AssetBassettView, SniperView,
│   │                        PumpNDumpView, CryptoJoeView, BacktestView,
│   │                        QuantLabView, OrderBookView, TestTradeView,
│   │                        ApiKeysView, MainWindow
│   ├── ViewModels/          DashboardViewModel, HydraViewModel, AssetBassettViewModel,
│   │                        SniperViewModel, PumpNDumpViewModel, CryptoJoeViewModel,
│   │                        BacktestViewModel, QuantLabViewModel,
│   │                        OrderBookViewModel, TestTradeViewModel,
│   │                        ApiKeysViewModel, ViewModelLocator
│   │   └── Base/            ViewModelBase, TradingViewModelBase, RelayCommand
│   ├── Styles/              Dark theme, control overrides, converters
│   │                        StrategyCards.xaml — shared card body templates (10 strategies)
│   └── Infrastructure/      AppBootstrapper.vb (DI composition root)
│
└── TopStepTrader.Tests/
    └── Trading/             TopStepXTickTests.vb, CryptoStrategyEngineTests.vb
```

---

## 2. Dependency Injection

### 2.1 Composition Root

`AppBootstrapper.vb` is a VB.NET Module (static singleton) that owns the `IHost`.

**Startup sequence:**

```
AppBootstrapper.BuildHost()
  1. IConfiguration — appsettings.json
  2. AddLogging (console + debug)
  3. IOptions<T> bindings:
       ProjectXSettings  ← "ProjectX"
       RiskSettings      ← "Risk"
       TradingSettings   ← "Trading"
       MLSettings        ← "ML"
       IngestionSettings ← "Ingestion"
       ClaudeSettings    ← "Claude"
  4. AddDataServices()     → EF Core DbContext + repositories
  5. AddApiServices()      → Named HttpClients + SignalR hubs
  6. AddMLServices()       → ModelManager (Singleton, FileSystemWatcher)
  7. AddApplicationServices() → All business services and engines

AppBootstrapper.InitialiseServices(host)
  1. Resolve IServiceScope → call db.EnsureCreated() + db.EnsureSchemaCurrent()
  2. Seed dummy BalanceHistory rows if table is empty
  3. Resolve ModelManager → start FileSystemWatcher for ML hot-reload
```

### 2.2 Service Lifetimes

**Singleton (one instance for app lifetime):**

| Registration | Type |
|---|---|
| `ITradingSessionContext` → `TradingSessionContext` | Global account state |
| `IApiKeyStore` → `ApiKeyStore` | File-backed credential store |
| `TopStepXInstrumentCatalog` | Tick spec cache (15-min TTL) |
| `ProjectXTokenManager` | ProjectX JWT shared across all PX clients |
| `MarketHubClient` | SignalR real-time ticks |
| `UserHubClient` | SignalR real-time fills/positions |
| `RateLimiter` | Shared HTTP rate-limit window |
| `BarIngestionWorker` (→ IHostedService) | Background bar polling |
| `TokenRefreshWorker` (→ IHostedService) | Background token refresh |
| `ModelManager` | ML model + FileSystemWatcher |
| `ViewModelLocator` | Per-view DI scope manager |
| `MainWindow` | Shell window |

**Scoped (one instance per IServiceScope / per view):**

| Registration | Type |
|---|---|
| `AppDbContext` | EF Core unit of work |
| `BarRepository` | Bar reads/writes |
| `OrderRepository` | Order reads/writes |
| `SignalRepository` | Signal reads/writes |
| `BacktestRepository` | Backtest run reads/writes |
| `ContractCacheRepository` | Contract metadata cache |
| `TradeOutcomeRepository` | Realised P&L records |
| `IOrderService` → `ProjectXOrderService` | TopStepX order routing |
| `IAccountService` → `AccountService` | Account metadata |
| `IBalanceHistoryService` → `BalanceHistoryService` | Balance snapshots |
| `BarIngestionService` | Per-session bar polling |
| `IBarCollectionService` → `BarCollectionService` | Historical bar download |
| `IContractMetadataService` → `ContractMetadataService` | Contract lookup |
| `TrendAnalysisService` | Technical analysis |
| `StrategyParserService` | NL strategy parsing |
| `ClaudeReviewService` | AI trade review |
| `IBacktestService` → `BacktestEngine` | Simulation runner |

**Transient (new instance every resolution):**

| Registration | Type |
|---|---|
| `StrategyExecutionEngine` | Hydra engine |
| `CryptoStrategyExecutionEngine` | CryptoJoe engine |
| `DiagnosticLogger` | Per-engine structured logger + companion positions CSV |
| All Views (XAML code-behind) | WPF controls |
| All ViewModels | MVVM view models |

> **Note:** `SniperExecutionEngine` and `PumpNDumpExecutionEngine` are instantiated directly by their ViewModels using `New` (with constructor injection resolved manually via the view's scope), rather than being in the DI container.

### 2.3 ViewModelLocator Pattern

`ViewModelLocator` (Singleton) maintains a dictionary of named `IServiceScope` instances, one per view. Each scope is created lazily on first property access.

```vb
' Simplified structure
Private _scopes As New Dictionary(Of String, IServiceScope)

Public ReadOnly Property DashboardViewModel As DashboardViewModel
    Get
        Return GetOrCreateScope("Dashboard").ServiceProvider
                .GetRequiredService(Of DashboardViewModel)()
    End Get
End Property
```

**Why:** WPF navigation does not have a natural scope boundary. Creating one scope per view ensures that EF Core's `DbContext` is correctly scoped (not shared across tabs), and that scoped services like repositories are not inadvertently shared between views that are active simultaneously.

### 2.4 TradingViewModelBase

`TradingViewModelBase` is the base class for ViewModels that deal with broker accounts. It contains **only session-level UI state** — nothing instrument-specific:

| Member | Type | Purpose |
|---|---|---|
| `Accounts` | `ObservableCollection(Of Account)` | Account dropdown items (populated at view load) |
| `SelectedAccount` | `Account` | Currently selected account; fires `IsFormReady` and `CanExecute` refresh |

**What it does NOT contain:** bars, indicator state, confidence scores, direction flags, vote counts, position state, or per-instrument data of any kind.

`HydraViewModel` and `CryptoJoeViewModel` both inherit this base class. Each declares its own `Assets As New ObservableCollection(Of HydraAssetViewModel)` property independently — the base class carries no asset collection. This ensures that Hydra's five futures cards and CryptoJoe's two crypto cards are separate, independently-owned collections and can never share a reference.

> **State isolation rule (from architecture review):** `TradingViewModelBase` is shared by multiple ViewModels. Any instrument-specific state placed here would be shared across all inheriting VMs — the last instrument to write wins. All per-instrument state must live inside the instrument's own `HydraAssetViewModel` or inside the engine instance that owns it.

---

## 3. Core Domain

### 3.1 StrategyDefinition

The full parameter set passed to an execution engine at startup. The ViewModel deep-copies it before passing to the engine so that UI changes mid-trade do not affect a running position.

| Property | Type | Notes |
|---|---|---|
| `ContractId` | String | ProjectX dotted contract ID or instrument symbol key |
| `AccountId` | String | Broker account ID |
| `CapitalAtRisk` | Decimal | Dollar amount used to compute contract count |
| `Quantity` | Integer | Contracts |
| `TimeframeMinutes` | Integer | Bar timeframe (maps to BarTimeframe enum) |
| `DurationHours` | Integer | Engine lifetime before auto-stop |
| `ExpiresAt` | DateTimeOffset | Calculated at start: UtcNow + DurationHours |
| `Indicator` | StrategyIndicatorType | Indicator family (EMA, RSI, BollingerBands, etc.) |
| `IndicatorPeriod` | Integer | Primary indicator period |
| `IndicatorMultiplier` | Double | Secondary multiplier (e.g. BB std dev) |
| `SecondaryPeriod` | Integer | Slow EMA for cross strategies |
| `Condition` | StrategyConditionType | Entry condition (16 types) |
| `GoLongWhenBelowBands` | Boolean | Long bias flag |
| `GoShortWhenAboveBands` | Boolean | Short bias flag |
| `MinConfidencePct` | Integer | 0–100, minimum score to open a trade |
| `AdxThreshold` | Double | ADX gate value (default 25) |
| `InitialSlAmount` | Decimal | Initial SL in dollars (fallback if ATR unavailable) |
| `InitialTpAmount` | Decimal | Initial TP in dollars (fallback if ATR unavailable) |
| `SlMultipleOfN` | Decimal | SL = N × ATR × DollarPerPoint |
| `TpMultipleOfN` | Decimal | TP = N × ATR × DollarPerPoint |
| `LeveragedSlMultipleOfN` | Decimal | Alternative SL multiple (reserved) |
| `InitialStopTicks` | Integer? | Direct tick override (bypasses dollar conversion) |
| `TickSize` | Decimal | Stamped from FavouriteContracts at start |
| `TickValue` | Decimal | Stamped from FavouriteContracts at start |
| `Leverage` | Integer | Capital scaling factor for contract count computation |
| `MaxScaleIns` | Integer | Maximum additional entries after initial |
| `ScaleInAmount` | Decimal | Dollar amount per scale-in |
| `ScaleInLeverage` | Integer | Leverage factor for scale-in entries |
| `Name` | String | Human-readable strategy name |

### 3.2 Order

Broker-agnostic order representation persisted to SQLite.

| Property | Type | Notes |
|---|---|---|
| `Id` | Long | Local SQLite PK |
| `ExternalOrderId` | String | Broker-assigned order ID |
| `ExternalPositionId` | String | Broker position ID (resolved post-fill via UserHub) |
| `ContractId` | String | Instrument identifier |
| `AccountId` | String | Broker account ID |
| `Side` | OrderSide | Buy or Sell |
| `OrderType` | OrderType | Market, Limit, StopOrder, StopLimit |
| `Quantity` | Integer | Number of contracts |
| `LimitPrice` | Decimal? | Limit price for Limit orders |
| `StopPrice` | Decimal? | Stop trigger for Stop/StopLimit orders |
| `StopLossRate` | Decimal? | Initial SL price (historic field) |
| `TakeProfitRate` | Decimal? | Initial TP price (historic field) |
| `InitialStopTicks` | Integer? | Bracket tick count (signed, ProjectX convention) |
| `InitialTakeProfitTicks` | Integer? | Bracket tick count (signed, ProjectX convention) |
| `EstimatedEntryPrice` | Decimal? | Pre-fill estimate for tick conversion |
| `Status` | OrderStatus | Pending/Working/Filled/Cancelled/etc. |
| `PlacedAt` | DateTimeOffset | When the order was submitted |
| `FilledAt` | DateTimeOffset? | When the order filled |
| `FillPrice` | Decimal? | Actual fill price |
| `Broker` | BrokerType | Always `TopStepX` |
| `Notes` | String | Strategy name / source tag |

### 3.3 LivePositionSnapshot

Returned by `GetLivePositionSnapshotAsync()` — a point-in-time position view from the broker.

| Property | Type | Notes |
|---|---|---|
| `PositionId` | Long | Broker-assigned position ID |
| `UnrealizedPnlUsd` | Decimal | Current open P&L in USD |
| `OpenedAtUtc` | DateTime | When the position was opened |
| `IsBuy` | Boolean | True = Long position |
| `OpenRate` | Decimal | Average entry price |
| `Units` | Decimal | Contract count |
| `PositionCount` | Integer | Number of scale-ins contributing |

### 3.4 FavouriteContracts

Static module (`FavouriteContracts.vb`) providing the master instrument list. Never hits the network — all data is hardcoded and version-controlled.

**Key methods:**

| Method | Returns | Notes |
|---|---|---|
| `GetDefaults(broker)` | List(Of FavouriteContract) | Filtered to instruments tradeable on that broker |
| `TryGetBySymbol(symbol)` | FavouriteContract? | Looks up by instrument symbol key or PX root symbol |
| `TryGetByPxContractId(id)` | FavouriteContract? | Looks up by full PX dotted contract ID |

**FavouriteContract fields (selection):**

| Field | Example |
|---|---|
| `Name` | "Micro Gold" |
| `EToroContractId` | "GOLD.24-7" (retained for bar storage key / Yahoo lookup) |
| `PxRootSymbol` | "MGC" |
| `PxContractId` | "CON.F.US.MGC.J26" (front-month, updated by catalog) |
| `PxTickSize` | 0.1 |
| `PxTickValue` | 1.0 |
| `PxMinStopDollars` | 15.0 (minimum stop in dollars for MES) |
| `PxMinStopTicks` | 4 (local fallback; catalog provides live value) |
| `YahooSymbol` | "GC=F" (used by BarCollectionService for Yahoo Finance requests) |
| `MultiConfluenceTimeframeMinutes` | 5 (bar timeframe used by the Multi-Confluence strategy for this instrument: Oil=5, Gold=10, S&P=5, EUR/USD=10, BTC=15) |

### 3.5 TickMath

Static module providing tick arithmetic.

| Method | Signature | Purpose |
|---|---|---|
| `DollarsToTicks` | `(dollars, tickValue, contracts, tickSize)` → Integer | Converts dollar SL/TP to tick count |
| `TicksToPrice` | `(entryPrice, ticks, side, tickSize)` → Decimal | Converts tick offset to absolute price |
| `TicksBetween` | `(priceA, priceB, tickSize)` → Decimal | Signed tick distance between two prices |
| `SnapToTickGrid` | `(price, tickSize, roundingMode)` → Decimal | Rounds price to nearest tick |
| `ApproximateTickSize` | `(lastPrice)` → Decimal | Heuristic tick size from price magnitude |

### 3.6 RiskProfile

Three named profiles (Lewis, Damian, Joe) with pre-set parameters applied when a profile button is clicked.

| Profile | Capital | SlN | TpN | ADX | Confidence |
|---|---|---|---|---|---|
| Lewis (conservative) | $200 | 1.5 | 3.0 | 25 | 90% |
| Damian (balanced) | $500 | 1.0 | 2.0 | 20 | 80% |
| Joe (aggressive) | $1,000 | 0.75 | 2.0 | 15 | 70% |

---

## 4. API Layer

### 4.1 HTTP Client Configuration

Two named `HttpClient` instances registered in DI:

| Name | BaseAddress | Timeout | Notes |
|---|---|---|---|
| `"ProjectX"` | ProjectX REST base URL | 60s | Shared by all PX clients |
| `"Yahoo"` | Yahoo Finance | 30s | Custom User-Agent to avoid 429 |

All PX clients attach the PX JWT from `ProjectXTokenManager` on every request via a delegating handler.

### 4.2 ProjectX Clients

All extend `PXHttpClientBase` which provides `GetAsync<T>` / `PostAsync<T>` with JWT injection and JSON deserialisation.

| Client | Key Methods |
|---|---|
| `PXAccountClient` | `GetAccountsAsync()`, `GetAccountAsync(id)` |
| `PXContractClient` | `SearchContractsAsync(text)`, `GetCatalogueAsync()`, `GetContractDetailsAsync(id)` |
| `PXOrderClient` | `PlaceOrderAsync(request)`, `CancelOrderAsync(accountId, orderId)`, `SearchOpenOrdersAsync(accountId)`, `EditOrderAsync(accountId, orderId, request)` |
| `PXHistoryClient` | `RetrieveBarsAsync(contractId, unit, unitNumber, limit, live, startTime, endTime, cancel)` — primary history fetch (unit code: 1=Second, 2=Minute, 3=Hour, 4=Day); `SearchOrderHistoryAsync(accountId, from, to)` |

### 4.3 Key API Request / Response Models

**`ModifyOrderRequest` (sent by `EditOrderAsync`):**

| Field | JSON key | Notes |
|---|---|---|
| `OrderId` | `orderId` | Resting bracket order ID to modify |
| `OrderType` | `type` | `1`=Limit (TP) or `4`=Stop (SL). Required by the API or the modify is silently rejected. Serialised only when non-null (`JsonIgnore(WhenWritingNull)`). |
| `Size` | `size` | Optional quantity change |
| `LimitPrice` | `limitPrice` | New limit price (TP edits) |
| `StopPrice` | `stopPrice` | New stop price (SL edits) |

**`OpenPositionResponse` (returned by `/api/Position/searchOpen`):**

The endpoint returns `size`/`type` rather than a signed `netPos`. The model exposes computed compatibility properties for downstream code:

| API field | JSON key | Type | Notes |
|---|---|---|---|
| `Size` | `size` | Integer | Raw unsigned contract count |
| `PositionType` | `type` | Integer | `1`=long (bought), `2`=short (sold) |
| `AveragePrice` | `averagePrice` | Double | Average fill price |
| `OpenPnL` | `openPnl` | Double | Always `0` for TopStepX futures — compute locally |
| `NetPos` *(computed)* | — | Integer | `+Size` if long, `-Size` if short — backward-compat alias |
| `NetPrice` *(computed)* | — | Double | Alias for `AveragePrice` — backward-compat alias |

**`OpenOrderResponse` / `OpenTradeResponse`:**

`CreationTimestamp` is a `String` (ISO-8601 or epoch text as returned by the API). Previously typed as `Long`, which caused JSON deserialisation failures on non-numeric timestamps.

### 4.3 SignalR Hubs

| Hub | Events | Purpose |
|---|---|---|
| `UserHubClient` | `GatewayUserTrade` | Order fill confirmation — provides fill price and time |
| `MarketHubClient` | `GatewayQuote` | Real-time bid/ask tick stream per subscribed contract |

Both hubs are Singleton and maintain a persistent WebSocket connection. The `UserHubClient` is subscribed by `TestTradeViewModel` and `ProjectXOrderService` to resolve fill details without polling.

---

## 5. Data Layer

### 5.1 Database Schema (SQLite via EF Core)

**Tables:**

| Table | Entity | Key Columns |
|---|---|---|
| `Bars` | `MarketBar` | `ContractId`, `Timeframe`, `Timestamp` (unique index) |
| `Orders` | `Order` | `Id` (PK), `ExternalOrderId`, `AccountId`, `Status` |
| `Signals` | `TradeSignal` | `Id` (PK), `ContractId`, `GeneratedAt` |
| `BacktestRuns` | `BacktestRunEntity` | `Id` (PK), `ContractId`, `StartDate`, `EndDate`, `Status` |
| `BacktestTrades` | `BacktestTradeEntity` | `Id` (PK), `RunId` (FK → BacktestRuns) |
| `ContractCache` | `ContractCacheEntry` | `ContractId` (PK), `CachedAt` |
| `BalanceHistory` | `BalanceHistoryEntry` | `Id` (PK), `AccountId`, `RecordedAt`. `DashboardViewModel` records `Balance` when `TotalValue = 0` (TopStepX futures accounts always return `TotalValue = 0`). |
| `TradeOutcomes` | `TradeOutcomeEntity` | `Id` (PK), `OrderId` (FK → Orders) |

**Schema migration:** `EnsureSchemaCurrent()` is called at startup. If a new column is detected as missing, it is added via `ALTER TABLE … ADD COLUMN` (additive-only migration — no destructive changes).

### 5.2 Repository Patterns

All repositories are Scoped and receive `AppDbContext` via constructor injection.

**Known EF Core / SQLite workarounds:**

| Workaround | Reason |
|---|---|
| `FromSqlInterpolated` for string filtering | VB.NET compiles `String = String` in EF expression trees to `String.Compare()`, which SQLite cannot translate to SQL |
| In-memory `.OrderBy(Function(x) x.Timestamp)` | EF Core SQLite cannot ORDER BY on `DateTimeOffset` columns |

**BarRepository** — key method:

```vb
' Uses FromSqlInterpolated to bypass VB.NET string comparison translation bug
Function GetBarsAsync(contractId As String, timeframe As BarTimeframe,
                      from As DateTime, [to] As DateTime) As Task(Of List(Of MarketBar))
```

---

## 6. Execution Engines

### 6.1 Common Engine Pattern

All four engines follow the same lifecycle:

```
Constructor  → Inject IOrderService, IBarIngestionService, ILogger, ITradingSessionContext
StartAsync() → Validate strategy, create CancellationTokenSource, start timer
  Timer tick → RunCycleAsync(ct)
    → Ingest bars → compute indicators → evaluate signal
    → If signal: OpenTradeAsync()
    → If position open: ManagePositionAsync() (trail SL/TP)
    → If exit condition: CloseTradeAsync()
StopAsync()  → Cancel CTS, await running cycle, FlattenContractAsync() if position open
```

### 6.2 StrategyExecutionEngine (Hydra / Asset Bassett)

**Purpose:** Multi-asset confidence engine. One instance per asset card per Hydra session, or one instance per enabled strategy per Asset Bassett session.

**Key state fields:**

| Field | Type | Purpose |
|---|---|---|
| `_positionOpen` | Boolean | Whether a position is currently open |
| `_openPositionId` | Long? | Broker position ID (resolved post-fill) |
| `_lastSlPrice` | Decimal | Last SL price pushed to broker |
| `_lastTpPrice` | Decimal | Last TP price pushed to broker |
| `_currentAtrValue` | Decimal | Latest ATR from bar data |
| `_lastEntryPrice` | Decimal | Entry fill price |
| `_lastEntrySide` | OrderSide | Long or Short |
| `_openTradeCount` | Integer | Scale-in counter |
| `_positionTimer` | `System.Threading.Timer` | Adaptive timer for `TrailBracketAsync`: 5s while a position is open, 60s when idle; independent of the 30-second main timer |
| `_positionCallbackRunning` | Integer | Interlocked reentrancy guard for `_positionTimer` (0 = idle, 1 = running) |
| `_lastBarClose` | Decimal | Freshest available price: upgraded to the most recent 15-second bar close via `GetLatestPriceAsync` on every tick when a position is open; falls back to the strategy-timeframe bar close. Single shared source for all P&L calculation paths. |
| `_lastApiPnl` | Decimal | Last calculated unrealised P&L (USD). Updated by the 30-second REST sync and by every SignalR `GatewayUserPosition` push. Used as the final P&L figure on close. |

**Public coordinator hooks (Asset Bassett):**

| Member | Type | Purpose |
|---|---|---|
| `IsOrderingAllowed` | `Func(Of Boolean)` | Lambda injected by the coordinator; returns `False` to block new entry and scale-in when another engine owns the position |
| `DangerZoneActive` | `Boolean` (read/write) | Set `True` by the coordinator when an opposing engine fires; triggers SL snap to breakeven in `ApplyAtrTrailAsync` |
| `LastEntryPrice` | `Decimal` (readonly) | Exposes `_lastEntryPrice` so the coordinator can verify the breakeven level |

**TopStepX order placement flow:**

```
PlaceBracketOrdersAsync()
  1. Fixed ClaudeTrader ticks: slTicks = Max(1, ProjectXSettings.DefaultSlTicks)   ' 50
                               tpTicks = Max(1, ProjectXSettings.DefaultTpTicks)   ' 25
  2. TopStepXInstrumentCatalog.ClampStopTicksAsync() → enforces API minimum
  3. Signed bracket convention:  Long SL = negative ticks, Short SL = positive ticks
  4. ProjectXOrderService.PlaceOrderAsync() — entry order with stopLossBracket + takeProfitBracket
  5. Post-placement: 4 × 750ms retry to resolve PositionId via GetLivePositionSnapshotAsync()
```

Note: ATR-based sizing (`slN × ATR × DollarPerPoint`) was removed. `SlMultipleOfN` / `TpMultipleOfN` are now **backtest-only** parameters and have no effect on live order sizing.

**Adaptive bracket trail (`TrailBracketAsync`):**

```
Fires every 5s while a position is open, 60s when idle (_positionTimer; independent of 30s main timer).
SetTrailTimerInterval(positionOpen) switches the period at every _positionOpen = True/False transition.

Guard conditions (all must pass):
  _openPositionId.HasValue
  _lastEntryPrice > 0
  _lastSlPrice > 0

  currentPrice = Await GetLatestPriceAsync(contractId)   ' 15-second bar close
  profitable   = (isBuy AND currentPrice > entry) OR (isSell AND currentPrice < entry)
  If Not profitable → Return

  newSl = TickMath.PriceFromTicks(currentPrice, slTicks, tickSize, isBuy, isStop:=True)
  newTp = TickMath.PriceFromTicks(currentPrice, tpTicks, tickSize, isBuy, isStop:=False)

  slAdvanced = (isBuy AND newSl > _lastSlPrice) OR (isSell AND newSl < _lastSlPrice)
  If Not slAdvanced → Return                               ' ratchet guard
  If TicksBetween(_lastSlPrice, newSl, tickSize) < 1 → Return

  EditPositionSlTpAsync(positionId, newSl, newTp)
  → type=4 Stop  → StopPrice  (SL)
  → type=1 Limit → LimitPrice (TP)
  If ok: _lastSlPrice = newSl; _lastTpPrice = newTp
         Raise TurtleBracketChanged(isFreeRide = newSl ≥ entry for long)
```

**Free-ride:** When SL advances to or past average entry price, `_freeRideActive = True`. A `TurtleBracketChanged` event fires with `IsFreeRide = True` to update the UI badge.

**Position ID resolution gap:** ProjectX `PlaceOrderAsync` returns `orderId` only — `positionId` is not available until the fill event arrives. The engine retries `GetLivePositionSnapshotAsync` up to 4 times (750ms intervals). If unresolved, the trail activates at the next 60s reconciliation cycle instead.

**Live P&L calculation (`_lastBarClose` + `GetLatestPriceAsync`):**

TopStepX REST and SignalR both return `openPnL = 0` for futures — the broker never populates this field. Real-time P&L is derived entirely from price movement against the entry rate.

`_lastBarClose` is the single shared price source for all P&L paths. On every bar-check tick it is first set from the strategy-timeframe bar close, then — when a position is open — upgraded to the most recent 15-second bar close fetched from `IBarIngestionService.GetLatestPriceAsync`.

```
P&L priority order (applied identically in REST sync and SignalR handler):
  1. snapshot.UnrealizedPnlUsd  if non-zero  (authoritative; currently always 0 for TopStepX)
  2. (_lastBarClose - snapshot.OpenRate) × DPP × direction  if _lastBarClose > 0 and DPP > 0
  3. 0D
```

**HydraViewModel force-close P&L:** `HydraViewModel` reads `HydraAssetViewModel.LastLivePnl` — the engine-computed P&L cached on every `PositionSynced` tick (~5 s, using sub-second MarketHub quotes). This gives the force-close monitor the same real-time accuracy as the tile display. When no engine P&L is available yet (e.g. position opened externally before engine start), the monitor falls back to `IBarIngestionService.GetLatestPriceAsync` and computes: `(currentPrice - snapshot.OpenRate) × DollarPerPoint × Amount × direction`.

**HydraAssetViewModel self-heal:** `UpdateTradeStatus(positionCount:=n)` now allows a broker-confirmed position update through even when `_positionCount = 0`, provided the broker reports `positionCount ≥ 1`. This prevents a transient API miss (which crossed `SyncMissThreshold` and zeroed the local count) from causing the tile to display "No position" while the trade is actually still open. `CloseTrade()` now shows `"— No position"` (no P&L suffix) and calls `ClearSlStatus()` on close to avoid stale SL text persisting on the tile.

`IBarIngestionService.GetLatestPriceAsync(contractId)` — implemented by:

| Implementation | Behaviour |
|---|---|
| `TopStepXBarIngestionService` | Calls `PXHistoryClient.RetrieveBarsAsync(unit:=1, unitNumber:=15, limit:=5)` — fetches the last five 15-second bars. Returns the close of the most recent bar. Silent catch returns `0D` on any failure. No DB write. |
| `BarIngestionService` (Yahoo) | Returns `Task.FromResult(0D)` — Yahoo has no sub-minute bars; callers fall back to strategy-tf bar close. |

### 6.3 DiagnosticLogger

`DiagnosticLogger` (Transient, `Services/Diagnostics/DiagnosticLogger.vb`) is injected into each engine instance. It writes two files per session:

**Primary JSONL log** — one JSON object per engine event (entry, exit, bar check, trail, etc.).

**Companion positions CSV** — one row every 5 minutes while a position is open. Created alongside the JSONL with suffix `_positions.csv`.

Columns: `Timestamp, Contract, Side, EntryPrice, CurrentPrice, PriceSource, PnL_USD, SL_Price, TP_Price, PositionID, SL_Ticks, TP_Ticks`

```
WritePositionSnapshot(contractId, side, entryPrice, currentPrice, priceSource,
                      pnl, slPrice, tpPrice, positionId, slTicks, tpTicks)
```

- Thread-safe via `_positionLock` (SyncLock).
- No-op when no session is active (`_positionWriter Is Nothing`).
- Both files share the same session ID and contract name in their file names.
- Both are flushed and closed in `Stop()` / `Dispose()`.

### 6.5 SniperExecutionEngine (Sniper)

**Purpose:** 3-EMA Cascade pyramid. Builds a position tier-by-tier using `BracketPair` objects.

**BracketPair (inner class):**

| Property | Purpose |
|---|---|
| `SlOrderId As Long?` | Resting SL bracket order ID |
| `TpOrderId As Long?` | Resting TP bracket order ID |
| `Qty As Integer` | Contracts for this tier |
| `EntryPrice As Decimal` | Fill price for this tier |
| `CurrentSlPrice As Decimal` | Current SL price (ratchets up) |
| `CurrentTpPrice As Decimal` | Current TP price (ratchets away) |
| `AddIndex As Integer` | 0 = first core, 1 = second core, etc. |

**Trail logic (`ManageTrailingStopsAsync`):**

```
For each bracket:
  isCore = AddIndex < _coreAddsCount
  trailFactor = If(isCore, 2.0, 1.0)    ' core trail = loose, add-on = tight
  trailDist = _stopLossTicks × trailFactor × tickSize

  BUY:
    potentialSl = currentPrice - trailDist
    Add-on breakeven: if profit > 5t → potentialSl ≥ entryPrice + 1t
    If potentialSl > (CurrentSlPrice + 1t) → shouldUpdate = True

  SELL:
    potentialSl = currentPrice + trailDist
    (symmetric)

  If shouldUpdate:
    Compute trailing TP:
      tpDist = _takeProfitTicks × trailFactor × tickSize
      BUY:  rawTp = ceil((currentPrice + tpDist) / tick) × tick
            If rawTp > CurrentTpPrice → newTpCandidate = rawTp
      SELL: rawTp = floor((currentPrice - tpDist) / tick) × tick
            If rawTp < CurrentTpPrice → newTpCandidate = rawTp
    → GetLivePositionSnapshotAsync() → EditPositionSlTpAsync(positionId, targetSl, newTp?)
    → Update CurrentSlPrice and CurrentTpPrice on success
```

**TopStepX bracket initialisation:** `PlaceBracketAsync()` stamps `CurrentSlPrice` and `CurrentTpPrice` from `_stopLossTicks × tickSize` and `_takeProfitTicks × tickSize` relative to `avgEntry`.

**Structure-fail exit:** If price breaks below EMA21 by more than `_ema21BreakTicks × tickSize` (configurable), `FlattenContractAsync()` is called and the engine stops.

### 6.6 CryptoStrategyExecutionEngine (CryptoJoe)

**Purpose:** Two CME crypto micro-futures engines (MBT — Micro Bitcoin, GMET — Micro Ether). Runs one independent instance per asset in CryptoJoeViewModel.

**Stepped trail (`ApplySteppedTrailAsync`):**

```
profitPct = (currentPrice - entryPrice) / entryPrice × 100   (for Buy)
If profitPct < 2.0% → Return   (trail not yet armed)

steps = Floor((profitPct - 2.0) / 0.5)   (0.5% step size)
If steps ≤ _trailLastSteps → Return       (same step — no change)

slPct     = (2.0 + steps × 0.5) - 1.5    (SL trails 1.5% behind step)
newSlPrice = entryPrice × (1 ± slPct/100)

Trailing TP:
  If _currentAtrValue > 0 AND _lastTpPrice > 0:
    rawTp = currentPrice ± (TpN × ATR)
    If isBuy AND rawTp > _lastTpPrice  → newTpPrice = Round(rawTp, 4)
    If isSell AND rawTp < _lastTpPrice → newTpPrice = Round(rawTp, 4)

EditPositionSlTpAsync(positionId, slRate:=newSlPrice, tpRate:=newTpPrice?)
  On success: _lastTpPrice = newTpPrice.Value
```

**Scale-in logic:** If `_scaleInTradeCount < MaxScaleInTrades` and a fresh entry signal fires while a position is already open, `ScaleInAsync()` adds contracts using `ScaleInAmount` and `ScaleInLeverage`.

### 6.7 PumpNDumpExecutionEngine (PumpNDump)

**Purpose:** 3-bar price-action scalp. Intentionally tightens TP on momentum fade — the only engine where TP moves toward price. After a configured P&L threshold is hit, it switches to a free-ride + SL ratchet mode to close the trade when the trend reverses.

**Entry:** Three consecutive same-direction bars trigger entry. Direction determined by comparing `bar.Close > bar.Open` (bullish) or `bar.Close < bar.Open` (bearish).

**TP tightening (`TightenTakeProfitsAsync`):** On each poll, if `barRange < FadeAtrFraction × ATR` (bar is smaller than the fade threshold), `currentTp` is adjusted by `TightenTicksPerBar × tickSize` toward entry. This continues until `_freeRideActive` becomes True, at which point TP management stops and SL ratcheting begins.

**Free-ride (`ApplyFreeRideAsync`):** Triggered once when `UnrealisedPnl ≥ _freeRidePnlThreshold`. Moves all bracket SLs to breakeven (entry price). Sets `_freeRideActive = True`.

```
For each bracket b in _brackets:
  If b.SlOrderId Is Nothing → skip (no SL to move)
  CancelOrderAsync(SlOrderId)
    → Success: replace with new SL at entry price
      → If replace fails: b.SlOrderId = Nothing, anyUnprotected = True
    → Failure: leave original SL in place
  If anyUnprotected → EmergencyCloseAsync()
```

**Trailing SL after free-ride (`TrailStopsAsync`):** Called from `DoCheckAsync` on every 15-second poll once `_freeRideActive = True`. Ratchets the SL behind current price by `_stopLossTicks × tickSize`. The ratchet only moves in the profit direction — it never loosens. When price reverses enough to hit the trailing SL, the broker closes the position automatically.

```
trailDistance = _stopLossTicks × tickSize
For each bracket b in _brackets:
  BUY:  targetSl = currentPrice - trailDistance
        If targetSl > b.CurrentSlPrice → shouldUpdate = True
  SELL: targetSl = currentPrice + trailDistance
        If targetSl < b.CurrentSlPrice → shouldUpdate = True
  If shouldUpdate:
    CancelOrderAsync(b.SlOrderId)
      → Success: PlaceOrderAsync(new SL at targetSl)
        → If fails: b.SlOrderId = Nothing, anyUnprotected = True
      → Failure: leave existing SL
If anyUnprotected → EmergencyCloseAsync()
```

**`DoCheckAsync` branching:**

```
If Not _freeRideActive Then
    TightenTakeProfitsAsync()           ' TP moves toward entry on fade
    If UnrealisedPnl ≥ threshold Then
        ApplyFreeRideAsync()            ' SLs move to breakeven, _freeRideActive = True
Else
    TrailStopsAsync(_lastClose)         ' SL ratchets behind price until trend reverses
End If
```

**TP does NOT use the ratchet-away pattern.** TP tightens toward entry (inverted) until free-ride activates, then TP management halts and SL takes over.

### 6.8 TestTradeViewModel (Test Trade)

**Purpose:** Diagnostic one-click order placement with full active bracket management. Not an autonomous engine — the user drives entry manually. Supports BUY / SELL / Close Trade buttons, ATR tier selection, and real-time SL/TP management via a 15-second timer.

**ATR tier selector:** Three mutually-exclusive buttons (Tight / Standard / Wide) bound to `SelectAtrTierCommand`. Sets `SlMultipleOfN` and `TpMultipleOfN` identically to the Hydra/AssetBassett tier system:

| Tier | SL | TP |
|---|---|---|
| Tight | 0.75 × ATR | 1.5 × ATR |
| Standard | 1.5 × ATR | 3.0 × ATR |
| Wide | 2.5 × ATR | 5.0 × ATR |

**`ManageTradeAsync` (runs every 15 seconds while position open):**

```
1. Seed _initialSlPrice from _currentSlPrice on first management tick.
2. Compute ATR(14) and lastBarClose from _cachedBars (≥14 bars required).
3. Fetch current market price via FetchCurrentPriceAsync (15-second bar).

ATR-based trailing SL:
  slDistance   = SlMultipleOfN × ATR(14)
  candidate    = currentPrice ∓ slDistance  (buy: minus, sell: plus)
  snap to tick grid (floor for buy, ceiling for sell)
  initial-SL guard: skip if candidate hasn't cleared _initialSlPrice yet
  ratchet guard: only update if improvement ≥ 1 tick in profitable direction
  → EditPositionSlTpAsync(positionId, newSl, Nothing)
  → If ok: _currentSlPrice = newSl; flag "FREE RIDE" when newSl ≥ entry

Extend TP on bar-close (if ExtendTpOnClose = True and _tpAdvanceCount < 3):
  If lastBarClose closed at or beyond _currentTpPrice:
    advance = TpMultipleOfN × ATR(14)
    newTp   = (currentTp ± advance) snapped to tick grid
    → EditPositionSlTpAsync(positionId, Nothing, newTp)
    → If ok: _currentTpPrice = newTp; _tpAdvanceCount += 1
```

**State reset on close:** `_initialSlPrice = 0D` and `_tpAdvanceCount = 0` are cleared in both `CloseTradeAsync` and `StopStrategy`.

**P&L computation:** TopStepX returns `openPnL = 0` for futures. The ViewModel fetches current price via `GetLivePositionSnapshotAsync` and derives P&L from `(currentPrice - entryPrice) × DollarPerPoint × direction`.

---

### 6.9 Multi-Confluence Dual-Frequency Evaluation (Hydra)

The Multi-Confluence strategy in Hydra uses two independent evaluation cadences:

#### Per-Asset Bar Timeframe

`FavouriteContracts.MultiConfluenceTimeframeMinutes` stores the correct strategy-bar timeframe for each MC asset. `HydraViewModel.ExecuteStart` reads this value and stamps `sd.TimeframeMinutes` per engine — overriding the strategy-level default when `Condition = MultiConfluence`.

| Asset | MC Timeframe |
|---|---|
| OIL (MCLE) | 5 min |
| GOLD (MGC) | 10 min |
| S&P 500 (MES) | 5 min |
| EUR/USD (M6E) | 10 min |
| BTC (MBT) | 15 min |

#### 15-Second Live Indicator Refresh

Between strategy-bar evaluations (every 5–15 min) the Hydra indicator grid would otherwise show stale Close, Ichimoku, ADX, and EMA values. A best-effort live-refresh block fires inside `DoCheckAsync` whenever the engine is in flat MC mode (`isMcFlat = True`):

```
1. GetLiveBarsAsync(contractId, BarTimeframe.FifteenSecond, 3)  — fetches the last three 15-second bars
2. Substitute the live bar's Close into the existing strategy-tf bar series (last element replaced)
3. MultiConfluenceStrategy.Evaluate(highs, lows, liveCloses, ...) → liveResult
4. Raise ConfidenceUpdated with IsDisplayOnly = True carrying:
     liveResult.Cloud1/2, Tenkan, Kijun, Ema21/50, PlusDI, MinusDI, AdxValue, LastClose = live close
     Bull/bear scores, MACD, StochRSI, LongCount, ShortCount preserved from the real mcArgs
5. Wrapped in Try/Catch — failure is silent; does not affect signal logic
```

**`ConfidenceUpdatedEventArgs.IsDisplayOnly`** — when `True`, `HydraAssetViewModel.ApplyConfidence` takes a fast path that updates only the indicator grid columns (GridClose, GridCloud1/2, GridTenkan, GridKijun, GridEma21/50, GridAdx, GridDiPlus/Minus) and returns immediately without touching confidence scores, signal summary, or tile state. Signal-path events (`IsDisplayOnly = False`) continue unchanged.

---

## 7. Order Service

### 7.1 ProjectXOrderService

**Key design decisions:**

| Decision | Detail |
|---|---|
| No `takeProfitBracket` by default | `ManageTakeProfit = False` — TP bracket only sent when explicitly set |
| Bracket type 4 = Stop Market | Only supported SL bracket type in ProjectX API |
| Bracket type 1 = Limit | TP bracket type |
| Long SL ticks = negative | ProjectX signed tick convention |
| Short SL ticks = positive | ProjectX signed tick convention |
| `FlattenContractAsync` cancels both legs | Cancels type=4 (SL) AND type=1 (TP) before `CloseContractAsync` to prevent orphaned TP Limit from re-opening a position |

**`EditPositionSlTpAsync` implementation:**

```
1. Fetch all open orders for account (single SearchOpenOrdersAsync call)
2. Filter to contractId + (type=4 OR type=1)
3. For SL (type=4):  update StopPrice
4. For TP (type=1):  update LimitPrice  (non-fatal if not found)
5. Only early-return if BOTH slRate AND tpRate are Nothing
```

**`PlaceOrderAsync` — TopStepX path:**

```vb
Dim request = New PXOrderRequest With {
    .AccountId  = accountId,
    .ContractId = resolvedContractId,
    .OrderType  = 2,           ' Market
    .Side       = side,
    .Size       = contracts,
    .StopLossBracket = New PXBracketSpec With {
        .StopType = 4,
        .Ticks    = slTicks     ' negative for Long, positive for Short
    },
    .TakeProfitBracket = If(ManageTakeProfit, New PXBracketSpec With {
        .StopType = 1,
        .Ticks    = tpTicks     ' positive for Long, negative for Short
    }, Nothing)
}
```

### 7.2 TopStepXInstrumentCatalog

Singleton with 15-minute TTL cache per instrument.

```
GetContractIdAsync(rootSymbol, isPractice)
  → Check cache (ConcurrentDictionary, TTL 15 min)
  → If stale: PXContractClient.SearchContractsAsync(rootSymbol)
      → Select front-month (nearest expiry, live=!isPractice)
      → Store in cache

ClampStopTicksAsync(rootSymbol, requestedTicks)
  → GetMinStopTicksAsync(rootSymbol)
      → PXContractClient.GetContractDetailsAsync()
      → Parse minimumTickStop from response
      → Fallback: FavouriteContracts.PxMinStopTicks
  → Return Math.Max(requestedTicks, minStopTicks)
```

---

## 8. Enumerations

### StrategyConditionType (16 values)

| Value | Engine | Description |
|---|---|---|
| `FullCandleOutsideBands` | — | Entire candle outside Bollinger Bands |
| `CloseOutsideBands` | — | Close only outside Bollinger Bands |
| `RSIOversold` | — | RSI below 30 |
| `RSIOverbought` | — | RSI above 70 |
| `EMACrossAbove` | — | Fast EMA crosses above slow EMA |
| `EMACrossBelow` | — | Fast EMA crosses below slow EMA |
| `EmaRsiWeightedScore` | Hydra, CryptoJoe | 6-signal weighted confidence score |
| `TripleEmaCascade` | Sniper | EMA8 > EMA21 > EMA50 alignment |
| `MultiConfluence` | Hydra, CryptoJoe | 7-indicator all-must-align |
| `LultDivergence` | Hydra, CryptoJoe | WaveTrend anchor/trigger divergence, time-gated |
| `BBSqueezeScalper` | Hydra, CryptoJoe | %B + RSI7, dual-mode (breakout + bounce) |
| `VidyaCross` | Hydra, CryptoJoe | VIDYA(14) adaptive EMA crossover |
| `ConnorsRsi2` | Backtest | ConnorsRSI-2 mean-reversion |
| `SuperTrend` | Backtest | SuperTrend trend-following |
| `DonchianBreakout` | Backtest | Donchian channel breakout |
| `BbRsiReversion` | Backtest | Bollinger Bands + RSI mean reversion |
