# Ticket 001 — Test Trade: EMA / RSI Trend Analysis with BUY / SELL Buttons

| Field            | Value                                                          |
|------------------|----------------------------------------------------------------|
| **Ticket ID**    | TICKET-001                                                     |
| **Date**         | 2026-02-26                                                     |
| **Requested By** | MrPike1426                                                     |
| **Implemented By** | Manus AI                                                     |
| **Status**       | Complete                                                       |
| **AI Model Used** | None at runtime (deterministic math — zero token cost)        |

---

## Request

> On the Test Trade tab, collect the past 24 bars and make a quick judgement based on combined EMA and RSI strategies based on which way the trend is going. Instead of making it a buy, offer a % chance of Up and Down re direction of action, then have two buttons instead of Run Test Trade. One for Test BUY and one for Test SELL.

---

## Strategy Reference Documents (Google Drive — Day Trading folder)

The implementation was guided by the following documents from the user's Google Drive:

1. **AIT TA Approach.pdf** — Defines the core technical analysis framework:
   - EMA 21 as the primary trend spotter
   - EMA 50 as the bigger-picture guide
   - RSI 14 for momentum and overbought/oversold detection
   - Stochastic RSI for additional confirmation

2. **TCG 3 EMA Strategies To Master.pdf** — Three EMA crossover strategies:
   - 9/21 EMA crossover for short-term signals
   - Price position relative to EMA for trend direction
   - EMA slope analysis for momentum

3. **Chart, Prices and Indicators Explained.pdf** — Indicator configuration:
   - EMA 21 (yellow) and EMA 50 (blue) on chart
   - RSI 14 with 70/30 overbought/oversold thresholds
   - Stochastic RSI (14, 14, 3, 3) for confirmation

---

## What Was Implemented

### 1. New Model: `TrendAnalysisResult` (Core layer)

**File:** `src/TopStepTrader.Core/Models/TrendAnalysisResult.vb`

A new model class that holds the output of the trend analysis:

| Property          | Type               | Description                                      |
|-------------------|--------------------|--------------------------------------------------|
| `UpProbability`   | `Double`           | Probability (0–100%) that price will move up     |
| `DownProbability` | `Double`           | Probability (0–100%) that price will move down   |
| `EMA21`           | `Single`           | Current EMA 21 value                             |
| `EMA50`           | `Single`           | Current EMA 50 value                             |
| `RSI14`           | `Single`           | Current RSI 14 value                             |
| `LastClose`       | `Decimal`          | Most recent bar close price                      |
| `Summary`         | `String`           | Human-readable summary of the analysis           |
| `BarsAnalysed`    | `Integer`          | Number of bars used in the analysis              |
| `AnalysedAt`      | `DateTimeOffset`   | Timestamp of the analysis                        |
| `Signals`         | `List(Of String)`  | Individual indicator signal descriptions         |

### 2. New Service: `TrendAnalysisService` (Services layer)

**File:** `src/TopStepTrader.Services/Trading/TrendAnalysisService.vb`

A deterministic trend analysis engine that combines six weighted indicator signals:

| Signal                | Weight | Logic                                                                 |
|-----------------------|--------|-----------------------------------------------------------------------|
| EMA Crossover         | 25%    | EMA 21 above/below EMA 50 → bullish/bearish                          |
| Price vs EMA 21       | 20%    | Close above/below EMA 21 → bullish/bearish                           |
| Price vs EMA 50       | 15%    | Close above/below EMA 50 → bullish/bearish                           |
| RSI Trend             | 20%    | RSI > 70 = overbought (bearish), < 30 = oversold (bullish), gradient between |
| EMA 21 Momentum       | 10%    | EMA 21 rising/falling compared to previous value                     |
| Recent Candle Pattern | 10%    | Last 3 bars — majority bullish or bearish candles                    |

The service:
- Fetches `barCount + 50` bars from the database (extra bars ensure EMA 50 has valid history)
- Uses existing `TechnicalIndicators.EMA()`, `RSI()`, `LastValid()`, and `PreviousValid()` methods
- Normalises bullish/bearish scores to Up/Down percentages that sum to 100%
- Requires at least 25 bars for a valid analysis (returns 50/50 otherwise)

