Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' Carries everything BacktestEngine needs to fill a pending entry from a strategy signal.
    ''' Returned by <see cref="TopStepTrader.Core.Interfaces.IStrategySignalProvider.Evaluate"/>.
    ''' </summary>
    Public Class SignalResult

        ''' <summary>"Buy", "Sell", or Nothing = no signal.</summary>
        Public Property Side As String

        ''' <summary>Signal confidence in the range [0, 1].</summary>
        Public Property Confidence As Single

        ''' <summary>
        ''' ATR-relative price distance from entry to stop-loss.
        ''' 0 = not set for this strategy (use config dollar bracket instead).
        ''' </summary>
        Public Property StopDelta As Decimal

        ''' <summary>
        ''' ATR-relative price distance from entry to take-profit.
        ''' 0 = not set for this strategy (use config dollar bracket instead).
        ''' </summary>
        Public Property TpDelta As Decimal

        ''' <summary>
        ''' Absolute stop-loss price anchored to an indicator level (e.g. SuperTrend line).
        ''' 0 = not used for this strategy.
        ''' </summary>
        Public Property AbsoluteSlPrice As Decimal

        ''' <summary>
        ''' Absolute take-profit price anchored to an indicator level.
        ''' 0 = not used for this strategy.
        ''' </summary>
        Public Property AbsoluteTpPrice As Decimal

        ''' <summary>
        ''' Indicator-anchored exit level stored at signal time.
        ''' Used by DonchianBreakout (mid channel), BbRsiMeanReversion (BB middle),
        ''' and DoubleBubbleButt (inner 1-SD band).  0 = not used.
        ''' </summary>
        Public Property IndicatorExitLevel As Decimal

        ''' <summary>True when Side = "Buy".</summary>
        Public Property IsLong As Boolean

        ''' <summary>
        ''' True when the EMA/RSI score falls in the neutral 40–60 range.
        ''' BacktestEngine uses this to close an open position at bar.Close;
        ''' no entry signal fires when this is True.
        ''' </summary>
        Public Property NeutralExit As Boolean

        ''' <summary>
        ''' True when the signal has partial conviction (e.g. 8/9 for MultiConfluence).
        ''' BacktestEngine uses half of config.Quantity for the entry leg.
        ''' </summary>
        Public Property IsPartialSignal As Boolean

    End Class

End Namespace
