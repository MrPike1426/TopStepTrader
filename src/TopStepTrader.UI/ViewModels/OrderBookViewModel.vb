Imports System.Collections.ObjectModel
Imports System.Windows
Imports Microsoft.Extensions.DependencyInjection
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.UI.ViewModels.Base
Imports TopStepTrader.UI.Views

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>View-model for the Trade History tab (redesigned Orders tab).</summary>
    Public Class OrderBookViewModel
        Inherits ViewModelBase

        Private Const MaxRows As Integer = 100

        Private ReadOnly _tradeService As ITradeRecordService
        Private ReadOnly _accountService As IAccountService

        Public ReadOnly Property TradeRows As New ObservableCollection(Of TradeRowVm)()

        ' ── Filter properties ─────────────────────────────────────────────

        Private _symbolFilter As String = String.Empty
        Public Property SymbolFilter As String
            Get
                Return _symbolFilter
            End Get
            Set(value As String)
                SetProperty(_symbolFilter, value)
            End Set
        End Property

        Private _selectedPnLFilter As String = "All"
        Public Property SelectedPnLFilter As String
            Get
                Return _selectedPnLFilter
            End Get
            Set(value As String)
                SetProperty(_selectedPnLFilter, value)
            End Set
        End Property

        Public ReadOnly Property PnLFilterOptions As String() = {"All", "Winners", "Losers"}

        Private _statusText As String = "Ready"
        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Set(value As String)
                SetProperty(_statusText, value)
            End Set
        End Property

        ' ── Commands ─────────────────────────────────────────────────────

        Public ReadOnly Property RefreshCommand As RelayCommand
        Public ReadOnly Property ApplyFilterCommand As RelayCommand
        Public ReadOnly Property OpenDetailCommand As RelayCommand(Of TradeRowVm)

        ' ── Constructor ──────────────────────────────────────────────────

        Public Sub New(tradeService As ITradeRecordService, accountService As IAccountService)
            _tradeService = tradeService
            _accountService = accountService
            RefreshCommand = New RelayCommand(Sub() LoadDataAsync(force:=True))
            ApplyFilterCommand = New RelayCommand(Sub() LoadDataAsync(force:=True))
            OpenDetailCommand = New RelayCommand(Of TradeRowVm)(AddressOf OpenDetail)
        End Sub

        ''' <summary>FEAT-51: Resolve TradeDetailViewModel from DI and open the detail window.</summary>
        Private Sub OpenDetail(row As TradeRowVm)
            If row Is Nothing Then Return
            Dim recordId As Long
            If Not Long.TryParse(row.RecordId, recordId) OrElse recordId <= 0 Then Return
            Dim sp = App.Services
            If sp Is Nothing Then Return
            Dim vm = sp.GetRequiredService(Of TradeDetailViewModel)()
            Dim window As New TradeDetailWindow() With {
                .DataContext = vm,
                .Owner = Application.Current?.MainWindow
            }
            ' Fire-and-forget load; window opens immediately and populates as data arrives.
            Task.Run(Function() vm.LoadAsync(recordId))
            window.ShowDialog()
        End Sub

        Public Sub LoadDataAsync()
            LoadDataAsync(force:=False)
        End Sub

        ''' <summary>BUG-66: debounce repeat tab-activation reloads (5 s window) and guard re-entrancy.</summary>
        Public Sub LoadDataAsync(force As Boolean)
            SyncLock _loadGate
                If _isLoading Then Return
                If Not force AndAlso (DateTime.UtcNow - _lastLoadUtc).TotalSeconds < 5 Then Return
                _isLoading = True
            End SyncLock
            Task.Run(Async Function()
                         Try
                             Await LoadDataWithRecovery()
                         Finally
                             SyncLock _loadGate
                                 _lastLoadUtc = DateTime.UtcNow
                                 _isLoading = False
                             End SyncLock
                         End Try
                     End Function)
        End Sub

        Private ReadOnly _loadGate As New Object()
        Private _isLoading As Boolean
        Private _lastLoadUtc As DateTime = DateTime.MinValue

        Private Async Function LoadDataWithRecovery() As Task
            ' Attempt crash recovery first (no-op if no open records)
            Try
                Dim accounts = Await _accountService.GetActiveAccountsAsync()
                Dim acct = accounts.FirstOrDefault()
                If acct IsNot Nothing Then
                    Await _tradeService.RecoverOpenTradesAsync(acct.Id)
                End If
            Catch
                ' Non-fatal — proceed to load regardless
            End Try
            LoadData()
        End Function

        Private Sub LoadData()
            Task.Run(Async Function()
                         Try
                             Dim pnlEnum As PnLFilterType = PnLFilterType.All
                             Select Case _selectedPnLFilter
                                 Case "Winners" : pnlEnum = PnLFilterType.Winners
                                 Case "Losers"  : pnlEnum = PnLFilterType.Losers
                             End Select

                             Dim filter As New TradeFilter With {
                                 .Symbol = If(_symbolFilter?.Trim() = "All", String.Empty, _symbolFilter?.Trim()),
                                 .PnLFilter = pnlEnum,
                                 .ClosedOnly = True
                             }

                             Dim trades = Await _tradeService.GetRecentTradesAsync(MaxRows, filter)

                             Dispatch(Sub()
                                          TradeRows.Clear()
                                          For Each t In trades
                                              TradeRows.Add(New TradeRowVm(t))
                                          Next
                                          StatusText = $"{trades.Count} trade(s) loaded"
                                      End Sub)
                         Catch ex As Exception
                             Dispatch(Sub() StatusText = $"Error loading trades: {ex.Message}")
                         End Try
                     End Function)
        End Sub

        Private Sub Dispatch(action As Action)
            If Application.Current?.Dispatcher IsNot Nothing Then
                Application.Current.Dispatcher.Invoke(action)
            End If
        End Sub

    End Class

    ''' <summary>View-friendly row for a single live trade record.</summary>
    Public Class TradeRowVm

        ''' <summary>Database id of the LiveTradeRecord (used by FEAT-51 to load the detail popup).</summary>
        Public Property RecordId As String
        Public Property OrderId As String
        Public Property Id As String
        Public Property Symbol As String
        Public Property Direction As String
        Public Property Sizes As String
        Public Property Leverage As String
        Public Property Strategy As String
        Public Property Persona As String
        Public Property Timeframe As String
        Public Property EntryTime As String
        Public Property ExitTime As String
        Public Property Duration As String
        Public Property EntryPrice As String
        Public Property ExitPrice As String
        Public Property PnLDisplay As String
        Public Property PnLRaw As Decimal
        Public Property Commission As String
        Public Property Fees As String
        Public Property ExitReason As String
        Public Property IsRecovered As Boolean

        Public ReadOnly Property PnLColor As String
            Get
                Return If(PnLRaw > 0D, "#4CAF50", If(PnLRaw < 0D, "#EF5350", "#AAAAAA"))
            End Get
        End Property

        Public ReadOnly Property DirectionColor As String
            Get
                Return If(Direction = "Long", "#4CAF50", "#EF5350")
            End Get
        End Property

        Public Sub New(t As LiveTradeRecord)
            RecordId = t.Id.ToString()
            ' OrderId: TopStepX market entry order ID
            OrderId = If(t.EntryOrderId > 0, t.EntryOrderId.ToString(), "—")

            ' ID: prefer TopStepX Trade ID, fall back to entry order ID
            Dim rawId = If(t.TopStepXTradeId.HasValue, t.TopStepXTradeId.Value, t.EntryOrderId)
            Id = If(rawId > 0, rawId.ToString(), "—")

            Symbol = t.Symbol
            Direction = t.Direction
            Sizes = t.Sizes.ToString()
            Leverage = $"{t.MaxScaleIns}×"
            Strategy = t.StrategyName
            Persona = t.Persona
            Timeframe = If(t.Timeframe = String.Empty, "—", t.Timeframe)
            EntryTime = t.EntryTime.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
            ExitTime = If(t.ExitTime.HasValue, t.ExitTime.Value.LocalDateTime.ToString("HH:mm:ss"), "—")
            Duration = FormatDuration(t.EntryTime, t.ExitTime)
            EntryPrice = If(t.EntryPrice <> 0D, t.EntryPrice.ToString("F4"), "—")
            ExitPrice = If(t.ExitPrice.HasValue, t.ExitPrice.Value.ToString("F4"), "—")

            PnLRaw = If(t.PnL.HasValue, t.PnL.Value, 0D)
            PnLDisplay = If(t.PnL.HasValue, $"{If(t.PnL.Value >= 0D, "+", "")}${t.PnL.Value:F2}", "—")

            Commission = $"-${t.CommissionUsd:F2}"
            Fees = $"-${t.FeesUsd:F2}"
            ExitReason = t.ExitReason
            IsRecovered = t.IsRecoveredFromCrash
        End Sub

        Private Shared Function FormatDuration(entry As DateTimeOffset, exitTime As DateTimeOffset?) As String
            If Not exitTime.HasValue Then Return "—"
            Dim span = exitTime.Value - entry
            If span.TotalHours >= 1 Then Return $"{CInt(Math.Floor(span.TotalHours))}h {span.Minutes:D2}m"
            If span.TotalMinutes >= 1 Then Return $"{CInt(Math.Floor(span.TotalMinutes))}m {span.Seconds:D2}s"
            Return $"{CInt(Math.Floor(span.TotalSeconds))}s"
        End Function

    End Class

End Namespace
