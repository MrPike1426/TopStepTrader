Imports System.Threading
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.API.Hubs
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Market
Imports Xunit

Namespace TopStepTrader.Tests.Services.Market

    ''' <summary>
    ''' PERF-02 F4a: covers F1 (quote-flow guard skips REST call) and F2 (BarPollPeriod = 10 s).
    ''' </summary>
    Public Class LivePnLServiceTests

        ' ── Stubs ───────────────────────────────────────────────────────────────

        Private Class StubQuoteFeed
            Implements IMarketQuoteFeed

            Public Event QuoteReceived As EventHandler(Of MarketQuoteEventArgs) Implements IMarketQuoteFeed.QuoteReceived

            Public Function SubscribeContractAsync(contractId As String,
                                                    Optional cancel As CancellationToken = Nothing) As Task _
                Implements IMarketQuoteFeed.SubscribeContractAsync
                Return Task.CompletedTask
            End Function

            Public Function UnsubscribeContractAsync(contractId As String,
                                                      Optional cancel As CancellationToken = Nothing) As Task _
                Implements IMarketQuoteFeed.UnsubscribeContractAsync
                Return Task.CompletedTask
            End Function

            Public Sub RaiseQuote(q As Quote)
                RaiseEvent QuoteReceived(Me, New MarketQuoteEventArgs(q))
            End Sub
        End Class

        Private Class StubBarIngestionService
            Implements IBarIngestionService

            Public CallCount As Integer = 0

            Public Function GetLatestPriceAsync(contractId As String,
                                                  Optional cancel As CancellationToken = Nothing) As Task(Of Decimal) _
                Implements IBarIngestionService.GetLatestPriceAsync
                CallCount += 1
                Return Task.FromResult(100D)
            End Function

            Public Function IngestAsync(contractId As String, timeframe As BarTimeframe,
                                          Optional barsToFetch As Integer = 500,
                                          Optional cancel As CancellationToken = Nothing) As Task(Of Integer) _
                Implements IBarIngestionService.IngestAsync
                Return Task.FromResult(0)
            End Function

            Public Function GetBarsForMLAsync(contractId As String, timeframe As BarTimeframe,
                                                Optional maxBars As Integer = 200,
                                                Optional cancel As CancellationToken = Nothing) As Task(Of IList(Of MarketBar)) _
                Implements IBarIngestionService.GetBarsForMLAsync
                Return Task.FromResult(Of IList(Of MarketBar))(New List(Of MarketBar)())
            End Function

            Public Function GetLiveBarsAsync(contractId As String, timeframe As BarTimeframe, barCount As Integer,
                                               Optional cancel As CancellationToken = Nothing,
                                               Optional live As Boolean = False) As Task(Of IList(Of MarketBar)) _
                Implements IBarIngestionService.GetLiveBarsAsync
                Return Task.FromResult(Of IList(Of MarketBar))(New List(Of MarketBar)())
            End Function
        End Class

        Private Class StubServiceProvider
            Implements IServiceProvider

            Private ReadOnly _bar As IBarIngestionService

            Public Sub New(bar As IBarIngestionService)
                _bar = bar
            End Sub

            Public Function GetService(serviceType As Type) As Object Implements IServiceProvider.GetService
                If serviceType = GetType(IBarIngestionService) Then Return _bar
                Return Nothing
            End Function
        End Class

        Private Class StubServiceScope
            Implements IServiceScope

            Private ReadOnly _provider As IServiceProvider

            Public Sub New(provider As IServiceProvider)
                _provider = provider
            End Sub

            Public ReadOnly Property ServiceProvider As IServiceProvider Implements IServiceScope.ServiceProvider
                Get
                    Return _provider
                End Get
            End Property

            Public Sub Dispose() Implements IDisposable.Dispose
            End Sub
        End Class

        Private Class StubServiceScopeFactory
            Implements IServiceScopeFactory

            Private ReadOnly _provider As IServiceProvider

            Public Sub New(provider As IServiceProvider)
                _provider = provider
            End Sub

            Public Function CreateScope() As IServiceScope Implements IServiceScopeFactory.CreateScope
                Return New StubServiceScope(_provider)
            End Function
        End Class

        ' ── Tests ───────────────────────────────────────────────────────────────

        <Fact>
        Public Async Function PollBarFallbackAsync_SkipsRestCall_WhenQuotesFlowing() As Task
            Dim barSvc = New StubBarIngestionService()
            Dim factory = New StubServiceScopeFactory(New StubServiceProvider(barSvc))
            Dim feed = New StubQuoteFeed()
            Dim svc = New LivePnLService(feed, factory, NullLogger(Of LivePnLService).Instance)

            Using svc.SubscribePrice("MNQ", Sub(t) End Sub)
                ' ContractId must contain "MNQ" for root-substring match in OnQuoteReceived.
                feed.RaiseQuote(New Quote With {.ContractId = "CON.F.US.MNQ.U26", .LastPrice = 22000D})

                ' Fail-fast: confirm the quote was registered before testing the guard.
                Dim state = svc.GetRootStateForTests("MNQ")
                Assert.True(state.GetQuotesPerSec5s(DateTime.UtcNow) > 0,
                            "Quote not recorded — ContractId must contain the root substring 'MNQ'.")

                Await svc.PollBarFallbackAsync("MNQ")

                Assert.Equal(0, barSvc.CallCount)
                Assert.Equal(DateTime.MinValue, state.LastBarUtc)
                Assert.Equal(0, state.BarFetchZeroCount)
            End Using
        End Function

        <Fact>
        Public Async Function PollBarFallbackAsync_CallsRestApi_WhenNoQuotesInWindow() As Task
            Dim barSvc = New StubBarIngestionService()
            Dim factory = New StubServiceScopeFactory(New StubServiceProvider(barSvc))
            Dim feed = New StubQuoteFeed()
            Dim svc = New LivePnLService(feed, factory, NullLogger(Of LivePnLService).Instance)

            Using svc.SubscribePrice("MNQ", Sub(t) End Sub)
                ' No quote events — GetQuotesPerSec5s returns 0.
                Await svc.PollBarFallbackAsync("MNQ")

                Assert.Equal(1, barSvc.CallCount)
            End Using
        End Function

        <Fact>
        Public Sub BarPollPeriod_IsTenSeconds()
            Assert.Equal(TimeSpan.FromSeconds(10), LivePnLService.BarPollPeriod)
        End Sub

    End Class

End Namespace
