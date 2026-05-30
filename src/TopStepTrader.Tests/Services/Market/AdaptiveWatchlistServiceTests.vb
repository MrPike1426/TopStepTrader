Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.Market
Imports Xunit

Namespace TopStepTrader.Tests.Services.Market

    ''' <summary>
    ''' FEAT-72: tests for <see cref="AdaptiveWatchlistService"/> and
    ''' <see cref="InstrumentOpportunityScorer"/>.
    '''
    ''' Uses a deterministic in-memory <see cref="IBarIngestionService"/> fake so the
    ''' score for each symbol is known up front, and an in-memory preferences fake so
    ''' settings can be mutated between scenarios without touching disk.
    ''' </summary>
    Public Class AdaptiveWatchlistServiceTests

        ''' <summary>F6-a — top-N selection. With <c>MaxSize = 5</c> and bars contrived so the
        ''' core seven score in a known order, only the top-5 root symbols appear.</summary>
        <Fact>
        Public Async Function TopN_SelectsTheHighestScorers() As Task
            Dim prefs = NewPrefs(enabled:=True, maxSize:=5)
            Dim bars = New FakeBarIngestionService()
            ' Engineer scores so MES > MNQ > MGC > MBT > M2K > MCLE > M6E > (extended low)
            bars.SetTrendingSeries("CON.F.US.MES.U26", strength:=1.0)
            bars.SetTrendingSeries("CON.F.US.MNQ.U26", strength:=0.9)
            bars.SetTrendingSeries("CON.F.US.MGC.M26", strength:=0.8)
            bars.SetTrendingSeries("CON.F.US.MBT.H26", strength:=0.7)
            bars.SetTrendingSeries("CON.F.US.M2K.U26", strength:=0.6)
            bars.SetTrendingSeries("CON.F.US.MCLE.M26", strength:=0.2)
            bars.SetTrendingSeries("CON.F.US.M6E.U26", strength:=0.1)

            Dim svc = BuildService(prefs, bars)
            Await svc.RefreshNowAsync()
            Dim picked = svc.GetCurrentWatchlist().Select(Function(c) c.PxRootSymbol).ToList()
            Assert.Equal(5, picked.Count)
            Assert.Contains("MES", picked)
            Assert.Contains("MNQ", picked)
            Assert.Contains("MGC", picked)
            Assert.Contains("MBT", picked)
            Assert.Contains("M2K", picked)
            Assert.DoesNotContain("MCLE", picked)
            Assert.DoesNotContain("M6E", picked)
        End Function

        ''' <summary>F6-b — pinned override. A low-scoring contract that is pinned still appears.</summary>
        <Fact>
        Public Async Function Pinned_AppearsEvenWithLowScore() As Task
            Dim prefs = NewPrefs(enabled:=True, maxSize:=3,
                                  pinned:=New List(Of String) From {"MCLE"})
            Dim bars = New FakeBarIngestionService()
            bars.SetTrendingSeries("CON.F.US.MES.U26", 1.0)
            bars.SetTrendingSeries("CON.F.US.MNQ.U26", 0.9)
            bars.SetTrendingSeries("CON.F.US.MGC.M26", 0.8)
            bars.SetTrendingSeries("CON.F.US.MCLE.M26", 0.05) ' very low — would be cut without pin

            Dim svc = BuildService(prefs, bars)
            Await svc.RefreshNowAsync()
            Dim picked = svc.GetCurrentWatchlist().Select(Function(c) c.PxRootSymbol).ToList()
            Assert.Equal(3, picked.Count)
            Assert.Contains("MCLE", picked)
        End Function

        ''' <summary>F6-c — blacklisted contract never appears even with a top score.</summary>
        <Fact>
        Public Async Function Blacklisted_NeverAppears() As Task
            Dim prefs = NewPrefs(enabled:=True, maxSize:=5,
                                  blacklisted:=New List(Of String) From {"MES"})
            Dim bars = New FakeBarIngestionService()
            ' MES is the highest scorer but must still be excluded.
            bars.SetTrendingSeries("CON.F.US.MES.U26", 1.0)
            bars.SetTrendingSeries("CON.F.US.MNQ.U26", 0.9)
            bars.SetTrendingSeries("CON.F.US.MGC.M26", 0.8)
            bars.SetTrendingSeries("CON.F.US.MBT.H26", 0.7)

            Dim svc = BuildService(prefs, bars)
            Await svc.RefreshNowAsync()
            Dim picked = svc.GetCurrentWatchlist().Select(Function(c) c.PxRootSymbol).ToList()
            Assert.DoesNotContain("MES", picked)
        End Function

        ''' <summary>F6-d — hysteresis. A contract added on refresh #1 keeps its slot on refresh #2
        ''' even when a higher-score contender would otherwise displace it, until tenure elapses.</summary>
        <Fact>
        Public Async Function Hysteresis_KeepsRecentMemberAgainstNewerHigherScorer() As Task
            Dim prefs = NewPrefs(enabled:=True, maxSize:=3, minTenureMinutes:=120)
            Dim bars = New FakeBarIngestionService()
            ' Refresh #1: MES > MNQ > MGC enter the list.
            bars.SetTrendingSeries("CON.F.US.MES.U26", 1.0)
            bars.SetTrendingSeries("CON.F.US.MNQ.U26", 0.9)
            bars.SetTrendingSeries("CON.F.US.MGC.M26", 0.8)
            bars.SetTrendingSeries("CON.F.US.MBT.H26", 0.1)

            Dim svc = BuildService(prefs, bars)
            Await svc.RefreshNowAsync()
            Dim first = svc.GetCurrentWatchlist().Select(Function(c) c.PxRootSymbol).ToHashSet(StringComparer.OrdinalIgnoreCase)
            Assert.Contains("MGC", first)
            Assert.DoesNotContain("MBT", first)

            ' Refresh #2: MBT becomes the new highest, MGC drops. Hysteresis should keep MGC.
            bars.SetTrendingSeries("CON.F.US.MGC.M26", 0.05)
            bars.SetTrendingSeries("CON.F.US.MBT.H26", 0.99)
            Await svc.RefreshNowAsync()
            Dim second = svc.GetCurrentWatchlist().Select(Function(c) c.PxRootSymbol).ToHashSet(StringComparer.OrdinalIgnoreCase)
            Assert.Contains("MGC", second)
        End Function

        ''' <summary>F6-e — open-slot pin. A contract with an open slot stays in the list even
        ''' when its score would otherwise cut it.</summary>
        <Fact>
        Public Async Function OpenSlotSource_PinsContractIntoWatchlist() As Task
            Dim prefs = NewPrefs(enabled:=True, maxSize:=3)
            Dim bars = New FakeBarIngestionService()
            bars.SetTrendingSeries("CON.F.US.MES.U26", 1.0)
            bars.SetTrendingSeries("CON.F.US.MNQ.U26", 0.9)
            bars.SetTrendingSeries("CON.F.US.MGC.M26", 0.8)
            bars.SetTrendingSeries("CON.F.US.MCLE.M26", 0.05)

            Dim svc = BuildService(prefs, bars)
            svc.RegisterOpenSlotSource(New FakeOpenSlotSource(New String() {"MCLE"}))

            Await svc.RefreshNowAsync()
            Dim picked = svc.GetCurrentWatchlist().Select(Function(c) c.PxRootSymbol).ToList()
            Assert.Contains("MCLE", picked)
        End Function

        ''' <summary>F6-f — scorer math fixture: a high-vol, high-ADX, high-volume series
        ''' returns a substantially higher score than a flat one.</summary>
        <Fact>
        Public Sub Scorer_TrendingSeriesScoresHigherThanFlat()
            Dim scorer As New InstrumentOpportunityScorer()
            Dim contract = FavouriteContracts.GetDefaults().First(Function(f) f.PxRootSymbol = "MES")

            Dim trending = SyntheticBars.BuildTrending(contract.PxContractId, strength:=1.0, count:=60)
            Dim flat = SyntheticBars.BuildFlat(contract.PxContractId, count:=60)

            Dim trendScore = scorer.Score(contract, trending)
            Dim flatScore = scorer.Score(contract, flat)
            Assert.True(trendScore > 30.0F, $"Trending score should be substantial, got {trendScore}")
            Assert.True(flatScore < trendScore, $"Flat score {flatScore} should be less than trending {trendScore}")
        End Sub

        ' ─── Fixture helpers ─────────────────────────────────────────────────

        Private Shared Function NewPrefs(enabled As Boolean,
                                          maxSize As Integer,
                                          Optional pinned As List(Of String) = Nothing,
                                          Optional blacklisted As List(Of String) = Nothing,
                                          Optional minTenureMinutes As Integer = 0) As FakePreferences
            Return New FakePreferences(New OpportunityScoreSettings With {
                .AdaptiveWatchlistEnabled = enabled,
                .AdaptiveWatchlistMaxSize = maxSize,
                .AdaptiveWatchlistRefreshMinutes = 60,
                .AdaptiveWatchlistMinTenureMinutes = minTenureMinutes,
                .ScoreBarsCount = 60,
                .IndicatorLength = 14,
                .PinnedRootSymbols = If(pinned, New List(Of String)()),
                .BlacklistedRootSymbols = If(blacklisted, New List(Of String)())
            })
        End Function

        Private Shared Function BuildService(prefs As IOpportunityScorePreferences,
                                              bars As IBarIngestionService) As AdaptiveWatchlistService
            Dim services As New ServiceCollection()
            services.AddSingleton(Of IBarIngestionService)(bars)
            Dim provider = services.BuildServiceProvider()
            Return New AdaptiveWatchlistService(
                provider.GetRequiredService(Of IServiceScopeFactory)(),
                New InstrumentOpportunityScorer(),
                prefs,
                NullLogger(Of AdaptiveWatchlistService).Instance)
        End Function

        ' ─── Fakes ───────────────────────────────────────────────────────────

        Private Class FakePreferences
            Implements IOpportunityScorePreferences

            Private _settings As OpportunityScoreSettings
            Public Event Changed As EventHandler Implements IOpportunityScorePreferences.Changed

            Public Sub New(s As OpportunityScoreSettings)
                _settings = s
            End Sub

            Public Function GetSettings() As OpportunityScoreSettings _
                Implements IOpportunityScorePreferences.GetSettings
                Return _settings
            End Function

            Public Sub Save(s As OpportunityScoreSettings) _
                Implements IOpportunityScorePreferences.Save
                _settings = s
                RaiseEvent Changed(Me, EventArgs.Empty)
            End Sub
        End Class

        Private Class FakeBarIngestionService
            Implements IBarIngestionService

            Private ReadOnly _series As New Dictionary(Of String, List(Of MarketBar))(StringComparer.OrdinalIgnoreCase)

            Public Sub SetTrendingSeries(contractId As String, strength As Double)
                _series(contractId) = SyntheticBars.BuildTrending(contractId, strength, 60)
            End Sub

            Public Function GetBarsForMLAsync(contractId As String,
                                              timeframe As BarTimeframe,
                                              Optional maxBars As Integer = 200,
                                              Optional cancel As CancellationToken = Nothing) As Task(Of IList(Of MarketBar)) _
                Implements IBarIngestionService.GetBarsForMLAsync
                Dim list As List(Of MarketBar) = Nothing
                If _series.TryGetValue(contractId, list) Then
                    Return Task.FromResult(Of IList(Of MarketBar))(list)
                End If
                Return Task.FromResult(Of IList(Of MarketBar))(New List(Of MarketBar)())
            End Function

            Public Function GetLatestPriceAsync(contractId As String,
                                                Optional cancel As CancellationToken = Nothing) As Task(Of Decimal) _
                Implements IBarIngestionService.GetLatestPriceAsync
                Return Task.FromResult(0D)
            End Function

            Public Function GetLiveBarsAsync(contractId As String,
                                             timeframe As BarTimeframe,
                                             barCount As Integer,
                                             Optional cancel As CancellationToken = Nothing,
                                             Optional live As Boolean = False) As Task(Of IList(Of MarketBar)) _
                Implements IBarIngestionService.GetLiveBarsAsync
                Return Task.FromResult(Of IList(Of MarketBar))(New List(Of MarketBar)())
            End Function

            Public Function IngestAsync(contractId As String,
                                        timeframe As BarTimeframe,
                                        Optional barsToFetch As Integer = 500,
                                        Optional cancel As CancellationToken = Nothing) As Task(Of Integer) _
                Implements IBarIngestionService.IngestAsync
                Return Task.FromResult(0)
            End Function
        End Class

        Private Class FakeOpenSlotSource
            Implements IOpenSlotInstrumentSource

            Private ReadOnly _symbols As IEnumerable(Of String)

            Public Sub New(symbols As IEnumerable(Of String))
                _symbols = symbols
            End Sub

            Public Function GetOpenInstrumentRootSymbols() As IEnumerable(Of String) _
                Implements IOpenSlotInstrumentSource.GetOpenInstrumentRootSymbols
                Return _symbols
            End Function
        End Class

    End Class

    ''' <summary>
    ''' Builds synthetic OHLCV series used by the F6 fixtures. <see cref="BuildTrending"/>
    ''' produces a near-linear uptrend with widening ranges (high vol, high ADX, growing
    ''' volume). <paramref name="strength"/> scales the per-bar move so callers can engineer
    ''' a deterministic score ordering between contracts.
    ''' </summary>
    Friend Module SyntheticBars

        Friend Function BuildTrending(contractId As String, strength As Double, count As Integer) As List(Of MarketBar)
            Dim bars As New List(Of MarketBar)
            Dim basePrice As Double = 100.0
            Dim baseTs = DateTimeOffset.UtcNow.AddMinutes(-15 * count)
            For i = 0 To count - 1
                Dim move = strength * (1.0 + 0.05 * i)
                Dim openP = basePrice
                Dim closeP = basePrice + move
                Dim highP = closeP + (0.4 * move)
                Dim lowP = openP - (0.2 * move)
                bars.Add(New MarketBar With {
                    .ContractId = contractId,
                    .Timestamp = baseTs.AddMinutes(15 * i),
                    .Timeframe = BarTimeframe.FifteenMinute,
                    .Open = CDec(openP),
                    .High = CDec(highP),
                    .Low = CDec(lowP),
                    .Close = CDec(closeP),
                    .Volume = CLng(1000 * (1 + (i / 30.0)))
                })
                basePrice = closeP
            Next
            Return bars
        End Function

        Friend Function BuildFlat(contractId As String, count As Integer) As List(Of MarketBar)
            Dim bars As New List(Of MarketBar)
            Dim basePrice As Decimal = 100D
            Dim baseTs = DateTimeOffset.UtcNow.AddMinutes(-15 * count)
            For i = 0 To count - 1
                bars.Add(New MarketBar With {
                    .ContractId = contractId,
                    .Timestamp = baseTs.AddMinutes(15 * i),
                    .Timeframe = BarTimeframe.FifteenMinute,
                    .Open = basePrice,
                    .High = basePrice + 0.05D,
                    .Low = basePrice - 0.05D,
                    .Close = basePrice,
                    .Volume = 1000L
                })
            Next
            Return bars
        End Function

    End Module

End Namespace