### 3. Modified ViewModel: `BacktestViewModel`

**File:** `src/TopStepTrader.UI/ViewModels/BacktestViewModel.vb`

Changes:
- **New constructor dependencies:** `TrendAnalysisService`, `IOrderService`, `IAccountService`
- **New properties:** `TestTradeContractId`, `TestTradeQuantity`, `HasTrendResult`, `UpProbabilityText`, `DownProbabilityText`, `TrendEMA21Text`, `TrendEMA50Text`, `TrendRSI14Text`, `TrendLastCloseText`, `TrendSummaryText`, `TestTradeStatus`, `TrendSignals`
- **New commands:** `AnalyseTrendCommand`, `TestBuyCommand`, `TestSellCommand`
- **New methods:** `ExecuteAnalyseTrend()`, `ExecuteTestTrade(side)`, `LoadAccountAsync()`
- All existing Tab 1 (Run Backtest) and Tab 2 (Previous Runs) functionality is preserved unchanged

### 4. Modified View: `BacktestView.xaml`

**File:** `src/TopStepTrader.UI/Views/BacktestView.xaml`

Added **Tab 3: "Test Trade"** with:
- Contract ID and Quantity input fields
- "Analyse Trend" button to trigger the EMA/RSI analysis
- Large **Up Probability %** (green) and **Down Probability %** (red) display
- Indicator values panel (EMA 21, EMA 50, RSI 14, Last Close)
- Individual signal breakdown list
- Two prominent action buttons: **Test BUY** (green, BuyButtonStyle) and **Test SELL** (red, SellButtonStyle)
- Status text showing analysis progress and order results

### 5. DI Registration

**File:** `src/TopStepTrader.Services/ServicesExtensions.vb`

Added: `services.AddScoped(Of TrendAnalysisService)()` under the Trading section.

---

## Files Changed

| File | Action |
|------|--------|
| `src/TopStepTrader.Core/Models/TrendAnalysisResult.vb` | **New** |
| `src/TopStepTrader.Services/Trading/TrendAnalysisService.vb` | **New** |
| `src/TopStepTrader.Services/ServicesExtensions.vb` | Modified (added DI registration) |
| `src/TopStepTrader.UI/ViewModels/BacktestViewModel.vb` | Modified (added Test Trade tab logic) |
| `src/TopStepTrader.UI/Views/BacktestView.xaml` | Modified (added Tab 3 UI) |
| `Manus_Tickets/TICKET-001_TestTrade_EMA_RSI_TrendAnalysis.md` | **New** (this file) |

---

## AI Model Decision

**No AI model is needed at runtime.** The EMA and RSI calculations are deterministic mathematical operations performed by the existing `TechnicalIndicators` module. The weighted scoring system is a simple rule-based algorithm — no LLM inference, no ML prediction, and therefore **zero token cost** per analysis.

If an AI model were needed for a future enhancement (e.g., natural language explanation of the trend), the recommendation would be **gpt-4.1-nano** as the cheapest option for simple text generation tasks.

---

## How It Works (User Flow)

1. Navigate to the **Backtest** section → click the **Test Trade** tab
2. Enter a **Contract ID** (e.g., the same one used in Market Data)
3. Set the **Quantity** (default: 1)
4. Click **Analyse Trend** — the service fetches 24 hourly bars from the database and runs the combined EMA/RSI analysis
5. The UI displays:
   - **Up Probability** (e.g., 67.5%) in green
   - **Down Probability** (e.g., 32.5%) in red
   - Current EMA 21, EMA 50, RSI 14, and Last Close values
   - A breakdown of each individual signal (e.g., "EMA Crossover: BULLISH")
6. Based on the analysis and their own judgement, the user clicks either:
   - **Test BUY** — places a market buy order
   - **Test SELL** — places a market sell order
7. The status bar shows order confirmation or error messages
