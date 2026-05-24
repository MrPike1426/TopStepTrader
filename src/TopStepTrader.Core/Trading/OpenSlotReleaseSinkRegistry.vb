Imports System.Collections.Concurrent
Imports TopStepTrader.Core.Interfaces

Namespace TopStepTrader.Core.Trading

    ''' <summary>
    ''' BUG-90 F1: process-wide singleton registry that decouples the singleton
    ''' <c>BrokerSlotSweepWorker</c> from the transient <c>SuperTrendPlusViewModel</c>
    ''' (which owns the live slots). The view-model registers itself when monitoring
    ''' starts and deregisters on stop / dispose; the worker iterates the registered
    ''' sinks on its 60 s cadence.
    ''' </summary>
    Public Class OpenSlotReleaseSinkRegistry

        Private ReadOnly _sinks As New ConcurrentDictionary(Of IOpenSlotReleaseSink, Byte)()

        ''' <summary>Adds <paramref name="sink"/>; safe to call repeatedly.</summary>
        Public Sub Register(sink As IOpenSlotReleaseSink)
            If sink Is Nothing Then Return
            _sinks.TryAdd(sink, 0)
        End Sub

        ''' <summary>Removes <paramref name="sink"/>; safe to call when not registered.</summary>
        Public Sub Unregister(sink As IOpenSlotReleaseSink)
            If sink Is Nothing Then Return
            Dim ignored As Byte
            _sinks.TryRemove(sink, ignored)
        End Sub

        ''' <summary>Snapshot of currently-registered sinks.</summary>
        Public Function Snapshot() As IReadOnlyList(Of IOpenSlotReleaseSink)
            Return _sinks.Keys.ToList()
        End Function

    End Class

End Namespace
