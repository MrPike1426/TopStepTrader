Imports System.Collections.ObjectModel
Imports System.Windows
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.UI.ViewModels.Base

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

        Private _strategyFilter As String = String.Empty
        Public Property StrategyFilter As String
            Get
                Return _strategyFilter
            End Get
            Set(value As String)
                SetProperty(_strategyFilter, value)
            End Set
        End Property

        Private _personaFilter As String = String.Empty
        Public Property PersonaFilter As String
            Get
                Return _personaFilter
            End Get
            Set(value As String)
                SetProperty(_personaFilter, value)
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

        ' ── Constructor ──────────────────────────────────────────────────

        Public Sub New(tradeService As ITradeRecordService, accountService As IAccountService)
            _tradeService = tradeService
            _accountService = accountService
            RefreshCommand = New RelayCommand(AddressOf LoadData)
            ApplyFilterCommand = New RelayCommand(AddressOf LoadData)
        End Sub

        Public Sub LoadDataAsync()
            Task.Run(AddressOf LoadDataWithRecovery)
        End Sub

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
                                 .Strategy = If(_strategyFilter?.Trim() = "All", String.Empty, _strategyFilter?.Trim()),
                                 .Persona = If(_personaFilter?.Trim() = "All", String.Empty, _personaFilter?.Trim()),
                                 .PnLFilter = pnlEnum
                             }

                             Dim trades = Await _tradeService.GetRecentTradesAsync(MaxRows, filter)

                             Dispatch(Sub()
                                          TradeRows.Clear()
                                          For Each t In trades
                                              TradeRows.Add(New TradeRowVm(t))
                                          Next
                                          Dim openCount = trades.Where(Function(t) t.IsOpen).Count()
                                          StatusText = $"{trades.Count} trade(s) loaded" &
                                                       If(openCount > 0, $" · {openCount} open", String.Empty)
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
        Public Property IsOpen As Boolean
        Public Property IsRecovered As Boolean

        Public ReadOnly Property PnLColor As String
            Get
                If IsOpen Then Return "#AAAAAA"
                Return If(PnLRaw > 0D, "#4CAF50", If(PnLRaw < 0D, "#EF5350", "#AAAAAA"))
            End Get
        End Property

        Public ReadOnly Property DirectionColor As String
            Get
                Return If(Direction = "Long", "#4CAF50", "#EF5350")
            End Get
        End Property

        Public ReadOnly Property RowOpacity As String
            Get
                Return If(IsOpen, "0.7", "1.0")
            End Get
        End Property

        Public Sub New(t As LiveTradeRecord)
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
            ExitTime = If(t.ExitTime.HasValue, t.ExitTime.Value.LocalDateTime.ToString("HH:mm:ss"), If(t.IsOpen, "Open", "—"))
            Duration = FormatDuration(t.EntryTime, t.ExitTime)
            EntryPrice = If(t.EntryPrice <> 0D, t.EntryPrice.ToString("F4"), "—")
            ExitPrice = If(t.ExitPrice.HasValue, t.ExitPrice.Value.ToString("F4"), If(t.IsOpen, "Live", "—"))

            PnLRaw = If(t.PnL.HasValue, t.PnL.Value, 0D)
            If t.IsOpen Then
                PnLDisplay = "…"
            ElseIf t.PnL.HasValue Then
                PnLDisplay = $"{If(t.PnL.Value >= 0D, "+", "")}${t.PnL.Value:F2}"
            Else
                PnLDisplay = "—"
            End If

            Commission = $"-${t.CommissionUsd:F2}"
            Fees = $"-${t.FeesUsd:F2}"
            ExitReason = t.ExitReason
            IsOpen = t.IsOpen
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
