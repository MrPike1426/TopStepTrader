Imports System.Collections.Concurrent

Namespace TopStepTrader.Core.Logging

    Public NotInheritable Class DebugLog

        Private Shared ReadOnly _queue As New ConcurrentQueue(Of String)()
        Private Const MaxEntries As Integer = 200

        Public Shared Event MessageLogged As Action(Of String)

        Public Shared Sub Log(message As String)
            Try
                Dim entry = $"[{DateTimeOffset.Now:HH:mm:ss}] {message}"
                _queue.Enqueue(entry)

                ' Trim queue if too large
                Dim tmp As String = Nothing
                While _queue.Count > MaxEntries
                    _queue.TryDequeue(tmp)
                End While

                RaiseEvent MessageLogged(entry)
            Catch
                ' Swallow logging failures — logging must not throw
            End Try
        End Sub

        Public Shared Function GetRecent() As String()
            Return _queue.ToArray()
        End Function

    End Class

End Namespace
