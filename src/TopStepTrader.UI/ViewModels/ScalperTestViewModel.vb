Imports System.Collections.ObjectModel
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Threading
Imports Microsoft.AspNetCore.SignalR.Client
Imports TopStepTrader.API.Hubs
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
        Private ReadOnly _marketHub As MarketHubClient

        Private _activeSubscription As IDisposable
        Private _activeContractId As String
        Private _disposed As Boolean = False
        Private _diagTimer As DispatcherTimer
        Private _brokerTimer As DispatcherTimer
        Private _lastBrokerSnapshotUtc As DateTime = DateTime.MinValue

        ''' <summary>
        ''' Number of <see cref="IOrderService.GetLivePositionSnapshotAsync"/> attempts after
        ''' <see cref="IOrderService.PlaceOrderAsync"/> returns. Mirrors the engine's
        ''' <c>_syncMissCount</c> retry pattern so seedEntry never falls back to a
        ''' (potentially zero) <c>placed.FillPrice</c>.
        ''' </summary>
        Private Const SeedSnapshotAttempts As Integer = 3
        Private Shared ReadOnly SeedSnapshotDelay As TimeSpan = TimeSpan.FromMilliseconds(500)

        Public Sub New(orderService As IOrderService,
                       accountService As IAccountService,
                       session As ITradingSessionContext,
                       livePnL As ILivePnLService,
                       Optional marketHub As MarketHubClient = Nothing)
            _orderService = orderService
            _accountService = accountService
            _session = session
            _livePnL = livePnL
            _marketHub = marketHub

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

        ' ── ARCH-07 F4: Hub status strip bindings ────────────────────────────

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

        ' ── ARCH-07 F7: Broker reference panel bindings ──────────────────────

        Private _brokerOpenRate As Decimal = 0D
        Public Property BrokerOpenRate As Decimal
            Get
                Return _brokerOpenRate
            End Get
            Set(value As Decimal)
                If SetProperty(_brokerOpenRate, value) Then NotifyPropertyChanged(NameOf(BrokerOpenRateText))
            End Set
        End Property

        Private _brokerUnits As Decimal = 0D
        Public Property BrokerUnits As Decimal
            Get
                Return _brokerUnits
            End Get
            Set(value As Decimal)
                If SetProperty(_brokerUnits, value) Then NotifyPropertyChanged(NameOf(BrokerUnitsText))
            End Set
        End Property

        Private _brokerIsBuy As Boolean = True
        Public Property BrokerIsBuy As Boolean
            Get
                Return _brokerIsBuy
            End Get
            Set(value As Boolean)
                If SetProperty(_brokerIsBuy, value) Then NotifyPropertyChanged(NameOf(BrokerSideText))
            End Set
        End Property

        Private _brokerUnrealisedPnL As Decimal = 0D
        Public Property BrokerUnrealisedPnL As Decimal
            Get
                Return _brokerUnrealisedPnL
            End Get
            Set(value As Decimal)
                If SetProperty(_brokerUnrealisedPnL, value) Then NotifyPropertyChanged(NameOf(BrokerPnLText))
            End Set
        End Property

        Private _brokerSnapshotAgeSec As Integer = 0
        Public Property BrokerSnapshotAgeSec As Integer
            Get
                Return _brokerSnapshotAgeSec
            End Get
            Set(value As Integer)
                If SetProperty(_brokerSnapshotAgeSec, value) Then NotifyPropertyChanged(NameOf(BrokerSnapshotAgeText))
            End Set
        End Property

        Private _hasBrokerSnapshot As Boolean = False
        Public Property HasBrokerSnapshot As Boolean
            Get
                Return _hasBrokerSnapshot
            End Get
            Set(value As Boolean)
                SetProperty(_hasBrokerSnapshot, value)
            End Set
        End Property

        Public ReadOnly Property BrokerOpenRateText As String
            Get
                If Not HasBrokerSnapshot OrElse BrokerOpenRate <= 0D Then Return "—"
                Return BrokerOpenRate.ToString(PriceFormat())
            End Get
        End Property

        Public ReadOnly Property BrokerUnitsText As String
            Get
                If Not HasBrokerSnapshot Then Return "—"
                Return BrokerUnits.ToString("0.##")
            End Get
        End Property

        Public ReadOnly Property BrokerSideText As String
            Get
                If Not HasBrokerSnapshot Then Return "—"
                Return If(BrokerIsBuy, "LONG", "SHORT")
            End Get
        End Property

        Public ReadOnly Property BrokerPnLText As String
            Get
                If Not HasBrokerSnapshot Then Return "—"
                Return $"{If(BrokerUnrealisedPnL >= 0D, "+", "")}${BrokerUnrealisedPnL:F2}"
            End Get
        End Property

        Public ReadOnly Property BrokerSnapshotAgeText As String
            Get
                If Not HasBrokerSnapshot Then Return "—"
                Return $"{BrokerSnapshotAgeSec}s ago"
            End Get
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

            ' ── ARCH-07 F5: eager warmup ─────────────────────────────────────
            ' Pre-resolve every favourite contract in the picker so the catalog
            ' is hot when BUY is clicked (mirrors HydraViewModel/AssetBassettViewModel
            ' warmup), then bring up MarketHubClient if it hasn't started yet.
            Try
                If Contracts IsNot Nothing Then
                    For Each sym In Contracts
                        Try
                            FavouriteContracts.TryGetBySymbolResolved(sym)
                        Catch ex As Exception
                            ' Non-fatal — log and continue with the rest of the list.
                            Dispatch(Sub() Log($"Catalog warmup failed for {sym}: {ex.Message}"))
                        End Try
                    Next
                End If
                If _marketHub IsNot Nothing AndAlso _marketHub.State <> HubConnectionState.Connected Then
                    Dispatch(Sub() Log($"MarketHub state={_marketHub.State}; starting..."))
                    Try
                        Await _marketHub.StartAsync()
                        Dispatch(Sub() Log($"MarketHub state={_marketHub.State}"))
                    Catch ex As Exception
                        Dispatch(Sub() Log($"MarketHub start error: {ex.Message}"))
                    End Try
                End If
            Catch ex As Exception
                Dispatch(Sub() Log($"Warmup error: {ex.Message}"))
            End Try

            ' Start the diagnostic poll once warmup is done so the user can see hub
            ' state on the status strip even before the first BUY click.
            Dispatch(AddressOf StartDiagnosticTimer)
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
            Dim sideLabel = side.ToString().ToUpperInvariant()

            Dispatch(Sub()
                         Status = $"Placing {sideLabel} {qty}x {pxId} (SL={sl}t)..."
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
                .InitialTakeProfitTicks = Nothing,
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

            ' Seed entry from the broker, not from placed.FillPrice (which TopStepX REST
            ' returns as null on PlaceOrder). Mirrors the StrategyExecutionEngine
            ' _syncMissCount pattern: poll GetLivePositionSnapshotAsync up to N times.
            Dim seedEntry = Await ResolveSeedEntryFromBrokerAsync(SelectedAccount.Id, pxId)

            ' Track the placement contract so CLOSE can flatten even if no subscription
            ' was started (e.g. the snapshot retries timed out) or if the user changes
            ' SelectedContract afterwards.
            _activeContractId = pxId

            If seedEntry <= 0D Then
                Dispatch(Sub()
                             Status = "Position not yet visible — retry"
                             Log(Status)
                             HasActivePosition = True
                             EntryPrice = 0D
                             EntryCorrected = False
                         End Sub)
                Return
            End If

            BeginLiveSubscription(seedEntry, signed)
            Dispatch(Sub()
                         Status = $"Open: {sideLabel} {qty}x  Order #{placed.ExternalOrderId}"
                         Log(Status)
                         HasActivePosition = True
                         EntryPrice = seedEntry
                         EntryCorrected = False
                     End Sub)
        End Function

        ''' <summary>
        ''' Polls the broker for the post-fill <c>OpenRate</c>, retrying up to
        ''' <see cref="SeedSnapshotAttempts"/> times. Returns 0 if every attempt
        ''' returns null or a non-positive rate — caller treats that as
        ''' "do not start the live subscription".
        ''' </summary>
        Friend Async Function ResolveSeedEntryFromBrokerAsync(accountId As Long,
                                                              pxId As String,
                                                              Optional delayOverride As TimeSpan? = Nothing) As Task(Of Decimal)
            Dim delay = If(delayOverride.HasValue, delayOverride.Value, SeedSnapshotDelay)
            For attempt = 1 To SeedSnapshotAttempts
                Dim attemptNo = attempt ' local copy for the lambda closure
                Try
                    Dim snap = Await _orderService.GetLivePositionSnapshotAsync(accountId, pxId)
                    If snap IsNot Nothing AndAlso snap.OpenRate > 0D Then Return snap.OpenRate
                Catch ex As Exception
                    Dim msg = ex.Message
                    Dispatch(Sub() Log($"Seed snapshot attempt {attemptNo}/{SeedSnapshotAttempts} error: {msg}"))
                End Try
                If attempt < SeedSnapshotAttempts AndAlso delay > TimeSpan.Zero Then
                    Await Task.Delay(delay)
                End If
            Next
            Return 0D
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
            Dim pxId = _activeContractId
            If String.IsNullOrEmpty(pxId) Then
                Dim fav = FavouriteContracts.TryGetBySymbolResolved(SelectedContract)
                pxId = If(fav IsNot Nothing AndAlso Not String.IsNullOrEmpty(fav.PxContractId),
                          fav.PxContractId, SelectedContract)
            End If
            Dispatch(Sub() Log($"Flatten {pxId}..."))
            Try
                Await _orderService.FlattenContractAsync(SelectedAccount.Id, pxId)
            Finally
                EndLiveSubscription()
                _activeContractId = Nothing
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
            Dispatch(AddressOf StartBrokerTimer)
        End Sub

        Private Sub EndLiveSubscription()
            Dim s = Interlocked.Exchange(_activeSubscription, Nothing)
            If s IsNot Nothing Then
                Try : s.Dispose() : Catch : End Try
            End If
            Dispatch(Sub()
                         StopBrokerTimer()
                         HasBrokerSnapshot = False
                     End Sub)
        End Sub

        ' ── ARCH-07 F4 / F7: status & broker reference timers ───────────────

        Private Sub StartDiagnosticTimer()
            If _diagTimer IsNot Nothing Then Return
            _diagTimer = New DispatcherTimer With {.Interval = TimeSpan.FromSeconds(1)}
            AddHandler _diagTimer.Tick, AddressOf OnDiagTick
            _diagTimer.Start()
            ' One immediate tick so the strip populates right away.
            OnDiagTick(Nothing, EventArgs.Empty)
        End Sub

        Private Sub StopDiagnosticTimer()
            If _diagTimer Is Nothing Then Return
            _diagTimer.Stop()
            RemoveHandler _diagTimer.Tick, AddressOf OnDiagTick
            _diagTimer = Nothing
        End Sub

        Private Sub OnDiagTick(sender As Object, e As EventArgs)
            Dim diag = _livePnL.GetDiagnostics(SelectedContract)
            HubStateText = If(String.IsNullOrEmpty(diag.SubscribeError),
                              diag.HubState,
                              $"{diag.HubState} — Subscribe failed: {diag.SubscribeError}")
            SubscribedContractIdText = If(String.IsNullOrEmpty(diag.SubscribedContractId), "—", diag.SubscribedContractId)
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

        Private Sub StartBrokerTimer()
            If _brokerTimer IsNot Nothing Then Return
            _brokerTimer = New DispatcherTimer With {.Interval = TimeSpan.FromSeconds(5)}
            AddHandler _brokerTimer.Tick, AddressOf OnBrokerTick
            _brokerTimer.Start()
            ' Kick off an immediate fetch so the panel populates without a 5 s lag.
            OnBrokerTick(Nothing, EventArgs.Empty)
        End Sub

        Private Sub StopBrokerTimer()
            If _brokerTimer Is Nothing Then Return
            _brokerTimer.Stop()
            RemoveHandler _brokerTimer.Tick, AddressOf OnBrokerTick
            _brokerTimer = Nothing
        End Sub

        Private Sub OnBrokerTick(sender As Object, e As EventArgs)
            ' Refresh "snapshot age" on every tick even if no new fetch lands.
            If HasBrokerSnapshot AndAlso _lastBrokerSnapshotUtc <> DateTime.MinValue Then
                BrokerSnapshotAgeSec = CInt(Math.Max(0, (DateTime.UtcNow - _lastBrokerSnapshotUtc).TotalSeconds))
            End If
            If Not HasActivePosition Then Return
            Dim acctId = If(SelectedAccount IsNot Nothing, SelectedAccount.Id, 0L)
            Dim pxId = _activeContractId
            If acctId = 0 OrElse String.IsNullOrEmpty(pxId) Then Return
            Task.Run(Async Function() As Task
                         Try
                             Dim snap = Await _orderService.GetLivePositionSnapshotAsync(acctId, pxId)
                             Dispatch(Sub() ApplyBrokerSnapshot(snap))
                         Catch ex As Exception
                             Dispatch(Sub() Log($"Broker snapshot error: {ex.Message}"))
                         End Try
                     End Function)
        End Sub

        Private Sub ApplyBrokerSnapshot(snap As LivePositionSnapshot)
            If snap Is Nothing Then
                HasBrokerSnapshot = False
                Return
            End If
            BrokerOpenRate = snap.OpenRate
            BrokerUnits = snap.Units
            BrokerIsBuy = snap.IsBuy
            BrokerUnrealisedPnL = snap.UnrealizedPnlUsd
            _lastBrokerSnapshotUtc = DateTime.UtcNow
            BrokerSnapshotAgeSec = 0
            HasBrokerSnapshot = True
        End Sub

        Private Sub OnLiveTick(t As LivePnLTick)
            Dim corrected = (t.EntryPrice <> EntryPrice AndAlso EntryPrice > 0D)
            Dim metadataOnly = (t.Source = LivePriceSource.None)
            Dispatch(Sub()
                         If corrected Then
                             Log($"📌 Entry corrected {EntryPrice:F5} -> {t.EntryPrice:F5}")
                             EntryCorrected = True
                         End If
                         EntryPrice = t.EntryPrice
                         UnrealisedPnL = t.UnrealisedPnL
                         If metadataOnly Then
                             ' Metadata-only tick (entry correction before any quote/bar):
                             ' do NOT overwrite the displayed price/source/age.
                             Log($"[None] entry={t.EntryPrice.ToString(PriceFormat())} pnl=${t.UnrealisedPnL:F2}")
                         Else
                             CurrentPrice = t.Price
                             PriceSource = t.Source.ToString()
                             PriceAgeMs = t.AgeMs
                             Log($"[{t.Source}] price={t.Price.ToString(PriceFormat())} age={t.AgeMs}ms pnl=${t.UnrealisedPnL:F2}")
                         End If
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
            Dispatch(Sub()
                         StopDiagnosticTimer()
                         StopBrokerTimer()
                     End Sub)
        End Sub

    End Class

End Namespace
