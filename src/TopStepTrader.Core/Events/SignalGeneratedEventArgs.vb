Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Core.Events

    Public Class SignalGeneratedEventArgs
        Inherits EventArgs
        Public ReadOnly Property Signal As TradeSignal
        Public Sub New(signal As TradeSignal)
            Me.Signal = signal
        End Sub
    End Class

    Public Class QuoteEventArgs
        Inherits EventArgs
        Public ReadOnly Property Quote As Quote
        Public Sub New(quote As Quote)
            Me.Quote = quote
        End Sub
    End Class

    Public Class BarEventArgs
        Inherits EventArgs
        Public ReadOnly Property Bar As MarketBar
        Public Sub New(bar As MarketBar)
            Me.Bar = bar
        End Sub
    End Class

    Public Class OrderFilledEventArgs
        Inherits EventArgs
        Public ReadOnly Property Order As Order
        Public Sub New(order As Order)
            Me.Order = order
        End Sub
    End Class

    Public Class OrderRejectedEventArgs
        Inherits EventArgs
        Public ReadOnly Property Order As Order
        Public ReadOnly Property Reason As String
        Public Sub New(order As Order, reason As String)
            Me.Order = order
            Me.Reason = reason
        End Sub
    End Class

    Public Class RiskHaltEventArgs
        Inherits EventArgs
        Public ReadOnly Property Reason As RiskHaltReason
        Public ReadOnly Property DailyPnL As Decimal
        Public ReadOnly Property Drawdown As Decimal
        Public Sub New(reason As RiskHaltReason, dailyPnL As Decimal, drawdown As Decimal)
            Me.Reason = reason
            Me.DailyPnL = dailyPnL
            Me.Drawdown = drawdown
        End Sub
    End Class

    ''' <summary>Raised by StrategyExecutionEngine when a trade entry order is placed.</summary>
    Public Class TradeOpenedEventArgs
        Inherits EventArgs
        Public ReadOnly Property Side As Core.Enums.OrderSide
        Public ReadOnly Property ContractId As String
        Public ReadOnly Property ConfidencePct As Integer
        Public ReadOnly Property EntryTime As DateTimeOffset
        ''' <summary>eToro external order ID for the entry order. Nothing if placement failed.</summary>
        Public ReadOnly Property ExternalOrderId As Long?
        ''' <summary>eToro positionId resolved after the order fills. Nothing if not yet resolved.</summary>
        Public ReadOnly Property EtoroPositionId As Long?
        ''' <summary>UTC timestamp recorded when the position was opened by the engine.</summary>
        Public ReadOnly Property OpenedAtUtc As DateTimeOffset
        ''' <summary>Cash amount invested (after min-notional clamp).</summary>
        Public ReadOnly Property Amount As Decimal
        ''' <summary>Leverage applied to the order.</summary>
        Public ReadOnly Property Leverage As Integer
        ''' <summary>Entry price used for order computation (last bar close at signal time).</summary>
        Public ReadOnly Property EntryPrice As Decimal
        Public Sub New(side As Core.Enums.OrderSide, contractId As String, confidencePct As Integer,
                       entryTime As DateTimeOffset, Optional externalOrderId As Long? = Nothing,
                       Optional etoroPositionId As Long? = Nothing,
                       Optional openedAtUtc As DateTimeOffset = Nothing,
                       Optional amount As Decimal = 0D,
                       Optional leverage As Integer = 1,
                       Optional entryPrice As Decimal = 0D)
            Me.Side = side
            Me.ContractId = contractId
            Me.ConfidencePct = confidencePct
            Me.EntryTime = entryTime
            Me.ExternalOrderId = externalOrderId
            Me.EtoroPositionId = etoroPositionId
            Me.OpenedAtUtc = If(openedAtUtc = DateTimeOffset.MinValue, entryTime, openedAtUtc)
            Me.Amount = amount
            ' Leverage = 0 signals a futures position (tile shows "Xct" contract count).
            ' Leverage > 0 signals a leveraged CFD (tile shows "$X×Y").
            ' Only clamp negative values; 0 is a valid sentinel for futures.
            Me.Leverage = If(leverage >= 0, leverage, 1)
            Me.EntryPrice = entryPrice
        End Sub
    End Class

    ''' <summary>Raised by StrategyExecutionEngine when the bracket position closes (TP or SL filled).</summary>
    Public Class TradeClosedEventArgs
        Inherits EventArgs
        Public ReadOnly Property ExitReason As String   ' "TP", "SL", "Reversal", or "Closed"
        Public ReadOnly Property PnL As Decimal
        Public Sub New(exitReason As String, pnl As Decimal)
            Me.ExitReason = exitReason
            Me.PnL = pnl
        End Sub
    End Class

    ''' <summary>
    ''' Raised by StrategyExecutionEngine on every 30-second tick while a position is open.
    ''' Carries API-authoritative P&amp;L and the eToro positionId (which may have been resolved
    ''' after order placement if the broker API had a propagation delay).
    ''' </summary>
    Public Class PositionSyncedEventArgs
        Inherits EventArgs
        ''' <summary>eToro positionId confirmed by the portfolio API.</summary>
        Public ReadOnly Property PositionId As Long
        ''' <summary>Unrealised P&amp;L in USD as reported by the broker.</summary>
        Public ReadOnly Property UnrealizedPnlUsd As Decimal
        ''' <summary>UTC timestamp the position was opened, from the broker.</summary>
        Public ReadOnly Property OpenedAtUtc As DateTimeOffset
        ''' <summary>Total contract count (futures) or cash amount, from the broker snapshot.</summary>
        Public ReadOnly Property Amount As Decimal
        ''' <summary>True = long, False = short (from broker snapshot).</summary>
        Public ReadOnly Property IsBuy As Boolean
        ''' <summary>Number of open positions aggregated (for scale-in display).</summary>
        Public ReadOnly Property PositionCount As Integer
        Public Sub New(positionId As Long, unrealizedPnlUsd As Decimal, openedAtUtc As DateTimeOffset,
                       Optional amount As Decimal = 0D,
                       Optional isBuy As Boolean = True,
                       Optional positionCount As Integer = 1)
            Me.PositionId = positionId
            Me.UnrealizedPnlUsd = unrealizedPnlUsd
            Me.OpenedAtUtc = openedAtUtc
            Me.Amount = amount
            Me.IsBuy = isBuy
            Me.PositionCount = positionCount
        End Sub
    End Class

    Public Class BacktestProgressEventArgs
        Inherits EventArgs
        Public ReadOnly Property PercentComplete As Integer
        Public ReadOnly Property CurrentDate As Date
        Public ReadOnly Property TradesExecuted As Integer
        Public Sub New(pct As Integer, currentDate As Date, trades As Integer)
            PercentComplete = pct
            Me.CurrentDate = currentDate
            TradesExecuted = trades
        End Sub
    End Class

    ''' <summary>
    ''' Raised by StrategyExecutionEngine after every bar check with the live EMA/RSI score.
    ''' Fires on every 30-second tick regardless of whether a trade signal is generated,
    ''' giving the UI a continuous confidence telemetry feed.
    ''' </summary>
    Public Class ConfidenceUpdatedEventArgs
        Inherits EventArgs
        ''' <summary>Bull score 0–100 (percentage of max weighted score that is bullish).</summary>
        Public ReadOnly Property UpPct As Integer
        ''' <summary>Bear score = 100 - UpPct.</summary>
        Public ReadOnly Property DownPct As Integer
        ''' <summary>
        ''' True when the ADX trend-strength gate passed (ADX ≥ 25).
        ''' False when the raw score is high but the signal is suppressed because ADX &lt; 25
        ''' (ranging market). Always True for strategies that embed ADX as a confluence
        ''' condition (MultiConfluence), where no separate gate exists.
        ''' </summary>
        Public ReadOnly Property AdxGatePassed As Boolean
        ''' <summary>Actual ADX value at bar-check time. 0 when not applicable (e.g. MultiConfluence, LULT).</summary>
        Public ReadOnly Property AdxValue As Single
        ''' <summary>Last bar close price at the time the event was raised. 0 when not provided.</summary>
        Public ReadOnly Property LastClose As Decimal
        Public Sub New(upPct As Integer, downPct As Integer,
                       Optional adxGatePassed As Boolean = True,
                       Optional adxValue As Single = 0,
                       Optional lastClose As Decimal = 0D)
            Me.UpPct = upPct
            Me.DownPct = downPct
            Me.AdxGatePassed = adxGatePassed
            Me.AdxValue = adxValue
            Me.LastClose = lastClose
        End Sub

        ' ── Extended multi-confluence indicator snapshot ─────────────────────────
        ' Set via object initialiser after construction; default to 0 / "not available".
        Public Property Cloud1 As Decimal = 0D
        Public Property Cloud2 As Decimal = 0D
        Public Property Tenkan As Decimal = 0D
        Public Property Kijun As Decimal = 0D
        Public Property Ema21 As Decimal = 0D
        Public Property Ema50 As Decimal = 0D
        Public Property PlusDI As Single = 0F
        Public Property MinusDI As Single = 0F
        Public Property MacdHist As Single = 0F
        Public Property MacdHistPrev As Single = 0F
        Public Property StochRsiK As Single = 0F
        Public Property LongCount As Integer = 0
        Public Property ShortCount As Integer = 0
        ''' <summary>Total number of conditions evaluated (7 for MultiConfluence; 6 for EmaRsiCombined; 0 for other strategies).</summary>
        Public Property TotalConditions As Integer = 0

        ' ── EMA/RSI Combined extended snapshot ──────────────────────────────────
        ''' <summary>RSI value at bar-check time (EMA/RSI Combined; period = IndicatorPeriod, default 14). 0 when not applicable.</summary>
        Public Property Rsi14 As Single = 0F
        ''' <summary>Volume ratio (bar volume / 20-bar avg) for the EMA/RSI signal bar. 0 when volume absent.</summary>
        Public Property VolumeRatio As Single = 0F
        ''' <summary>True when EMA21 is higher than its previous-bar value (EMA/RSI Combined condition 5). False when not applicable.</summary>
        Public Property Ema21Rising As Boolean = False
        ''' <summary>True when the majority of the last 3 candles closed above their open (EMA/RSI Combined condition 6). False when not applicable.</summary>
        Public Property RecentCandlesBullish As Boolean = False
        ''' <summary>
        ''' ADX threshold used by the active risk profile (Lewis=25, Damian=20, Joe=15).
        ''' Default 20.0 (Damian). Used by the UI to display whether the trend gate is met
        ''' without hardcoding a threshold in the ViewModel.
        ''' </summary>
        Public Property AdxThreshold As Single = 20.0F

        ''' <summary>
        ''' Minimum confidence % required for a trade to fire (e.g. Lewis=90, Damian=80, Joe=70).
        ''' Carried on every event so the tile can show how close the current score is to the
        ''' entry bar — e.g. "need 90%" when sitting at 86%.  0 when not applicable.
        ''' </summary>
        Public Property MinConfidencePct As Integer = 0

        ''' <summary>
        ''' True when the engine detected a fresh→stale bar transition (market closed or
        ''' outside trading hours).  Fired exactly once per close event.  The UI should
        ''' dim the tile and show a "market closed" indicator rather than frozen values.
        ''' All other properties are 0 / default when this is True.
        ''' </summary>
        Public Property IsMarketClosed As Boolean = False

        ' ── VIDYA Cross extended snapshot (TotalConditions = -1) ────────────────
        ''' <summary>Current VIDYA line value. 0 when strategy is not VIDYA.</summary>
        Public Property VidyaValue As Decimal = 0D
        ''' <summary>CMO(9) momentum value at bar-check time. 0 when not applicable.</summary>
        Public Property CmoValue As Single = 0F
        ''' <summary>6-bar delta-volume fraction (e.g. 0.35 = +35%). 0 when not applicable.</summary>
        Public Property DeltaVol As Single = 0F
    End Class

    ''' <summary>
    ''' Raised by StrategyExecutionEngine when the Turtle bracket is first placed or advances a step.
    ''' </summary>
    Public Class TurtleBracketChangedEventArgs
        Inherits EventArgs
        Public ReadOnly Property BracketNumber As Integer
        Public ReadOnly Property SlPrice As Decimal
        Public ReadOnly Property TpPrice As Decimal
        ''' <summary>
        ''' True when this event represents a bracket advance triggered by a price level being
        ''' hit (TP reached → SL steps up).  False for initial bracket placement on order entry
        ''' or bracket reattachment on engine restart.
        ''' The UI uses this flag to decide whether to display the "Turtle Applied" status
        ''' message — only a genuine advance warrants user-visible confirmation.
        ''' </summary>
        Public ReadOnly Property IsAdvance As Boolean
        ''' <summary>
        ''' True when the bracket SL has advanced into positive territory — i.e. the SL dollar
        ''' value (CurrentSlDollars) is &gt; 0, meaning the position is guaranteed to close at a
        ''' profit even if price reverses to the SL right now.
        ''' Derived from TurtleBracketState.CurrentSlDollars &gt; 0 in the engine.
        ''' The UI displays a "Free Ride" badge in Forest Green when this is True.
        ''' </summary>
        Public ReadOnly Property IsFreeRide As Boolean
        Public Sub New(bracketNumber As Integer, slPrice As Decimal, tpPrice As Decimal,
                       isAdvance As Boolean, isFreeRide As Boolean)
            Me.BracketNumber = bracketNumber
            Me.SlPrice = slPrice
            Me.TpPrice = tpPrice
            Me.IsAdvance = isAdvance
            Me.IsFreeRide = isFreeRide
        End Sub
    End Class

End Namespace
