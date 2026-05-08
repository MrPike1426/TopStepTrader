Imports System.Collections.Concurrent
Imports System.Threading
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.API.Hubs
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Trading

Namespace TopStepTrader.Services.Market

    ''' <summary>
    ''' ARCH-06 — Singleton, per-instrument live price + unrealised P&amp;L feed.
    '''
    ''' Consolidates the three view-local copies of MarketHub plumbing
    ''' (StrategyExecutionEngine, SuperTrendPlusViewModel, TestTradeViewModel) behind
    ''' one shared service.  Internals:
    '''
    '''  • One <see cref="IMarketQuoteFeed.QuoteReceived"/> handler at the service level,
    '''    attached on first subscription, detached when the last subscription drops.
    '''  • Per-root state keyed by canonical <c>PxRootSymbol</c> with subscription ref count.
    '''  • 2-second bar-poll timer per active root via <see cref="IBarIngestionService.GetLatestPriceAsync"/>.
    '''  • 30-second entry-price self-correction timer per subscription.
    '''  • Volatile.Read/Write on the cached quote price (Decimal stored as Double).
    '''  • Quotes filtered by exact <c>ContractId</c> OR root-symbol substring (roll-safe).
    '''  • Bid/ask mid fallback when <c>LastPrice = 0</c>.
    ''' </summary>
    Public Class LivePnLService
        Implements ILivePnLService
        Implements IDisposable

        Private ReadOnly _feed As IMarketQuoteFeed
        Private ReadOnly _scopeFactory As IServiceScopeFactory
        Private ReadOnly _logger As ILogger(Of LivePnLService)

        Private ReadOnly _roots As New ConcurrentDictionary(Of String, RootState)(StringComparer.OrdinalIgnoreCase)
        ''' <summary>0 = quote handler not attached; 1 = attached.</summary>
        Private _handlerAttached As Integer = 0
        Private _disposed As Boolean = False

        ''' <summary>Bar-poll period (matches StrategyExecutionEngine 2-second cadence).</summary>
        Public Shared ReadOnly BarPollPeriod As TimeSpan = TimeSpan.FromSeconds(2)
        ''' <summary>Entry-correction poll period.</summary>
        Public Shared ReadOnly EntryCorrectionPeriod As TimeSpan = TimeSpan.FromSeconds(30)
        ''' <summary>Window during which a quote is considered fresh (preferred over bar).</summary>
        Public Shared ReadOnly QuoteFreshWindow As TimeSpan = TimeSpan.FromSeconds(2)

        Public Sub New(feed As IMarketQuoteFeed,
                       scopeFactory As IServiceScopeFactory,
                       logger As ILogger(Of LivePnLService))
            _feed = feed
            _scopeFactory = scopeFactory
            _logger = logger
        End Sub

        ' ── Public API ─────────────────────────────────────────────────────────

        Public Function Subscribe(contractId As String,
                                  entryPrice As Decimal,
                                  signedSize As Integer,
                                  onTick As Action(Of LivePnLTick),
                                  Optional accountId As Long = 0) As IDisposable Implements ILivePnLService.Subscribe
            If onTick Is Nothing Then Throw New ArgumentNullException(NameOf(onTick))
            Return AddSubscription(contractId, entryPrice, signedSize, onTick, Nothing, accountId)
        End Function

        Public Function SubscribePrice(contractId As String,
                                       onTick As Action(Of LivePriceTick)) As IDisposable Implements ILivePnLService.SubscribePrice
            If onTick Is Nothing Then Throw New ArgumentNullException(NameOf(onTick))
            Return AddSubscription(contractId, 0D, 0, Nothing, onTick, 0)
        End Function

        ''' <summary>
        ''' Test/poll hook: simulates the 2-second bar-poll timer for a given root.
        ''' Production code wires this to a System.Threading.Timer in <see cref="EnsureRootStarted"/>.
        ''' Safe to invoke from tests directly.
        ''' </summary>
        Public Async Function PollBarFallbackAsync(rootKey As String,
                                                    Optional cancel As CancellationToken = Nothing) As Task
            Dim state As RootState = Nothing
            If Not _roots.TryGetValue(NormalizeRoot(rootKey), state) Then Return
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim ingestion = scope.ServiceProvider.GetRequiredService(Of IBarIngestionService)()
                    Dim price = Await ingestion.GetLatestPriceAsync(state.RootKey, cancel)
                    If price > 0D Then
                        state.LastBarPrice = price
                        state.LastBarUtc = DateTime.UtcNow
                        EmitFromState(state)
                    End If
                End Using
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                _logger?.LogDebug(ex, "LivePnLService: bar fallback fetch failed for {Root}", state.RootKey)
            End Try
        End Function

        ''' <summary>
        ''' Test/poll hook: runs the 30-second entry-price self-correction sweep.
        ''' Looks up <see cref="IOrderService.GetLivePositionSnapshotAsync"/> and rewrites
        ''' the cached entry price for any subscription whose seed differs by &gt; 1 tick.
        ''' </summary>
        Public Async Function CheckEntryCorrectionsAsync(Optional cancel As CancellationToken = Nothing) As Task
            For Each state In _roots.Values
                Dim subs = state.SnapshotSubscriptions()
                For Each subItem In subs
                    If subItem.AccountId = 0 OrElse subItem.PnLCallback Is Nothing Then Continue For
                    Try
                        Dim snap As Core.Models.LivePositionSnapshot = Nothing
                        Using scope = _scopeFactory.CreateScope()
                            Dim orderSvc = scope.ServiceProvider.GetRequiredService(Of IOrderService)()
                            snap = Await orderSvc.GetLivePositionSnapshotAsync(
                                subItem.AccountId, state.PxContractId, Nothing, cancel)
                        End Using
                        If snap Is Nothing OrElse snap.OpenRate <= 0D Then Continue For
                        Dim tickSize = If(state.TickSize > 0D, state.TickSize, 0.0001D)
                        If Math.Abs(snap.OpenRate - subItem.EntryPrice) > tickSize Then
                            Dim oldPrice = subItem.EntryPrice
                            subItem.EntryPrice = snap.OpenRate
                            subItem.EntryCorrected = True
                            _logger?.LogInformation("📌 Entry corrected {Old:F5} → {New:F5}",
                                                    oldPrice, snap.OpenRate)
                        End If
                    Catch ex As OperationCanceledException
                        Throw
                    Catch ex As Exception
                        _logger?.LogDebug(ex, "LivePnLService: entry correction failed for {Root}", state.RootKey)
                    End Try
                Next
                EmitFromState(state)
            Next
        End Function

        ''' <summary>For tests: returns true when at least one subscription exists for the root.</summary>
        Public Function HasSubscribers(rootKey As String) As Boolean
            Dim state As RootState = Nothing
            If Not _roots.TryGetValue(NormalizeRoot(rootKey), state) Then Return False
            Return state.RefCount > 0
        End Function

        ' ── Internals ──────────────────────────────────────────────────────────

        Private Function AddSubscription(contractId As String,
                                         entryPrice As Decimal,
                                         signedSize As Integer,
                                         pnlCallback As Action(Of LivePnLTick),
                                         priceCallback As Action(Of LivePriceTick),
                                         accountId As Long) As IDisposable
            If String.IsNullOrWhiteSpace(contractId) Then Throw New ArgumentException("contractId required")

            Dim fav = FavouriteContracts.TryGetBySymbolResolved(contractId)
            Dim rootKey = NormalizeRoot(If(fav?.PxRootSymbol, contractId))

            Dim state = _roots.GetOrAdd(rootKey,
                Function(k) New RootState(k, fav))

            Dim subItem As New Subscription(state, AddressOf OnSubscriptionDisposed) With {
                .EntryPrice = entryPrice,
                .SignedSize = signedSize,
                .PnLCallback = pnlCallback,
                .PriceCallback = priceCallback,
                .AccountId = accountId
            }
            state.AddSubscription(subItem)

            EnsureRootStarted(state)
            ' Emit one initial tick so the consumer sees current state immediately when available.
            EmitFromState(state)
            Return subItem
        End Function

        Private Sub EnsureRootStarted(state As RootState)
            If Interlocked.CompareExchange(_handlerAttached, 1, 0) = 0 Then
                AddHandler _feed.QuoteReceived, AddressOf OnQuoteReceived
            End If
            If Not state.MarketHubSubscribed AndAlso Not String.IsNullOrEmpty(state.PxContractId) Then
                state.MarketHubSubscribed = True
                Dim id = state.PxContractId
                Task.Run(Async Function() As Task
                             Try
                                 Await _feed.SubscribeContractAsync(id)
                             Catch ex As Exception
                                 _logger?.LogDebug(ex, "LivePnLService: SubscribeContractAsync failed for {Id}", id)
                             End Try
                         End Function)
            End If
            state.EnsureBarTimer(AddressOf OnBarTimerTick)
            state.EnsureEntryTimer(AddressOf OnEntryTimerTick)
        End Sub

        Private Sub OnBarTimerTick(stateObj As Object)
            Dim state = TryCast(stateObj, RootState)
            If state Is Nothing Then Return
            Try
                PollBarFallbackAsync(state.RootKey).GetAwaiter().GetResult()
            Catch ex As Exception
                _logger?.LogDebug(ex, "LivePnLService: bar timer error for {Root}", state.RootKey)
            End Try
        End Sub

        Private Sub OnEntryTimerTick(stateObj As Object)
            Try
                CheckEntryCorrectionsAsync().GetAwaiter().GetResult()
            Catch ex As Exception
                _logger?.LogDebug(ex, "LivePnLService: entry timer error")
            End Try
        End Sub

        Private Sub OnQuoteReceived(sender As Object, e As MarketQuoteEventArgs)
            If e Is Nothing OrElse e.Quote Is Nothing Then Return
            Dim quoteId = If(e.Quote.ContractId, String.Empty)

            ' Match by exact contract ID or by root-symbol substring (roll-safe).
            For Each state In _roots.Values
                Dim exactMatch = Not String.IsNullOrEmpty(state.PxContractId) AndAlso
                                  String.Equals(quoteId, state.PxContractId, StringComparison.OrdinalIgnoreCase)
                Dim rootMatch = Not String.IsNullOrEmpty(state.RootKey) AndAlso
                                quoteId.IndexOf(state.RootKey, StringComparison.OrdinalIgnoreCase) >= 0
                If Not (exactMatch OrElse rootMatch) Then Continue For

                ' Prefer last-trade price; fall back to bid/ask mid when no trade has printed.
                Dim price As Decimal = e.Quote.LastPrice
                If price <= 0D AndAlso e.Quote.BidPrice > 0D AndAlso e.Quote.AskPrice > 0D Then
                    price = (e.Quote.BidPrice + e.Quote.AskPrice) / 2D
                End If
                If price <= 0D Then Continue For

                state.SetQuotePrice(price)
                EmitFromState(state)
            Next
        End Sub

        Private Sub EmitFromState(state As RootState)
            Dim now = DateTime.UtcNow
            Dim quotePrice = state.GetQuotePrice()
            Dim quoteUtc = state.LastQuoteUtc
            Dim barPrice = state.LastBarPrice
            Dim barUtc = state.LastBarUtc

            ' Decide source: prefer fresh quote (within QuoteFreshWindow), else bar fallback.
            Dim usingQuote = quotePrice > 0D AndAlso (now - quoteUtc) <= QuoteFreshWindow
            Dim price As Decimal = 0D
            Dim source As LivePriceSource = LivePriceSource.None
            Dim ts As DateTime = now
            If usingQuote Then
                price = quotePrice
                source = LivePriceSource.Quote
                ts = quoteUtc
            ElseIf barPrice > 0D Then
                price = barPrice
                source = LivePriceSource.Bar
                ts = barUtc
            ElseIf quotePrice > 0D Then
                ' Quote is stale but it's all we have.
                price = quotePrice
                source = LivePriceSource.Quote
                ts = quoteUtc
            Else
                Return ' nothing to emit yet
            End If

            Dim ageMs = CLng(Math.Max(0, (now - ts).TotalMilliseconds))
            Dim subs = state.SnapshotSubscriptions()
            For Each subItem In subs
                If subItem.PriceCallback IsNot Nothing Then
                    Dim t As New LivePriceTick With {
                        .ContractId = state.RootKey,
                        .Price = price,
                        .Source = source,
                        .AgeMs = ageMs,
                        .TimestampUtc = now
                    }
                    Try : subItem.PriceCallback.Invoke(t) : Catch : End Try
                End If
                If subItem.PnLCallback IsNot Nothing Then
                    Dim dpp = state.DollarPerPoint
                    Dim pnl = ComputePnL(price, subItem.EntryPrice, subItem.SignedSize, dpp)
                    Dim t As New LivePnLTick With {
                        .ContractId = state.RootKey,
                        .Price = price,
                        .Source = source,
                        .AgeMs = ageMs,
                        .TimestampUtc = now,
                        .EntryPrice = subItem.EntryPrice,
                        .Size = subItem.SignedSize,
                        .UnrealisedPnL = pnl,
                        .DollarPerPoint = dpp
                    }
                    Try : subItem.PnLCallback.Invoke(t) : Catch : End Try
                End If
            Next
        End Sub

        ''' <summary>
        ''' P&amp;L = (current − entry) × signedSize × DollarPerPoint
        ''' (signedSize already encodes direction: positive when price rises for a long, etc.)
        ''' </summary>
        Friend Shared Function ComputePnL(currentPrice As Decimal,
                                          entryPrice As Decimal,
                                          signedSize As Integer,
                                          dollarPerPoint As Decimal) As Decimal
            If currentPrice <= 0D OrElse entryPrice <= 0D OrElse signedSize = 0 Then Return 0D
            Return Math.Round((currentPrice - entryPrice) * signedSize * dollarPerPoint, 2)
        End Function

        Private Shared Function NormalizeRoot(s As String) As String
            Return If(s, String.Empty).Trim().ToUpperInvariant()
        End Function

        Friend Sub OnSubscriptionDisposed(subItem As Subscription)
            Dim state = subItem.State
            If state Is Nothing Then Return
            Dim remaining = state.RemoveSubscription(subItem)
            If remaining > 0 Then Return
            ' Last subscriber for this root — tear down hub subscription + timers.
            If state.MarketHubSubscribed AndAlso Not String.IsNullOrEmpty(state.PxContractId) Then
                state.MarketHubSubscribed = False
                Dim id = state.PxContractId
                Task.Run(Async Function() As Task
                             Try
                                 Await _feed.UnsubscribeContractAsync(id)
                             Catch ex As Exception
                                 _logger?.LogDebug(ex, "LivePnLService: UnsubscribeContractAsync failed for {Id}", id)
                             End Try
                         End Function)
            End If
            state.DisposeTimers()
            Dim removed As RootState = Nothing
            _roots.TryRemove(state.RootKey, removed)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            If Interlocked.Exchange(_handlerAttached, 0) = 1 Then
                Try : RemoveHandler _feed.QuoteReceived, AddressOf OnQuoteReceived : Catch : End Try
            End If
            For Each state In _roots.Values
                state.DisposeTimers()
            Next
            _roots.Clear()
        End Sub

        ' ── Per-root cache ─────────────────────────────────────────────────────

        Friend Class RootState
            Public ReadOnly RootKey As String
            Public ReadOnly PxContractId As String
            Public ReadOnly TickSize As Decimal
            Public ReadOnly DollarPerPoint As Decimal

            Private ReadOnly _subs As New List(Of Subscription)
            Private ReadOnly _subsLock As New Object()
            Private _lastQuotePriceDouble As Double = 0.0
            Public Property LastQuoteUtc As DateTime = DateTime.MinValue
            Public Property LastBarPrice As Decimal = 0D
            Public Property LastBarUtc As DateTime = DateTime.MinValue
            Public Property MarketHubSubscribed As Boolean = False
            Private _barTimer As Timer
            Private _entryTimer As Timer

            Public Sub New(rootKey As String, fav As FavouriteContract)
                Me.RootKey = rootKey
                If fav IsNot Nothing Then
                    Me.PxContractId = If(fav.PxContractId, String.Empty)
                    Me.TickSize = fav.PxTickSize
                    Dim ts = If(fav.PxTickSize > 0D, fav.PxTickSize, 0.0001D)
                    Dim tv = If(fav.PxTickValue > 0D, fav.PxTickValue, 0D)
                    Me.DollarPerPoint = If(ts > 0D AndAlso tv > 0D, tv / ts, 0D)
                Else
                    Me.PxContractId = String.Empty
                    Me.TickSize = 0D
                    Me.DollarPerPoint = 0D
                End If
            End Sub

            Public ReadOnly Property RefCount As Integer
                Get
                    SyncLock _subsLock
                        Return _subs.Count
                    End SyncLock
                End Get
            End Property

            Public Sub AddSubscription(subItem As Subscription)
                SyncLock _subsLock
                    _subs.Add(subItem)
                End SyncLock
            End Sub

            Public Function RemoveSubscription(subItem As Subscription) As Integer
                SyncLock _subsLock
                    _subs.Remove(subItem)
                    Return _subs.Count
                End SyncLock
            End Function

            Public Function SnapshotSubscriptions() As Subscription()
                SyncLock _subsLock
                    Return _subs.ToArray()
                End SyncLock
            End Function

            Public Sub SetQuotePrice(price As Decimal)
                Volatile.Write(_lastQuotePriceDouble, CDbl(price))
                LastQuoteUtc = DateTime.UtcNow
            End Sub

            Public Function GetQuotePrice() As Decimal
                Dim raw = Volatile.Read(_lastQuotePriceDouble)
                Return If(raw > 0.0, CDec(raw), 0D)
            End Function

            Public Sub EnsureBarTimer(cb As TimerCallback)
                If _barTimer IsNot Nothing Then Return
                _barTimer = New Timer(cb, Me, BarPollPeriod, BarPollPeriod)
            End Sub

            Public Sub EnsureEntryTimer(cb As TimerCallback)
                If _entryTimer IsNot Nothing Then Return
                _entryTimer = New Timer(cb, Me, EntryCorrectionPeriod, EntryCorrectionPeriod)
            End Sub

            Public Sub DisposeTimers()
                _barTimer?.Dispose()
                _barTimer = Nothing
                _entryTimer?.Dispose()
                _entryTimer = Nothing
            End Sub
        End Class

        Friend Class Subscription
            Implements IDisposable

            Public ReadOnly State As RootState
            Public Property EntryPrice As Decimal
            Public Property SignedSize As Integer
            Public Property AccountId As Long
            Public Property PnLCallback As Action(Of LivePnLTick)
            Public Property PriceCallback As Action(Of LivePriceTick)
            Public Property EntryCorrected As Boolean = False
            Private ReadOnly _onDispose As Action(Of Subscription)
            Private _disposed As Integer = 0

            Public Sub New(state As RootState, onDispose As Action(Of Subscription))
                Me.State = state
                _onDispose = onDispose
            End Sub

            Public Sub Dispose() Implements IDisposable.Dispose
                If Interlocked.Exchange(_disposed, 1) = 1 Then Return
                _onDispose?.Invoke(Me)
            End Sub
        End Class

    End Class

End Namespace
