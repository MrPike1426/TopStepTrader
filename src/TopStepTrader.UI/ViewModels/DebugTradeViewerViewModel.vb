Imports System.Collections.ObjectModel
Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Threading.Tasks
Imports System.Windows
Imports Microsoft.Win32
Imports TopStepTrader.Core.Models.Debug
Imports TopStepTrader.Data.Debug
Imports TopStepTrader.Services.Debug
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    Public Class DebugTradeRowViewModel
        Public Property TradeId As String
        Public Property Instrument As String
        Public Property Persona As String
        Public Property EntryTime As String
        Public Property EntryMode As String
        Public Property Direction As String
        Public Property ContractCount As Integer
        Public Property FinalPnL As String
        Public Property Status As String
        Public Property ReconciliationStatus As String
        Public Property ExitReason As String
        Public Property Record As DebugTradeRecord
    End Class

    Public Class DebugActionRowViewModel
        Public Property Id As Integer
        Public Property Timestamp As String
        Public Property ActionType As String
        Public Property Detail As String
        Public Property Source As String
        Public Property Record As DebugTradeAction
    End Class

    Public Class DebugTradeViewerViewModel
        Inherits ViewModelBase

        Private ReadOnly _db As DebugTradeDbContext
        Private ReadOnly _reconciliation As IDebugTradeReconciliationService

        Private _allTrades As List(Of DebugTradeRowViewModel) = New List(Of DebugTradeRowViewModel)()
        Private _trades As ObservableCollection(Of DebugTradeRowViewModel) = New ObservableCollection(Of DebugTradeRowViewModel)()
        Private _selectedTrade As DebugTradeRowViewModel
        Private _allActions As List(Of DebugActionRowViewModel) = New List(Of DebugActionRowViewModel)()
        Private _actions As ObservableCollection(Of DebugActionRowViewModel) = New ObservableCollection(Of DebugActionRowViewModel)()
        Private _configJson As String = String.Empty
        Private _isConfigExpanded As Boolean = False
        Private _isLoading As Boolean = False
        Private _isEmpty As Boolean = False
        Private _statusMessage As String = String.Empty
        Private _isReconciling As Boolean = False

        Public ReadOnly Property Trades As ObservableCollection(Of DebugTradeRowViewModel)
            Get
                Return _trades
            End Get
        End Property

        Public Property SelectedTrade As DebugTradeRowViewModel
            Get
                Return _selectedTrade
            End Get
            Set(value As DebugTradeRowViewModel)
                If SetProperty(_selectedTrade, value) Then
                    If value IsNot Nothing Then
                        LoadActionsAsync(value.TradeId)
                    Else
                        _allActions.Clear()
                        _actions.Clear()
                        ConfigJson = String.Empty
                    End If
                End If
            End Set
        End Property

        Public ReadOnly Property Actions As ObservableCollection(Of DebugActionRowViewModel)
            Get
                Return _actions
            End Get
        End Property

        Public Property ConfigJson As String
            Get
                Return _configJson
            End Get
            Set(value As String)
                SetProperty(_configJson, value)
            End Set
        End Property

        Public Property IsConfigExpanded As Boolean
            Get
                Return _isConfigExpanded
            End Get
            Set(value As Boolean)
                SetProperty(_isConfigExpanded, value)
            End Set
        End Property

        Public Property IsLoading As Boolean
            Get
                Return _isLoading
            End Get
            Set(value As Boolean)
                SetProperty(_isLoading, value)
            End Set
        End Property

        Public Property IsEmpty As Boolean
            Get
                Return _isEmpty
            End Get
            Set(value As Boolean)
                SetProperty(_isEmpty, value)
            End Set
        End Property

        Public Property StatusMessage As String
            Get
                Return _statusMessage
            End Get
            Set(value As String)
                SetProperty(_statusMessage, value)
            End Set
        End Property

        Public Property IsReconciling As Boolean
            Get
                Return _isReconciling
            End Get
            Set(value As Boolean)
                SetProperty(_isReconciling, value)
            End Set
        End Property

        Public ReadOnly Property RefreshCommand As RelayCommand
        Public ReadOnly Property ReconcileCommand As RelayCommand
        Public ReadOnly Property ExportCsvCommand As RelayCommand
        Public ReadOnly Property ExportJsonCommand As RelayCommand
        Public ReadOnly Property OpenInExcelCommand As RelayCommand

        Public Sub New(db As DebugTradeDbContext, reconciliation As IDebugTradeReconciliationService)
            _db = db
            _reconciliation = reconciliation
            RefreshCommand = New RelayCommand(Sub() LoadDataAsync())
            ReconcileCommand = New RelayCommand(Sub() ReconcileAsync(), Function() Not _isReconciling)
            ExportCsvCommand = New RelayCommand(AddressOf ExportCsv, Function() _selectedTrade IsNot Nothing)
            ExportJsonCommand = New RelayCommand(AddressOf ExportJson, Function() _selectedTrade IsNot Nothing)
            OpenInExcelCommand = New RelayCommand(AddressOf OpenInExcel, Function() _selectedTrade IsNot Nothing)
        End Sub

        Public Async Sub LoadDataAsync()
            IsLoading = True
            StatusMessage = "Loading trades…"
            Try
                Await _db.EnsureSchemaAsync()

                ' FEAT-56: auto-reconcile open trades on first load so the post-mortem
                ' is authoritative even if the engine missed an exit callback.
                Try
                    Dim updated = Await _reconciliation.ReconcileOpenTradesAsync()
                    If updated > 0 Then
                        StatusMessage = $"Reconciled {updated} open trade(s) from broker."
                    End If
                Catch ex As Exception
                    StatusMessage = $"Reconciliation skipped: {ex.Message}"
                End Try

                Dim rows = Await _db.GetAllTradesAsync()
                Dim vms = rows.Select(AddressOf MapTradeRow).ToList()
                _allTrades = vms
                Application.Current.Dispatcher.Invoke(Sub()
                    _trades.Clear()
                    For Each v In vms
                        _trades.Add(v)
                    Next
                End Sub)
                IsEmpty = (vms.Count = 0)
                If IsEmpty Then
                    StatusMessage = "No captured trades found. Enable debug capture on the SuperTrend+ page to start recording."
                ElseIf String.IsNullOrEmpty(StatusMessage) OrElse Not StatusMessage.StartsWith("Reconcil") Then
                    StatusMessage = $"{vms.Count} trade(s) loaded."
                End If
            Catch ex As Exception
                StatusMessage = $"Error loading trades: {ex.Message}"
            Finally
                IsLoading = False
            End Try
        End Sub

        Private Async Sub ReconcileAsync()
            If _isReconciling Then Return
            IsReconciling = True
            StatusMessage = "Reconciling open trades with TopStepX…"
            Try
                Dim updated = Await _reconciliation.ReconcileOpenTradesAsync()
                StatusMessage = $"Reconciliation complete — {updated} trade(s) updated."
                LoadDataAsync()
            Catch ex As Exception
                StatusMessage = $"Reconciliation failed: {ex.Message}"
            Finally
                IsReconciling = False
            End Try
        End Sub

        Private Shared Function MapTradeRow(r As DebugTradeRecord) As DebugTradeRowViewModel
            Dim pnlStr = If(r.RealisedPnLDollars.HasValue,
                            $"${r.RealisedPnLDollars.Value:F2}",
                            "Open")
            Return New DebugTradeRowViewModel With {
                .TradeId = r.TradeId,
                .Instrument = r.Instrument,
                .Persona = r.Persona,
                .EntryTime = r.EntryTime,
                .EntryMode = r.EntryMode,
                .Direction = r.Direction,
                .ContractCount = r.ContractCount,
                .FinalPnL = pnlStr,
                .Status = If(String.IsNullOrEmpty(r.ClosedAt), "Open", "Closed"),
                .ReconciliationStatus = If(String.IsNullOrEmpty(r.ReconciliationStatus), "—", r.ReconciliationStatus),
                .ExitReason = If(String.IsNullOrEmpty(r.ExitReason), "", r.ExitReason),
                .Record = r
            }
        End Function

        Private Async Sub LoadActionsAsync(tradeId As String)
            Try
                Dim acts = Await _db.GetActionsAsync(tradeId)
                Dim vms = acts.Select(Function(a, i) MapActionRow(a, i + 1)).ToList()
                _allActions = vms

                Dim trade = _selectedTrade?.Record
                If trade IsNot Nothing Then
                    ConfigJson = FormatJson(trade.SuperTrendConfigJson)
                End If

                Application.Current.Dispatcher.Invoke(Sub()
                    _actions.Clear()
                    For Each a In vms
                        _actions.Add(a)
                    Next
                End Sub)
            Catch ex As Exception
                StatusMessage = $"Error loading actions: {ex.Message}"
            End Try
        End Sub

        Private Shared Function MapActionRow(a As DebugTradeAction, idx As Integer) As DebugActionRowViewModel
            Return New DebugActionRowViewModel With {
                .Id = idx,
                .Timestamp = a.TimestampUtc,
                .ActionType = a.ActionType,
                .Detail = FormatActionDetail(a),
                .Source = If(String.IsNullOrEmpty(a.Source), "Local", a.Source),
                .Record = a
            }
        End Function

        Private Shared Function FormatActionDetail(a As DebugTradeAction) As String
            Dim parts As New List(Of String)()
            If a.Price.HasValue Then parts.Add($"price={a.Price.Value:F4}")
            If a.OldValue.HasValue OrElse a.NewValue.HasValue Then
                Dim oldS = If(a.OldValue.HasValue, a.OldValue.Value.ToString("F4"), "—")
                Dim newS = If(a.NewValue.HasValue, a.NewValue.Value.ToString("F4"), "—")
                parts.Add($"{oldS} → {newS}")
            End If
            If a.Quantity.HasValue Then parts.Add($"qty={a.Quantity.Value}")
            If a.OrderId.HasValue Then parts.Add($"order={a.OrderId.Value}")
            If Not String.IsNullOrEmpty(a.Reason) Then parts.Add(a.Reason)
            Return String.Join("  ", parts)
        End Function

        Private Shared Function FormatJson(raw As String) As String
            If String.IsNullOrWhiteSpace(raw) Then Return raw
            Try
                Dim doc = JsonDocument.Parse(raw)
                Return JsonSerializer.Serialize(doc, New JsonSerializerOptions With {.WriteIndented = True})
            Catch
                Return raw
            End Try
        End Function

        ' ── Export helpers ────────────────────────────────────────────────────────

        Private Sub ExportCsv()
            If _selectedTrade Is Nothing Then Return
            Dim dlg As New SaveFileDialog With {
                .Title = "Export Actions to CSV",
                .Filter = "CSV files (*.csv)|*.csv",
                .FileName = $"trade_{_selectedTrade.TradeId}.csv"
            }
            If dlg.ShowDialog() <> True Then Return
            Dim sb As New StringBuilder()
            sb.AppendLine("Timestamp,ActionType,Detail,Source")
            For Each a In _allActions
                sb.AppendLine($"{CsvEsc(a.Timestamp)},{CsvEsc(a.ActionType)},{CsvEsc(a.Detail)},{CsvEsc(a.Source)}")
            Next
            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8)
            StatusMessage = $"Exported {_allActions.Count} action(s) to {dlg.FileName}"
        End Sub

        Private Sub ExportJson()
            If _selectedTrade Is Nothing Then Return
            Dim dlg As New SaveFileDialog With {
                .Title = "Export Trade to JSON",
                .Filter = "JSON files (*.json)|*.json",
                .FileName = $"trade_{_selectedTrade.TradeId}.json"
            }
            If dlg.ShowDialog() <> True Then Return
            Dim payload = New With {
                .Trade = _selectedTrade.Record,
                .Actions = _allActions.Select(Function(a) a.Record).ToList()
            }
            Dim json = JsonSerializer.Serialize(payload, New JsonSerializerOptions With {.WriteIndented = True})
            File.WriteAllText(dlg.FileName, json, Encoding.UTF8)
            StatusMessage = $"Exported to {dlg.FileName}"
        End Sub

        Private Sub OpenInExcel()
            If _selectedTrade Is Nothing Then Return
            Dim tmp = Path.Combine(Path.GetTempPath(), $"trade_{_selectedTrade.TradeId}.csv")
            Dim sb As New StringBuilder()
            sb.AppendLine("Timestamp,ActionType,Detail,Source")
            For Each a In _allActions
                sb.AppendLine($"{CsvEsc(a.Timestamp)},{CsvEsc(a.ActionType)},{CsvEsc(a.Detail)},{CsvEsc(a.Source)}")
            Next
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8)
            System.Diagnostics.Process.Start(New System.Diagnostics.ProcessStartInfo(tmp) With {.UseShellExecute = True})
        End Sub

        Private Shared Function CsvEsc(v As String) As String
            If v Is Nothing Then Return String.Empty
            If v.Contains(",") OrElse v.Contains("""") OrElse v.Contains(vbLf) Then
                Return """" & v.Replace("""", """""") & """"
            End If
            Return v
        End Function

    End Class

End Namespace
