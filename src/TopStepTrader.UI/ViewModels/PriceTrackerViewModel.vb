Imports System.Threading
Imports System.Windows
Imports System.Windows.Threading
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' FEAT-53 — Price Tracker view model.
    '''
    ''' Minimal harness for verifying <see cref="ILivePnLService"/> against TopStepX:
    ''' subscribes to MBT (Micro Bitcoin — 24/7), shows current price + source + age,
    ''' captures an entry on demand and surfaces unrealised P&amp;L vs that entry.
    ''' No order placement, no broker calls, no AI gate.
    ''' </summary>
    Public Class PriceTrackerViewModel
        Inherits ViewModelBase
        Implements IDisposable

        Public Const TrackedSymbol As String = "MBT"
        Private Const TickSize As Decimal = 5D
        Private Const TickValue As Decimal = 0.5D
        Private Const DollarPerPoint As Decimal = 0.1D

        Private ReadOnly _livePnL As ILivePnLService
        Private _priceSubscription As IDisposable
        Private _pnlSubscription As IDisposable
        Private _diagTimer As DispatcherTimer
        Private _disposed As Boolean = False

        Public Sub New(livePnL As ILivePnLService)
            _livePnL = livePnL
            CaptureEntryCommand = New RelayCommand(AddressOf CaptureEntry,
                                                   Function() CurrentPrice > 0D AndAlso Not HasEntry)
            ResetCommand = New RelayCommand(AddressOf Reset, Function() HasEntry)
        End Sub

        ''' <summary>Hardcoded — this harness only tracks MBT (24/7 weekend liquidity).</summary>
        Public ReadOnly Property Symbol As String = TrackedSymbol

        ' ── Bindable state ───────────────────────────────────────────────────

        Private _currentPrice As Decimal = 0D
        Public Property CurrentPrice As Decimal
            Get
                Return _currentPrice
            End Get
            Set(value As Decimal)
                If SetProperty(_currentPrice, value) Then
                    NotifyPropertyChanged(NameOf(CurrentPriceText))
                    NotifyPropertyChanged(NameOf(TickDelta))
                    NotifyPropertyChanged(NameOf(TickDeltaText))
                    RelayCommand.RaiseCanExecuteChanged()
                End If
            End Set
        End Property

        Private _entryPrice As Decimal = 0D
        Public Property EntryPrice As Decimal
            Get
                Return _entryPrice
            End Get
            Set(value As Decimal)
                If SetProperty(_entryPrice, value) Then
                    NotifyPropertyChanged(NameOf(EntryPriceText))
                    NotifyPropertyChanged(NameOf(HasEntry))
                    NotifyPropertyChanged(NameOf(TickDelta))
                    NotifyPropertyChanged(NameOf(TickDeltaText))
                    RelayCommand.RaiseCanExecuteChanged()
                End If
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

        ' ── Diagnostic strip (mirrors ScalperTestViewModel) ──────────────────

        Private _hubStateText As String = "Unknown"
        Public Property HubStateText As String
            Get
                Return _hubStateText
            End Get
            Set(value As String)
                SetProperty(_hubStateText, value)
            End Set
        End Property

        Private _subscribedContractIdText As String = "—"
        Public Property SubscribedContractIdText As String
            Get
                Return _subscribedContractIdText
            End Get
            Set(value As String)
                SetProperty(_subscribedContractIdText, value)
            End Set
        End Property

        Private _quotesPerSecText As String = "0.0/s"
        Public Property QuotesPerSecText As String
            Get
                Return _quotesPerSecText
            End Get
            Set(value As String)
                SetProperty(_quotesPerSecText, value)
            End Set
        End Property

        Private _lastQuoteAgeText As String = "—"
        Public Property LastQuoteAgeText As String
            Get
                Return _lastQuoteAgeText
            End Get
            Set(value As String)
                SetProperty(_lastQuoteAgeText, value)
            End Set
        End Property

        Private _lastBarAgeText As String = "—"
        Public Property LastBarAgeText As String
            Get
                Return _lastBarAgeText
            End Get
            Set(value As String)
                SetProperty(_lastBarAgeText, value)
            End Set
        End Property

        Private _barZeroCount As Integer = 0
        Public Property BarZeroCount As Integer
            Get
                Return _barZeroCount
            End Get
            Set(value As Integer)
                If SetProperty(_barZeroCount, value) Then
                    NotifyPropertyChanged(NameOf(IsStaleFeed))
                End If
            End Set
        End Property

        Public ReadOnly Property IsStaleFeed As Boolean
            Get
                Return _barZeroCount >= 5
            End Get
        End Property

        ' ── Display projections ──────────────────────────────────────────────

        Public ReadOnly Property HasEntry As Boolean
            Get
                Return _entryPrice > 0D
            End Get
        End Property

        Public ReadOnly Property TickDelta As Integer
            Get
                If Not HasEntry Then Return 0
                Return CInt(Math.Truncate((_currentPrice - _entryPrice) / TickSize))
            End Get
        End Property

        Public ReadOnly Property TickDeltaText As String
            Get
                Dim d = TickDelta
                Return $"{If(d >= 0, "+", "")}{d}"
            End Get
        End Property

        Public ReadOnly Property CurrentPriceText As String
            Get
                If _currentPrice <= 0D Then Return "—"
                Return _currentPrice.ToString("0.0")
            End Get
        End Property

        Public ReadOnly Property EntryPriceText As String
            Get
                If Not HasEntry Then Return "—"
                Return _entryPrice.ToString("0.0")
            End Get
        End Property

        Public ReadOnly Property UnrealisedPnLText As String
            Get
                Return $"{If(_unrealisedPnL >= 0D, "+", "")}${_unrealisedPnL:F2}"
            End Get
        End Property

        Public ReadOnly Property IsPnLPositive As Boolean
            Get
                Return _unrealisedPnL >= 0D
            End Get
        End Property

        ' ── Commands ─────────────────────────────────────────────────────────

        Public Property CaptureEntryCommand As RelayCommand
        Public Property ResetCommand As RelayCommand

        ' ── Lifecycle ────────────────────────────────────────────────────────

        ''' <summary>Starts the price-only subscription and the diagnostic poll timer.</summary>
        Public Sub LoadAsync()
            BeginPriceSubscription()
            Dispatch(AddressOf StartDiagnosticTimer)
        End Sub

        Private Sub CaptureEntry()
            If _currentPrice <= 0D OrElse HasEntry Then Return
            Dim entry = _currentPrice
            EndPriceSubscription()
            EntryPrice = entry
            BeginPnLSubscription(entry)
        End Sub

        Private Sub Reset()
            If Not HasEntry Then Return
            EndPnLSubscription()
            EntryPrice = 0D
            UnrealisedPnL = 0D
            BeginPriceSubscription()
        End Sub

        ' ── Subscriptions ────────────────────────────────────────────────────

        Private Sub BeginPriceSubscription()
            EndPriceSubscription()
            _priceSubscription = _livePnL.SubscribePrice(TrackedSymbol, AddressOf OnPriceTick)
        End Sub

        Private Sub EndPriceSubscription()
            Dim s = Interlocked.Exchange(_priceSubscription, Nothing)
            If s IsNot Nothing Then
                Try : s.Dispose() : Catch : End Try
            End If
        End Sub

        Private Sub BeginPnLSubscription(entry As Decimal)
            EndPnLSubscription()
            ' accountId=0 disables entry-price self-correction — we want the captured
            ' entry preserved exactly so the user can verify pnl ≈ TickDelta × $0.50.
            _pnlSubscription = _livePnL.Subscribe(TrackedSymbol, entry, +1,
                                                   AddressOf OnPnLTick, accountId:=0L)
        End Sub

        Private Sub EndPnLSubscription()
            Dim s = Interlocked.Exchange(_pnlSubscription, Nothing)
            If s IsNot Nothing Then
                Try : s.Dispose() : Catch : End Try
            End If
        End Sub

        Private Sub OnPriceTick(t As LivePriceTick)
            If t.Source = LivePriceSource.None Then Return
            Dispatch(Sub()
                         CurrentPrice = t.Price
                         PriceSource = t.Source.ToString()
                         PriceAgeMs = t.AgeMs
                     End Sub)
        End Sub

        Private Sub OnPnLTick(t As LivePnLTick)
            Dim metadataOnly = (t.Source = LivePriceSource.None)
            Dispatch(Sub()
                         If metadataOnly Then
                             ' Metadata-only tick (entry correction before any quote/bar):
                             ' do NOT overwrite the displayed price/source/age.
                             UnrealisedPnL = t.UnrealisedPnL
                         Else
                             CurrentPrice = t.Price
                             PriceSource = t.Source.ToString()
                             PriceAgeMs = t.AgeMs
                             UnrealisedPnL = t.UnrealisedPnL
                         End If
                     End Sub)
        End Sub

        ' ── Diagnostic timer ─────────────────────────────────────────────────

        Private Sub StartDiagnosticTimer()
            If _diagTimer IsNot Nothing Then Return
            _diagTimer = New DispatcherTimer With {.Interval = TimeSpan.FromSeconds(1)}
            AddHandler _diagTimer.Tick, AddressOf OnDiagTick
            _diagTimer.Start()
            OnDiagTick(Nothing, EventArgs.Empty)
        End Sub

        Private Sub StopDiagnosticTimer()
            If _diagTimer Is Nothing Then Return
            _diagTimer.Stop()
            RemoveHandler _diagTimer.Tick, AddressOf OnDiagTick
            _diagTimer = Nothing
        End Sub

        Private Sub OnDiagTick(sender As Object, e As EventArgs)
            Dim diag = _livePnL.GetDiagnostics(TrackedSymbol)
            HubStateText = If(String.IsNullOrEmpty(diag.SubscribeError),
                              diag.HubState,
                              $"{diag.HubState} — Subscribe failed: {diag.SubscribeError}")
            SubscribedContractIdText = If(String.IsNullOrEmpty(diag.SubscribedContractId),
                                          "—", diag.SubscribedContractId)
            QuotesPerSecText = $"{diag.QuotesPerSec5s:0.0}/s"
            LastQuoteAgeText = FormatAge(diag.LastQuoteUtc)
            LastBarAgeText = FormatAge(diag.LastBarUtc)
            BarZeroCount = diag.BarFetchZeroCount
        End Sub

        Private Shared Function FormatAge(utc As DateTime) As String
            If utc = DateTime.MinValue Then Return "—"
            Dim ms = (DateTime.UtcNow - utc).TotalMilliseconds
            If ms < 0 Then ms = 0
            If ms < 1000 Then Return $"{CInt(ms)} ms"
            Return $"{ms / 1000.0:0.0} s"
        End Function

        ' ── Helpers / disposal ───────────────────────────────────────────────

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
            EndPriceSubscription()
            EndPnLSubscription()
            Dispatch(AddressOf StopDiagnosticTimer)
        End Sub

    End Class

End Namespace
