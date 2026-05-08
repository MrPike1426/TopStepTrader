Imports System.Collections.ObjectModel
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' ARCH-06 — Scalper Test view model.
    '''
    ''' Minimal validation harness for <see cref="ILivePnLService"/>.  Places one bracket
    ''' BUY/SELL via <see cref="IOrderService"/>, subscribes to the shared live-price /
    ''' P&amp;L feed for the duration of the position, and surfaces price source + age
    ''' so the service can be validated end-to-end against the TopStepX Positions tab.
    '''
    ''' Deliberately omits everything else from TestTradeView (AI, ATR tiers, personas,
    ''' Claude analysis, force-close threshold) — this tab is flat trade placement +
    ''' display only.
    ''' </summary>
    Public Class ScalperTestViewModel
        Inherits ViewModelBase
        Implements IDisposable

        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _accountService As IAccountService
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _livePnL As ILivePnLService

        Private _activeSubscription As IDisposable
        Private _disposed As Boolean = False

        Public Sub New(orderService As IOrderService,
                       accountService As IAccountService,
                       session As ITradingSessionContext,
                       livePnL As ILivePnLService)
            _orderService = orderService
            _accountService = accountService
            _session = session
            _livePnL = livePnL

            Contracts = New ObservableCollection(Of String) From {"MNQ", "MES", "MGC", "M6E", "OIL"}
            SelectedContract = "MNQ"

            BuyCommand = New RelayCommand(Sub() PlaceTradeFireAndForget(OrderSide.Buy),
                                          Function() CanTrade())
            SellCommand = New RelayCommand(Sub() PlaceTradeFireAndForget(OrderSide.Sell),
                                           Function() CanTrade())
            CloseCommand = New RelayCommand(AddressOf CloseFireAndForget,
                                            Function() HasActivePosition)
            ClearLogCommand = New RelayCommand(Sub() Dispatch(Sub() DiagnosticLog.Clear()))
        End Sub

        ' ── Bindable state ───────────────────────────────────────────────────

        Public Property Accounts As New ObservableCollection(Of Account)()
        Public Property Contracts As ObservableCollection(Of String)
        Public Property DiagnosticLog As New ObservableCollection(Of String)()

        Private _selectedAccount As Account
        Public Property SelectedAccount As Account
            Get
                Return _selectedAccount
            End Get
            Set(value As Account)
                If SetProperty(_selectedAccount, value) Then
                    RelayCommand.RaiseCanExecuteChanged()
                End If
            End Set
        End Property

        Private _selectedContract As String = "MNQ"
        Public Property SelectedContract As String
            Get
                Return _selectedContract
            End Get
            Set(value As String)
                If SetProperty(_selectedContract, value) Then
                    NotifyPropertyChanged(NameOf(TickSizeHint))
                    RelayCommand.RaiseCanExecuteChanged()
                End If
            End Set
        End Property

        Private _size As Integer = 1
        Public Property Size As Integer
            Get
                Return _size
            End Get
            Set(value As Integer)
                SetProperty(_size, Math.Max(1, value))
            End Set
        End Property

        Private _slTicks As Integer = 20
        Public Property SlTicks As Integer
            Get
                Return _slTicks
            End Get
            Set(value As Integer)
                SetProperty(_slTicks, Math.Max(1, value))
            End Set
        End Property

        Private _tpTicks As Integer = 40
        Public Property TpTicks As Integer
            Get
                Return _tpTicks
            End Get
            Set(value As Integer)
                SetProperty(_tpTicks, Math.Max(1, value))
            End Set
        End Property

        Private _currentPrice As Decimal = 0D
        Public Property CurrentPrice As Decimal
            Get
                Return _currentPrice
            End Get
            Set(value As Decimal)
                If SetProperty(_currentPrice, value) Then NotifyPropertyChanged(NameOf(CurrentPriceText))
            End Set
        End Property

        Private _entryPrice As Decimal = 0D
        Public Property EntryPrice As Decimal
            Get
                Return _entryPrice
            End Get
            Set(value As Decimal)
                If SetProperty(_entryPrice, value) Then NotifyPropertyChanged(NameOf(EntryPriceText))
            End Set
        End Property

        Private _entryCorrected As Boolean = False
        Public Property EntryCorrected As Boolean
            Get
                Return _entryCorrected
            End Get
            Set(value As Boolean)
                SetProperty(_entryCorrected, value)
            End Set
        End Property

        Private _unrealisedPnL As Decimal = 0D
        Public Property UnrealisedPnL As Decimal
            Get
                Return _unrealisedPnL
            End Get
            Set(value As Decimal)
                If SetProperty(_unrealisedPnL, value) Then
                    NotifyPropertyChanged(NameOf(UnrealisedPnLText))
                    NotifyPropertyChanged(NameOf(IsPnLPositive))
                End If
            End Set
        End Property

        Private _priceSource As String = "—"
        Public Property PriceSource As String
            Get
                Return _priceSource
            End Get
            Set(value As String)
                SetProperty(_priceSource, value)
            End Set
        End Property

        Private _priceAgeMs As Long = 0
        Public Property PriceAgeMs As Long
            Get
                Return _priceAgeMs
            End Get
            Set(value As Long)
                SetProperty(_priceAgeMs, value)
            End Set
        End Property

        Private _hasActivePosition As Boolean = False
        Public Property HasActivePosition As Boolean
            Get
                Return _hasActivePosition
            End Get
            Set(value As Boolean)
                If SetProperty(_hasActivePosition, value) Then RelayCommand.RaiseCanExecuteChanged()
            End Set
        End Property

        Private _status As String = "Idle"
        Public Property Status As String
            Get
                Return _status
            End Get
            Set(value As String)
                SetProperty(_status, value)
            End Set
        End Property

        ' ── Display projections ──────────────────────────────────────────────

        Public ReadOnly Property CurrentPriceText As String
            Get
                If CurrentPrice <= 0D Then Return "—"
                Return CurrentPrice.ToString(PriceFormat())
            End Get
        End Property

        Public ReadOnly Property EntryPriceText As String
            Get
                If EntryPrice <= 0D Then Return "—"
                Return EntryPrice.ToString(PriceFormat())
            End Get
        End Property

        Public ReadOnly Property UnrealisedPnLText As String
            Get
                Return $"{If(UnrealisedPnL >= 0D, "+", "")}${UnrealisedPnL:F2}"
            End Get
        End Property

        Public ReadOnly Property IsPnLPositive As Boolean
            Get
                Return UnrealisedPnL >= 0D
            End Get
        End Property

        Public ReadOnly Property TickSizeHint As String
            Get
                Dim fav = FavouriteContracts.TryGetBySymbol(SelectedContract)
                If fav Is Nothing Then Return ""
                Return $"tick={fav.PxTickSize:0.####}  ${fav.PxTickValue:0.00}/tick"
            End Get
        End Property

        ' ── Commands ─────────────────────────────────────────────────────────

        Public Property BuyCommand As RelayCommand
        Public Property SellCommand As RelayCommand
        Public Property CloseCommand As RelayCommand
        Public Property ClearLogCommand As RelayCommand

        ' ── Lifecycle ────────────────────────────────────────────────────────

        Public Async Function LoadDataAsync() As Task
            Try
                Dim list = Await _accountService.GetActiveAccountsAsync()
                Dispatch(Sub()
                             Accounts.Clear()
                             For Each a In list
                                 Accounts.Add(a)
                             Next
                             If Accounts.Count > 0 Then
                                 Dim sessionAcc = _session.SelectedAccount
                                 Dim preferred = If(sessionAcc IsNot Nothing,
                                     Accounts.FirstOrDefault(Function(a) a.Id = sessionAcc.Id),
                                     Nothing)
                                 SelectedAccount = If(preferred, Accounts.FirstOrDefault(
                                     Function(a) a.Name IsNot Nothing AndAlso
                                                 a.Name.StartsWith("PRAC", StringComparison.OrdinalIgnoreCase)))
                                 If SelectedAccount Is Nothing Then SelectedAccount = Accounts(0)
                                 Log($"Loaded {Accounts.Count} account(s); selected {SelectedAccount.DisplayName}")
                             Else
                                 Log("No accounts returned. Check API Keys.")
                             End If
                         End Sub)
            Catch ex As Exception
                Dispatch(Sub() Log($"Account load error: {ex.Message}"))
            End Try
        End Function

        ' ── Trade placement ──────────────────────────────────────────────────

        Private Function CanTrade() As Boolean
            Return SelectedAccount IsNot Nothing AndAlso
                   Not String.IsNullOrWhiteSpace(SelectedContract) AndAlso
                   Not HasActivePosition
        End Function

        Private Sub PlaceTradeFireAndForget(side As OrderSide)
            Task.Run(Async Function() As Task
                         Try
                             Await PlaceTradeAsync(side)
                         Catch ex As Exception
                             Dispatch(Sub() Log($"Place error: {ex.Message}"))
                         End Try
                     End Function)
        End Sub

        Private Async Function PlaceTradeAsync(side As OrderSide) As Task
            If Not CanTrade() Then Return

            Dim fav = FavouriteContracts.TryGetBySymbolResolved(SelectedContract)
            Dim pxId = If(fav IsNot Nothing AndAlso Not String.IsNullOrEmpty(fav.PxContractId),
                          fav.PxContractId, SelectedContract)
            Dim qty = Math.Max(1, Size)
            Dim sl = Math.Max(1, SlTicks)
            Dim tp = Math.Max(1, TpTicks)
            Dim sideLabel = side.ToString().ToUpperInvariant()

            Dispatch(Sub()
                         Status = $"Placing {sideLabel} {qty}x {pxId} (SL={sl}t TP={tp}t)..."
                         Log(Status)
                     End Sub)

            Dim order As New Order With {
                .AccountId = SelectedAccount.Id,
                .Broker = BrokerType.TopStepX,
                .ContractId = pxId,
                .Side = side,
                .Quantity = qty,
                .OrderType = OrderType.Market,
                .InitialStopTicks = sl,
                .InitialTakeProfitTicks = tp,
                .Status = OrderStatus.Pending,
                .PlacedAt = DateTimeOffset.UtcNow,
                .Notes = $"Scalper Test — {sideLabel}"
            }

            Dim placed = Await _orderService.PlaceOrderAsync(order)
            If placed.Status = OrderStatus.Rejected Then
                Dispatch(Sub()
                             Status = $"REJECTED: {placed.Notes}"
                             Log(Status)
                         End Sub)
                Return
            End If

            Dim signed = If(side = OrderSide.Buy, qty, -qty)
            Dim seedEntry = If(placed.FillPrice.HasValue AndAlso placed.FillPrice.Value > 0D,
                               placed.FillPrice.Value, CurrentPrice)
            BeginLiveSubscription(seedEntry, signed)
            Dispatch(Sub()
                         Status = $"Open: {sideLabel} {qty}x  Order #{placed.ExternalOrderId}"
                         Log(Status)
                         HasActivePosition = True
                         EntryPrice = seedEntry
                         EntryCorrected = False
                     End Sub)
        End Function

        Private Sub CloseFireAndForget()
            Task.Run(Async Function() As Task
                         Try
                             Await CloseAsync()
                         Catch ex As Exception
                             Dispatch(Sub() Log($"Close error: {ex.Message}"))
                         End Try
                     End Function)
        End Sub

        Private Async Function CloseAsync() As Task
            If SelectedAccount Is Nothing Then Return
            Dim fav = FavouriteContracts.TryGetBySymbolResolved(SelectedContract)
            Dim pxId = If(fav IsNot Nothing AndAlso Not String.IsNullOrEmpty(fav.PxContractId),
                          fav.PxContractId, SelectedContract)
            Dispatch(Sub() Log($"Flatten {pxId}..."))
            Try
                Await _orderService.FlattenContractAsync(SelectedAccount.Id, pxId)
            Finally
                EndLiveSubscription()
                Dispatch(Sub()
                             HasActivePosition = False
                             Status = "Flat"
                             Log(Status)
                         End Sub)
            End Try
        End Function

        ' ── Live subscription ────────────────────────────────────────────────

        Private Sub BeginLiveSubscription(seedEntry As Decimal, signedSize As Integer)
            EndLiveSubscription()
            Dim acctId = If(SelectedAccount IsNot Nothing, SelectedAccount.Id, 0L)
            _activeSubscription = _livePnL.Subscribe(SelectedContract, seedEntry, signedSize,
                                                     AddressOf OnLiveTick,
                                                     accountId:=acctId)
        End Sub

        Private Sub EndLiveSubscription()
            Dim s = Interlocked.Exchange(_activeSubscription, Nothing)
            If s IsNot Nothing Then
                Try : s.Dispose() : Catch : End Try
            End If
        End Sub

        Private Sub OnLiveTick(t As LivePnLTick)
            Dim corrected = (t.EntryPrice <> EntryPrice AndAlso EntryPrice > 0D)
            Dispatch(Sub()
                         CurrentPrice = t.Price
                         If corrected Then
                             Log($"📌 Entry corrected {EntryPrice:F5} -> {t.EntryPrice:F5}")
                             EntryCorrected = True
                         End If
                         EntryPrice = t.EntryPrice
                         UnrealisedPnL = t.UnrealisedPnL
                         PriceSource = t.Source.ToString()
                         PriceAgeMs = t.AgeMs
                         Log($"[{t.Source}] price={t.Price.ToString(PriceFormat())} age={t.AgeMs}ms pnl=${t.UnrealisedPnL:F2}")
                     End Sub)
        End Sub

        ' ── Helpers ──────────────────────────────────────────────────────────

        Private Function PriceFormat() As String
            Dim fav = FavouriteContracts.TryGetBySymbol(SelectedContract)
            If fav Is Nothing OrElse fav.PxTickSize <= 0D Then Return "0.00"
            ' Number of decimals from tick size: 0.0001 -> 4, 0.01 -> 2, 0.10 -> 2 etc.
            Dim decimals = Math.Max(0, BitConverter.GetBytes(Decimal.GetBits(fav.PxTickSize)(3))(2))
            Return "0." & New String("0"c, Math.Max(2, decimals))
        End Function

        Private Sub Log(message As String)
            Dim line = $"{DateTime.Now:HH:mm:ss.fff}  {message}"
            DiagnosticLog.Add(line)
            ' Cap buffer
            While DiagnosticLog.Count > 200
                DiagnosticLog.RemoveAt(0)
            End While
        End Sub

        Private Shared Sub Dispatch(action As Action)
            Dim app = Application.Current
            If app IsNot Nothing AndAlso app.Dispatcher IsNot Nothing AndAlso Not app.Dispatcher.CheckAccess() Then
                app.Dispatcher.BeginInvoke(action)
            Else
                action()
            End If
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            EndLiveSubscription()
        End Sub

    End Class

End Namespace
