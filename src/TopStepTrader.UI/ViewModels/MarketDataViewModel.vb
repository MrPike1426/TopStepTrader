Imports System.Windows
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' Live market data — subscribe to a contract and see real-time bid/ask/last price.
    ''' </summary>
    Public Class MarketDataViewModel
        Inherits ViewModelBase

        Private ReadOnly _marketDataService As IMarketDataService
        Private ReadOnly _tradingSettings   As TradingSettings

        ' ── Bindable properties ──────────────────────────────────────────────

        Private _contractId As Integer = 0
        Public Property ContractId As Integer
            Get
                Return _contractId
            End Get
            Set(value As Integer)
                SetProperty(_contractId, value)
                OnPropertyChanged(NameOf(IsSubscribed))
            End Set
        End Property

        Private _contractIdText As String = ""
        Public Property ContractIdText As String
            Get
                Return _contractIdText
            End Get
            Set(value As String)
                SetProperty(_contractIdText, value)
            End Set
        End Property

        Public ReadOnly Property IsSubscribed As Boolean
            Get
                Return _contractId > 0 AndAlso _marketDataService.IsSubscribed(_contractId)
            End Get
        End Property

        Private _bidPrice As Decimal
        Public Property BidPrice As Decimal
            Get
                Return _bidPrice
            End Get
            Set(value As Decimal)
                SetProperty(_bidPrice, value)
            End Set
        End Property

        Private _askPrice As Decimal
        Public Property AskPrice As Decimal
            Get
                Return _askPrice
            End Get
            Set(value As Decimal)
                SetProperty(_askPrice, value)
            End Set
        End Property

        Private _lastPrice As Decimal
        Public Property LastPrice As Decimal
            Get
                Return _lastPrice
            End Get
            Set(value As Decimal)
                SetProperty(_lastPrice, value)
                OnPropertyChanged(NameOf(PriceChangeColor))
            End Set
        End Property

        Private _prevLastPrice As Decimal
        Private _volume As Long
        Public Property Volume As Long
            Get
                Return _volume
            End Get
            Set(value As Long)
                SetProperty(_volume, value)
            End Set
        End Property

        Private _priceChange As Decimal
        Public Property PriceChange As Decimal
            Get
                Return _priceChange
            End Get
            Set(value As Decimal)
                SetProperty(_priceChange, value)
            End Set
        End Property

        Private _updateTime As String = "—"
        Public Property UpdateTime As String
            Get
                Return _updateTime
            End Get
            Set(value As String)
                SetProperty(_updateTime, value)
            End Set
        End Property

        Private _statusText As String = "Enter a Contract ID and click Subscribe"
        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Set(value As String)
                SetProperty(_statusText, value)
            End Set
        End Property

        Public ReadOnly Property PriceChangeColor As String
            Get
                Return If(_lastPrice >= _prevLastPrice, "BuyBrush", "SellBrush")
            End Get
        End Property

        ' ── Commands ─────────────────────────────────────────────────────────

        Public ReadOnly Property SubscribeCommand   As RelayCommand
        Public ReadOnly Property UnsubscribeCommand As RelayCommand

        ' ── Constructor ──────────────────────────────────────────────────────

        Public Sub New(marketDataService As IMarketDataService,
                       tradingOptions As IOptions(Of TradingSettings))
            _marketDataService = marketDataService
            _tradingSettings   = tradingOptions.Value

            SubscribeCommand   = New RelayCommand(AddressOf ExecuteSubscribe,
                                                   Function() Not IsSubscribed AndAlso _contractId > 0)
            UnsubscribeCommand = New RelayCommand(AddressOf ExecuteUnsubscribe,
                                                   Function() IsSubscribed)

            AddHandler _marketDataService.QuoteReceived, AddressOf OnQuoteReceived
        End Sub

        ' ── Commands impl ────────────────────────────────────────────────────

        Private Sub ExecuteSubscribe()
            Dim id As Integer
            If Integer.TryParse(_contractIdText.Trim(), id) AndAlso id > 0 Then
                ContractId = id
                Task.Run(Async Function()
                             Try
                                 Await _marketDataService.SubscribeAsync(_contractId)
                                 Dispatch(Sub()
                                              StatusText = $"Subscribed to contract {_contractId}"
                                              OnPropertyChanged(NameOf(IsSubscribed))
                                          End Sub)
                             Catch ex As Exception
                                 Dispatch(Sub() StatusText = $"Subscribe error: {ex.Message}")
                             End Try
                         End Function)
            Else
                StatusText = "Please enter a valid integer Contract ID"
            End If
        End Sub

        Private Sub ExecuteUnsubscribe()
            Task.Run(Async Function()
                         Try
                             Await _marketDataService.UnsubscribeAsync(_contractId)
                             Dispatch(Sub()
                                          ContractId = 0
                                          BidPrice = 0
                                          AskPrice = 0
                                          LastPrice = 0
                                          Volume = 0
                                          StatusText = "Unsubscribed"
                                          OnPropertyChanged(NameOf(IsSubscribed))
                                      End Sub)
                         Catch ex As Exception
                             Dispatch(Sub() StatusText = $"Unsubscribe error: {ex.Message}")
                         End Try
                     End Function)
        End Sub

        ' ── Event handlers ───────────────────────────────────────────────────

        Private Sub OnQuoteReceived(sender As Object, e As QuoteEventArgs)
            If e.Quote.ContractId <> _contractId Then Return
            Dispatch(Sub()
                         _prevLastPrice = _lastPrice
                         BidPrice = e.Quote.BidPrice
                         AskPrice = e.Quote.AskPrice
                         If e.Quote.LastPrice > 0 Then LastPrice = e.Quote.LastPrice
                         Volume = e.Quote.Volume
                         PriceChange = LastPrice - _prevLastPrice
                         UpdateTime = DateTimeOffset.Now.LocalDateTime.ToString("HH:mm:ss.fff")
                         OnPropertyChanged(NameOf(PriceChangeColor))
                     End Sub)
        End Sub

        Private Sub Dispatch(action As Action)
            If Application.Current?.Dispatcher IsNot Nothing Then
                Application.Current.Dispatcher.Invoke(action)
            End If
        End Sub

    End Class

End Namespace
