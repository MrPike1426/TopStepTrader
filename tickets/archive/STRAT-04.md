# [STRAT-04] Default ATR Tier to Standard When Multi-Confluence + Gold

**Status:** ✅ Complete  
**Category:** Strategy Improvements  
**Size:** XS  

## Summary
Added Gold+Damian detection in `HydraViewModel.ApplyRiskProfile()`. When `profile.Name.Contains("Damian")` and any asset in the roster has `Symbol = "Gold"` or `ContractId.Contains("MGC")`, `ApplyAtrTier("Standard")` is called automatically. Prevents the Tight tier's 0.75×ATR stop from sitting inside Gold's normal 15-min bar range. All other persona/asset combos unaffected. Build: 338 passed, 0 failed.
