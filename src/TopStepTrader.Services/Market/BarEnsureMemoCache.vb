Imports System.Collections.Concurrent
Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.Services.Market

    ''' <summary>
    ''' FEAT-68: In-memory "last ensured" map shared across every BarCollectionService scope.
    '''
    ''' <see cref="BarCollectionService"/> is registered Scoped (it depends on the Scoped
    ''' BarRepository → AppDbContext chain), so any state held directly on the service is
    ''' discarded between scans. This class is Singleton and owns the memoization table so the
    ''' "do I need to ensure?" decision survives the per-scan scope rebuild and the orchestrator's
    ''' 5-second tick loop does not hammer SQLite when bars are already known fresh.
    '''
    ''' Key format: contractId|timeframeLabel|fromDate ISO|toDate ISO (UTC-normalised by the caller).
    ''' </summary>
    Public Class BarEnsureMemoCache

        Private Shared ReadOnly _windowByTimeframe As New Dictionary(Of BarTimeframe, TimeSpan) From {
            {BarTimeframe.OneMinute, TimeSpan.FromSeconds(50)},
            {BarTimeframe.FiveMinute, TimeSpan.FromMinutes(4)},
            {BarTimeframe.FifteenMinute, TimeSpan.FromMinutes(14)},
            {BarTimeframe.OneHour, TimeSpan.FromMinutes(55)},
            {BarTimeframe.Daily, TimeSpan.FromHours(12)}
        }

        Private Shared ReadOnly _defaultWindow As TimeSpan = TimeSpan.FromMinutes(5)

        Private ReadOnly _lastEnsuredAt As New ConcurrentDictionary(Of String, DateTimeOffset)(StringComparer.Ordinal)
        Private ReadOnly _clock As Func(Of DateTimeOffset)

        Public Sub New()
            Me.New(Function() DateTimeOffset.UtcNow)
        End Sub

        ''' <summary>Test seam — inject a deterministic clock for unit tests.</summary>
        Friend Sub New(clock As Func(Of DateTimeOffset))
            _clock = If(clock, Function() DateTimeOffset.UtcNow)
        End Sub

        ''' <summary>Returns the freshness window for a timeframe; 5 min default for unmapped values.</summary>
        Public Shared Function WindowFor(timeframe As BarTimeframe) As TimeSpan
            Dim w As TimeSpan
            If _windowByTimeframe.TryGetValue(timeframe, w) Then Return w
            Return _defaultWindow
        End Function

        Public Shared Function BuildKey(contractId As String,
                                        timeframe As BarTimeframe,
                                        fromDateUtcMidnight As DateTimeOffset,
                                        toDateUtcMidnight As DateTimeOffset) As String
            Return contractId & "|" & timeframe.ToString() & "|" &
                   fromDateUtcMidnight.ToString("o") & "|" &
                   toDateUtcMidnight.ToString("o")
        End Function

        ''' <summary>True if a successful Ensure was recorded inside the freshness window.</summary>
        Public Function IsFresh(key As String, timeframe As BarTimeframe) As Boolean
            Dim at As DateTimeOffset
            If Not _lastEnsuredAt.TryGetValue(key, at) Then Return False
            Dim elapsed = _clock() - at
            Return elapsed < WindowFor(timeframe)
        End Function

        ''' <summary>Records a successful Ensure for the key. Failed calls must NOT call this.</summary>
        Public Sub Record(key As String)
            _lastEnsuredAt(key) = _clock()
        End Sub

        ''' <summary>
        ''' Clears all entries matching the (contractId, timeframe) prefix. Used by
        ''' StartupBarCheckService on app boot and by tests that need to force a re-fetch.
        ''' </summary>
        Public Sub Invalidate(contractId As String, timeframe As BarTimeframe)
            If String.IsNullOrEmpty(contractId) Then Return
            Dim prefix = contractId & "|" & timeframe.ToString() & "|"
            For Each k In _lastEnsuredAt.Keys.Where(Function(s) s.StartsWith(prefix, StringComparison.Ordinal)).ToList()
                Dim ignored As DateTimeOffset
                _lastEnsuredAt.TryRemove(k, ignored)
            Next
        End Sub

        Friend ReadOnly Property EntryCount As Integer
            Get
                Return _lastEnsuredAt.Count
            End Get
        End Property

    End Class

End Namespace
