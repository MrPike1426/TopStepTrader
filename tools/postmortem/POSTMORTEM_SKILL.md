# Post-Mortem Skill

## Purpose
Run a **deterministic post-mortem** on a TopStepTrader trade using data captured
to `debug_trades.db` during a live session.

The same report structure is produced every time so post-mortems are comparable
across dates, symbols, and sessions.

---

## Two IDs, One Trade

TopStepX tracks a trade under **two different IDs**:

| ID | What it maps to | Where seen in TopStepX |
|---|---|---|
| **Order ID** | The initial entry order | Orders tab |
| **Trade ID** | The closed trade (wraps TP/SL orders) | Trades tab |

**Correlation key:** `Order Time` in the Orders tab == `Entry Time` in the Trades tab.
Use the time (to the second) to match a TopStepX record to an internal debug record.

---

## How Closure Source Is Determined

When running the skill, specify **who closed the trade**:

| Value | Meaning |
|---|---|
| `app` | TP or SL fired programmatically by the application |
| `manual` | Trader closed the position manually on the TopStepX platform |
| `unknown` | Not confirmed — verify against the TopStepX Orders tab |

Distinguish these by looking at the Orders tab:
- If the paired SL Stop Market order shows `Filled` → **app**
- If it shows `Cancelled (Cancelled by trader)` and a separate Market order closed it → **manual**

---

## Running the Skill

### Interactive (prompted)
```
python tools/postmortem/postmortem.py
```

### Non-interactive (CI / Copilot / Claude Code)
```
python tools/postmortem/postmortem.py \
  --order-id  2927418157 \
  --trade-id  2549065559 \
  --symbol    /MNQ \
  --entry-time "2026-05-05T14:15:03" \
  --closed-by app \
  --issue "Why was this trade placed if it was closed 39 seconds later?"
```

`--issue` is optional free text — state your specific concern about the trade.
It appears verbatim in **Section 8 (Trader Issue)** and is used by the
auto-analysis in **Section 9** to give that concern priority context.

### List recent debug trades (discovery)
```
python tools/postmortem/postmortem.py --list
```

### Override DB path
```
python tools/postmortem/postmortem.py --db "C:\path\to\debug_trades.db" ...
```

---

## Default DB Location

The CLI resolves the DB the same way the app does
(`DebugTradeDbContext.ResolveDiagnosticsFolder` in
`src/TopStepTrader.Data/Debug/DebugTradeDbContext.vb`):

1. **Dev build** — walks up from the current working directory (and the script's
   own directory as a fallback) looking for any `*.sln`/`*.slnx`, up to 10 hops.
   When found, uses `<solution-root>\Diagnostics\debug_trades.db`.
2. **Published build** — falls back to
   `%LOCALAPPDATA%\TopStepTrader\Diagnostics\debug_trades.db`.

Use `--db <path>` to override when running against a copy or a different machine.

---

## Report Sections (always in this order)

1. **Trade Identity** — TopStepX Order ID, Trade ID, internal debug ID, symbol, direction, slot
2. **Execution Summary** — entry time, entry price, fill price, slippage, exit, duration, P&L
3. **Risk Profile** — initial SL/TP, risk $, planned R:R, MFE, MAE, ATR, ADX
4. **Closure** — app vs manual, exit reason, final stop phase
5. **Event Timeline** — all debug snapshot events in order
6. **Bar-by-Bar** — OHLC + indicators for each BarClose event
7. **AI / Signal Checks** — any AiCheck events logged during the trade
8. **Trader Issue** — your verbatim concern passed via `--issue` (or prompted interactively)
9. **Auto-Analysis** — flags derived automatically from the debug data, e.g.:
   - 🚩 Trade lasted < 60s with `Health=Exiting` on first bar → entry/exit logic misalignment
   - ⚠️ Short duration (< 3 min) with borderline health
   - ⚠️ Significant fill slippage relative to trade duration
   - 🚩 Loss at or near full initial risk (stop hit at worst case)
   - ✅ No concerns detected
10. **Verdict** — Winner / Loser / Breakeven + free-text observations

---

## Output

| Destination | Path |
|---|---|
| Markdown file | `tools/postmortem/reports/<SYMBOL>_<YYYYMMDD>_<order-id>.md` |
| Stdout | Full report printed to terminal |

---

## No Debug Data?

If no matching record is found the skill generates a **skeleton report** using
only the TopStepX IDs and entry time you supplied.

Reasons data may be missing:
- Debug Capture was not enabled (`Settings → Debug Capture = Off`)
- The database was purged (auto-purge runs at startup, keeps last N days)
- Entry time prefix is wrong — try a shorter prefix, e.g. `2026-05-05T14:15`

---

## Example Post-Mortem Invocation (from the screenshots)

```
python tools/postmortem/postmortem.py \
  --order-id  2927418157 \
  --trade-id  2549065559 \
  --symbol    /MNQ \
  --entry-time "2026-05-05T14:15:03" \
  --closed-by manual
```

Explanation:
- Order ID `2927418157` → `/MNQ` Buy Market filled at 27,991.25 on 2026-05-05 14:15:03
- Trade ID `2549065559` → entry 14:15:03.904, exit 14:46:37.148, P&L **+$206.50**
- Closure source: `manual` — the SL order `2927418638` shows `Filled` (Stop Market
  hit at 2,828.8 on `/M2K` companion slot), but the MNQ position ran for 31 mins
  before exiting — consistent with a manual close on the platform

---

## For AI Agents (Copilot / Claude Code)

When a user asks for a post-mortem on a trade:

1. Ask for (or extract from context): **Order ID**, **Trade ID**, **Symbol**, **Order Time**, **how was it closed**
2. Run: `python tools/postmortem/postmortem.py --order-id X --trade-id Y --symbol Z --entry-time T --closed-by C`
3. The report will be printed to stdout **and** saved under `tools/postmortem/reports/`
4. Summarise the Verdict section and highlight any MFE vs exit discrepancy (left money on the table?) or MAE vs SL placement insight
