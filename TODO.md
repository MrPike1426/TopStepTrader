# TopStepTrader — Development Backlog

## TODO-001 · Wire ITradeOutcomeRepository into StrategyExecutionEngine

**Priority:** High
**Effort:** ~2 hours
**Goal:** Create a proper live trade ledger in SQLite so application-triggered trades can be queried and analysed by date, contract, strategy, and P&L.

### Background

The `TradeOutcomes` SQLite table and `ITradeOutcomeRepository` already exist and are part of the schema. However, `StrategyExecutionEngine` never injects or writes to this repository — so no live trade P&L history is captured. The `Orders` table records individual order placements and `DiagnosticLogger` writes JSONL indicator snapshots, but there is no single queryable record linking an entry to its exit and outcome.

### Steps

1. **Verify DI registration**
   Check `DataServiceExtensions.vb` (called from `AddDataServices()` in `AppBootstrapper.vb`).
   Confirm `ITradeOutcomeRepository` → `TradeOutcomeRepository` is already registered as Scoped.
   If not, add it.

2. **Inject into StrategyExecutionEngine**
   Add `ITradeOutcomeRepository` as a constructor parameter (alongside `DiagnosticLogger`).
   `StrategyExecutionEngine` is resolved as **Transient** — EF Core Scoped services can be injected into Transient because each engine instance is created inside a per-view `IServiceScope` (see `ViewModelLocator.vb`).

3. **On TradeOpened — save open record**
   In the code path that fires `TradeOpened` (after bracket placement succeeds), call:
   ```vb
   Await _tradeRepo.SaveAsync(New TradeOutcomeEntity With {
       .ContractId     = _strategy.ContractId,
       .Timeframe      = _strategy.TimeframeMinutes,
       .SignalType      = e.Side.ToString(),
       .SignalConfidence = e.ConfidencePct,
       .ModelVersion   = _strategy.StrategyName,
       .EntryTime      = e.EntryTime,
       .EntryPrice     = e.EntryPrice,
       .IsOpen         = True
   })
   ```
   Store the returned entity ID in a field (e.g. `_openTradeOutcomeId As Integer?`) so it can be referenced at close.

4. **On TradeClosed — update the record**
   In the code path that fires `TradeClosed`, call:
   ```vb
   If _openTradeOutcomeId.HasValue Then
       Await _tradeRepo.UpdateAsync(_openTradeOutcomeId.Value, New TradeOutcomeUpdate With {
           .ExitTime   = DateTimeOffset.UtcNow,
           .ExitPrice  = exitPrice,
           .PnL        = e.PnL,
           .ExitReason = e.ExitReason,
           .IsWinner   = e.PnL > 0,
           .IsOpen     = False
       })
       _openTradeOutcomeId = Nothing
   End If
   ```

5. **Reset state on engine restart**
   In `ResetTrailState()` (or wherever `_lastBarWasStale` is reset), also reset `_openTradeOutcomeId = Nothing`.

6. **Run tests**
   `dotnet test --project src/TopStepTrader.Tests/TopStepTrader.Tests.vbproj`
   All 213 tests should pass with no regressions.

### TradeOutcomeEntity fields (reference)

| Field | Type | Source |
|---|---|---|
| `SignalId` | Integer | auto |
| `OrderId` | Long? | ExternalOrderId from TradeOpened |
| `ContractId` | String | StrategyDefinition.ContractId |
| `Timeframe` | Integer | StrategyDefinition.TimeframeMinutes |
| `SignalType` | String | e.Side.ToString() ("Buy"/"Sell") |
| `SignalConfidence` | Integer | e.ConfidencePct |
| `ModelVersion` | String | StrategyDefinition.StrategyName |
| `EntryTime` | DateTimeOffset | e.EntryTime |
| `EntryPrice` | Decimal | e.EntryPrice |
| `ExitTime` | DateTimeOffset? | set on close |
| `ExitPrice` | Decimal? | set on close |
| `PnL` | Decimal? | e.PnL |
| `IsWinner` | Boolean? | PnL > 0 |
| `ExitReason` | String | e.ExitReason ("TP"/"SL"/"Reversal"/"Closed") |
| `IsOpen` | Boolean | True on open, False on close |

### Query examples (after implementation)

```sql
-- All completed trades for a contract, newest first
SELECT * FROM TradeOutcomes WHERE ContractId='CON.F.US.MBT.M26' AND IsOpen=0 ORDER BY EntryTime DESC;

-- Win rate by strategy
SELECT ModelVersion, COUNT(*) AS Trades, SUM(CASE WHEN IsWinner=1 THEN 1 ELSE 0 END) AS Wins,
       ROUND(AVG(PnL),2) AS AvgPnL
FROM TradeOutcomes WHERE IsOpen=0
GROUP BY ModelVersion ORDER BY AvgPnL DESC;

-- Today's P&L
SELECT SUM(PnL) FROM TradeOutcomes WHERE IsOpen=0 AND DATE(EntryTime) = DATE('now');
```

---

*Added 2026-03-30*
