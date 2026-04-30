Imports System.Threading
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' Abstraction over the Anthropic Claude API for strategy review, pre-trade checks,
    ''' and post-trade analysis. Implement this interface (or use FakeClaudeReviewService
    ''' in tests) to decouple ViewModels and the execution engine from the concrete HTTP client.
    ''' </summary>
    Public Interface IClaudeReviewService
        Function ReviewStrategyAsync(strategy As StrategyDefinition, Optional cancel As CancellationToken = Nothing) As Task(Of String)
        Function ConfidenceCheckAsync(contractId As String, Optional cancel As CancellationToken = Nothing) As Task(Of String)
        Function AnalyseBacktestResultsAsync(resultsSummary As String, Optional cancel As CancellationToken = Nothing) As Task(Of String)
        Function PreTradeCheckAsync(ctx As PreTradeContext, Optional cancel As CancellationToken = Nothing) As Task(Of (Proceed As Boolean, Reasoning As String))
        Function PostTradeAnalysisAsync(ctx As PostTradeContext, Optional cancel As CancellationToken = Nothing) As Task(Of PostTradeAnalysisResult)
        Function TradeAdviceAsync(contractName As String, bars As IReadOnlyList(Of MarketBar), Optional cancel As CancellationToken = Nothing) As Task(Of (Direction As String, Rationale As String))
        ''' <summary>
        ''' Mid-trade sense check for an open position. Sends bar history + live position data
        ''' and returns a traffic-light verdict (GREEN/AMBER/RED), explanation, and suggested action.
        ''' </summary>
        Function MidTradeCheckAsync(instrument As String, side As String, adxVal As Single, plusDi As Single, minusDi As Single,
                                    stopPhaseLabel As String, unrealizedPnl As Decimal,
                                    bars As IReadOnlyList(Of MarketBar),
                                    Optional cancel As CancellationToken = Nothing) As Task(Of (Verdict As String, Explanation As String, SuggestedAction As String))
    End Interface

End Namespace
