Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' Evaluates one bar and returns a trade signal (or Nothing if no signal fires).
    ''' Implementations are stateless — all state lives in BacktestEngine.
    ''' </summary>
    Public Interface IStrategySignalProvider

        ''' <summary>
        ''' Evaluate a single bar for entry or continuation signals.
        ''' </summary>
        ''' <param name="bar">The current bar being evaluated.</param>
        ''' <param name="indicators">Pre-calculated indicator series for all bars.</param>
        ''' <param name="config">Backtest configuration (ADX gate, ATR multiples, etc.).</param>
        ''' <param name="barIndex">Zero-based index into the indicator arrays for the current bar.</param>
        ''' <returns>A populated <see cref="SignalResult"/> when a signal fires; Nothing otherwise.</returns>
        Function Evaluate(bar As MarketBar,
                          indicators As StrategyIndicators,
                          config As BacktestConfiguration,
                          barIndex As Integer) As SignalResult

    End Interface

End Namespace
