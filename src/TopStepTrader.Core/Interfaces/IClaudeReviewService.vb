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
    End Interface

End Namespace
