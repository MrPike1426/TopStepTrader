Imports System.Collections.ObjectModel
Imports System.Diagnostics
Imports System.IO
Imports System.Windows
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Data.Entities
Imports TopStepTrader.Data.Repositories
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' FEAT-51: View-model for the Trade Detail popup launched from the Order ID
    ''' hyperlink in the Trade History grid. Sources every tab from local SQLite —
    ''' no live PX calls at open time.
    ''' </summary>
    Public Class TradeDetailViewModel
        Inherits ViewModelBase

        Private ReadOnly _tradeService As ITradeRecordService
        Private ReadOnly _snapshotRepo As ITradeSnapshotRepository
        Private ReadOnly _stopAdjRepo As ITradeStopAdjustmentRepository
        Private ReadOnly _launcher As IPostMortemLauncher
        Private ReadOnly _logger As ILogger(Of TradeDetailViewModel)

        Private _record As LiveTradeRecord

        Public ReadOnly Property Orders As New ObservableCollection(Of TradeOrderSnapshotEntity)()
        Public ReadOnly Property Positions As New ObservableCollection(Of TradePositionSnapshotEntity)()
        Public ReadOnly Property Fills As New ObservableCollection(Of TradeFillSnapshotEntity)()
        Public ReadOnly Property RatchetRows As New ObservableCollection(Of TradeStopAdjustmentEntity)()

        ' ── Header bindings ─────────────────────────────────────────────
        Private _title As String = "Trade Detail"
        Public Property Title As String
            Get
                Return _title
            End Get
            Set(value As String)
                SetProperty(_title, value)
            End Set
        End Property

        Private _headerLine As String = String.Empty
        Public Property HeaderLine As String
            Get
                Return _headerLine
            End Get
            Set(value As String)
                SetProperty(_headerLine, value)
            End Set
        End Property

        Private _statusText As String = String.Empty
        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Set(value As String)
                SetProperty(_statusText, value)
            End Set
        End Property

        Private _isBusy As Boolean
        Public Property IsBusy As Boolean
            Get
                Return _isBusy
            End Get
            Set(value As Boolean)
                SetProperty(_isBusy, value)
            End Set
        End Property

        Public ReadOnly Property RunPostMortemCommand As RelayCommand
        Public ReadOnly Property CloseCommand As RelayCommand

        Public Event RequestClose As EventHandler
        Public Event RequestIssueText As EventHandler(Of IssueTextRequest)

        Public Sub New(tradeService As ITradeRecordService,
                       snapshotRepo As ITradeSnapshotRepository,
                       stopAdjRepo As ITradeStopAdjustmentRepository,
                       launcher As IPostMortemLauncher,
                       logger As ILogger(Of TradeDetailViewModel))
            _tradeService = tradeService
            _snapshotRepo = snapshotRepo
            _stopAdjRepo = stopAdjRepo
            _launcher = launcher
            _logger = logger
            RunPostMortemCommand = New RelayCommand(AddressOf OnRunPostMortem)
            CloseCommand = New RelayCommand(Sub() RaiseEvent RequestClose(Me, EventArgs.Empty))
        End Sub

        Public Async Function LoadAsync(recordId As Long) As Task
            IsBusy = True
            Try
                ' Locate record (no dedicated GetByIdAsync — fetch a wide window).
                Dim trades = Await _tradeService.GetRecentTradesAsync(2000, Nothing)
                _record = trades.FirstOrDefault(Function(r) r.Id = recordId)
                If _record Is Nothing Then
                    StatusText = $"Record {recordId} not found."
                    Return
                End If

                Title = $"Trade #{_record.EntryOrderId} — {_record.Symbol} {_record.Direction}"
                HeaderLine = BuildHeader(_record)

                Dim orderRows = Await _snapshotRepo.GetOrdersAsync(recordId)
                Dim positionRows = Await _snapshotRepo.GetPositionsAsync(recordId)
                Dim fillRows = Await _snapshotRepo.GetFillsAsync(recordId)
                Dim ratchet = Await _stopAdjRepo.GetByTradeRecordAsync(recordId)

                Dispatch(Sub()
                             Orders.Clear() : For Each o In orderRows : Orders.Add(o) : Next
                             Positions.Clear() : For Each p In positionRows : Positions.Add(p) : Next
                             Fills.Clear() : For Each f In fillRows : Fills.Add(f) : Next
                             RatchetRows.Clear() : For Each r In ratchet : RatchetRows.Add(r) : Next
                             StatusText = $"Loaded {Orders.Count} orders, {Positions.Count} positions, {Fills.Count} fills, {RatchetRows.Count} ratchet rows."
                         End Sub)
            Catch ex As Exception
                _logger.LogWarning(ex, "TradeDetailViewModel.LoadAsync failed for {Id}", recordId)
                StatusText = $"Load failed: {ex.Message}"
            Finally
                IsBusy = False
            End Try
        End Function

        Private Sub OnRunPostMortem()
            If _record Is Nothing Then
                StatusText = "No record loaded."
                Return
            End If

            Dim req As New IssueTextRequest()
            RaiseEvent RequestIssueText(Me, req)
            If req.Cancelled Then Return

            ' Fire-and-forget; never block the UI thread.
            Dim issueText As String = req.IssueText
            StatusText = "Running post-mortem…"
            Task.Run(Async Function()
                         Await RunPostMortemAsync(issueText)
                     End Function)
        End Sub

        Public Async Function RunPostMortemAsync(issueText As String) As Task
            Try
                Dim entryLocal = _record.EntryTime.ToLocalTime()
                Dim result = Await _launcher.RunAsync(_record.EntryOrderId,
                                                     _record.TopStepXTradeId,
                                                     _record.Symbol,
                                                     entryLocal,
                                                     issueText)
                If result.Success AndAlso Not String.IsNullOrEmpty(result.ReportPath) AndAlso File.Exists(result.ReportPath) Then
                    Try
                        Process.Start(New ProcessStartInfo(result.ReportPath) With {.UseShellExecute = True})
                    Catch openEx As Exception
                        _logger.LogWarning(openEx, "Failed to open post-mortem report")
                    End Try
                    Dispatch(Sub() StatusText = $"Report opened: {Path.GetFileName(result.ReportPath)}")
                Else
                    Dim tail = If(String.IsNullOrEmpty(result.StdErrTail), $"exit {result.ExitCode}", result.StdErrTail)
                    Dispatch(Sub() StatusText = $"Failed: {tail}")
                End If
            Catch ex As Exception
                _logger.LogWarning(ex, "RunPostMortemAsync failed")
                Dispatch(Sub() StatusText = $"Failed: {ex.Message}")
            End Try
        End Function

        Private Shared Function BuildHeader(r As LiveTradeRecord) As String
            Dim entry = r.EntryTime.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
            Dim exitT = If(r.ExitTime.HasValue, r.ExitTime.Value.LocalDateTime.ToString("HH:mm:ss"), "—")
            Dim duration = If(r.ExitTime.HasValue, FormatSpan(r.ExitTime.Value - r.EntryTime), "—")
            Dim pnl = If(r.PnL.HasValue, $"${r.PnL.Value:F2}", "—")
            Return $"#{r.EntryOrderId}  |  {r.Symbol}  {r.Direction}  |  TF {If(String.IsNullOrEmpty(r.Timeframe), "—", r.Timeframe)}  |  {r.Persona}  |  {r.StrategyName}  |  {entry} → {exitT}  |  {duration}  |  P&L {pnl}  |  Comm ${r.CommissionUsd:F2}  |  Fees ${r.FeesUsd:F2}"
        End Function

        Private Shared Function FormatSpan(span As TimeSpan) As String
            If span.TotalHours >= 1 Then Return $"{CInt(Math.Floor(span.TotalHours))}h {span.Minutes:D2}m"
            If span.TotalMinutes >= 1 Then Return $"{CInt(Math.Floor(span.TotalMinutes))}m {span.Seconds:D2}s"
            Return $"{CInt(Math.Floor(span.TotalSeconds))}s"
        End Function

        Private Sub Dispatch(action As Action)
            If Application.Current?.Dispatcher IsNot Nothing Then
                Application.Current.Dispatcher.Invoke(action)
            Else
                action()
            End If
        End Sub

    End Class

    ''' <summary>Round-trip carrier for the modal issue prompt.</summary>
    Public Class IssueTextRequest
        Public Property IssueText As String = String.Empty
        Public Property Cancelled As Boolean = True
    End Class

End Namespace
