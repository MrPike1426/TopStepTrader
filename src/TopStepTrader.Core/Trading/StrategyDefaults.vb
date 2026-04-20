Namespace TopStepTrader.Core.Trading

    ''' <summary>
    ''' Canonical parameter defaults for each supported backtest/live-trade strategy.
    '''
    ''' Design rule (TICKET-006): only combined multi-indicator strategies are registered here.
    ''' Single-indicator strategies (pure RSI, pure EMA, Double Bottom, etc.) are excluded —
    ''' backtesting a single-indicator strategy does not produce reliable live trading signals.
    ''' </summary>
    Public NotInheritable Class StrategyDefaults

        Private Sub New()
        End Sub

        ''' <summary>
        ''' All registered strategies and their default parameters.
        ''' Key lookup is case-insensitive.
        ''' </summary>
        ' Capital (USD cash) and Quantity per entry. ATR-based SL/TP replaces dollar amounts.
        Public Shared ReadOnly Defaults As IReadOnlyDictionary(Of String, StrategyParameterSet) =
            New Dictionary(Of String, StrategyParameterSet)(StringComparer.OrdinalIgnoreCase) From {
                {"EMA/RSI Combined",       New StrategyParameterSet("1000", "1", "20", "15")},
                {"Multi-Confluence Engine", New StrategyParameterSet("1000", "1")},
                {"BB Squeeze Scalper",     New StrategyParameterSet("1000", "1")},
                {"LULT Divergence",        New StrategyParameterSet("1000", "1")},
                {"VIDYA Cross",            New StrategyParameterSet("1000", "1")},
                {"Naked Trader",           New StrategyParameterSet("1000", "1")},
                {"Double Bubble Butt",     New StrategyParameterSet("1000", "1")}
            }

        ''' <summary>
        ''' Look up the default parameters for <paramref name="strategyName"/>.
        ''' Returns Nothing when the strategy is not registered or the name is null/empty.
        ''' </summary>
        Public Shared Function TryGet(strategyName As String) As StrategyParameterSet
            If String.IsNullOrEmpty(strategyName) Then Return Nothing
            Dim result As StrategyParameterSet = Nothing
            Defaults.TryGetValue(strategyName, result)
            Return result
        End Function

    End Class

    ''' <summary>
    ''' Immutable set of capital/quantity defaults for a strategy.
    ''' Values are stored as strings to match the ViewModel's text-bound input fields.
    ''' SL and TP are ATR-based (set via SlAtrMultiple/TpAtrMultiple on BacktestConfiguration).
    ''' SlDollarBracket and TpDollarBracket are optional dollar-based overrides.
    ''' </summary>
    Public NotInheritable Class StrategyParameterSet

        Public ReadOnly Property Capital As String
        Public ReadOnly Property Qty As String
        Public ReadOnly Property TpDollarBracket As String
        Public ReadOnly Property SlDollarBracket As String

        Public Sub New(capital As String, qty As String,
                       Optional TpDollarBracket As String = Nothing,
                       Optional SlDollarBracket As String = Nothing)
            Me.Capital = capital
            Me.Qty = qty
            Me.TpDollarBracket = TpDollarBracket
            Me.SlDollarBracket = SlDollarBracket
        End Sub

    End Class

End Namespace
