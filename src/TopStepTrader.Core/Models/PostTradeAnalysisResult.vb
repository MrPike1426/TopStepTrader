Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' Result returned by IClaudeReviewService.PostTradeAnalysisAsync.
    ''' Contains AI-generated post-mortem analysis for a closed trade.
    ''' </summary>
    Public Class PostTradeAnalysisResult
        Public Property Analysis As String = String.Empty
        Public Property Succeeded As Boolean = True
        Public Property ErrorMessage As String = String.Empty

        Public Shared Function Failure(message As String) As PostTradeAnalysisResult
            Return New PostTradeAnalysisResult With {
                .Succeeded = False,
                .ErrorMessage = message
            }
        End Function
    End Class

End Namespace
