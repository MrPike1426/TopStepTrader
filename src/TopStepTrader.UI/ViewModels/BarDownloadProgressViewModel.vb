Imports System.Collections.ObjectModel
Imports System.Threading
Imports System.Windows.Input
Imports TopStepTrader.Services.Market
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ' ── Per-row status values ────────────────────────────────────────────────────

    Public Enum DownloadItemStatus
        Pending
        Downloading
        Done
        Failed
    End Enum

    ''' <summary>
    ''' Represents one contract/timeframe slot in the download progress list.
    ''' </summary>
    Public Class DownloadProgressItem
        Inherits ViewModelBase

        Public ReadOnly Property FriendlyName As String
        Public ReadOnly Property TimeframeLabel As String

        Private _status As DownloadItemStatus = DownloadItemStatus.Pending
        Public Property Status As DownloadItemStatus
            Get
                Return _status
            End Get
            Set(value As DownloadItemStatus)
                If SetProperty(_status, value) Then
                    OnPropertyChanged(NameOf(StatusIcon))
                    OnPropertyChanged(NameOf(StatusText))
                    OnPropertyChanged(NameOf(StatusBrushKey))
                End If
            End Set
        End Property

        Public ReadOnly Property StatusIcon As String
            Get
                Select Case _status
                    Case DownloadItemStatus.Downloading : Return "⬇"
                    Case DownloadItemStatus.Done        : Return "✓"
                    Case DownloadItemStatus.Failed      : Return "✗"
                    Case Else                           : Return "●"
                End Select
            End Get
        End Property

        Public ReadOnly Property StatusText As String
            Get
                Select Case _status
                    Case DownloadItemStatus.Downloading : Return "Downloading…"
                    Case DownloadItemStatus.Done        : Return "Done"
                    Case DownloadItemStatus.Failed      : Return "Failed"
                    Case Else                           : Return "Pending"
                End Select
            End Get
        End Property

        ''' <summary>Resource key resolved by BrushKeyConverter in the view.</summary>
        Public ReadOnly Property StatusBrushKey As String
            Get
                Select Case _status
                    Case DownloadItemStatus.Downloading : Return "AccentBrush"
                    Case DownloadItemStatus.Done        : Return "BuyBrush"
                    Case DownloadItemStatus.Failed      : Return "SellBrush"
                    Case Else                           : Return "TextSecondaryBrush"
                End Select
            End Get
        End Property

        Public Sub New(friendlyName As String, timeframeLabel As String)
            Me.FriendlyName = friendlyName
            Me.TimeframeLabel = timeframeLabel
        End Sub

    End Class

    ' ── Main ViewModel ──────────────────────────────────────────────────────────

    ''' <summary>
    ''' Drives the <see cref="Views.BarDownloadProgressWindow"/>.
    ''' Created manually (not via DI) in App.xaml.vb before the main window is shown.
    ''' </summary>
    Public Class BarDownloadProgressViewModel
        Inherits ViewModelBase

        Private ReadOnly _missing As List(Of StartupBarCheckResult)
        Private ReadOnly _downloadItem As Func(Of StartupBarCheckResult, IProgress(Of String), CancellationToken, Task)
        Private ReadOnly _tcs As New TaskCompletionSource()

        ' ── Observable item list ─────────────────────────────────────────────

        Public ReadOnly Property Items As New ObservableCollection(Of DownloadProgressItem)()

        ' ── Bindable properties ──────────────────────────────────────────────

        Private _summaryText As String
        Public Property SummaryText As String
            Get
                Return _summaryText
            End Get
            Set(value As String)
                SetProperty(_summaryText, value)
            End Set
        End Property

        Private _currentOperation As String = String.Empty
        Public Property CurrentOperation As String
            Get
                Return _currentOperation
            End Get
            Set(value As String)
                SetProperty(_currentOperation, value)
            End Set
        End Property

        Private _progressValue As Double = 0
        Public Property ProgressValue As Double
            Get
                Return _progressValue
            End Get
            Set(value As Double)
                SetProperty(_progressValue, value)
            End Set
        End Property

        Private _isDownloading As Boolean = False
        Public Property IsDownloading As Boolean
            Get
                Return _isDownloading
            End Get
            Set(value As Boolean)
                If SetProperty(_isDownloading, value) Then
                    OnPropertyChanged(NameOf(ShowActionButtons))
                    OnPropertyChanged(NameOf(ShowProgressArea))
                End If
            End Set
        End Property

        Private _isComplete As Boolean = False
        Public Property IsComplete As Boolean
            Get
                Return _isComplete
            End Get
            Set(value As Boolean)
                If SetProperty(_isComplete, value) Then
                    OnPropertyChanged(NameOf(ShowActionButtons))
                    OnPropertyChanged(NameOf(ShowCloseButton))
                    OnPropertyChanged(NameOf(ShowProgressArea))
                End If
            End Set
        End Property

        ''' <summary>True before the download starts or after it completes.</summary>
        Public ReadOnly Property ShowActionButtons As Boolean
            Get
                Return Not _isDownloading AndAlso Not _isComplete
            End Get
        End Property

        ''' <summary>True while downloading or after completion (keeps progress visible).</summary>
        Public ReadOnly Property ShowProgressArea As Boolean
            Get
                Return _isDownloading OrElse _isComplete
            End Get
        End Property

        ''' <summary>True once all downloads have finished — replaces the Download/Skip row.</summary>
        Public ReadOnly Property ShowCloseButton As Boolean
            Get
                Return _isComplete
            End Get
        End Property

        ' ── Commands ─────────────────────────────────────────────────────────

        Public ReadOnly Property DownloadCommand As ICommand
        Public ReadOnly Property CloseCommand As ICommand

        ' ── Awaitable completion ─────────────────────────────────────────────

        ''' <summary>
        ''' Completes when the user clicks Close (after download) or Skip (before download).
        ''' App.xaml.vb awaits this before showing the main window.
        ''' </summary>
        Public ReadOnly Property CompletionTask As Task
            Get
                Return _tcs.Task
            End Get
        End Property

        ' ── Constructor ──────────────────────────────────────────────────────

        ''' <summary>
        ''' Creates the ViewModel.
        ''' </summary>
        ''' <param name="missing">Results from <see cref="IStartupBarCheckService.CheckMissingBarsAsync"/>.</param>
        ''' <param name="downloadItem">
        ''' Delegate that downloads a single item.  Wired up in App.xaml.vb to
        ''' <see cref="IStartupBarCheckService"/>'s per-item logic so this ViewModel
        ''' stays independent of the service layer.
        ''' </param>
        Public Sub New(missing As List(Of StartupBarCheckResult),
                       downloadItem As Func(Of StartupBarCheckResult, IProgress(Of String), CancellationToken, Task))
            _missing = missing
            _downloadItem = downloadItem

            Dim contractCount = missing.Select(Function(r) r.ContractId).Distinct().Count()
            SummaryText = $"Missing bar data detected for {missing.Count} slot(s) across {contractCount} favourite contract(s)."

            For Each r In missing
                Items.Add(New DownloadProgressItem(
                    r.FriendlyName,
                    StartupBarCheckService.TimeframeLabel(r.Timeframe)))
            Next

            DownloadCommand = New RelayCommand(Async Sub() Await ExecuteDownloadAsync())
            CloseCommand = New RelayCommand(Sub() _tcs.TrySetResult())
        End Sub

        ' ── Private helpers ──────────────────────────────────────────────────

        Private Async Function ExecuteDownloadAsync() As Task
            IsDownloading = True
            ProgressValue = 0

            For i = 0 To _missing.Count - 1
                Dim item = _missing(i)
                Dim row = Items(i)

                row.Status = DownloadItemStatus.Downloading
                CurrentOperation = $"Downloading {row.FriendlyName} ({row.TimeframeLabel})…"

                Dim prog As New Progress(Of String)(Sub(msg) CurrentOperation = msg)
                Try
                    Await _downloadItem(item, prog, CancellationToken.None)
                    row.Status = DownloadItemStatus.Done
                Catch ex As Exception
                    row.Status = DownloadItemStatus.Failed
                End Try

                ProgressValue = ((i + 1) / CDbl(_missing.Count)) * 100D
            Next

            CurrentOperation = "✓  All downloads complete — ready to trade!"
            IsDownloading = False
            IsComplete = True
            ' CompletionTask finalises when the user explicitly clicks Close.
        End Function

    End Class

End Namespace
