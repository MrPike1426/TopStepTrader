Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' FEAT-51: Wraps the python post-mortem script invocation so it can be mocked.
    ''' Implementations must be fire-and-forget and never block the WPF UI thread.
    ''' </summary>
    Public Interface IPostMortemLauncher

        ''' <summary>
        ''' Runs <c>tools/postmortem/postmortem.py</c> for the supplied trade.
        ''' Returns the absolute path of the generated report (or <c>Nothing</c> on failure)
        ''' along with the captured stderr tail for status display.
        ''' </summary>
        Function RunAsync(orderId As Long,
                          tradeId As Long?,
                          symbol As String,
                          entryTimeLocal As DateTimeOffset,
                          issueText As String) As Task(Of PostMortemResult)

    End Interface

    Public Class PostMortemResult
        Public Property Success As Boolean
        Public Property ReportPath As String
        Public Property StdErrTail As String
        Public Property ExitCode As Integer
    End Class

End Namespace
