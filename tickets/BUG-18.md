# BUG-18 PumpNDump EmergencyCloseAsync uses stale internal qty instead of broker-confirmed position count

**Status:** Open  
**Category:** Bugs  
**Size:** S  
**Source:** Code-Review  
**Files:** `src\TopStepTrader.Services\Trading\PumpNDumpExecutionEngine.vb:948–987`

## Problem

`EmergencyCloseAsync` places a single market close order for `_currentQty` contracts (line 958).
`_currentQty` is maintained purely by the engine's internal state machine — incremented by 1
each time `PlaceOrderAsync` returns without throwing (lines 573, 628). It is never reconciled
against the broker's actual reported position count.

If any entry or scale-in order was accepted by the order service but subsequently rejected at
the exchange (reported as `Working` briefly then cancelled), `_currentQty` overstates the real
broker position. The emergency close order will be for more contracts than the broker holds,
causing:
- A broker rejection of the over-size order, OR
- A partial fill that leaves a residual open position the engine no longer tracks.

In either case the engine resets all state (lines 980–986) and raises `TradeClosed`, falsely
declaring the position closed while a real position remains live at the broker.

## Proposed Fix

Before placing the emergency close order, call `GetLivePositionSnapshotAsync` to retrieve the
broker-authoritative contract count. Use the broker-reported quantity for the close order
quantity, falling back to `_currentQty` only if the snapshot call fails:

```
Dim snapshot = Await _orderService.GetLivePositionSnapshotAsync(_accountId, _contractId, Nothing, ct)
Dim closeQty = If(snapshot IsNot Nothing AndAlso snapshot.Units > 0,
                  CInt(snapshot.Units), _currentQty)
```

Log a discrepancy warning if `closeQty <> _currentQty`.

## Acceptance Criteria

- [ ] `EmergencyCloseAsync` queries `GetLivePositionSnapshotAsync` before placing the close order.
- [ ] Close order quantity uses broker-reported units when available.
- [ ] A `⚠️` log line is emitted when broker qty differs from `_currentQty`.
- [ ] Falls back to `_currentQty` if the snapshot call throws or returns Nothing.
- [ ] Build passes; all tests still pass.
