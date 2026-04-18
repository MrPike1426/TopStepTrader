Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Data.Entities
Imports TopStepTrader.Data.Repositories
Imports TopStepTrader.ML.Features

Namespace TopStepTrader.Services.Backtest

    ''' <summary>
    ''' Walk-forward backtest engine. Replays historical bars through the same EMA/RSI
    ''' weighted-scoring algorithm used by StrategyExecutionEngine (live trading), so that
    ''' backtest results represent what live trading will actually produce.
    '''
    ''' Signal algorithm (mirrors StrategyExecutionEngine.EmaRsiWeightedScore):
    '''   Six signals scored 0–100; fire Long when bullScore ≥ threshold, Short when bearScore ≥ threshold.
    '''   1. EMA21 > EMA50 crossover — 25 pts
    '''   2. Close > EMA21 — 20 pts
    '''   3. Close > EMA50 — 15 pts
    '''   4. RSI14 trending zone (50–70 = +20 pts, else 0 pts) — up to 20 pts
    '''   5. EMA21 momentum (rising since prior bar) — 10 pts
    '''   6. 2+ of last 3 candles bullish — 10 pts
    '''
    ''' Exit rules (EmaRsiWeightedScore):
    '''   TP / SL intrabar fills (price-level triggers via bar.High/Low).
    '''   Neutral confidence exit: score 40–60% → close at bar close (mirrors live engine priority).
    '''
    ''' Pure calculation logic lives in <see cref="BacktestMetrics"/> (Friend module)
    ''' so it can be unit-tested independently.
    ''' </summary>
    Public Class BacktestEngine
        Implements IBacktestService

        Private ReadOnly _barRepository As BarRepository
        Private ReadOnly _backtestRepository As BacktestRepository
        Private ReadOnly _logger As ILogger(Of BacktestEngine)

        Public Event ProgressUpdated As EventHandler(Of BacktestProgressEventArgs) _
            Implements IBacktestService.ProgressUpdated

        Public Sub New(barRepository As BarRepository,
                       backtestRepository As BacktestRepository,
                       logger As ILogger(Of BacktestEngine))
            _barRepository = barRepository
            _backtestRepository = backtestRepository
            _logger = logger
        End Sub

        Public Async Function RunBacktestAsync(config As BacktestConfiguration,
                                                cancel As CancellationToken) _
            As Task(Of BacktestResult) Implements IBacktestService.RunBacktestAsync

            _logger.LogInformation("Starting backtest '{Name}' from {Start} to {End}",
                                   config.RunName, config.StartDate, config.EndDate)

            ' Load bars for the configured date range — GetBarsAsync returns domain MarketBar objects
            Dim from As DateTimeOffset = New DateTimeOffset(DateTime.SpecifyKind(config.StartDate, DateTimeKind.Unspecified), TimeSpan.Zero)
            Dim [to] As DateTimeOffset = New DateTimeOffset(DateTime.SpecifyKind(config.EndDate, DateTimeKind.Unspecified), TimeSpan.Zero).AddDays(1)
            Dim filteredBars = Await _barRepository.GetBarsAsync(
                config.ContractId, CType(config.Timeframe, BarTimeframe), from, [to], cancel)

            If filteredBars.Count < 50 Then
                Throw New InvalidOperationException(
                    $"Insufficient bars for backtest: {filteredBars.Count}. Need at least 50.")
            End If

            _logger.LogInformation("Replaying {N} bars", filteredBars.Count)

            ' ── Pre-calculate full indicator series ONCE for all bars ────────────
            ' This mirrors how the live engine works: EMA/RSI carries full price history,
            ' not a truncated window.  Much more accurate and efficient than per-bar recalc.
            Dim allCloses = filteredBars.Select(Function(b) b.Close).ToList()
            Dim allHighs = filteredBars.Select(Function(b) b.High).ToList()
            Dim allLows = filteredBars.Select(Function(b) b.Low).ToList()
            Dim ema21Series = TechnicalIndicators.EMA(allCloses, 21)  ' valid from index 20
            Dim ema50Series = TechnicalIndicators.EMA(allCloses, 50)  ' valid from index 49
            Dim rsi14Series = TechnicalIndicators.RSI(allCloses, 14)  ' valid from index 14
            ' ADX(14) — mirrors the live ADX gate in StrategyExecutionEngine (TICKET-019).
            ' Suppresses EmaRsiWeightedScore entry signals when ADX < 25 (ranging market).
            ' Valid from index 2*14-1=27; warmUp=55 ensures it's valid before any signal fires.
            Dim adx14Series = TechnicalIndicators.DMI(allHighs, allLows, allCloses).ADX

            ' EMA8 only needed for Sniper (TripleEmaCascade) strategy.
            Dim ema8Series As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.TripleEmaCascade Then
                ema8Series = TechnicalIndicators.EMA(allCloses, 8)    ' valid from index 7
            End If

            ' Universal ATR(14) — pre-calculated when UseAtrMode=True for non-MC strategies.
            ' MultiConfluence has its own mcAtr14; all others share this series.
            ' Also used when dynamic exits are enabled on non-ATR strategies.
            Dim universalAtr14 As Single() = Nothing
            If config.UseAtrMode AndAlso
               config.StrategyCondition <> StrategyConditionType.MultiConfluence AndAlso
               config.StrategyCondition <> StrategyConditionType.SuperTrend Then
                universalAtr14 = TechnicalIndicators.ATR(allHighs, allLows, allCloses, 14)
            End If

            ' ── QuantLab strategies — pre-calculate indicator series ─────────────

            ' ConnorsRsi2 (=11): RSI(2), SMA(5), SMA(200)
            Dim qlRsi2 As Single() = Nothing
            Dim qlSma5 As Single() = Nothing
            Dim qlSma200 As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.ConnorsRsi2 Then
                qlRsi2 = TechnicalIndicators.Rsi2(allCloses)          ' valid from index 2
                qlSma5 = TechnicalIndicators.SMA(allCloses, 5)        ' valid from index 4
                qlSma200 = TechnicalIndicators.SMA(allCloses, 200)    ' valid from index 199
            End If

            ' SuperTrend (=12): ATR(10) × 3.0 multiplier
            Dim qlStLine As Single() = Nothing
            Dim qlStDir As Single() = Nothing
            Dim qlStAtr10 As Single() = Nothing    ' pre-calculated ATR(10) for TP sizing
            If config.StrategyCondition = StrategyConditionType.SuperTrend Then
                Dim stResult = TechnicalIndicators.SuperTrend(allHighs, allLows, allCloses, 10, 3.0)
                qlStLine = stResult.Line
                qlStDir = stResult.Direction
                qlStAtr10 = TechnicalIndicators.ATR(allHighs, allLows, allCloses, 10)
            End If

            ' DonchianBreakout (=13): 20-bar Donchian channel + 10-bar exit channel
            Dim qlDonUpper20 As Single() = Nothing
            Dim qlDonLower20 As Single() = Nothing
            Dim qlDonMid10 As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.DonchianBreakout Then
                Dim don20 = TechnicalIndicators.DonchianChannel(allHighs, allLows, 20)
                qlDonUpper20 = don20.Upper
                qlDonLower20 = don20.Lower
                Dim don10 = TechnicalIndicators.DonchianChannel(allHighs, allLows, 10)
                qlDonMid10 = don10.Middle     ' exit when close crosses mid of 10-bar channel
            End If

            ' BbRsiMeanReversion (=14): BB(20,2) + RSI(14) — both already computed above
            ' (ema21/rsi14 series already available; BB needs its own pre-calc)
            Dim qlBbUpper As Single() = Nothing
            Dim qlBbMiddle As Single() = Nothing
            Dim qlBbLower As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.BbRsiMeanReversion Then
                Dim bb20 = TechnicalIndicators.BollingerBands(allCloses, 20, 2.0)
                qlBbUpper = bb20.Upper
                qlBbMiddle = bb20.Middle
                qlBbLower = bb20.Lower
            End If

            ' DoubleBubbleButt (=17): BB(20, 1.0 SD) inner + BB(20, 2.0 SD) outer + ATR(20) for TP
            Dim dbbInnerUpper As Single() = Nothing
            Dim dbbInnerLower As Single() = Nothing
            Dim dbbOuterUpper As Single() = Nothing
            Dim dbbOuterLower As Single() = Nothing
            Dim dbbAtr20 As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.DoubleBubbleButt Then
                Dim dbbInner = TechnicalIndicators.BollingerBands(allCloses, 20, 1.0)
                dbbInnerUpper = dbbInner.Upper
                dbbInnerLower = dbbInner.Lower
                Dim dbbOuter = TechnicalIndicators.BollingerBands(allCloses, 20, 2.0)
                dbbOuterUpper = dbbOuter.Upper
                dbbOuterLower = dbbOuter.Lower
                dbbAtr20 = TechnicalIndicators.ATR(allHighs, allLows, allCloses, 20)
            End If

            ' State: open SuperTrend SL/TP prices (price-level exit, like MultiConfluence)
            Dim qlStOpenSlPrice As Decimal = 0D
            Dim qlStOpenTpPrice As Decimal = 0D
            Dim qlStIsLong As Boolean = True
            Dim qlStPrevDir As Single = 0.0F   ' previous SuperTrend direction for flip detection

            ' State: open Donchian / BbRsi / DoubleBubbleButt exit levels
            Dim qlDonOpenMidExit As Decimal = 0D    ' adverse mid-channel level at entry
            Dim qlDonIsLong As Boolean = True
            Dim qlBbOpenMidExit As Decimal = 0D     ' BB middle band (SMA20) at entry
            Dim qlBbIsLong As Boolean = True
            Dim dbbIsLong As Boolean = True          ' DoubleBubbleButt position direction
            Dim dbbInner1SdExit As Decimal = 0D     ' inner 1-SD band level at entry (neutral-zone exit trigger)

            ' MultiConfluence — pre-calculate full indicator series once for all bars.
            ' Senkou Span B needs senkouBPeriod(52) + displacement(26) = 78 bars minimum.
            Dim mcIchiTenkan As Single() = Nothing
            Dim mcIchiKijun As Single() = Nothing
            Dim mcIchiSpanA As Single() = Nothing
            Dim mcIchiSpanB As Single() = Nothing
            Dim mcPlusDI As Single() = Nothing
            Dim mcMinusDI As Single() = Nothing
            Dim mcAdxSeries As Single() = Nothing
            Dim mcMacdHist As Single() = Nothing
            Dim mcStochRsiK As Single() = Nothing
            Dim mcAtr14 As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.MultiConfluence Then
                Dim ichi = TechnicalIndicators.IchimokuCloud(allHighs, allLows, allCloses, 9, 26, 52, 26)
                mcIchiTenkan = ichi.Tenkan
                mcIchiKijun = ichi.Kijun
                mcIchiSpanA = ichi.SpanA
                mcIchiSpanB = ichi.SpanB
                Dim dmiMc = TechnicalIndicators.DMI(allHighs, allLows, allCloses, 14)
                mcPlusDI = dmiMc.PlusDI
                mcMinusDI = dmiMc.MinusDI
                mcAdxSeries = dmiMc.ADX
                mcMacdHist = TechnicalIndicators.MACD(allCloses).Histogram
                mcStochRsiK = TechnicalIndicators.StochasticRSI(allCloses).K
                mcAtr14 = TechnicalIndicators.ATR(allHighs, allLows, allCloses, 14)
            End If

            ' ── VIDYA Cross (=15): VIDYA(14,9), CMO(9), DeltaVolume(6) ───────────
            Dim vcVidyaSeries As Single() = Nothing
            Dim vcCmoSeries As Single() = Nothing
            Dim vcDeltaVolSeries As Single() = Nothing
            Dim vcAtr14 As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.VidyaCross Then
                vcVidyaSeries = TechnicalIndicators.VIDYA(allCloses, 14, 9)
                vcCmoSeries = TechnicalIndicators.CMO(allCloses, 9)
                Dim allOpens = filteredBars.Select(Function(b) b.Open).ToList()
                Dim allVols = filteredBars.Select(Function(b) b.Volume).ToList()
                vcDeltaVolSeries = TechnicalIndicators.DeltaVolume(allCloses, allOpens, allVols, 6)
                vcAtr14 = TechnicalIndicators.ATR(allHighs, allLows, allCloses, 14)
            End If

            ' ── Naked Trader (=16): EMA(9), EMA(21), MACD(8,17,9), DMI(14), VWAP ──
            Dim ntEma9 As Single() = Nothing
            Dim ntEma21 As Single() = Nothing
            Dim ntMacdHist As Single() = Nothing
            Dim ntMacdLine As Single() = Nothing
            Dim ntPlusDI As Single() = Nothing
            Dim ntMinusDI As Single() = Nothing
            Dim ntAdxSeries As Single() = Nothing
            Dim ntVwapSeries As Single() = Nothing
            Dim ntVolMa20 As Single() = Nothing
            Dim ntAtr14 As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.NakedTrader Then
                ntEma9 = TechnicalIndicators.EMA(allCloses, 9)
                ntEma21 = TechnicalIndicators.EMA(allCloses, 21)
                Dim macdNt = TechnicalIndicators.MACD(allCloses, 8, 17, 9)
                ntMacdHist = macdNt.Histogram
                ntMacdLine = macdNt.Line
                Dim dmiNt = TechnicalIndicators.DMI(allHighs, allLows, allCloses, 14)
                ntPlusDI = dmiNt.PlusDI
                ntMinusDI = dmiNt.MinusDI
                ntAdxSeries = dmiNt.ADX
                Dim allVols = filteredBars.Select(Function(b) b.Volume).ToList()
                ntVwapSeries = TechnicalIndicators.VWAP(allHighs, allLows, allCloses, allVols)
                Dim volDecimals = allVols.Select(Function(v) CDec(v)).ToList()
                ntVolMa20 = TechnicalIndicators.SMA(volDecimals, 20)
                ntAtr14 = TechnicalIndicators.ATR(allHighs, allLows, allCloses, 14)
            End If

            ' ── LULT Divergence (=9): WaveTrend(10,21,4) ────────────────────────
            Dim lultWt1 As Single() = Nothing
            Dim lultWt2 As Single() = Nothing
            Dim lultAtr14 As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.LultDivergence Then
                Dim wt = TechnicalIndicators.WaveTrend(allHighs, allLows, allCloses, 10, 21, 4)
                lultWt1 = wt.Wt1
                lultWt2 = wt.Wt2
                lultAtr14 = TechnicalIndicators.ATR(allHighs, allLows, allCloses, 14)
            End If

            ' ── BB Squeeze Scalper (=10): BB(12,2), BBW, %B, RSI(7), EMA(5), ATR(10) ──
            Dim bbsBands As (Upper As Single(), Middle As Single(), Lower As Single()) = (Nothing, Nothing, Nothing)
            Dim bbsBbwArr As Single() = Nothing
            Dim bbsPctBArr As Single() = Nothing
            Dim bbsRsi7 As Single() = Nothing
            Dim bbsEma5 As Single() = Nothing
            Dim bbsBbwSma As Single() = Nothing
            Dim bbsAtr10 As Single() = Nothing
            If config.StrategyCondition = StrategyConditionType.BbSqueezeScalper Then
                bbsBands = TechnicalIndicators.BollingerBands(allCloses, 12, 2.0)
                bbsBbwArr = TechnicalIndicators.BollingerBandWidth(allCloses, 12, 2.0)
                bbsPctBArr = TechnicalIndicators.BollingerPercentB(allCloses, 12, 2.0)
                bbsRsi7 = TechnicalIndicators.RSI(allCloses, 7)
                bbsEma5 = TechnicalIndicators.EMA(allCloses, 5)
                bbsBbwSma = TechnicalIndicators.SMA(
                    bbsBbwArr.Select(Function(v) SafeD(v)).ToList(), 20)
                bbsAtr10 = TechnicalIndicators.ATR(allHighs, allLows, allCloses, 10)
            End If

            ' Warm-up: EMA50 needs 50 bars; add 5-bar buffer so EMA21Prev also valid.
            ' MultiConfluence warm-up: Senkou Span B + displacement = 78 bars (80 with buffer).
            ' ConnorsRsi2 warm-up: SMA(200) needs 200 bars; add 5-bar buffer = 205.
            ' SuperTrend/DonchianBreakout/BbRsiMeanReversion warm-up: 25 bars is sufficient.
            Dim warmUp As Integer
            Select Case config.StrategyCondition
                Case StrategyConditionType.MultiConfluence
                    warmUp = 80
                Case StrategyConditionType.ConnorsRsi2
                    warmUp = 205
                Case StrategyConditionType.DoubleBubbleButt
                    warmUp = 25   ' BB(20) needs 20 bars; ATR(20) needs 20 bars
                Case StrategyConditionType.LultDivergence
                    warmUp = 100  ' WaveTrend warm-up + anchor lookback
                Case StrategyConditionType.NakedTrader
                    warmUp = 55   ' ADX(14) needs 28 bars; 55 is comfortable
                Case StrategyConditionType.VidyaCross
                    warmUp = 55   ' VIDYA(14) + CMO(9) needs ~25 bars
                Case StrategyConditionType.BbSqueezeScalper
                    warmUp = 55   ' BB(12) + BBW SMA(20) needs ~32 bars
                Case Else
                    warmUp = 55
            End Select

            ' Validate critical configuration before replay begins.
            If config.PointValue <= 0D Then
                Throw New InvalidOperationException(
                    $"BacktestConfiguration.PointValue must be > 0 for contract '{config.ContractId}'. " &
                    $"Got {config.PointValue}. Set the correct point value (e.g. MES=$5, MGC=$10, MCL=$100).")
            End If

            Dim trades As New List(Of BacktestTrade)()
            Dim capital = config.InitialCapital
            Dim peakCapital = capital
            Dim maxDrawdown = 0D
            ' openLegs holds all entry/scale-in legs for the currently open position.
            ' All legs share the same PositionGroupId and exit together.
            Dim openLegs As New List(Of BacktestTrade)()
            Dim positionGroupCounter As Integer = 0
            ' Tracks the bar index of the most recent ForceClose exit.
            ' Used to suppress re-entry for 3 bars after a profit-cap close,
            ' preventing artificial churn where a position is closed and immediately re-entered.
            ' Sentinel is -1000 (not Integer.MinValue) to avoid Int32 overflow in the
            ' (i - lastForceCloseBarIndex) subtraction at the cooldown check below.
            Dim lastForceCloseBarIndex As Integer = -1000

            ' MultiConfluence ATR-based SL/TP prices — set at entry, cleared on exit.
            ' Used instead of tick-based config values for MultiConfluence positions.
            Dim mcOpenSlPrice As Decimal = 0D
            Dim mcOpenTpPrice As Decimal = 0D
            Dim mcIsLong As Boolean = True

            ' Dynamic exit tracking — shared across all strategies when any of the three
            ' dynamic-exit config flags is True.  Reset to 0D on position close.
            '   dynStop      — current SL price level (may trail or advance to break-even)
            '   dynTp        — current TP price level (may extend on close beyond initial target)
            '   dynStopDelta — initial price-unit distance from entry to SL (held constant)
            '   dynTpDelta   — initial price-unit distance from entry to TP (held constant)
            ' When all three flags are False these stay at 0D and are not used.
            ' UseAtrMode needs price-level dynStop/dynTp initialised at entry even when
            ' trailing/BE/extend are off, so the price-level CheckExit path is used.
            Dim dynEnabled = config.UseAtrMode OrElse
                             config.TrailingStopEnabled OrElse
                             config.BreakEvenOnHalfTpEnabled OrElse
                             config.ExtendTpEnabled
            Dim dynStop      As Decimal = 0D
            Dim dynTp        As Decimal = 0D
            Dim dynStopDelta As Decimal = 0D
            Dim dynTpDelta   As Decimal = 0D

            ' MinSignalConfidence is stored as 0.0–1.0 (e.g. 0.75 = 75%).
            ' The EMA/RSI score produces 0–100, so convert once here.
            Dim minPct As Double = config.MinSignalConfidence * 100.0

            Dim progressStep = Math.Max(1, CInt(filteredBars.Count / 20))

            For i = warmUp To filteredBars.Count - 1
                cancel.ThrowIfCancellationRequested()

                ' Progress events every ~5%
                If i Mod progressStep = 0 Then
                    Dim pct = CInt((i / CDbl(filteredBars.Count)) * 100)
                    RaiseEvent ProgressUpdated(Me, New BacktestProgressEventArgs(
                        pct, filteredBars(i).Timestamp.Date, trades.Count))
                End If

                Dim bar = filteredBars(i)

                ' ── Check exit for open position ──────────────────────────────────
                ' TP/SL levels are anchored to the first leg's entry price; all legs exit together.
                ' MultiConfluence / SuperTrend use ATR-based price-level checks.
                ' DonchianBreakout / BbRsiMeanReversion use indicator-level exits.
                ' All others use tick-based config.
                '
                ' IMPORTANT — exit checks intentionally run BEFORE UpdateDynamicExits.
                ' UpdateDynamicExits (trailing stop, break-even, extend TP) runs at the END
                ' of this block only when the trade survives the bar (exitReason Is Nothing).
                ' Running it first would cause extend-TP to raise dynTp on the same bar that
                ' bar.High first reaches the original TP, making CheckExit test against the
                ' extended level and skip the natural exit.  OHLC guarantees bar.Close ≥ TP
                ' → bar.High ≥ TP, so extend-TP before CheckExit always defeats the TP exit.
                If openLegs.Count > 0 Then
                    Dim exitReason As String = Nothing
                    Dim exitPrice As Decimal = bar.Close

                    ' ── Force Close profit cap — per-position P&L check ────────────────
                    ' If the sum of unrealised P&L across all open legs ≥ ForceCloseAmount,
                    ' close everything at bar.Close. Only positive P&L triggers — losses
                    ' are managed by SL/TP brackets.
                    If config.ForceCloseEnabled AndAlso config.ForceCloseAmount > 0D Then
                        Dim fcPnl As Decimal = 0D
                        For Each leg In openLegs
                            Dim legPnl = BacktestMetrics.CalculatePnL(
                                New BacktestTrade With {
                                    .EntryPrice = leg.EntryPrice,
                                    .ExitPrice = bar.Close,
                                    .Side = leg.Side,
                                    .Quantity = leg.Quantity
                                }, config)
                            fcPnl += legPnl
                        Next
                        If fcPnl >= config.ForceCloseAmount Then
                            Dim fcPositionPnL = 0D
                            For Each leg In openLegs
                                leg.ExitTime = bar.Timestamp
                                leg.ExitPrice = bar.Close
                                leg.ExitReason = "ForceClose"
                                Dim pnl = BacktestMetrics.CalculatePnL(leg, config)
                                leg.PnL = pnl
                                fcPositionPnL += pnl
                                trades.Add(leg)
                            Next
                            capital += fcPositionPnL
                            If capital > peakCapital Then peakCapital = capital
                            Dim fcDd = peakCapital - capital
                            If fcDd > maxDrawdown Then maxDrawdown = fcDd
                            openLegs.Clear()
                            mcOpenSlPrice = 0D : mcOpenTpPrice = 0D
                            qlStOpenSlPrice = 0D : qlStOpenTpPrice = 0D
                            qlDonOpenMidExit = 0D : qlBbOpenMidExit = 0D
                            dbbInner1SdExit = 0D
                            dynStop = 0D : dynTp = 0D : dynStopDelta = 0D : dynTpDelta = 0D
                            lastForceCloseBarIndex = i  ' arm 3-bar re-entry cooldown
                            Continue For  ' ForceClose handled exit — skip remaining checks for this bar
                        End If
                    End If

                    ' ── SuperTrend price-level exit ────────────────────────────────
                    If config.StrategyCondition = StrategyConditionType.SuperTrend AndAlso qlStOpenSlPrice <> 0D Then
                        If qlStIsLong Then
                            If bar.Low <= qlStOpenSlPrice Then
                                exitReason = "StopLoss"
                                exitPrice = qlStOpenSlPrice
                            ElseIf bar.High >= qlStOpenTpPrice Then
                                exitReason = "TakeProfit"
                                exitPrice = qlStOpenTpPrice
                            End If
                        Else
                            If bar.High >= qlStOpenSlPrice Then
                                exitReason = "StopLoss"
                                exitPrice = qlStOpenSlPrice
                            ElseIf bar.Low <= qlStOpenTpPrice Then
                                exitReason = "TakeProfit"
                                exitPrice = qlStOpenTpPrice
                            End If
                        End If
                    End If

                    ' ── DonchianBreakout indicator-level exit ──────────────────────
                    If exitReason Is Nothing AndAlso
                       config.StrategyCondition = StrategyConditionType.DonchianBreakout AndAlso
                       qlDonOpenMidExit <> 0D Then
                        If qlDonIsLong AndAlso bar.Close < qlDonOpenMidExit Then
                            exitReason = "NeutralExit"
                        ElseIf Not qlDonIsLong AndAlso bar.Close > qlDonOpenMidExit Then
                            exitReason = "NeutralExit"
                        End If
                    End If

                    ' ── BbRsiMeanReversion indicator-level exit ────────────────────
                    ' Exit Long when close >= BB middle (SMA20) or RSI crosses back above 50.
                    ' Exit Short when close <= BB middle (SMA20) or RSI crosses back below 50.
                    If exitReason Is Nothing AndAlso
                       config.StrategyCondition = StrategyConditionType.BbRsiMeanReversion AndAlso
                       qlBbOpenMidExit <> 0D Then
                        Dim rsiExitVal = If(qlBbIsLong,
                                            rsi14Series(i) <= 50.0F,   ' long: exit when RSI reverses ≤ 50
                                            rsi14Series(i) >= 50.0F)   ' short: exit when RSI reverses ≥ 50
                        If qlBbIsLong AndAlso
                           (bar.Close >= qlBbOpenMidExit OrElse
                            (Not Single.IsNaN(rsi14Series(i)) AndAlso rsiExitVal)) Then
                            exitReason = "TakeProfit"
                        ElseIf Not qlBbIsLong AndAlso
                               (bar.Close <= qlBbOpenMidExit OrElse
                                (Not Single.IsNaN(rsi14Series(i)) AndAlso rsiExitVal)) Then
                            exitReason = "TakeProfit"
                        End If
                    End If

                    ' ── DoubleBubbleButt neutral-zone exit ─────────────────────────
                    ' Exit Long  when close drops back below the upper 1.0 SD band (neutral zone).
                    ' Exit Short when close rises back above the lower 1.0 SD band (neutral zone).
                    If exitReason Is Nothing AndAlso
                       config.StrategyCondition = StrategyConditionType.DoubleBubbleButt AndAlso
                       dbbInner1SdExit <> 0D Then
                        If dbbIsLong AndAlso bar.Close < dbbInner1SdExit Then
                            exitReason = "NeutralExit"
                        ElseIf Not dbbIsLong AndAlso bar.Close > dbbInner1SdExit Then
                            exitReason = "NeutralExit"
                        End If
                    End If

                    ' ── ConnorsRsi2 RSI-based exit ─────────────────────────────────
                    ' Exit Long when close > SMA(5) or RSI(2) > 65.
                    ' Exit Short when close < SMA(5) or RSI(2) < 35.
                    If exitReason Is Nothing AndAlso
                       config.StrategyCondition = StrategyConditionType.ConnorsRsi2 AndAlso
                       qlRsi2 IsNot Nothing AndAlso qlSma5 IsNot Nothing Then
                        If Not (Single.IsNaN(qlRsi2(i)) OrElse Single.IsNaN(qlSma5(i))) Then
                            If openLegs(0).Side = "Buy" Then
                                If bar.Close > SafeD(qlSma5(i)) OrElse qlRsi2(i) > 65.0F Then
                                    exitReason = "TakeProfit"
                                End If
                            Else
                                If bar.Close < SafeD(qlSma5(i)) OrElse qlRsi2(i) < 35.0F Then
                                    exitReason = "TakeProfit"
                                End If
                            End If
                        End If
                    End If

                    If config.StrategyCondition = StrategyConditionType.MultiConfluence AndAlso mcOpenSlPrice <> 0D Then
                        If mcIsLong Then
                            If bar.Low <= mcOpenSlPrice Then
                                exitReason = "StopLoss"
                                exitPrice = mcOpenSlPrice
                            ElseIf bar.High >= mcOpenTpPrice Then
                                exitReason = "TakeProfit"
                                exitPrice = mcOpenTpPrice
                            End If
                        Else
                            If bar.High >= mcOpenSlPrice Then
                                exitReason = "StopLoss"
                                exitPrice = mcOpenSlPrice
                            ElseIf bar.Low <= mcOpenTpPrice Then
                                exitReason = "TakeProfit"
                                exitPrice = mcOpenTpPrice
                            End If
                        End If
                     ElseIf exitReason Is Nothing Then
                        ' Use dynamic price levels when any dynamic-exit flag is on;
                        ' fall back to config-derived fixed levels otherwise.
                        If dynEnabled AndAlso dynStop <> 0D Then
                            exitReason = BacktestMetrics.CheckExit(openLegs(0), bar, dynStop, dynTp)
                            If exitReason IsNot Nothing Then
                                exitPrice = BacktestMetrics.GetExitPrice(openLegs(0), bar, exitReason, dynStop, dynTp)
                            End If
                        Else
                            exitReason = BacktestMetrics.CheckExit(openLegs(0), bar, config)
                            If exitReason IsNot Nothing Then
                                exitPrice = BacktestMetrics.GetExitPrice(openLegs(0), bar, exitReason, config)
                            End If
                        End If
                    End If
                    If exitReason IsNot Nothing Then
                        ' Apply stop-loss slippage: SL fills degrade by SlippageTicks in the
                        ' adverse direction (long fills lower, short fills higher).
                        ' TP and other exits fill at target — no slippage applied.
                        If exitReason = "StopLoss" AndAlso config.SlippageTicks > 0 AndAlso
                           openLegs.Count > 0 AndAlso config.TickSize > 0D Then
                            Dim slipDelta = config.SlippageTicks * config.TickSize
                            exitPrice += If(openLegs(0).Side = "Buy", -slipDelta, slipDelta)
                        End If
                        Dim positionPnL = 0D
                        For Each leg In openLegs
                            leg.ExitTime = bar.Timestamp
                            leg.ExitPrice = exitPrice
                            leg.ExitReason = exitReason
                            Dim pnl = BacktestMetrics.CalculatePnL(leg, config)
                            leg.PnL = pnl
                            positionPnL += pnl
                            trades.Add(leg)
                        Next
                        capital += positionPnL
                        If capital > peakCapital Then peakCapital = capital
                        Dim dd = peakCapital - capital
                        If dd > maxDrawdown Then maxDrawdown = dd
                        openLegs.Clear()
                        mcOpenSlPrice = 0D
                        mcOpenTpPrice = 0D
                        ' Clear QuantLab state
                        qlStOpenSlPrice = 0D
                        qlStOpenTpPrice = 0D
                        qlDonOpenMidExit = 0D
                        qlBbOpenMidExit = 0D
                        dbbInner1SdExit = 0D
                        ' Clear dynamic exit state
                        dynStop      = 0D
                        dynTp        = 0D
                        dynStopDelta = 0D
                        dynTpDelta   = 0D
                    Else
                        ' No exit this bar — advance trailing stop / break-even / extend TP
                        ' ready for the NEXT bar's exit check.  Must run AFTER all exit checks
                        ' so it never raises dynTp before CheckExit has had a chance to test it.
                        If dynEnabled AndAlso dynStopDelta > 0D Then
                            BacktestMetrics.UpdateDynamicExits(
                                openLegs(0), bar, config,
                                dynStopDelta, dynTpDelta,
                                dynStop, dynTp)
                            ' Keep strategy-specific price-level variables in sync.
                            If config.StrategyCondition = StrategyConditionType.MultiConfluence Then
                                mcOpenSlPrice = dynStop
                                mcOpenTpPrice = dynTp
                            ElseIf config.StrategyCondition = StrategyConditionType.SuperTrend Then
                                qlStOpenSlPrice = dynStop
                                qlStOpenTpPrice = dynTp
                            End If
                        End If
                    End If
                End If

                ' ── Signal evaluation — Only when flat (no open trade) ─────────────
                ' Branches on config.StrategyCondition: EmaRsiWeightedScore or TripleEmaCascade.

                ' ForceClose re-entry cooldown: suppress all entry signals for 3 bars after a
                ' profit-cap exit. Prevents a position from being closed and immediately re-entered
                ' on the next bar, which inflates trade count and overstates strategy consistency.
                If openLegs.Count = 0 AndAlso (i - lastForceCloseBarIndex) <= 3 Then Continue For

                ' ── 3-EMA Cascade (Sniper) signal ─────────────────────────────────
                If openLegs.Count = 0 AndAlso
                   config.StrategyCondition = StrategyConditionType.TripleEmaCascade Then

                    Dim ema8Now = ema8Series(i)
                    Dim ema8Prev = ema8Series(i - 1)
                    Dim ema21CascNow = ema21Series(i)
                    Dim ema50CascNow = ema50Series(i)
                    Dim ema50CascPrev = ema50Series(i - 1)

                    If Not (Single.IsNaN(ema8Now) OrElse Single.IsNaN(ema8Prev) OrElse
                            Single.IsNaN(ema21CascNow) OrElse Single.IsNaN(ema50CascNow) OrElse
                            Single.IsNaN(ema50CascPrev)) Then

                        Dim lastCascadeClose = bar.Close
                        Dim crossedAbove = ema8Prev <= ema21Series(i - 1) AndAlso ema8Now > ema21CascNow
                        Dim crossedBelow = ema8Prev >= ema21Series(i - 1) AndAlso ema8Now < ema21CascNow
                        Dim ema50Rising = ema50CascNow > ema50CascPrev
                        Dim ema50Falling = ema50CascNow < ema50CascPrev

                        Dim cascadeSide As String = Nothing
                        If crossedAbove AndAlso lastCascadeClose > SafeD(ema50CascNow) AndAlso ema50Rising Then
                            cascadeSide = "Buy"
                        ElseIf crossedBelow AndAlso lastCascadeClose < SafeD(ema50CascNow) AndAlso ema50Falling Then
                            cascadeSide = "Sell"
                        End If

                        If cascadeSide IsNot Nothing Then
                            positionGroupCounter += 1
                            openLegs.Add(New BacktestTrade With {
                                .PositionGroupId = positionGroupCounter,
                                .EntryTime = bar.Timestamp,
                                .EntryPrice = bar.Close,
                                .Side = cascadeSide,
                                .Quantity = config.Quantity,
                                .SignalConfidence = 1.0F
                            })
                            If dynEnabled Then
                                Dim atrCasc As Single = If(universalAtr14 IsNot Nothing AndAlso i < universalAtr14.Length AndAlso
                                                            Not Single.IsNaN(universalAtr14(i)), universalAtr14(i), 0.0F)
                                If config.UseAtrMode AndAlso atrCasc > 0.0F Then
                                    dynStopDelta = SafeD(atrCasc) * config.SlAtrMultiple
                                    dynTpDelta   = SafeD(atrCasc) * config.TpAtrMultiple
                                Else
                                    Dim dpp = config.PointValue * config.Quantity
                                    If dpp > 0D Then
                                        dynStopDelta = Math.Round(config.InitialSlAmount / dpp, 4)
                                        dynTpDelta   = Math.Round(config.InitialTpAmount / dpp, 4)
                                    End If
                                End If
                                If dynStopDelta > 0D Then
                                    Dim isCascBuy = (cascadeSide = "Buy")
                                    dynStop = If(isCascBuy, bar.Close - dynStopDelta, bar.Close + dynStopDelta)
                                    dynTp   = If(isCascBuy, bar.Close + dynTpDelta,   bar.Close - dynTpDelta)
                                End If
                            End If
                        End If
                    End If

                    Continue For  ' skip EMA/RSI block for this bar
                End If

                ' ── Multi-Confluence Engine signal ─────────────────────────────────
                ' ALL 7 conditions (Ichimoku cloud + EMA21 + Tenkan/Kijun + Chikou +
                ' ADX/DMI + MACD histogram + StochRSI) must align for Long or Short.
                ' SL = min(1.5×ATR, Ichimoku cloud edge); TP = 2:1 reward-to-risk.
                If config.StrategyCondition = StrategyConditionType.MultiConfluence Then
                    Dim mcSpanA = If(mcIchiSpanA IsNot Nothing, mcIchiSpanA(i), Single.NaN)
                    Dim mcSpanB = If(mcIchiSpanB IsNot Nothing, mcIchiSpanB(i), Single.NaN)
                    Dim mcTenkan = If(mcIchiTenkan IsNot Nothing, mcIchiTenkan(i), Single.NaN)
                    Dim mcKijun = If(mcIchiKijun IsNot Nothing, mcIchiKijun(i), Single.NaN)
                    Dim mcAdxVal = If(mcAdxSeries IsNot Nothing, mcAdxSeries(i), Single.NaN)
                    Dim mcPlusDIVal = If(mcPlusDI IsNot Nothing, mcPlusDI(i), Single.NaN)
                    Dim mcMinusDIVal = If(mcMinusDI IsNot Nothing, mcMinusDI(i), Single.NaN)
                    Dim mcHistNow = If(mcMacdHist IsNot Nothing AndAlso Not Single.IsNaN(mcMacdHist(i)), mcMacdHist(i), Single.NaN)
                    Dim mcHistPrev = If(mcMacdHist IsNot Nothing AndAlso i > 0 AndAlso Not Single.IsNaN(mcMacdHist(i - 1)), mcMacdHist(i - 1), Single.NaN)
                    Dim mcStochK = If(mcStochRsiK IsNot Nothing AndAlso Not Single.IsNaN(mcStochRsiK(i)), mcStochRsiK(i), Single.NaN)
                    Dim mcAtrVal = If(mcAtr14 IsNot Nothing AndAlso Not Single.IsNaN(mcAtr14(i)), mcAtr14(i), 0.0F)
                    Dim mcEma21Val = ema21Series(i)
                    Dim mcLastClose = bar.Close

                    ' Skip if any indicator is still warming up
                    If Not (Single.IsNaN(mcSpanA) OrElse Single.IsNaN(mcSpanB) OrElse
                            Single.IsNaN(mcTenkan) OrElse Single.IsNaN(mcKijun) OrElse
                            Single.IsNaN(mcAdxVal) OrElse Single.IsNaN(mcHistNow) OrElse
                            Single.IsNaN(mcHistPrev) OrElse Single.IsNaN(mcStochK) OrElse
                            Single.IsNaN(mcEma21Val)) Then

                        Dim mcCloudTop = SafeD(Math.Max(mcSpanA, mcSpanB))
                        Dim mcCloudBottom = SafeD(Math.Min(mcSpanA, mcSpanB))
                        Dim mcLagIdx = i - 26
                        Dim mcLagClose = If(mcLagIdx >= 0, filteredBars(mcLagIdx).Close, Decimal.MinValue)

                        ' ── Long: all 7 conditions ────────────────────────────────
                        Dim lcl1 = (mcLastClose > mcCloudTop)
                        Dim lcl2 = (mcLastClose > SafeD(mcEma21Val))
                        Dim lcl3 = (mcTenkan > mcKijun)
                        Dim lcl4 = (mcLagIdx >= 0 AndAlso mcLastClose > mcLagClose)
                        Dim lcl5 = ((config.MinAdxThreshold <= 0.0F OrElse mcAdxVal >= config.MinAdxThreshold) AndAlso mcPlusDIVal > mcMinusDIVal)
                        Dim lcl6 = (mcHistNow > 0 AndAlso mcHistNow > mcHistPrev)
                        Dim lcl7 = (mcStochK < 0.8F)

                        ' ── Short: all 7 conditions ───────────────────────────────
                        Dim scl1 = (mcLastClose < mcCloudBottom)
                        Dim scl2 = (mcLastClose < SafeD(mcEma21Val))
                        Dim scl3 = (mcTenkan < mcKijun)
                        Dim scl4 = (mcLagIdx >= 0 AndAlso mcLastClose < mcLagClose)
                        Dim scl5 = ((config.MinAdxThreshold <= 0.0F OrElse mcAdxVal >= config.MinAdxThreshold) AndAlso mcMinusDIVal > mcPlusDIVal)
                        Dim scl6 = (mcHistNow < 0 AndAlso mcHistNow < mcHistPrev)
                        Dim scl7 = (mcStochK > 0.2F)

                        If openLegs.Count = 0 Then
                            Dim mcSide As String = Nothing
                            Dim mcSlCand As Decimal = 0D
                            Dim mcTpCand As Decimal = 0D
                            Dim mcAtrSlLevel As Decimal = 0D

                            If lcl1 AndAlso lcl2 AndAlso lcl3 AndAlso lcl4 AndAlso lcl5 AndAlso lcl6 AndAlso lcl7 Then
                                mcSide = "Buy"
                                ' SL = min(1.5×ATR, cloud bottom); TP = 2:1 R:R from actual SL
                                mcAtrSlLevel = mcLastClose - SafeD(mcAtrVal * 1.5F)
                                mcSlCand = If(mcCloudBottom > mcAtrSlLevel, mcCloudBottom, mcAtrSlLevel)
                                mcTpCand = mcLastClose + (mcLastClose - mcSlCand) * 2D
                            ElseIf scl1 AndAlso scl2 AndAlso scl3 AndAlso scl4 AndAlso scl5 AndAlso scl6 AndAlso scl7 Then
                                mcSide = "Sell"
                                ' SL = min(1.5×ATR, cloud top); TP = 2:1 R:R from actual SL
                                mcAtrSlLevel = mcLastClose + SafeD(mcAtrVal * 1.5F)
                                mcSlCand = If(mcCloudTop < mcAtrSlLevel, mcCloudTop, mcAtrSlLevel)
                                mcTpCand = mcLastClose - (mcSlCand - mcLastClose) * 2D
                            End If

                            If mcSide IsNot Nothing AndAlso mcSlCand <> 0D Then
                                positionGroupCounter += 1
                                openLegs.Add(New BacktestTrade With {
                                    .PositionGroupId = positionGroupCounter,
                                    .EntryTime = bar.Timestamp,
                                    .EntryPrice = mcLastClose,
                                    .Side = mcSide,
                                    .Quantity = config.Quantity,
                                    .SignalConfidence = 1.0F
                                })
                                mcOpenSlPrice = mcSlCand
                                mcOpenTpPrice = mcTpCand
                                mcIsLong = (mcSide = "Buy")
                                ' Initialise dynamic exit levels from ATR-derived entry distances
                                If dynEnabled Then
                                    dynStopDelta = Math.Abs(mcLastClose - mcSlCand)
                                    dynTpDelta   = Math.Abs(mcTpCand - mcLastClose)
                                    dynStop = mcSlCand
                                    dynTp   = mcTpCand
                                End If
                            End If
                        End If
                    End If

                    Continue For  ' skip EMA/RSI block for this bar
                End If

                ' ── ConnorsRsi2 signal ────────────────────────────────────────────
                ' Long: RSI(2) < 10 AND close > SMA(200) — short-term dip in long-term uptrend.
                ' Short: RSI(2) > 90 AND close < SMA(200) — short-term rally in long-term downtrend.
                If openLegs.Count = 0 AndAlso
                   config.StrategyCondition = StrategyConditionType.ConnorsRsi2 Then
                    If qlRsi2 IsNot Nothing AndAlso qlSma5 IsNot Nothing AndAlso qlSma200 IsNot Nothing Then
                        Dim rsi2Val = qlRsi2(i)
                        Dim sma200Val = qlSma200(i)
                        If Not (Single.IsNaN(rsi2Val) OrElse Single.IsNaN(sma200Val)) Then
                            Dim crSide As String = Nothing
                            If rsi2Val < 10.0F AndAlso bar.Close > SafeD(sma200Val) Then
                                crSide = "Buy"
                            ElseIf rsi2Val > 90.0F AndAlso bar.Close < SafeD(sma200Val) Then
                                crSide = "Sell"
                            End If
                            If crSide IsNot Nothing Then
                                positionGroupCounter += 1
                                openLegs.Add(New BacktestTrade With {
                                    .PositionGroupId = positionGroupCounter,
                                    .EntryTime = bar.Timestamp,
                                    .EntryPrice = bar.Close,
                                    .Side = crSide,
                                    .Quantity = config.Quantity,
                                    .SignalConfidence = 1.0F
                                })
                                If dynEnabled Then
                                    Dim atrCr As Single = If(universalAtr14 IsNot Nothing AndAlso i < universalAtr14.Length AndAlso
                                                              Not Single.IsNaN(universalAtr14(i)), universalAtr14(i), 0.0F)
                                    If config.UseAtrMode AndAlso atrCr > 0.0F Then
                                        dynStopDelta = SafeD(atrCr) * config.SlAtrMultiple
                                        dynTpDelta   = SafeD(atrCr) * config.TpAtrMultiple
                                    Else
                                        Dim dppCr = config.PointValue * config.Quantity
                                        If dppCr > 0D Then
                                            dynStopDelta = Math.Round(config.InitialSlAmount / dppCr, 4)
                                            dynTpDelta   = Math.Round(config.InitialTpAmount / dppCr, 4)
                                        End If
                                    End If
                                    If dynStopDelta > 0D Then
                                        Dim isCrBuy = (crSide = "Buy")
                                        dynStop = If(isCrBuy, bar.Close - dynStopDelta, bar.Close + dynStopDelta)
                                        dynTp   = If(isCrBuy, bar.Close + dynTpDelta,   bar.Close - dynTpDelta)
                                    End If
                                End If
                            End If
                        End If
                    End If
                    Continue For
                End If

                ' ── SuperTrend signal ──────────────────────────────────────────────
                ' Long on direction flip from −1 to +1; Short on flip from +1 to −1.
                ' SL = SuperTrend line level at entry; TP = 2× ATR(10) from entry.
                If config.StrategyCondition = StrategyConditionType.SuperTrend Then
                    If qlStLine IsNot Nothing AndAlso qlStDir IsNot Nothing Then
                        Dim stDirNow = qlStDir(i)
                        Dim stLineNow = qlStLine(i)
                        If Not (Single.IsNaN(stDirNow) OrElse Single.IsNaN(stLineNow)) Then
                            ' Entry on direction flip (open legs = 0 only)
                            If openLegs.Count = 0 AndAlso qlStPrevDir <> 0.0F Then
                                Dim stSide As String = Nothing
                                If stDirNow = 1.0F AndAlso qlStPrevDir = -1.0F Then
                                    stSide = "Buy"
                                ElseIf stDirNow = -1.0F AndAlso qlStPrevDir = 1.0F Then
                                    stSide = "Sell"
                                End If
                                If stSide IsNot Nothing Then
                                    ' Use pre-calculated ATR(10) for TP sizing
                                    Dim stAtrVal = If(qlStAtr10 IsNot Nothing AndAlso
                                                      i < qlStAtr10.Length AndAlso
                                                      Not Single.IsNaN(qlStAtr10(i)),
                                                      SafeD(qlStAtr10(i)), 0D)
                                    positionGroupCounter += 1
                                    openLegs.Add(New BacktestTrade With {
                                        .PositionGroupId = positionGroupCounter,
                                        .EntryTime = bar.Timestamp,
                                        .EntryPrice = bar.Close,
                                        .Side = stSide,
                                        .Quantity = config.Quantity,
                                        .SignalConfidence = 1.0F
                                    })
                                    qlStIsLong = (stSide = "Buy")
                                    ' SL = SuperTrend line; TP = 2× ATR reward
                                    If qlStIsLong Then
                                        qlStOpenSlPrice = SafeD(stLineNow)
                                        qlStOpenTpPrice = If(stAtrVal > 0D,
                                                             bar.Close + stAtrVal * 2D,
                                                             bar.Close * 1.02D)
                                    Else
                                        qlStOpenSlPrice = SafeD(stLineNow)
                                        qlStOpenTpPrice = If(stAtrVal > 0D,
                                                             bar.Close - stAtrVal * 2D,
                                                             bar.Close * 0.98D)
                                    End If
                                    ' Initialise dynamic exit levels from SuperTrend price levels
                                    If dynEnabled Then
                                        dynStopDelta = Math.Abs(bar.Close - qlStOpenSlPrice)
                                        dynTpDelta   = Math.Abs(qlStOpenTpPrice - bar.Close)
                                        dynStop = qlStOpenSlPrice
                                        dynTp   = qlStOpenTpPrice
                                    End If
                                End If
                            End If
                            qlStPrevDir = stDirNow
                        End If
                    End If
                    Continue For
                End If

                ' ── DonchianBreakout signal ────────────────────────────────────────
                ' Long on close > 20-bar upper channel; Short on close < 20-bar lower channel.
                ' Exit when close crosses the 10-bar middle channel in adverse direction.
                If openLegs.Count = 0 AndAlso
                   config.StrategyCondition = StrategyConditionType.DonchianBreakout Then
                    If qlDonUpper20 IsNot Nothing AndAlso qlDonLower20 IsNot Nothing AndAlso qlDonMid10 IsNot Nothing Then
                        Dim donUpperVal = qlDonUpper20(i)
                        Dim donLowerVal = qlDonLower20(i)
                        Dim donMidVal = qlDonMid10(i)
                        Dim prevDonUpperVal = If(i > 0, qlDonUpper20(i - 1), Single.NaN)
                        Dim prevDonLowerVal = If(i > 0, qlDonLower20(i - 1), Single.NaN)
                        If Not (Single.IsNaN(donUpperVal) OrElse Single.IsNaN(donLowerVal) OrElse
                                Single.IsNaN(donMidVal) OrElse Single.IsNaN(prevDonUpperVal) OrElse
                                Single.IsNaN(prevDonLowerVal)) Then
                            Dim donSide As String = Nothing
                            ' Break above prior bar's upper = new breakout long
                            If bar.Close > SafeD(prevDonUpperVal) Then
                                donSide = "Buy"
                            ' Break below prior bar's lower = new breakout short
                            ElseIf bar.Close < SafeD(prevDonLowerVal) Then
                                donSide = "Sell"
                            End If
                            If donSide IsNot Nothing Then
                                positionGroupCounter += 1
                                openLegs.Add(New BacktestTrade With {
                                    .PositionGroupId = positionGroupCounter,
                                    .EntryTime = bar.Timestamp,
                                    .EntryPrice = bar.Close,
                                    .Side = donSide,
                                    .Quantity = config.Quantity,
                                    .SignalConfidence = 1.0F
                                })
                                qlDonIsLong = (donSide = "Buy")
                                ' Initialise dynamic exit levels — dynStop is a trailing hard stop;
                                ' dynTp set far away since Donchian uses indicator-based exits.
                                If dynEnabled Then
                                    Dim atrDon As Single = If(universalAtr14 IsNot Nothing AndAlso i < universalAtr14.Length AndAlso
                                                               Not Single.IsNaN(universalAtr14(i)), universalAtr14(i), 0.0F)
                                    If config.UseAtrMode AndAlso atrDon > 0.0F Then
                                        dynStopDelta = SafeD(atrDon) * config.SlAtrMultiple
                                        dynTpDelta   = SafeD(atrDon) * config.TpAtrMultiple
                                    Else
                                        Dim dppDon = config.PointValue * config.Quantity
                                        If dppDon > 0D Then
                                            dynStopDelta = Math.Round(config.InitialSlAmount / dppDon, 4)
                                            dynTpDelta   = Math.Round(config.InitialTpAmount / dppDon, 4)
                                        End If
                                    End If
                                    If dynStopDelta > 0D Then
                                        Dim isDonBuy = (donSide = "Buy")
                                        dynStop = If(isDonBuy, bar.Close - dynStopDelta, bar.Close + dynStopDelta)
                                        dynTp   = If(isDonBuy, bar.Close + dynTpDelta,   bar.Close - dynTpDelta)
                                    End If
                                End If
                                ' Exit when close crosses the 10-bar mid-channel (adverse direction)
                                qlDonOpenMidExit = SafeD(donMidVal)
                            End If
                        End If
                    End If
                    Continue For
                End If

                ' ── BbRsiMeanReversion signal ──────────────────────────────────────
                ' Long when close < lower BB(20,2) AND RSI(14) < 30 (dual oversold).
                ' Short when close > upper BB(20,2) AND RSI(14) > 70 (dual overbought).
                ' Exit at middle BB (SMA20) or RSI(14) crosses 50.
                If openLegs.Count = 0 AndAlso
                   config.StrategyCondition = StrategyConditionType.BbRsiMeanReversion Then
                    If qlBbUpper IsNot Nothing AndAlso qlBbLower IsNot Nothing AndAlso qlBbMiddle IsNot Nothing Then
                        Dim bbUpperVal = qlBbUpper(i)
                        Dim bbLowerVal = qlBbLower(i)
                        Dim bbMidVal = qlBbMiddle(i)
                        Dim rsiMrVal = rsi14Series(i)
                        If Not (Single.IsNaN(bbUpperVal) OrElse Single.IsNaN(bbLowerVal) OrElse
                                Single.IsNaN(bbMidVal) OrElse Single.IsNaN(rsiMrVal)) Then
                            Dim mrSide As String = Nothing
                            If bar.Close < SafeD(bbLowerVal) AndAlso rsiMrVal < 30.0F Then
                                mrSide = "Buy"
                            ElseIf bar.Close > SafeD(bbUpperVal) AndAlso rsiMrVal > 70.0F Then
                                mrSide = "Sell"
                            End If
                            If mrSide IsNot Nothing Then
                                positionGroupCounter += 1
                                openLegs.Add(New BacktestTrade With {
                                    .PositionGroupId = positionGroupCounter,
                                    .EntryTime = bar.Timestamp,
                                    .EntryPrice = bar.Close,
                                    .Side = mrSide,
                                    .Quantity = config.Quantity,
                                    .SignalConfidence = 1.0F
                                })
                                qlBbIsLong = (mrSide = "Buy")
                                ' Exit when price returns to middle BB (SMA20)
                                qlBbOpenMidExit = SafeD(bbMidVal)
                                If dynEnabled Then
                                    Dim atrBb As Single = If(universalAtr14 IsNot Nothing AndAlso i < universalAtr14.Length AndAlso
                                                              Not Single.IsNaN(universalAtr14(i)), universalAtr14(i), 0.0F)
                                    If config.UseAtrMode AndAlso atrBb > 0.0F Then
                                        dynStopDelta = SafeD(atrBb) * config.SlAtrMultiple
                                        dynTpDelta   = SafeD(atrBb) * config.TpAtrMultiple
                                    Else
                                        Dim dppBb = config.PointValue * config.Quantity
                                        If dppBb > 0D Then
                                            dynStopDelta = Math.Round(config.InitialSlAmount / dppBb, 4)
                                            dynTpDelta   = Math.Round(config.InitialTpAmount / dppBb, 4)
                                        End If
                                    End If
                                    If dynStopDelta > 0D Then
                                        Dim isBbBuy = (mrSide = "Buy")
                                        dynStop = If(isBbBuy, bar.Close - dynStopDelta, bar.Close + dynStopDelta)
                                        dynTp   = If(isBbBuy, bar.Close + dynTpDelta,   bar.Close - dynTpDelta)
                                    End If
                                End If
                            End If
                        End If
                    End If
                    Continue For
                End If

                ' ── DoubleBubbleButt signal ────────────────────────────────────────
                ' Long  when close > upper inner 1.0 SD band (enters Buy Zone).
                ' Short when close < lower inner 1.0 SD band (enters Sell Zone).
                ' Hard SL = outer 2.0 SD band at entry; TP = 2× ATR(20) from entry.
                ' Exit is handled above via neutral-zone re-entry (dbbInner1SdExit).
                If openLegs.Count = 0 AndAlso
                   config.StrategyCondition = StrategyConditionType.DoubleBubbleButt Then
                    If dbbInnerUpper IsNot Nothing AndAlso dbbInnerLower IsNot Nothing AndAlso
                       dbbOuterUpper IsNot Nothing AndAlso dbbOuterLower IsNot Nothing Then
                        Dim dbbIU = dbbInnerUpper(i)
                        Dim dbbIL = dbbInnerLower(i)
                        Dim dbbOU = dbbOuterUpper(i)
                        Dim dbbOL = dbbOuterLower(i)
                        Dim dbbAtrVal = If(dbbAtr20 IsNot Nothing AndAlso i < dbbAtr20.Length, dbbAtr20(i), Single.NaN)
                        If Not (Single.IsNaN(dbbIU) OrElse Single.IsNaN(dbbIL) OrElse
                                Single.IsNaN(dbbOU) OrElse Single.IsNaN(dbbOL)) Then
                            Dim dbbSide As String = Nothing
                            ' Long: close enters Buy Zone (above upper 1-SD band)
                            If bar.Close > SafeD(dbbIU) Then
                                dbbSide = "Buy"
                            ' Short: close enters Sell Zone (below lower 1-SD band)
                            ElseIf bar.Close < SafeD(dbbIL) Then
                                dbbSide = "Sell"
                            End If
                            If dbbSide IsNot Nothing Then
                                positionGroupCounter += 1
                                openLegs.Add(New BacktestTrade With {
                                    .PositionGroupId = positionGroupCounter,
                                    .EntryTime = bar.Timestamp,
                                    .EntryPrice = bar.Close,
                                    .Side = dbbSide,
                                    .Quantity = config.Quantity,
                                    .SignalConfidence = 1.0F
                                })
                                dbbIsLong = (dbbSide = "Buy")
                                ' Record inner 1-SD level for neutral-zone exit trigger
                                dbbInner1SdExit = If(dbbIsLong, SafeD(dbbIU), SafeD(dbbIL))
                                ' SL = outer 2.0 SD band at entry; TP = 2× ATR(20) from entry
                                Dim dbbAtr = If(Not Single.IsNaN(dbbAtrVal), SafeD(dbbAtrVal), 0D)
                                If dynEnabled Then
                                    ' ATR mode: SL = outer band distance, TP = 2× ATR
                                    Dim outerBandDist = If(dbbIsLong,
                                        bar.Close - SafeD(dbbOL),   ' long SL = outer lower band
                                        SafeD(dbbOU) - bar.Close)   ' short SL = outer upper band
                                    dynStopDelta = If(outerBandDist > 0D, outerBandDist,
                                                     If(dbbAtr > 0D, dbbAtr * 2D, 0D))
                                    dynTpDelta = If(dbbAtr > 0D, dbbAtr * 2D, dynStopDelta)
                                    If dynStopDelta > 0D Then
                                        dynStop = If(dbbIsLong, bar.Close - dynStopDelta, bar.Close + dynStopDelta)
                                        dynTp   = If(dbbIsLong, bar.Close + dynTpDelta,   bar.Close - dynTpDelta)
                                    End If
                                End If
                            End If
                        End If
                    End If
                    Continue For
                End If

                ' ── VIDYA Cross signal ──────────────────────────────────────────────
                ' Long when close crosses above VIDYA(14); Short when close crosses below.
                ' Gate: 6-bar ΔVol ≥ +20% (long) or ≤ −20% (short).
                ' Confidence = |ΔVol| × 100.
                If openLegs.Count = 0 AndAlso
                   config.StrategyCondition = StrategyConditionType.VidyaCross Then
                    If vcVidyaSeries IsNot Nothing AndAlso vcCmoSeries IsNot Nothing AndAlso vcDeltaVolSeries IsNot Nothing Then
                        Dim vidyaNow = vcVidyaSeries(i)
                        Dim vidyaPrev = vcVidyaSeries(i - 1)
                        Dim deltaVol = vcDeltaVolSeries(i)
                        If Not (Single.IsNaN(vidyaNow) OrElse Single.IsNaN(vidyaPrev) OrElse Single.IsNaN(deltaVol)) Then
                            Dim prevClose = filteredBars(i - 1).Close
                            Dim vcSide As String = Nothing
                            ' Cross above VIDYA + positive volume delta ≥ 20%
                            If prevClose <= SafeD(vidyaPrev) AndAlso bar.Close > SafeD(vidyaNow) AndAlso deltaVol >= 0.2F Then
                                vcSide = "Buy"
                            ' Cross below VIDYA + negative volume delta ≤ -20%
                            ElseIf prevClose >= SafeD(vidyaPrev) AndAlso bar.Close < SafeD(vidyaNow) AndAlso deltaVol <= -0.2F Then
                                vcSide = "Sell"
                            End If
                            If vcSide IsNot Nothing Then
                                Dim vcConf = CSng(Math.Min(100.0, Math.Abs(deltaVol) * 100.0)) / 100.0F
                                If vcConf < config.MinSignalConfidence Then vcSide = Nothing
                            End If
                            If vcSide IsNot Nothing Then
                                Dim vcConf2 = CSng(Math.Min(100.0, Math.Abs(deltaVol) * 100.0)) / 100.0F
                                positionGroupCounter += 1
                                openLegs.Add(New BacktestTrade With {
                                    .PositionGroupId = positionGroupCounter,
                                    .EntryTime = bar.Timestamp,
                                    .EntryPrice = bar.Close,
                                    .Side = vcSide,
                                    .Quantity = config.Quantity,
                                    .SignalConfidence = vcConf2
                                })
                                If dynEnabled Then
                                    Dim atrVc As Single = If(vcAtr14 IsNot Nothing AndAlso i < vcAtr14.Length AndAlso
                                                              Not Single.IsNaN(vcAtr14(i)), vcAtr14(i), 0.0F)
                                    If config.UseAtrMode AndAlso atrVc > 0.0F Then
                                        dynStopDelta = SafeD(atrVc) * config.SlAtrMultiple
                                        dynTpDelta   = SafeD(atrVc) * config.TpAtrMultiple
                                    Else
                                        Dim dppVc = config.PointValue * config.Quantity
                                        If dppVc > 0D Then
                                            dynStopDelta = Math.Round(config.InitialSlAmount / dppVc, 4)
                                            dynTpDelta   = Math.Round(config.InitialTpAmount / dppVc, 4)
                                        End If
                                    End If
                                    If dynStopDelta > 0D Then
                                        Dim isVcBuy = (vcSide = "Buy")
                                        dynStop = If(isVcBuy, bar.Close - dynStopDelta, bar.Close + dynStopDelta)
                                        dynTp   = If(isVcBuy, bar.Close + dynTpDelta,   bar.Close - dynTpDelta)
                                    End If
                                End If
                            End If
                        End If
                    End If
                    Continue For
                End If

                ' ── Naked Trader signal ────────────────────────────────────────────
                ' 4-vote consensus: EMA(9/21), MACD(8,17,9), DMI(14), VWAP.
                ' Fires Medium (3/4 votes, ADX≥20) or High (all votes, ADX≥25+vol).
                If openLegs.Count = 0 AndAlso
                   config.StrategyCondition = StrategyConditionType.NakedTrader Then
                    If ntEma9 IsNot Nothing AndAlso ntEma21 IsNot Nothing AndAlso ntMacdHist IsNot Nothing Then
                        Dim nEma9 = ntEma9(i)
                        Dim nEma21v = ntEma21(i)
                        Dim nMacdH = ntMacdHist(i)
                        Dim nMacdL = ntMacdLine(i)
                        Dim nPdi = ntPlusDI(i)
                        Dim nMdi = ntMinusDI(i)
                        Dim nAdx = ntAdxSeries(i)
                        Dim nVwap = ntVwapSeries(i)
                        If Not (Single.IsNaN(nEma9) OrElse Single.IsNaN(nEma21v) OrElse Single.IsNaN(nAdx)) Then
                            Dim ntUp As Integer = 0
                            Dim ntDown As Integer = 0
                            Dim ntTotal As Integer = 0
                            ' Vote 1: EMA — 0.1% gap filter
                            Const EmaGapPct As Single = 0.001F
                            If nEma9 > nEma21v * (1.0F + EmaGapPct) Then
                                ntTotal += 1 : ntUp += 1
                            ElseIf nEma9 < nEma21v * (1.0F - EmaGapPct) Then
                                ntTotal += 1 : ntDown += 1
                            End If
                            ' Vote 2: MACD — hist or line with ≥0.001 magnitude
                            Const MacdMinMag As Single = 0.001F
                            Dim macdVote As Single = Single.NaN
                            If Not Single.IsNaN(nMacdH) AndAlso Math.Abs(nMacdH) >= MacdMinMag Then
                                macdVote = nMacdH
                            ElseIf Not Single.IsNaN(nMacdL) AndAlso Math.Abs(nMacdL) >= MacdMinMag Then
                                macdVote = nMacdL
                            End If
                            If Not Single.IsNaN(macdVote) Then
                                ntTotal += 1
                                If macdVote > 0 Then ntUp += 1 Else ntDown += 1
                            End If
                            ' Vote 3: DMI — ≥1.0 pt spread
                            Const DiMinSpread As Single = 1.0F
                            If Not Single.IsNaN(nPdi) AndAlso Not Single.IsNaN(nMdi) Then
                                If Math.Abs(nPdi - nMdi) >= DiMinSpread Then
                                    ntTotal += 1
                                    If nPdi > nMdi Then ntUp += 1 Else ntDown += 1
                                End If
                            End If
                            ' Vote 4: VWAP — 0.1% gap
                            If Not Single.IsNaN(nVwap) Then
                                Const VwapGapPct As Single = 0.001F
                                Dim closeDbl = CDbl(bar.Close)
                                Dim vwapDbl = CDbl(nVwap)
                                If closeDbl > vwapDbl * (1.0 + VwapGapPct) Then
                                    ntTotal += 1 : ntUp += 1
                                ElseIf closeDbl < vwapDbl * (1.0 - VwapGapPct) Then
                                    ntTotal += 1 : ntDown += 1
                                End If
                            End If
                            ' Direction and confidence
                            Dim ntAligned = Math.Max(ntUp, ntDown)
                            Dim ntIsBull = (ntUp > ntDown)
                            Dim ntIsTie = (ntUp = ntDown)
                            Dim ntConf As Single = 0.0F
                            Dim ntFireable = False
                            Dim ntAdxGate = CSng(If(config.MinAdxThreshold > 0, config.MinAdxThreshold, 20.0))
                            If Not ntIsTie AndAlso nAdx >= ntAdxGate Then
                                If nAdx >= 25.0F AndAlso ntAligned = ntTotal AndAlso ntTotal >= 3 Then
                                    ' High confidence: all votes aligned + ADX≥25
                                    Dim volOk = (ntVolMa20 IsNot Nothing AndAlso Not Single.IsNaN(ntVolMa20(i)) AndAlso
                                                  i < filteredBars.Count AndAlso filteredBars(i).Volume > SafeD(ntVolMa20(i)))
                                    ntConf = If(volOk, 0.9F, 0.6F)
                                    ntFireable = True
                                ElseIf ntAligned >= ntTotal - 1 AndAlso ntTotal >= 3 Then
                                    ' Medium confidence: 3/4 votes aligned + ADX≥20
                                    ntConf = 0.6F
                                    ntFireable = True
                                End If
                            End If
                            If ntFireable AndAlso ntConf >= config.MinSignalConfidence Then
                                Dim ntSide = If(ntIsBull, "Buy", "Sell")
                                positionGroupCounter += 1
                                openLegs.Add(New BacktestTrade With {
                                    .PositionGroupId = positionGroupCounter,
                                    .EntryTime = bar.Timestamp,
                                    .EntryPrice = bar.Close,
                                    .Side = ntSide,
                                    .Quantity = config.Quantity,
                                    .SignalConfidence = ntConf
                                })
                                If dynEnabled Then
                                    Dim atrNt As Single = If(ntAtr14 IsNot Nothing AndAlso i < ntAtr14.Length AndAlso
                                                              Not Single.IsNaN(ntAtr14(i)), ntAtr14(i), 0.0F)
                                    If config.UseAtrMode AndAlso atrNt > 0.0F Then
                                        dynStopDelta = SafeD(atrNt) * config.SlAtrMultiple
                                        dynTpDelta   = SafeD(atrNt) * config.TpAtrMultiple
                                    Else
                                        Dim dppNt = config.PointValue * config.Quantity
                                        If dppNt > 0D Then
                                            dynStopDelta = Math.Round(config.InitialSlAmount / dppNt, 4)
                                            dynTpDelta   = Math.Round(config.InitialTpAmount / dppNt, 4)
                                        End If
                                    End If
                                    If dynStopDelta > 0D Then
                                        Dim isNtBuy = (ntSide = "Buy")
                                        dynStop = If(isNtBuy, bar.Close - dynStopDelta, bar.Close + dynStopDelta)
                                        dynTp   = If(isNtBuy, bar.Close + dynTpDelta,   bar.Close - dynTpDelta)
                                    End If
                                End If
                            End If
                        End If
                    End If
                    Continue For
                End If

                ' ── LULT Divergence signal ─────────────────────────────────────────
                ' 6-step WaveTrend divergence: Anchor (WT1 ≷ ±60) → Trigger (shallower)
                ' → Price divergence → Dot signal (WT1×WT2 cross) → Engulfing candle.
                ' Time filter: 11–17 UTC. SL at trigger extreme. TP = 2R.
                If openLegs.Count = 0 AndAlso
                   config.StrategyCondition = StrategyConditionType.LultDivergence Then
                    If lultWt1 IsNot Nothing AndAlso lultWt2 IsNot Nothing AndAlso i >= 100 Then
                        ' Time filter: bar timestamp hour must be 11–17 UTC
                        Dim barUtcHour = bar.Timestamp.UtcDateTime.Hour
                        Dim inWindow = (barUtcHour >= 11 AndAlso barUtcHour < 17)
                        If inWindow Then
                            ' Evaluate both bull and bear setups using backward scan
                            ' (Simplified inline version of LultDivergenceStrategy.EvaluateSetup)
                            Dim lultSide As String = Nothing
                            Dim triggerExtreme As Decimal = 0D
                            For Each isBull In {True, False}
                                If lultSide IsNot Nothing Then Exit For
                                Dim searchFrom = Math.Max(1, i - 2 - 80)
                                Dim extremes As New List(Of (Idx As Integer, Wt1Val As Single, PriceEx As Decimal))
                                For ei = searchFrom To i - 2
                                    If Single.IsNaN(lultWt1(ei)) OrElse Single.IsNaN(lultWt1(ei - 1)) OrElse Single.IsNaN(lultWt1(ei + 1)) Then Continue For
                                    If isBull Then
                                        If lultWt1(ei) <= lultWt1(ei - 1) AndAlso lultWt1(ei) <= lultWt1(ei + 1) AndAlso
                                           (lultWt1(ei) < lultWt1(ei - 1) OrElse lultWt1(ei) < lultWt1(ei + 1)) Then
                                            extremes.Add((ei, lultWt1(ei), filteredBars(ei).Low))
                                        End If
                                    Else
                                        If lultWt1(ei) >= lultWt1(ei - 1) AndAlso lultWt1(ei) >= lultWt1(ei + 1) AndAlso
                                           (lultWt1(ei) > lultWt1(ei - 1) OrElse lultWt1(ei) > lultWt1(ei + 1)) Then
                                            extremes.Add((ei, lultWt1(ei), filteredBars(ei).High))
                                        End If
                                    End If
                                Next
                                If extremes.Count < 2 Then Continue For
                                For ti = extremes.Count - 1 To 1 Step -1
                                    Dim trigger = extremes(ti)
                                    If i - trigger.Idx < 2 Then Continue For
                                    For anchorI = ti - 1 To 0 Step -1
                                        Dim anchor = extremes(anchorI)
                                        ' Step 1-2: anchor breaches ±60
                                        Dim anchorBreached = If(isBull, anchor.Wt1Val < -60.0F, anchor.Wt1Val > 60.0F)
                                        If Not anchorBreached Then Continue For
                                        ' Step 3: trigger shallower than anchor
                                        Dim trigShallower = If(isBull, trigger.Wt1Val > anchor.Wt1Val, trigger.Wt1Val < anchor.Wt1Val)
                                        If Not trigShallower Then Continue For
                                        ' Step 4: price divergence
                                        Dim hasDiverg = If(isBull, trigger.PriceEx < anchor.PriceEx, trigger.PriceEx > anchor.PriceEx)
                                        If Not hasDiverg Then Continue For
                                        ' Step 5: dot signal — WT1 crosses WT2 after trigger
                                        Dim dotIdx = -1
                                        Dim dotEnd = Math.Min(i - 1, trigger.Idx + 15)
                                        For di = trigger.Idx + 1 To dotEnd
                                            If Single.IsNaN(lultWt1(di)) OrElse Single.IsNaN(lultWt2(di)) OrElse
                                               Single.IsNaN(lultWt1(di - 1)) OrElse Single.IsNaN(lultWt2(di - 1)) Then Continue For
                                            If isBull Then
                                                If lultWt1(di - 1) < lultWt2(di - 1) AndAlso lultWt1(di) >= lultWt2(di) Then dotIdx = di : Exit For
                                            Else
                                                If lultWt1(di - 1) > lultWt2(di - 1) AndAlso lultWt1(di) <= lultWt2(di) Then dotIdx = di : Exit For
                                            End If
                                        Next
                                        If dotIdx < 0 Then Continue For
                                        ' Step 6: engulfing candle at bar[i] within window of dot
                                        If i <= dotIdx OrElse i > dotIdx + 6 Then Continue For
                                        ' Check engulfing pattern
                                        Dim curO = filteredBars(i).Open : Dim curC = bar.Close
                                        Dim prvO = filteredBars(i - 1).Open : Dim prvC = filteredBars(i - 1).Close
                                        Dim prvBodyLo = Math.Min(prvO, prvC) : Dim prvBodyHi = Math.Max(prvO, prvC)
                                        Dim bodySize = Math.Abs(curC - curO)
                                        If bodySize = 0D Then Continue For
                                        Dim engulfOk As Boolean = False
                                        If isBull Then
                                            If curC > curO AndAlso curO <= prvBodyLo AndAlso curC >= prvBodyHi Then
                                                Dim lWick = curO - filteredBars(i).Low
                                                engulfOk = (CDbl(lWick) / CDbl(bodySize) <= 0.4)
                                            End If
                                        Else
                                            If curC < curO AndAlso curO >= prvBodyHi AndAlso curC <= prvBodyLo Then
                                                Dim uWick = filteredBars(i).High - curO
                                                engulfOk = (CDbl(uWick) / CDbl(bodySize) <= 0.4)
                                            End If
                                        End If
                                        If Not engulfOk Then Continue For
                                        ' All 6 steps confirmed
                                        lultSide = If(isBull, "Buy", "Sell")
                                        triggerExtreme = trigger.PriceEx
                                        Exit For
                                    Next ' ai
                                    If lultSide IsNot Nothing Then Exit For
                                Next ' ti
                            Next ' isBull
                            If lultSide IsNot Nothing Then
                                positionGroupCounter += 1
                                Dim lultConf = 1.0F  ' all 6 steps confirmed
                                openLegs.Add(New BacktestTrade With {
                                    .PositionGroupId = positionGroupCounter,
                                    .EntryTime = bar.Timestamp,
                                    .EntryPrice = bar.Close,
                                    .Side = lultSide,
                                    .Quantity = config.Quantity,
                                    .SignalConfidence = lultConf
                                })
                                ' SL at trigger extreme; TP = 2R
                                Dim lultSlDist = Math.Abs(bar.Close - triggerExtreme)
                                If lultSlDist = 0D Then lultSlDist = 1D  ' safety: avoid zero-distance
                                Dim isLultBuy = (lultSide = "Buy")
                                If dynEnabled OrElse True Then
                                    dynStopDelta = lultSlDist
                                    dynTpDelta   = lultSlDist * 2D
                                    dynStop = If(isLultBuy, bar.Close - dynStopDelta, bar.Close + dynStopDelta)
                                    dynTp   = If(isLultBuy, bar.Close + dynTpDelta,   bar.Close - dynTpDelta)
                                End If
                            End If
                        End If
                    End If
                    Continue For
                End If

                ' ── BB Squeeze Scalper signal ──────────────────────────────────────
                ' Dual-mode: Mode A (Squeeze Breakout) or Mode B (Band Bounce).
                ' Mode A: BBW < SMA(BBW,20) for ≥3 consecutive bars + close breaks
                '         outer band ×1.0025 + EMA5 slope confirms + RSI7 confirms.
                ' Mode B: %B ≤ -0.1 or ≥ 1.1, RSI7 extreme, rejection wick ≥60%.
                If openLegs.Count = 0 AndAlso
                   config.StrategyCondition = StrategyConditionType.BbSqueezeScalper Then
                    If bbsBands.Upper IsNot Nothing AndAlso bbsBbwArr IsNot Nothing Then
                        Dim bUpper = bbsBands.Upper(i)
                        Dim bLower = bbsBands.Lower(i)
                        Dim bPctB = bbsPctBArr(i)
                        Dim bRsi7 = bbsRsi7(i)
                        Dim bEma5Now = bbsEma5(i)
                        Dim bEma5Prev = If(i > 0, bbsEma5(i - 1), Single.NaN)
                        Dim bBbw = bbsBbwArr(i)
                        Dim bBbwSma = bbsBbwSma(i)
                        If Not (Single.IsNaN(bUpper) OrElse Single.IsNaN(bLower) OrElse Single.IsNaN(bPctB) OrElse
                                Single.IsNaN(bRsi7) OrElse Single.IsNaN(bEma5Now) OrElse Single.IsNaN(bEma5Prev) OrElse
                                Single.IsNaN(bBbw) OrElse Single.IsNaN(bBbwSma)) Then
                            ' Count consecutive squeeze bars (BBW < SMA(BBW))
                            Dim sqzCount As Integer = 0
                            For si = i To Math.Max(0, i - 9) Step -1
                                If si < bbsBbwArr.Length AndAlso si < bbsBbwSma.Length AndAlso
                                   Not Single.IsNaN(bbsBbwArr(si)) AndAlso Not Single.IsNaN(bbsBbwSma(si)) AndAlso
                                   bbsBbwSma(si) > 0 AndAlso bbsBbwArr(si) < bbsBbwSma(si) Then
                                    sqzCount += 1
                                Else
                                    Exit For
                                End If
                            Next
                            Dim sqzActive = sqzCount >= 3
                            Dim bbSide As String = Nothing
                            Dim bbEma5Rising = (bEma5Now > bEma5Prev)
                            If sqzActive Then
                                ' Mode A: Squeeze Breakout
                                If bar.Close > SafeD(bUpper) * 1.0025D AndAlso bbEma5Rising AndAlso bRsi7 >= 60.0F Then
                                    bbSide = "Buy"
                                ElseIf bar.Close < SafeD(bLower) * 0.9975D AndAlso Not bbEma5Rising AndAlso bRsi7 <= 40.0F Then
                                    bbSide = "Sell"
                                End If
                            Else
                                ' Mode B: Band Bounce (mean-reversion)
                                Dim bbBarRange = bar.High - bar.Low
                                If bbBarRange > 0D Then
                                    Dim bbLwPct = CDbl(Math.Min(bar.Open, bar.Close) - bar.Low) / CDbl(bbBarRange)
                                    Dim bbUwPct = CDbl(bar.High - Math.Max(bar.Open, bar.Close)) / CDbl(bbBarRange)
                                    If bPctB <= -0.1F AndAlso bRsi7 < 25.0F AndAlso bbLwPct >= 0.6 Then
                                        bbSide = "Buy"
                                    ElseIf bPctB >= 1.1F AndAlso bRsi7 > 75.0F AndAlso bbUwPct >= 0.6 Then
                                        bbSide = "Sell"
                                    End If
                                End If
                            End If
                            If bbSide IsNot Nothing Then
                                Dim bbConf = If(sqzActive, 0.8F, 0.7F)
                                If bbConf < config.MinSignalConfidence Then bbSide = Nothing
                            End If
                            If bbSide IsNot Nothing Then
                                Dim bbConf2 = If(sqzActive, 0.8F, 0.7F)
                                positionGroupCounter += 1
                                openLegs.Add(New BacktestTrade With {
                                    .PositionGroupId = positionGroupCounter,
                                    .EntryTime = bar.Timestamp,
                                    .EntryPrice = bar.Close,
                                    .Side = bbSide,
                                    .Quantity = config.Quantity,
                                    .SignalConfidence = bbConf2
                                })
                                If dynEnabled Then
                                    Dim atrBbs As Single = If(bbsAtr10 IsNot Nothing AndAlso i < bbsAtr10.Length AndAlso
                                                               Not Single.IsNaN(bbsAtr10(i)), bbsAtr10(i), 0.0F)
                                    If config.UseAtrMode AndAlso atrBbs > 0.0F Then
                                        dynStopDelta = SafeD(atrBbs) * config.SlAtrMultiple
                                        dynTpDelta   = SafeD(atrBbs) * config.TpAtrMultiple
                                    Else
                                        Dim dppBbs = config.PointValue * config.Quantity
                                        If dppBbs > 0D Then
                                            dynStopDelta = Math.Round(config.InitialSlAmount / dppBbs, 4)
                                            dynTpDelta   = Math.Round(config.InitialTpAmount / dppBbs, 4)
                                        End If
                                    End If
                                    If dynStopDelta > 0D Then
                                        Dim isBbsBuy = (bbSide = "Buy")
                                        dynStop = If(isBbsBuy, bar.Close - dynStopDelta, bar.Close + dynStopDelta)
                                        dynTp   = If(isBbsBuy, bar.Close + dynTpDelta,   bar.Close - dynTpDelta)
                                    End If
                                End If
                            End If
                        End If
                    End If
                    Continue For
                End If

                ' ── EMA/RSI weighted signal — same algorithm as StrategyExecutionEngine ──
                ' Score is computed on every bar (open or flat): used for neutral-confidence
                ' exit when a position is open, and for entry evaluation when flat.
                ' Guarded to avoid running for TripleEmaCascade or MultiConfluence bars.
                If config.StrategyCondition <> StrategyConditionType.TripleEmaCascade AndAlso
                   config.StrategyCondition <> StrategyConditionType.MultiConfluence AndAlso
                   config.StrategyCondition <> StrategyConditionType.ConnorsRsi2 AndAlso
                   config.StrategyCondition <> StrategyConditionType.SuperTrend AndAlso
                   config.StrategyCondition <> StrategyConditionType.DonchianBreakout AndAlso
                   config.StrategyCondition <> StrategyConditionType.BbRsiMeanReversion AndAlso
                   config.StrategyCondition <> StrategyConditionType.VidyaCross AndAlso
                   config.StrategyCondition <> StrategyConditionType.NakedTrader AndAlso
                   config.StrategyCondition <> StrategyConditionType.LultDivergence AndAlso
                   config.StrategyCondition <> StrategyConditionType.BbSqueezeScalper AndAlso
                   config.StrategyCondition <> StrategyConditionType.DoubleBubbleButt Then
                    Dim ema21Now = ema21Series(i)
                    Dim ema21Prev = ema21Series(i - 1)
                    Dim ema50Now = ema50Series(i)
                    Dim rsiVal = rsi14Series(i)

                    ' Skip bar if any indicator hasn't finished warming up yet
                    If Single.IsNaN(ema21Now) OrElse Single.IsNaN(ema21Prev) OrElse
                       Single.IsNaN(ema50Now) OrElse Single.IsNaN(rsiVal) Then Continue For

                    Dim lastClose = bar.Close
                    Dim bullScore As Double = 0

                    ' 1. EMA21 vs EMA50 crossover — 25 pts (mirrors live: requires ≥0.05% separation)
                    If ema21Now > ema50Now * 1.0005 Then bullScore += 25

                    ' 2. Close vs EMA21 — 20 pts
                    If lastClose > SafeD(ema21Now) Then bullScore += 20

                    ' 3. Close vs EMA50 — 15 pts
                    If lastClose > SafeD(ema50Now) Then bullScore += 15

                    ' 4. RSI trending zone — 20 pts
                    ' Mirrors live StrategyExecutionEngine: awards 20 pts when RSI is in the
                    ' 55–70 range (tightened from 50–70; 50–54 is non-directional noise).
                    If rsiVal >= 55 AndAlso rsiVal < 70 Then bullScore += 20
                    bullScore = Math.Max(0.0, Math.Min(100.0, bullScore))  ' clamp after RSI contribution

                    ' 5. EMA21 momentum (rising since prior bar) — 10 pts
                    If ema21Now > ema21Prev Then bullScore += 10

                    ' 6. Recent 3 candles: ≥ 2 bullish — 10 pts
                    Dim bullCandles As Integer = 0
                    For c = i - 2 To i
                        If filteredBars(c).IsBullish Then bullCandles += 1
                    Next
                    If bullCandles >= 2 Then bullScore += 10

                    Dim downPct As Double = 100.0 - bullScore

                    ' ── Neutral confidence exit ───────────────────────────────────────────
                    ' Mirrors live EvaluateConfidenceActionsAsync: when the score falls into
                    ' the 40–60% neutral band, close all open legs at bar close.
                    ' TP/SL intrabar price-level fills are handled first (above); this exit
                    ' applies only when neither level was touched this bar.
                    If openLegs.Count > 0 AndAlso
                       bullScore >= 40.0 AndAlso bullScore <= 60.0 Then
                        Dim neutralPositionPnL = 0D
                        For Each leg In openLegs
                            leg.ExitTime = bar.Timestamp
                            leg.ExitPrice = bar.Close
                            leg.ExitReason = "NeutralExit"
                            Dim pnl = BacktestMetrics.CalculatePnL(leg, config)
                            leg.PnL = pnl
                            neutralPositionPnL += pnl
                            trades.Add(leg)
                        Next
                        capital += neutralPositionPnL
                        If capital > peakCapital Then peakCapital = capital
                        Dim neutralDd = peakCapital - capital
                        If neutralDd > maxDrawdown Then maxDrawdown = neutralDd
                        openLegs.Clear()
                        dynStop      = 0D
                        dynTp        = 0D
                        dynStopDelta = 0D
                        dynTpDelta   = 0D
                    End If

                    ' ── Entry signal — initial entry when flat, scale-in when same direction ──
                    ' Initial entry: fires when no position is open and signal meets threshold.
                    ' Scale-in:      fires when one leg is already open in the same direction;
                    '                capped at one additional entry (two legs max per position).
                    ' ── ADX trend-strength gate (configurable) ───────────────────────
                    ' config.MinAdxThreshold = 0  → gate disabled, all bars evaluated.
                    ' config.MinAdxThreshold = 25 → matches live StrategyExecutionEngine.
                    Dim adxVal = adx14Series(i)
                    Dim adxGate = config.MinAdxThreshold <= 0.0F OrElse
                                  (Not Single.IsNaN(adxVal) AndAlso adxVal >= config.MinAdxThreshold)

                    Dim tradeableSide As String = Nothing
                    Dim sigConf As Single = 0
                    If adxGate Then
                        If bullScore >= minPct Then
                            tradeableSide = "Buy"
                            sigConf = CSng(bullScore) / 100.0F
                        ElseIf downPct >= minPct Then
                            tradeableSide = "Sell"
                            sigConf = CSng(downPct) / 100.0F
                        End If
                    End If

                    If tradeableSide IsNot Nothing Then
                        If openLegs.Count = 0 Then
                            positionGroupCounter += 1
                            openLegs.Add(New BacktestTrade With {
                                .PositionGroupId = positionGroupCounter,
                                .EntryTime = bar.Timestamp,
                                .EntryPrice = bar.Close,
                                .Side = tradeableSide,
                                .Quantity = config.Quantity,
                                .SignalConfidence = sigConf
                            })
                            ' Initialise dynamic exit levels — ATR mode or fixed dollar mode
                            If dynEnabled Then
                                Dim atrEma As Single = If(universalAtr14 IsNot Nothing AndAlso i < universalAtr14.Length AndAlso
                                                           Not Single.IsNaN(universalAtr14(i)), universalAtr14(i), 0.0F)
                                If config.UseAtrMode AndAlso atrEma > 0.0F Then
                                    dynStopDelta = SafeD(atrEma) * config.SlAtrMultiple
                                    dynTpDelta   = SafeD(atrEma) * config.TpAtrMultiple
                                Else
                                    Dim dpp = config.PointValue * config.Quantity
                                    If dpp > 0D Then
                                        dynStopDelta = Math.Round(config.InitialSlAmount / dpp, 4)
                                        dynTpDelta   = Math.Round(config.InitialTpAmount / dpp, 4)
                                    End If
                                End If
                                If dynStopDelta > 0D Then
                                    Dim isBuyEntry = (tradeableSide = "Buy")
                                    dynStop = If(isBuyEntry, bar.Close - dynStopDelta, bar.Close + dynStopDelta)
                                    dynTp   = If(isBuyEntry, bar.Close + dynTpDelta,   bar.Close - dynTpDelta)
                                End If
                            End If
                        ElseIf openLegs.Count < config.MaxScaleIns AndAlso openLegs(0).Side = tradeableSide Then
                            ' Scale-in: same direction, up to MaxScaleIns additional legs
                            openLegs.Add(New BacktestTrade With {
                                .PositionGroupId = openLegs(0).PositionGroupId,
                                .EntryTime = bar.Timestamp,
                                .EntryPrice = bar.Close,
                                .Side = tradeableSide,
                                .Quantity = config.Quantity,
                                .SignalConfidence = sigConf
                            })
                        End If
                    End If
                End If
            Next

            ' Close any open position at end of data
            If openLegs.Count > 0 Then
                Dim lastBar = filteredBars.Last()
                For Each leg In openLegs
                    leg.ExitTime = lastBar.Timestamp
                    leg.ExitPrice = lastBar.Close
                    leg.ExitReason = "EndOfData"
                    leg.PnL = BacktestMetrics.CalculatePnL(leg, config)
                    capital += leg.PnL.GetValueOrDefault()
                    trades.Add(leg)
                Next
            End If

            ' Calculate metrics
            Dim result = BacktestMetrics.BuildResult(config, trades, capital, maxDrawdown)

            ' Persist to database
            Try
                Await PersistResultAsync(result, filteredBars.Count, config.Timeframe)
            Catch ex As Exception
                _logger.LogError(ex, "Failed to persist backtest result")
            End Try

            _logger.LogInformation(
                "Backtest complete: {Trades} trades, PnL={PnL:C}, WinRate={WR:P1}",
                result.TotalTrades, result.TotalPnL, result.WinRate)

            Return result
        End Function

        Public Async Function GetBacktestRunsAsync() As Task(Of IEnumerable(Of BacktestResult)) _
            Implements IBacktestService.GetBacktestRunsAsync
            Dim entities = Await _backtestRepository.GetRecentRunsAsync()
            Return entities.Select(Function(e) MapRunToResult(e))
        End Function

        ' ─── Helpers ────────────────────────────────────────────────────────────

        Private Async Function PersistResultAsync(result As BacktestResult, barCount As Integer, timeframe As Integer) As Task
            Dim entity = New BacktestRunEntity With {
                .RunName = result.RunName,
                .ContractId = result.ContractId,
                .Timeframe = timeframe,
                .StartDate = result.StartDate,
                .EndDate = result.EndDate,
                .InitialCapital = result.InitialCapital,
                .FinalCapital = result.FinalCapital,
                .TotalTrades = result.TotalTrades,
                .WinningTrades = result.WinningTrades,
                .LosingTrades = result.LosingTrades,
                .TotalPnL = result.TotalPnL,
                .MaxDrawdown = result.MaxDrawdown,
                .WinRate = result.WinRate,
                .SharpeRatio = result.SharpeRatio,
                .AveragePnLPerTrade = result.AveragePnLPerTrade,
                .ModelVersion = "EMA/RSI-Rule-Based",
                .CompletedAt = DateTimeOffset.UtcNow,
                .Trades = result.Trades.Select(Function(t) New BacktestTradeEntity With {
                    .EntryTime = t.EntryTime,
                    .ExitTime = t.ExitTime,
                    .Side = t.Side,
                    .EntryPrice = t.EntryPrice,
                    .ExitPrice = t.ExitPrice,
                    .Quantity = t.Quantity,
                    .PnL = t.PnL,
                    .ExitReason = t.ExitReason,
                    .SignalConfidence = t.SignalConfidence,
                    .PositionGroupId = t.PositionGroupId
                }).ToList()
            }
            result.Id = Await _backtestRepository.SaveRunAsync(entity)
        End Function

        Private Shared Function MapRunToResult(e As BacktestRunEntity) As BacktestResult
            Return New BacktestResult With {
                .Id = e.Id,
                .RunName = e.RunName,
                .ContractId = e.ContractId,
                .StartDate = e.StartDate,
                .EndDate = e.EndDate,
                .InitialCapital = e.InitialCapital,
                .FinalCapital = e.FinalCapital,
                .TotalTrades = e.TotalTrades,
                .WinningTrades = e.WinningTrades,
                .LosingTrades = e.LosingTrades,
                .TotalPnL = e.TotalPnL,
                .MaxDrawdown = e.MaxDrawdown,
                .WinRate = e.WinRate.GetValueOrDefault(),
                .SharpeRatio = e.SharpeRatio,
                .AveragePnLPerTrade = e.AveragePnLPerTrade
            }
        End Function

            ''' <summary>
            ''' Safe CDec for Single values: returns 0D when the value is NaN or Infinity,
            ''' preventing OverflowException from CDec(Single.PositiveInfinity).
            ''' </summary>
            Private Shared Function SafeD(v As Single) As Decimal
                Return If(Single.IsNaN(v) OrElse Single.IsInfinity(v), 0D, CDec(v))
            End Function

        End Class

    End Namespace
