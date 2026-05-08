Imports System.ComponentModel
Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Interfaces

Namespace TopStepTrader.Services.PostMortem

    ''' <summary>
    ''' FEAT-51: Default <see cref="IPostMortemLauncher"/> implementation.
    ''' Spawns <c>python tools/postmortem/postmortem.py …</c> fire-and-forget,
    ''' awaits the process on a background thread, then resolves the resulting
    ''' markdown report path under <c>tools/postmortem/reports</c>.
    ''' </summary>
    Public Class PostMortemLauncher
        Implements IPostMortemLauncher

        Private ReadOnly _logger As ILogger(Of PostMortemLauncher)

        Public Sub New(logger As ILogger(Of PostMortemLauncher))
            _logger = logger
        End Sub

        Private Shared ReadOnly _slnRoot As New Lazy(Of String)(AddressOf ComputeSolutionRoot)

        Public Async Function RunAsync(orderId As Long,
                                       tradeId As Long?,
                                       symbol As String,
                                       entryTimeLocal As DateTimeOffset,
                                       issueText As String) As Task(Of PostMortemResult) _
            Implements IPostMortemLauncher.RunAsync

            Dim slnRoot = _slnRoot.Value
            Dim scriptPath = Path.Combine(slnRoot, "tools", "postmortem", "postmortem.py")
            Dim reportsDir = Path.Combine(slnRoot, "tools", "postmortem", "reports")

            Dim result As New PostMortemResult()
            If Not File.Exists(scriptPath) Then
                _logger.LogWarning("PostMortemLauncher: script not found at {Path}", scriptPath)
                result.Success = False
                result.StdErrTail = $"postmortem.py not found at {scriptPath}"
                Return result
            End If

            Dim args As New StringBuilder()
            args.Append("""").Append(scriptPath).Append(""" ")
            args.Append("--order-id ").Append(orderId).Append(" ")
            If tradeId.HasValue Then
                args.Append("--trade-id ").Append(tradeId.Value).Append(" ")
            End If
            args.Append("--symbol ").Append(QuoteArg(symbol)).Append(" ")
            args.Append("--entry-time ").Append(QuoteArg(entryTimeLocal.ToString("o"))).Append(" ")
            args.Append("--closed-by app ")
            args.Append("--issue ").Append(QuoteArg(If(issueText, String.Empty)))

            Dim proc As Process = Nothing
            Try
                Dim psi As New ProcessStartInfo("py", "-3 " & args.ToString()) With {
                    .UseShellExecute = False,
                    .RedirectStandardOutput = True,
                    .RedirectStandardError = True,
                    .CreateNoWindow = True,
                    .WorkingDirectory = slnRoot
                }
                proc = Process.Start(psi)
            Catch ex As Win32Exception
                Try
                    Dim psi As New ProcessStartInfo("python", args.ToString()) With {
                        .UseShellExecute = False,
                        .RedirectStandardOutput = True,
                        .RedirectStandardError = True,
                        .CreateNoWindow = True,
                        .WorkingDirectory = slnRoot
                    }
                    proc = Process.Start(psi)
                Catch ex2 As Exception
                    _logger.LogWarning(ex2, "PostMortemLauncher: failed to start python")
                    result.Success = False
                    result.StdErrTail = ex2.Message
                    Return result
                End Try
            Catch ex As Exception
                _logger.LogWarning(ex, "PostMortemLauncher: failed to start python")
                result.Success = False
                result.StdErrTail = ex.Message
                Return result
            End Try
            If proc Is Nothing Then
                result.Success = False
                result.StdErrTail = "Process.Start returned Nothing"
                Return result
            End If

            Using proc
                ' Capture stdout/stderr so the buffers don't deadlock the python process.
                Dim stdoutTask = proc.StandardOutput.ReadToEndAsync()
                Dim stderrTask = proc.StandardError.ReadToEndAsync()
                Await proc.WaitForExitAsync().ConfigureAwait(False)
                Await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(False)

                result.ExitCode = proc.ExitCode
                Dim stderr = stderrTask.Result
                result.StdErrTail = TailLines(stderr, 6)

                If proc.ExitCode <> 0 Then
                    _logger.LogWarning("PostMortem exit {Code}: {Err}", proc.ExitCode, result.StdErrTail)
                    result.Success = False
                    Return result
                End If
            End Using

            result.ReportPath = ResolveReportPath(reportsDir, symbol, orderId, entryTimeLocal)
            result.Success = Not String.IsNullOrEmpty(result.ReportPath)
            Return result
        End Function

        Private Shared Function QuoteArg(value As String) As String
            If String.IsNullOrEmpty(value) Then Return """"""
            Return """" & value.Replace("""", "\""") & """"
        End Function

        Private Shared Function TailLines(text As String, maxLines As Integer) As String
            If String.IsNullOrEmpty(text) Then Return String.Empty
            Dim lines = text.Split({Environment.NewLine, vbLf}, StringSplitOptions.RemoveEmptyEntries)
            If lines.Length <= maxLines Then Return text.Trim()
            Return String.Join(Environment.NewLine, lines.Skip(lines.Length - maxLines)).Trim()
        End Function

        Private Shared Function ComputeSolutionRoot() As String
            Dim dir As New DirectoryInfo(AppContext.BaseDirectory)
            While dir IsNot Nothing
                If dir.GetFiles("*.sln").Any() Then Return dir.FullName
                dir = dir.Parent
            End While
            Return AppContext.BaseDirectory
        End Function

        Private Shared Function ResolveReportPath(reportsDir As String,
                                                  symbol As String,
                                                  orderId As Long,
                                                  entryTimeLocal As DateTimeOffset) As String
            If Not Directory.Exists(reportsDir) Then Return Nothing

            Dim cleanSymbol = If(symbol, String.Empty).TrimStart("/"c).ToUpperInvariant()
            Dim datePart = entryTimeLocal.ToString("yyyyMMdd")
            Dim expected = Path.Combine(reportsDir, $"{cleanSymbol}_{datePart}_{orderId}.md")
            If File.Exists(expected) Then Return expected

            ' Fall back: newest file that mentions the order id.
            Dim match = New DirectoryInfo(reportsDir).GetFiles("*.md") _
                .Where(Function(f) f.Name.Contains(orderId.ToString())) _
                .OrderByDescending(Function(f) f.LastWriteTimeUtc) _
                .FirstOrDefault()
            If match IsNot Nothing Then Return match.FullName

            ' Final fall back: newest report overall.
            Dim newest = New DirectoryInfo(reportsDir).GetFiles("*.md") _
                .OrderByDescending(Function(f) f.LastWriteTimeUtc) _
                .FirstOrDefault()
            Return If(newest Is Nothing, Nothing, newest.FullName)
        End Function

    End Class

End Namespace
