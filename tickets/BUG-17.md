# BUG-17 PumpNDump ViewModel passes eToro BrokerType — all entry orders rejected by ProjectX API

**Status:** Open  
**Category:** Bugs  
**Size:** XS  
**Source:** Code-Review  
**Files:** `src\TopStepTrader.UI\ViewModels\PumpNDumpViewModel.vb:404–410`

## Problem

`PumpNDumpViewModel.ExecuteStart()` passes the selected account's broker type directly to
`IPumpNDumpExecutionEngine.Start()` (line 409):

```vb
If(_selectedAccount IsNot Nothing, _selectedAccount.Broker, BrokerType.eToro)
```

Inside `PumpNDumpExecutionEngine.Start()`, the TopStepX / ProjectX contract-ID resolution
only activates when `brokerType = BrokerType.TopStepX` (line 168). When an eToro account is
selected:
- `_effectiveContractId` is set to the raw Yahoo Finance symbol (e.g. `"ES=F"`).
- Tick size and tick value are NOT overridden from `FavouriteContracts.PxTickSize/PxTickValue`.
- Every entry and bracket order is submitted to the ProjectX API with the wrong contract ID
  and potentially wrong tick specs.
- The API rejects all orders. The engine runs silently, watching bars but never trading.

The PumpNDump strategy is TopStepX-only (it uses `ProjectXOrderService` and PX contract IDs).
An eToro account selection should be blocked at the UI level with a clear error, not silently
misconfigured at the engine level.

## Proposed Fix

In `PumpNDumpViewModel`:
1. Add a guard in `CanStart` (or `ExecuteStart`) that returns `False` / shows an error message
   when `_selectedAccount.Broker <> BrokerType.TopStepX`.
2. Update the `CanStart` property message / tooltip so the user sees "TopStepX account required"
   when a non-TopStepX account is selected.

No engine changes required.

## Acceptance Criteria

- [ ] Selecting an eToro account disables the Start button (or shows a validation message).
- [ ] Selecting a TopStepX account re-enables Start as before.
- [ ] Engine `Start()` is never called with `BrokerType.eToro` from this ViewModel.
- [ ] Build passes; all tests still pass.
