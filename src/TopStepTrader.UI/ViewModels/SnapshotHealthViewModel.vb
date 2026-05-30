Imports System.Collections.ObjectModel
Imports System.Threading
Imports System.Windows
Imports System.Windows.Threading
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Data.Repositories
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' OBS-07 F2: Snapshot Health tile for the Diagnostics tab. Shows the most recent
    ''' N trade IDs and a green/red dot per record indicating whether broker-side
    ''' snapshot rows exist for it. Polls the database every 30 s while the tab is
    ''' visible — not a hot path; the cadence keeps the SQLite IO load negligible.
    ''' </summary>
    Public Class SnapshotHealthViewModel
        Inherits ViewModelBase
        Implements IDisposable

        Friend Const RecentLimit As Integer = 10
        Friend Shared ReadOnly PollInterval As TimeSpan = TimeSpan.FromSeconds(30)

        Private ReadOnly _scopeFactory As IServiceScopeFactory
        Private ReadOnly _logger As ILogger(Of SnapshotHealthViewModel)
        Private ReadOnly _timer As DispatcherTimer
        Private _isRefreshing As Boolean
        Private _totalTrades As Integer
        Private _tradesWithSnapshots As Integer
        Private _tradesMissingSnapshots As Integer
        Private _lastRefreshUtc As DateTimeOffset
        Private _disposed As Boolean

        Public Sub New(scopeFactory As IServiceScopeFactory,
                       logger As ILogger(Of SnapshotHealthViewModel))
            _scopeFactory = scopeFactory
            _logger = logger
            _timer = New DispatcherTimer With {.Interval = PollInterval}
            AddHandler _timer.Tick, AddressOf OnTick
        End Sub

        Public ReadOnly Property Recent As New ObservableCollection(Of SnapshotHealthRow)()

        Public Property TotalTrades As Integer
            Get
                Return _totalTrades
            End Get
            Private Set(value As Integer)
                SetProperty(_totalTrades, value)
                OnPropertyChanged(NameOf(SummaryLine))
            End Set
        End Property

        Public Property TradesWithSnapshots As Integer
            Get
                Return _tradesWithSnapshots
            End Get
            Private Set(value As Integer)
                SetProperty(_tradesWithSnapshots, value)
                OnPropertyChanged(NameOf(SummaryLine))
            End Set
        End Property

        Public Property TradesMissingSnapshots As Integer
            Get
                Return _tradesMissingSnapshots
            End Get
            Private Set(value As Integer)
                SetProperty(_tradesMissingSnapshots, value)
                OnPropertyChanged(NameOf(SummaryLine))
            End Set
        End Property

        Public Property LastRefreshUtc As DateTimeOffset
            Get
                Return _lastRefreshUtc
            End Get
            Private Set(value As DateTimeOffset)
                SetProperty(_lastRefreshUtc, value)
                OnPropertyChanged(NameOf(LastRefreshDisplay))
            End Set
        End Property

        Public ReadOnly Property LastRefreshDisplay As String
            Get
                If _lastRefreshUtc = DateTimeOffset.MinValue Then Return "never"
                Return _lastRefreshUtc.ToLocalTime().ToString("HH:mm:ss")
            End Get
        End Property

        Public ReadOnly Property SummaryLine As String
            Get
                Return $"{TotalTrades} trades / {TradesWithSnapshots} with snapshots / {TradesMissingSnapshots} missing"
            End Get
        End Property

        ''' <summary>Start polling. Idempotent — calling twice is a no-op.</summary>
        Public Sub Start()
            If _timer.IsEnabled Then Return
            _timer.Start()
            ' Fire a first refresh immediately so the tile is not empty for 30 s.
            FireRefresh()
        End Sub

        ''' <summary>Stop polling. Idempotent — calling twice is a no-op.</summary>
        Public Sub [Stop]()
            If Not _timer.IsEnabled Then Return
            _timer.Stop()
        End Sub

        Private Sub OnTick(sender As Object, e As EventArgs)
            FireRefresh()
        End Sub

        Private Sub FireRefresh()
            If _isRefreshing Then Return
            _isRefreshing = True
#Disable Warning BC42358
            Task.Run(Async Function()
                         Try
                             Await RefreshAsync()
                         Catch ex As Exception
                             _logger.LogWarning(ex, "SnapshotHealth: refresh failed")
                         Finally
                             _isRefreshing = False
                         End Try
                     End Function)
#Enable Warning BC42358
        End Sub

        Friend Async Function RefreshAsync() As Task
            Dim rows As New List(Of SnapshotHealthRow)()
            Dim withSnap As Integer = 0
            Dim total As Integer = 0
            Using scope = _scopeFactory.CreateScope()
                Dim tradeRepo = scope.ServiceProvider.GetRequiredService(Of ILiveTradeRecordRepository)()
                Dim snapRepo = scope.ServiceProvider.GetRequiredService(Of ITradeSnapshotRepository)()
                Dim recent = Await tradeRepo.GetRecentAsync(RecentLimit, closedOnly:=True)
                total = If(recent IsNot Nothing, recent.Count, 0)
                If recent IsNot Nothing Then
                    For Each rec In recent
                        Dim hasAny As Boolean = False
                        Try
                            hasAny = Await snapRepo.HasAnySnapshotsAsync(rec.Id)
                        Catch ex As Exception
                            _logger.LogDebug(ex, "SnapshotHealth: HasAny failed for record {Id}", rec.Id)
                        End Try
                        If hasAny Then withSnap += 1
                        rows.Add(New SnapshotHealthRow With {
                            .TradeId = rec.Id,
                            .Symbol = rec.Symbol,
                            .ExitTime = rec.ExitTime,
                            .HasSnapshots = hasAny
                        })
                    Next
                End If
            End Using

            Dim disp = Application.Current?.Dispatcher
            Dim apply = Sub()
                            Recent.Clear()
                            For Each r In rows
                                Recent.Add(r)
                            Next
                            TotalTrades = total
                            TradesWithSnapshots = withSnap
                            TradesMissingSnapshots = Math.Max(0, total - withSnap)
                            LastRefreshUtc = DateTimeOffset.UtcNow
                        End Sub
            If disp IsNot Nothing AndAlso Not disp.CheckAccess() Then
                disp.Invoke(apply)
            Else
                apply()
            End If
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            RemoveHandler _timer.Tick, AddressOf OnTick
            _timer.Stop()
        End Sub

    End Class

    ''' <summary>OBS-07 F2: row in the Snapshot Health tile.</summary>
    Public Class SnapshotHealthRow
        Public Property TradeId As Long
        Public Property Symbol As String
        Public Property ExitTime As DateTimeOffset?
        Public Property HasSnapshots As Boolean
        Public ReadOnly Property StatusGlyph As String
            Get
                Return If(HasSnapshots, "●", "●")
            End Get
        End Property
        Public ReadOnly Property StatusLabel As String
            Get
                Return If(HasSnapshots, "ok", "missing")
            End Get
        End Property
        Public ReadOnly Property ExitTimeDisplay As String
            Get
                If Not ExitTime.HasValue Then Return "(open)"
                Return ExitTime.Value.ToLocalTime().ToString("HH:mm:ss")
            End Get
        End Property
    End Class

End Namespace
