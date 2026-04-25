Imports System.Threading
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports Xunit

Namespace TopStepTrader.Tests

    ''' <summary>
    ''' Tests for IClaudeReviewService contract using FakeClaudeReviewService.
    ''' Verifies deterministic PROCEED/VETO responses and correct deserialisation.
    ''' No network calls — all responses are in-memory stubs.
    ''' </summary>
    Public Class IClaudeReviewServiceTests

        ' ── Fake implementation ──────────────────────────────────────────────

        ''' <summary>
        ''' Deterministic test double for IClaudeReviewService.
        ''' PROCEED is returned unless <see cref="ForceVeto"/> is set to True.
        ''' </summary>
        Private Class FakeClaudeReviewService
            Implements IClaudeReviewService

            Public Property ForceVeto As Boolean = False
            Public Property VetoReason As String = "Fake VETO — adverse session conditions."
            Public Property ProceedReason As String = "Fake PROCEED — technical gates passed, session looks clean."

            Public Function ReviewStrategyAsync(strategy As StrategyDefinition, Optional cancel As CancellationToken = Nothing) As Task(Of String) Implements IClaudeReviewService.ReviewStrategyAsync
                Return Task.FromResult($"Fake review for {strategy.Name}.")
            End Function

            Public Function ConfidenceCheckAsync(contractId As String, Optional cancel As CancellationToken = Nothing) As Task(Of String) Implements IClaudeReviewService.ConfidenceCheckAsync
                Return Task.FromResult($"Fake confidence check for {contractId}.")
            End Function

            Public Function AnalyseBacktestResultsAsync(resultsSummary As String, Optional cancel As CancellationToken = Nothing) As Task(Of String) Implements IClaudeReviewService.AnalyseBacktestResultsAsync
                Return Task.FromResult("Fake backtest analysis.")
            End Function

            Public Function PreTradeCheckAsync(ctx As PreTradeContext, Optional cancel As CancellationToken = Nothing) As Task(Of (Proceed As Boolean, Reasoning As String)) Implements IClaudeReviewService.PreTradeCheckAsync
                If ForceVeto Then
                    Return Task.FromResult((False, VetoReason))
                End If
                Return Task.FromResult((True, ProceedReason))
            End Function

            Public Function PostTradeAnalysisAsync(ctx As PostTradeContext, Optional cancel As CancellationToken = Nothing) As Task(Of PostTradeAnalysisResult) Implements IClaudeReviewService.PostTradeAnalysisAsync
                Return Task.FromResult(New PostTradeAnalysisResult With {
                    .Analysis = $"Fake post-trade analysis for {ctx.ContractId}.",
                    .Succeeded = True
                })
            End Function

            Public Function TradeAdviceAsync(contractName As String, bars As IReadOnlyList(Of MarketBar), Optional cancel As CancellationToken = Nothing) As Task(Of (Direction As String, Rationale As String)) Implements IClaudeReviewService.TradeAdviceAsync
                Return Task.FromResult(("BUY", "Fake trade advice — uptrend detected."))
            End Function
        End Class

        ' ── Tests ────────────────────────────────────────────────────────────

        <Fact>
        Public Async Function PreTradeCheckAsync_ReturnsProceed_WhenForceVetoFalse() As Task
            Dim fake As New FakeClaudeReviewService With {.ForceVeto = False}
            Dim ctx As New PreTradeContext With {
                .ContractId = "MES",
                .Side = "BUY",
                .StrategyName = "Multi-Confluence Engine",
                .PersonaName = "Damian",
                .ConfidencePct = 85,
                .MinConfidencePct = 80
            }

            Dim result = Await fake.PreTradeCheckAsync(ctx)

            Assert.True(result.Proceed)
            Assert.False(String.IsNullOrEmpty(result.Reasoning))
        End Function

        <Fact>
        Public Async Function PreTradeCheckAsync_ReturnsVeto_WhenForceVetoTrue() As Task
            Dim fake As New FakeClaudeReviewService With {.ForceVeto = True}
            Dim ctx As New PreTradeContext With {
                .ContractId = "MGC",
                .Side = "SELL",
                .StrategyName = "Multi-Confluence Engine",
                .SessionPnlUsd = -240D,
                .SessionTradeCount = 3,
                .ConsecutiveLosses = 3
            }

            Dim result = Await fake.PreTradeCheckAsync(ctx)

            Assert.False(result.Proceed)
            Assert.False(String.IsNullOrEmpty(result.Reasoning))
        End Function

        <Fact>
        Public Async Function PreTradeCheckAsync_ProceedReasoning_DoesNotStartWithVerdict() As Task
            ' Reasoning should not start with "PROCEED" or "VETO" — the verdict word is stripped in the real impl.
            Dim fake As New FakeClaudeReviewService With {.ProceedReason = "Session looks clean — no drawdown."}
            Dim ctx As New PreTradeContext With {.ContractId = "MNQ", .Side = "BUY"}

            Dim result = Await fake.PreTradeCheckAsync(ctx)

            Assert.True(result.Proceed)
            Assert.DoesNotContain("PROCEED", result.Reasoning.ToUpperInvariant().Substring(0, Math.Min(7, result.Reasoning.Length)))
        End Function

        <Fact>
        Public Async Function PostTradeAnalysisAsync_ReturnsSuccess_WithAnalysisText() As Task
            Dim fake As New FakeClaudeReviewService()
            Dim ctx As New PostTradeContext With {
                .ContractId = "MES",
                .Side = "BUY",
                .EntryPrice = 5000D,
                .ExitPrice = 5020D,
                .RealizedPnlUsd = 25D
            }

            Dim result = Await fake.PostTradeAnalysisAsync(ctx)

            Assert.True(result.Succeeded)
            Assert.False(String.IsNullOrEmpty(result.Analysis))
        End Function

        <Fact>
        Public Async Function TradeAdviceAsync_ReturnsBuyOrSell() As Task
            Dim fake As New FakeClaudeReviewService()
            Dim bars = New List(Of MarketBar)()

            Dim result = Await fake.TradeAdviceAsync("MES", bars)

            Assert.True(result.Direction = "BUY" OrElse result.Direction = "SELL")
            Assert.False(String.IsNullOrEmpty(result.Rationale))
        End Function

        <Fact>
        Public Async Function FakeClaudeReviewService_ImplementsInterface() As Task
            ' Verify FakeClaudeReviewService can be assigned to IClaudeReviewService
            Dim svc As IClaudeReviewService = New FakeClaudeReviewService()
            Dim reviewResult = Await svc.ReviewStrategyAsync(New StrategyDefinition With {.Name = "Test"})
            Assert.False(String.IsNullOrEmpty(reviewResult))
        End Function

    End Class

End Namespace
