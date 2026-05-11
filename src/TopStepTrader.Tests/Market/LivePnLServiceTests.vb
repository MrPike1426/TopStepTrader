Imports System.Collections.Concurrent
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging.Abstractions
Imports Moq
Imports TopStepTrader.API.Hubs
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Market
Imports Xunit

Namespace TopStepTrader.Tests.Market

    ''' <summary>
    ''' ARCH-06 — unit tests for <see cref="LivePnLService"/>.
    ''' Covers the ten validation cases listed in the ticket: quote source,
    ''' bar fallback, quote resumption, bid/ask mid fallback, root-symbol
    ''' roll match, long/short P&amp;L sign, entry-price self-correction, and
    ''' ref-counted MarketHub teardown.
    ''' </summary>
    Public Class LivePnLServiceTests

        ' ── Test fakes ────────────────────────────────────────────────────────

        Private Class FakeMarketQuoteFeed
            Implements IMarketQuoteFeed

            Public Event QuoteReceived As EventHandler(Of MarketQuoteEventArgs) Implements IMarketQuoteFeed.QuoteReceived

            Public ReadOnly Subscribed As New ConcurrentBag(Of String)
            Public ReadOnly Unsubscribed As New ConcurrentBag(Of String)
            ''' <summary>When set, SubscribeContractAsync throws this exception (Bug C regression).</summary>
            Public SubscribeException As Exception = Nothing

            Public Function SubscribeContractAsync(contractId As String,
                                                    Optional cancel As CancellationToken = Nothing) As Task Implements IMarketQuoteFeed.SubscribeContractAsync
                If SubscribeException IsNot Nothing Then
                    Return Task.FromException(SubscribeException)
                End If
                Subscribed.Add(contractId)
                Return Task.CompletedTask
            End Function

            Public Function UnsubscribeContractAsync(contractId As String,
                                                      Optional cancel As CancellationToken = Nothing) As Task Implements IMarketQuoteFeed.UnsubscribeContractAsync
                Unsubscribed.Add(contractId)
                Return Task.CompletedTask
            End Function

            Public Sub Raise(q As Quote)
                RaiseEvent QuoteReceived(Me, New MarketQuoteEventArgs(q))
            End Sub
        End Class

        Private Class TestHarness
            Implements IDisposable

            Public ReadOnly Feed As New FakeMarketQuoteFeed()
            Public ReadOnly BarSvc As New Mock(Of IBarIngestionService)()
            Public ReadOnly OrderSvc As New Mock(Of IOrderService)()
            Public ReadOnly Provider As ServiceProvider
            Public ReadOnly Service As LivePnLService

            Public Sub New()
                Dim sc As New ServiceCollection()
                sc.AddScoped(Function(_sp) BarSvc.Object)
                sc.AddScoped(Function(_sp) OrderSvc.Object)
                Provider = sc.BuildServiceProvider()
                Service = New LivePnLService(
                    Feed,
                    Provider.GetRequiredService(Of IServiceScopeFactory)(),
                    NullLogger(Of LivePnLService).Instance)
            End Sub

            Public Sub Dispose() Implements IDisposable.Dispose
                Service.Dispose()
                Provider.Dispose()
            End Sub
        End Class

        Private Shared Function MakeQuote(contractId As String,
                                           Optional last As Decimal = 0D,
                                           Optional bid As Decimal = 0D,
                                           Optional ask As Decimal = 0D) As Quote
            Return New Quote With {
                .ContractId = contractId,
                .LastPrice = last,
                .BidPrice = bid,
                .AskPrice = ask,
                .Timestamp = DateTimeOffset.UtcNow
            }
        End Function

        ' ── Tests ─────────────────────────────────────────────────────────────

        <Fact>
        Public Sub Subscribe_FirstQuote_EmitsTickWithSourceQuote()
            Using h As New TestHarness()
                Dim received As LivePnLTick = Nothing
                Dim sub_ = h.Service.Subscribe("MGC", 2350.5D, 1, Sub(t) received = t)
                h.Feed.Raise(MakeQuote("CON.F.US.MGC.M26", last:=2351.0D))
                Assert.NotNull(received)
                Assert.Equal(LivePriceSource.Quote, received.Source)
                Assert.True(received.AgeMs <= 100, $"AgeMs={received.AgeMs}")
                Assert.Equal(2351.0D, received.Price)
                sub_.Dispose()
            End Using
        End Sub

        <Fact>
        Public Async Function Subscribe_NoQuoteForFiveSeconds_EmitsBarFallback() As Task
            Using h As New TestHarness()
                h.BarSvc.Setup(Function(b) b.GetLatestPriceAsync("MGC", It.IsAny(Of CancellationToken))).
                         ReturnsAsync(2349.8D)
                Dim received As LivePnLTick = Nothing
                Dim sub_ = h.Service.Subscribe("MGC", 2350.5D, 1, Sub(t) received = t)
                Await h.Service.PollBarFallbackAsync("MGC")
                Assert.NotNull(received)
                Assert.Equal(LivePriceSource.Bar, received.Source)
                Assert.Equal(2349.8D, received.Price)
                sub_.Dispose()
            End Using
        End Function

        <Fact>
        Public Async Function Subscribe_QuoteResumesAfterBar_ReturnsToQuoteSource() As Task
            Using h As New TestHarness()
                h.BarSvc.Setup(Function(b) b.GetLatestPriceAsync("MGC", It.IsAny(Of CancellationToken))).
                         ReturnsAsync(2349.8D)
                Dim received As LivePnLTick = Nothing
                Dim sub_ = h.Service.Subscribe("MGC", 2350.5D, 1, Sub(t) received = t)
                Await h.Service.PollBarFallbackAsync("MGC")
                Assert.Equal(LivePriceSource.Bar, received.Source)
                h.Feed.Raise(MakeQuote("CON.F.US.MGC.M26", last:=2351.0D))
                Assert.Equal(LivePriceSource.Quote, received.Source)
                Assert.Equal(2351.0D, received.Price)
                sub_.Dispose()
            End Using
        End Function

        <Fact>
        Public Sub OnQuote_LastPriceZero_UsesBidAskMid()
            Using h As New TestHarness()
                Dim received As LivePnLTick = Nothing
                Dim sub_ = h.Service.Subscribe("M6E", 1.1780D, 1, Sub(t) received = t)
                h.Feed.Raise(MakeQuote("CON.F.US.M6E.U26", last:=0D, bid:=1.178D, ask:=1.1782D))
                Assert.NotNull(received)
                Assert.Equal(1.1781D, received.Price)
                sub_.Dispose()
            End Using
        End Sub

        <Fact>
        Public Sub OnQuote_RolledContractIdMatchesByRoot()
            Using h As New TestHarness()
                Dim received As LivePnLTick = Nothing
                Dim sub_ = h.Service.Subscribe("M6E", 1.1780D, 1, Sub(t) received = t)
                ' Cached PxContractId is the previous expiry (...U26).  Quote arrives
                ' for a different expiry (...M26) — the root-substring match must catch it.
                h.Feed.Raise(MakeQuote("CON.F.US.M6E.M26", last:=1.1790D))
                Assert.NotNull(received)
                Assert.Equal(1.1790D, received.Price)
                sub_.Dispose()
            End Using
        End Sub

        <Fact>
        Public Sub Pnl_LongPosition_PositiveDeltaPositivePnL()
            ' MGC: tickSize=0.10, tickValue=$1.00 -> $10/pt.  Long 2 @ 2350.50, current 2351.00
            ' (2351.00 - 2350.50) * +2 * $10 = $10.00
            Using h As New TestHarness()
                Dim received As LivePnLTick = Nothing
                Dim sub_ = h.Service.Subscribe("MGC", 2350.5D, 2, Sub(t) received = t)
                h.Feed.Raise(MakeQuote("CON.F.US.MGC.M26", last:=2351.0D))
                Assert.NotNull(received)
                Assert.Equal(10.0D, received.UnrealisedPnL)
                Assert.Equal(10.0D, received.DollarPerPoint)
                sub_.Dispose()
            End Using
        End Sub

        <Fact>
        Public Sub Pnl_ShortPosition_PositiveDeltaNegativePnL()
            ' MNQ: tickSize=0.25, tickValue=$0.50 -> $2/pt.  Short 1 @ 28860, current 28852
            ' (28852 - 28860) * -1 * $2 = $16.00 (short profits when price falls)
            Using h As New TestHarness()
                Dim received As LivePnLTick = Nothing
                Dim sub_ = h.Service.Subscribe("MNQ", 28860D, -1, Sub(t) received = t)
                h.Feed.Raise(MakeQuote("CON.F.US.MNQ.U26", last:=28852D))
                Assert.NotNull(received)
                Assert.Equal(16D, received.UnrealisedPnL)
                Assert.Equal(2D, received.DollarPerPoint)
                sub_.Dispose()
            End Using
        End Sub

        <Fact>
        Public Async Function EntryCorrection_BrokerVwapDiffersByMoreThanTick_RewritesEntry() As Task
            Using h As New TestHarness()
                h.OrderSvc.Setup(Function(o) o.GetLivePositionSnapshotAsync(
                                    It.IsAny(Of Long), It.IsAny(Of String),
                                    It.IsAny(Of Long?), It.IsAny(Of CancellationToken))).
                          ReturnsAsync(New LivePositionSnapshot With {.OpenRate = 1.1782D, .IsBuy = True, .Units = 1})

                Dim received As LivePnLTick = Nothing
                Dim sub_ = h.Service.Subscribe("M6E", 1.178D, 1, Sub(t) received = t, accountId:=42L)
                h.Feed.Raise(MakeQuote("CON.F.US.M6E.U26", last:=1.179D))
                Dim beforeEntry = received.EntryPrice
                Await h.Service.CheckEntryCorrectionsAsync()
                Assert.Equal(1.178D, beforeEntry)
                Assert.Equal(1.1782D, received.EntryPrice)
                sub_.Dispose()
            End Using
        End Function

        ''' <summary>
        ''' ARCH-07 Bug B regression: <see cref="LivePnLService.CheckEntryCorrectionsAsync"/>
        ''' fires before any quote/bar has arrived. The pre-fix
        ''' <c>EmitFromState</c> early-returned ("nothing to emit yet") so the
        ''' consumer never saw the corrected entry. After the fix it emits a
        ''' metadata-only tick (Source = None, Price = 0) carrying the corrected
        ''' EntryPrice.
        ''' </summary>
        <Fact>
        Public Async Function EntryCorrection_NoQuoteOrBarYet_EmitsMetadataOnlyTickWithCorrectedEntry() As Task
            Using h As New TestHarness()
                h.OrderSvc.Setup(Function(o) o.GetLivePositionSnapshotAsync(
                                    It.IsAny(Of Long), It.IsAny(Of String),
                                    It.IsAny(Of Long?), It.IsAny(Of CancellationToken))).
                          ReturnsAsync(New LivePositionSnapshot With {.OpenRate = 1.1782D, .IsBuy = True, .Units = 1})

                Dim received As LivePnLTick = Nothing
                ' entry=0, no quotes, no bars — the initial Subscribe-time emit is suppressed
                ' because price is 0; only the post-correction emit should land.
                Dim sub_ = h.Service.Subscribe("M6E", 0D, 1, Sub(t) received = t, accountId:=42L)
                Assert.Null(received)

                Await h.Service.CheckEntryCorrectionsAsync()

                Assert.NotNull(received)
                Assert.Equal(LivePriceSource.None, received.Source)
                Assert.Equal(0D, received.Price)
                Assert.Equal(0L, received.AgeMs)
                Assert.Equal(1.1782D, received.EntryPrice)
                sub_.Dispose()
            End Using
        End Function

        <Fact>
        Public Async Function Dispose_LastSubscriber_DetachesMarketHub() As Task
            Using h As New TestHarness()
                Dim sub_ = h.Service.Subscribe("MGC", 2350D, 1, Sub(t) Exit Sub)
                ' Subscribe is fire-and-forget via Task.Run; allow the scheduler to flush.
                Await Task.Delay(50)
                sub_.Dispose()
                Await Task.Delay(50)
                Assert.Contains("CON.F.US.MGC.M26", h.Feed.Unsubscribed)
                Assert.False(h.Service.HasSubscribers("MGC"))
            End Using
        End Function

        ''' <summary>
        ''' ARCH-07 Bug C regression: when <see cref="IMarketQuoteFeed.SubscribeContractAsync"/>
        ''' throws, <see cref="LivePnLService.GetDiagnostics"/> must surface the failure
        ''' (was a silent <c>LogDebug</c> before — UI showed no signal that quotes were
        ''' never wired up).
        ''' </summary>
        <Fact>
        Public Async Function GetDiagnostics_SubscribeThrows_SurfacesSubscribeError() As Task
            Using h As New TestHarness()
                h.Feed.SubscribeException = New InvalidOperationException("hub auth failed")
                Dim sub_ = h.Service.Subscribe("MGC", 2350D, 1, Sub(t) Exit Sub)
                ' Subscribe runs the underlying SubscribeContractAsync via Task.Run; let it land.
                Await Task.Delay(100)
                Dim diag = h.Service.GetDiagnostics("MGC")
                Assert.False(String.IsNullOrEmpty(diag.SubscribeError),
                             "SubscribeError should be populated when the feed throws")
                Assert.Contains("hub auth failed", diag.SubscribeError)
                sub_.Dispose()
            End Using
        End Function

        ''' <summary>
        ''' ARCH-07 Bug D regression: when the bar fallback returns 0 repeatedly,
        ''' diagnostics must reflect the consecutive zero-count and the ring buffer
        ''' must contain one <c>BarFetch=0</c> entry per failing poll. The pre-fix
        ''' service swallowed price = 0 silently — the harness sat blank for 3 minutes.
        ''' </summary>
        <Fact>
        Public Async Function GetDiagnostics_FiveZeroBarPolls_ZeroCountIsFiveAndRingBufferContainsEntries() As Task
            Using h As New TestHarness()
                h.BarSvc.Setup(Function(b) b.GetLatestPriceAsync("MGC", It.IsAny(Of CancellationToken))).
                         ReturnsAsync(0D)

                Dim sub_ = h.Service.Subscribe("MGC", 2350D, 1, Sub(t) Exit Sub)
                For i = 1 To 5
                    Await h.Service.PollBarFallbackAsync("MGC")
                Next

                Dim diag = h.Service.GetDiagnostics("MGC")
                Assert.Equal(5, diag.BarFetchZeroCount)
                Dim zeroEntries = Enumerable.Count(diag.RecentEvents,
                                                   Function(s) s.Contains("BarFetch=0"))
                Assert.Equal(5, zeroEntries)
                sub_.Dispose()
            End Using
        End Function

        <Fact>
        Public Async Function Dispose_OneOfTwoSubscribers_KeepsMarketHubSubscribed() As Task
            Using h As New TestHarness()
                Dim sub1 = h.Service.Subscribe("MGC", 2350D, 1, Sub(t) Exit Sub)
                Dim sub2 = h.Service.Subscribe("MGC", 2350D, 1, Sub(t) Exit Sub)
                Await Task.Delay(50)
                sub1.Dispose()
                Await Task.Delay(50)
                Assert.DoesNotContain("CON.F.US.MGC.M26", h.Feed.Unsubscribed)
                Assert.True(h.Service.HasSubscribers("MGC"))
                sub2.Dispose()
            End Using
        End Function

    End Class

End Namespace
