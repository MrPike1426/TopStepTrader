Imports System.Collections.ObjectModel
Imports System.Windows
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' Open orders, order history, and place-new-order form.
    ''' </summary>
    Public Class OrderBookViewModel
        Inherits ViewModelBase

        Private ReadOnly _orderService   As IOrderService
        Private ReadOnly _accountService As IAccountService

        Private _accountId As Long = 0

        ' ── Bindable properties ──────────────────────────────────────────────

        Public ReadOnly Property OpenOrders   As New ObservableCollection(Of OrderRowVm)()
        Public ReadOnly Property FilledOrders As New ObservableCollection(Of OrderRowVm)()

        Private _selectedOrder As OrderRowVm
        Public Property SelectedOrder As OrderRowVm
            Get
                Return _selectedOrder
            End Get
            Set(value As OrderRowVm)
                SetProperty(_selectedOrder, value)
            End Set
        End Property

        ' Place-order form fields
        Private _newContractId As String = ""
        Public Property NewContractId As String
            Get
                Return _newContractId
            End Get
            Set(value As String)
                SetProperty(_newContractId, value)
            End Set
        End Property

        Private _newQuantity As String = "1"
        Public Property NewQuantity As String
            Get
                Return _newQuantity
            End Get
            Set(value As String)
                SetProperty(_newQuantity, value)
            End Set
        End Property

        Private _selectedSide As OrderSide = OrderSide.Buy
        Public Property SelectedSide As OrderSide
            Get
                Return _selectedSide
            End Get
            Set(value As OrderSide)
                SetProperty(_selectedSide, value)
            End Set
        End Property

        Private _statusText As String = "Ready"
        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Set(value As String)
                SetProperty(_statusText, value)
            End Set
        End Property

        Public ReadOnly Property OrderSides As OrderSide() = {OrderSide.Buy, OrderSide.Sell}

        ' ── Commands ─────────────────────────────────────────────────────────

        Public ReadOnly Property PlaceBuyCommand    As RelayCommand
        Public ReadOnly Property PlaceSellCommand   As RelayCommand
        Public ReadOnly Property CancelOrderCommand As RelayCommand
        Public ReadOnly Property RefreshCommand     As RelayCommand

        ' ── Constructor ──────────────────────────────────────────────────────

        Public Sub New(orderService As IOrderService, accountService As IAccountService)
            _orderService   = orderService
            _accountService = accountService

            PlaceBuyCommand    = New RelayCommand(Sub() ExecutePlaceOrder(OrderSide.Buy))
            PlaceSellCommand   = New RelayCommand(Sub() ExecutePlaceOrder(OrderSide.Sell))
            CancelOrderCommand = New RelayCommand(AddressOf ExecuteCancelOrder,
                                                  Function() _selectedOrder IsNot Nothing)
            RefreshCommand     = New RelayCommand(AddressOf LoadOrders)

            AddHandler _orderService.OrderFilled,   AddressOf OnOrderFilled
            AddHandler _orderService.OrderRejected, AddressOf OnOrderRejected
        End Sub

        Public Sub LoadDataAsync()
            Task.Run(AddressOf LoadAccountThenOrders)
        End Sub

        Private Async Function LoadAccountThenOrders() As Task
            Try
                Dim accounts = Await _accountService.GetActiveAccountsAsync()
                Dim first = accounts.FirstOrDefault()
                If first IsNot Nothing Then _accountId = first.Id
                LoadOrders()
            Catch ex As Exception
                Dispatch(Sub() StatusText = $"Account error: {ex.Message}")
            End Try
        End Function

        Private Sub LoadOrders()
            If _accountId = 0 Then Return
            Task.Run(Async Function()
                         Try
                             Dim open = Await _orderService.GetOpenOrdersAsync(_accountId)
                             Dim filled = Await _orderService.GetOrderHistoryAsync(
                                 _accountId, DateTime.Today, DateTime.Now)
                             Dispatch(Sub()
                                          OpenOrders.Clear()
                                          For Each o In open
                                              OpenOrders.Add(New OrderRowVm(o))
                                          Next
                                          FilledOrders.Clear()
                                          For Each o In filled.Where(Function(x) x.Status = OrderStatus.Filled)
                                              FilledOrders.Add(New OrderRowVm(o))
                                          Next
                                          StatusText = $"{OpenOrders.Count} open  |  {FilledOrders.Count} filled today"
                                      End Sub)
                         Catch ex As Exception
                             Dispatch(Sub() StatusText = $"Error: {ex.Message}")
                         End Try
                     End Function)
        End Sub

        Private Sub ExecutePlaceOrder(side As OrderSide)
            Dim contractId = _newContractId.Trim()
            Dim qty As Integer
            If String.IsNullOrEmpty(contractId) Then
                StatusText = "Invalid Contract ID" : Return
            End If
            If Not Integer.TryParse(_newQuantity.Trim(), qty) OrElse qty <= 0 Then
                StatusText = "Invalid quantity" : Return
            End If

            Dim order As New Order With {
                .AccountId  = _accountId,
                .ContractId = contractId,
                .Side       = side,
                .OrderType  = OrderType.Market,
                .Quantity   = qty,
                .Status     = OrderStatus.Pending,
                .PlacedAt   = DateTimeOffset.UtcNow
            }

            Task.Run(Async Function()
                         Try
                             Dispatch(Sub() StatusText = "Placing order...")
                             Dim placed = Await _orderService.PlaceOrderAsync(order)
                             Dispatch(Sub()
                                          StatusText = $"Order {placed.Id} placed ({side})"
                                          LoadOrders()
                                      End Sub)
                         Catch ex As Exception
                             Dispatch(Sub() StatusText = $"Place error: {ex.Message}")
                         End Try
                     End Function)
        End Sub

        Private Sub ExecuteCancelOrder(param As Object)
            If _selectedOrder Is Nothing Then Return
            Dim orderId = _selectedOrder.OrderId
            Task.Run(Async Function()
                         Try
                             Dim ok = Await _orderService.CancelOrderAsync(orderId)
                             Dispatch(Sub()
                                          StatusText = If(ok, $"Order {orderId} cancelled", "Cancel failed")
                                          LoadOrders()
                                      End Sub)
                         Catch ex As Exception
                             Dispatch(Sub() StatusText = $"Cancel error: {ex.Message}")
                         End Try
                     End Function)
        End Sub

        Private Sub OnOrderFilled(sender As Object, e As OrderFilledEventArgs)
            Dispatch(Sub()
                         StatusText = $"Order {e.Order.Id} FILLED @ {e.Order.FillPrice:F2}"
                         LoadOrders()
                     End Sub)
        End Sub

        Private Sub OnOrderRejected(sender As Object, e As OrderRejectedEventArgs)
            Dispatch(Sub() StatusText = $"Order {e.Order.Id} REJECTED: {e.Reason}")
        End Sub

        Private Sub Dispatch(action As Action)
            If Application.Current?.Dispatcher IsNot Nothing Then
                Application.Current.Dispatcher.Invoke(action)
            End If
        End Sub

    End Class

    ''' <summary>View-friendly wrapper around Order.</summary>
    Public Class OrderRowVm
        Public Property OrderId    As Long
        Public Property ContractId As String
        Public Property Side       As String
        Public Property Qty        As Integer
        Public Property Status     As String
        Public Property Price      As String
        Public Property PlacedAt   As String
        Public Property FilledAt   As String
        Public Property FillPrice  As String

        Public ReadOnly Property SideColor As String
            Get
                Return If(Side = "Buy", "BuyBrush", "SellBrush")
            End Get
        End Property

        Public Sub New(o As Order)
            OrderId    = o.Id
            ContractId = o.ContractId
            Side       = o.Side.ToString()
            Qty        = o.Quantity
            Status     = o.Status.ToString()
            Price      = If(o.LimitPrice.HasValue, o.LimitPrice.Value.ToString("F2"), "Market")
            PlacedAt   = o.PlacedAt.LocalDateTime.ToString("HH:mm:ss")
            FilledAt   = If(o.FilledAt.HasValue, o.FilledAt.Value.LocalDateTime.ToString("HH:mm:ss"), "—")
            FillPrice  = If(o.FillPrice.HasValue, o.FillPrice.Value.ToString("F2"), "—")
        End Sub
    End Class

End Namespace
