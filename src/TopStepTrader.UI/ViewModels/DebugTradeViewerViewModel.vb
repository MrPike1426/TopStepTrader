Imports System.Collections.ObjectModel
Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Threading.Tasks
Imports System.Windows
Imports Microsoft.Win32
Imports TopStepTrader.Core.Models.Debug
Imports TopStepTrader.Data.Debug
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
        Public Property Record As DebugTradeRecord
    End Class

    Public Class DebugSnapshotRowViewModel
        Public Property Id As Integer
        Public Property Timestamp As String
        Public Property EventType As String
        Public Property LastPrice As String
        Public Property CurrentSL As String
        Public Property SuperTrendValue As String
        Public Property UnrealizedPnLDollars As String
        Public Property StopPhase As String
        Public Property Notes As String
        Public Property Record As DebugSnapshotRecord
    End Class

    Public Class ChartPoint
        Public Property Timestamp As DateTime
        Public Property LastPrice As Double
        Public Property CurrentSL As Double
        Public Property SuperTrendValue As Double
        Public Property IsMarker As Boolean
        Public Property MarkerLabel As String
    End Class

    Public Class DebugTradeViewerViewModel
        Inherits ViewModelBase

        Private ReadOnly _db As DebugTradeDbContext

        Private _allTrades As List(Of DebugTradeRowViewModel) = New List(Of DebugTradeRowViewModel)()
        Private _trades As ObservableCollection(Of DebugTradeRowViewModel) = New ObservableCollection(Of DebugTradeRowViewModel)()
        Private _selectedTrade As DebugTradeRowViewModel
        Private _allSnapshots As List(Of DebugSnapshotRowViewModel) = New List(Of DebugSnapshotRowViewModel)()
        Private _snapshots As ObservableCollection(Of DebugSnapshotRowViewModel) = New ObservableCollection(Of DebugSnapshotRowViewModel)()
        Private _chartPoints As ObservableCollection(Of ChartPoint) = New ObservableCollection(Of ChartPoint)()
        Private _selectedEventFilter As String = "All"
        Private _configJson As String = String.Empty
        Private _isConfigExpanded As Boolean = False
        Private _isLoading As Boolean = False
        Private _isEmpty As Boolean = False
        Private _statusMessage As String = String.Empty

        Public ReadOnly Property EventFilterOptions As List(Of String) =
            New List(Of String) From {"All", "Heartbeat", "BarClose", "SlAdjust", "AiCheck", "PartialFill", "Exit"}

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
                        LoadSnapshotsAsync(value.TradeId)
                    Else
                        _allSnapshots.Clear()
                        _snapshots.Clear()
                        _chartPoints.Clear()
                        ConfigJson = String.Empty
                    End If
                End If
            End Set
        End Property

        Public ReadOnly Property Snapshots As ObservableCollection(Of DebugSnapshotRowViewModel)
            Get
                Return _snapshots
            End Get
        End Property

        Public ReadOnly Property ChartPoints As ObservableCollection(Of ChartPoint)
            Get
                Return _chartPoints
            End Get
        End Property

        Public Property SelectedEventFilter As String
            Get
                Return _selectedEventFilter
            End Get
            Set(value As String)
                If SetProperty(_selectedEventFilter, value) Then
                    ApplySnapshotFilter()
                End If
            End Set
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

        Public ReadOnly Property RefreshCommand As RelayCommand
        Public ReadOnly Property ExportCsvCommand As RelayCommand
        Public ReadOnly Property ExportJsonCommand As RelayCommand
        Public ReadOnly Property OpenInExcelCommand As RelayCommand

        Public Sub New(db As DebugTradeDbContext)
            _db = db
            RefreshCommand = New RelayCommand(Sub() LoadDataAsync())
            ExportCsvCommand = New RelayCommand(AddressOf ExportCsv, Function() _selectedTrade IsNot Nothing)
            ExportJsonCommand = New RelayCommand(AddressOf ExportJson, Function() _selectedTrade IsNot Nothing)
            OpenInExcelCommand = New RelayCommand(AddressOf OpenInExcel, Function() _selectedTrade IsNot Nothing)
        End Sub

        Public Async Sub LoadDataAsync()
            IsLoading = True
            StatusMessage = "Loading trades…"
            Try
                Await _db.EnsureSchemaAsync()
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
                StatusMessage = If(IsEmpty, "No captured trades found. Enable debug capture on the SuperTrend+ page to start recording.", $"{vms.Count} trade(s) loaded.")
            Catch ex As Exception
                StatusMessage = $"Error loading trades: {ex.Message}"
            Finally
                IsLoading = False
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
                .Record = r
            }
        End Function

        Private Async Sub LoadSnapshotsAsync(tradeId As String)
            Try
                Dim snaps = Await _db.GetSnapshotsAsync(tradeId)
                Dim vms = snaps.Select(Function(s, i) MapSnapshotRow(s, i + 1)).ToList()
                _allSnapshots = vms

                Dim trade = _selectedTrade?.Record
                If trade IsNot Nothing Then
                    ConfigJson = FormatJson(trade.SuperTrendConfigJson)
                End If

                Application.Current.Dispatcher.Invoke(Sub()
                    ApplySnapshotFilter()
                    BuildChartPoints(snaps)
                End Sub)
            Catch ex As Exception
                StatusMessage = $"Error loading snapshots: {ex.Message}"
            End Try
        End Sub

        Private Shared Function MapSnapshotRow(s As DebugSnapshotRecord, idx As Integer) As DebugSnapshotRowViewModel
            Return New DebugSnapshotRowViewModel With {
                .Id = idx,
                .Timestamp = s.Timestamp,
                .EventType = s.EventType,
                .LastPrice = If(s.LastPrice.HasValue, s.LastPrice.Value.ToString("F4"), ""),
                .CurrentSL = If(s.CurrentSL.HasValue, s.CurrentSL.Value.ToString("F4"), ""),
                .SuperTrendValue = If(s.SuperTrendValue.HasValue, s.SuperTrendValue.Value.ToString("F4"), ""),
                .UnrealizedPnLDollars = If(s.UnrealizedPnLDollars.HasValue, $"${s.UnrealizedPnLDollars.Value:F2}", ""),
                .StopPhase = If(s.StopPhase IsNot Nothing, s.StopPhase, ""),
                .Notes = If(s.Notes IsNot Nothing, s.Notes, ""),
                .Record = s
            }
        End Function

        Private Sub ApplySnapshotFilter()
            _snapshots.Clear()
            Dim filtered = If(_selectedEventFilter = "All",
                              _allSnapshots,
                              _allSnapshots.Where(Function(s) s.EventType = _selectedEventFilter).ToList())
            For Each s In filtered
                _snapshots.Add(s)
            Next
        End Sub

        Private Sub BuildChartPoints(snaps As List(Of DebugSnapshotRecord))
            _chartPoints.Clear()
            For Each s In snaps
                If Not s.LastPrice.HasValue Then Continue For
                Dim ts As DateTime
                If Not DateTime.TryParse(s.Timestamp, ts) Then Continue For
                Dim isMarker = (s.EventType = "SlAdjust" OrElse s.EventType = "AiCheck" OrElse s.EventType = "Exit")
                _chartPoints.Add(New ChartPoint With {
                    .Timestamp = ts,
                    .LastPrice = CDbl(s.LastPrice.Value),
                    .CurrentSL = If(s.CurrentSL.HasValue, CDbl(s.CurrentSL.Value), 0),
                    .SuperTrendValue = If(s.SuperTrendValue.HasValue, CDbl(s.SuperTrendValue.Value), 0),
                    .IsMarker = isMarker,
                    .MarkerLabel = If(isMarker, s.EventType, String.Empty)
                })
            Next
        End Sub

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
                .Title = "Export Snapshots to CSV",
                .Filter = "CSV files (*.csv)|*.csv",
                .FileName = $"trade_{_selectedTrade.TradeId}.csv"
            }
            If dlg.ShowDialog() <> True Then Return
            Dim sb As New StringBuilder()
            sb.AppendLine("Timestamp,EventType,LastPrice,CurrentSL,SuperTrendValue,UnrealizedPnLDollars,StopPhase,Notes")
            For Each s In _allSnapshots
                sb.AppendLine($"{CsvEsc(s.Timestamp)},{CsvEsc(s.EventType)},{CsvEsc(s.LastPrice)},{CsvEsc(s.CurrentSL)},{CsvEsc(s.SuperTrendValue)},{CsvEsc(s.UnrealizedPnLDollars)},{CsvEsc(s.StopPhase)},{CsvEsc(s.Notes)}")
            Next
            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8)
            StatusMessage = $"Exported {_allSnapshots.Count} snapshot(s) to {dlg.FileName}"
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
                .Snapshots = _allSnapshots.Select(Function(s) s.Record).ToList()
            }
            Dim json = JsonSerializer.Serialize(payload, New JsonSerializerOptions With {.WriteIndented = True})
            File.WriteAllText(dlg.FileName, json, Encoding.UTF8)
            StatusMessage = $"Exported to {dlg.FileName}"
        End Sub

        Private Sub OpenInExcel()
            If _selectedTrade Is Nothing Then Return
            Dim tmp = Path.Combine(Path.GetTempPath(), $"trade_{_selectedTrade.TradeId}.csv")
            Dim sb As New StringBuilder()
            sb.AppendLine("Timestamp,EventType,LastPrice,CurrentSL,SuperTrendValue,UnrealizedPnLDollars,StopPhase,Notes")
            For Each s In _allSnapshots
                sb.AppendLine($"{CsvEsc(s.Timestamp)},{CsvEsc(s.EventType)},{CsvEsc(s.LastPrice)},{CsvEsc(s.CurrentSL)},{CsvEsc(s.SuperTrendValue)},{CsvEsc(s.UnrealizedPnLDollars)},{CsvEsc(s.StopPhase)},{CsvEsc(s.Notes)}")
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
