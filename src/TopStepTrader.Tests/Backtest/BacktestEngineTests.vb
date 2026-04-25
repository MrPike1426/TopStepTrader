Option Strict On
Option Explicit On

Imports System.Threading
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Backtest
Imports Xunit

Namespace TopStepTrader.Tests.Backtest

    ''' <summary>
    ''' Unit tests for BacktestEngine.RunReplay() under-warmup / NaN propagation scenarios.
    ''' TEST-05: verifies that RunReplay never throws and always returns 0 closed trades
    ''' when the bar count is below the indicator warm-up threshold.
    '''
    ''' BacktestEngine is constructed with Nothing repositories because RunReplay
    ''' does not touch the database — it works entirely in-memory with the supplied
    ''' bar list.  The Friend access is granted via InternalsVisibleTo in the Services
    ''' project file.
    ''' </summary>
    Public Class BacktestEngineTests

        ''' <summary>
        ''' Build a minimal BacktestConfiguration with valid PointValue / TickSize so
        ''' RunReplay does not throw the PointValue guard.
        ''' </summary>
        Private Shared Function MakeConfig(
                Optional strategy As StrategyConditionType = StrategyConditionType.EmaRsiWeightedScore,
                Optional useAtr As Boolean = False) As BacktestConfiguration
            Return New BacktestConfiguration() With {
                .ContractId = "MES",
                .PointValue = 5.0D,
                .TickSize = 0.25D,
                .StrategyCondition = strategy,
                .UseAtrMode = useAtr
            }
        End Function

        ''' <summary>
        ''' Build a list of <paramref name="count"/> simple 1-minute bars starting at a
        ''' fixed reference time.  OHLCV values are intentionally flat (no signal noise).
        ''' </summary>
        Private Shared Function MakeBars(count As Integer) As List(Of MarketBar)
            Dim t0 = New DateTimeOffset(2026, 1, 6, 9, 30, 0, TimeSpan.Zero)
            Dim bars As New List(Of MarketBar)(count)
            For i As Integer = 0 To count - 1
                bars.Add(New MarketBar With {
                    .ContractId = "MES",
                    .Timeframe = BarTimeframe.OneMinute,
                    .Timestamp = t0.AddMinutes(i),
                    .Open = 5000D + i,
                    .High = 5002D + i,
                    .Low = 4998D + i,
                    .Close = 5001D + i,
                    .Volume = 200L,
                    .VWAP = 5000.5D + i
                })
            Next
            Return bars
        End Function

        Private Shared ReadOnly _sut As New BacktestEngine(
            Nothing, Nothing, NullLogger(Of BacktestEngine).Instance)

        ' ══════════════════════════════════════════════════════════════════
        ' Under-warmup — EmaRsiWeightedScore (default strategy)

        ''' <summary>
        ''' 5 bars is well below every indicator warm-up period.
        ''' RunReplay must return 0 closed trades and not throw.
        ''' </summary>
        <Fact>
        Public Sub RunReplay_FiveBars_EmaRsi_ReturnsZeroTrades()
            Dim config = MakeConfig()
            Dim bars = MakeBars(5)

            Dim result = _sut.RunReplay(config, bars, CancellationToken.None)

            Assert.NotNull(result)
            Assert.Empty(result.Trades)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' Under-warmup — MultiConfluence strategy

        ''' <summary>
        ''' 25 bars with the MultiConfluence strategy.
        ''' Indicators are still in the NaN warm-up range; no trade should fire.
        ''' </summary>
        <Fact>
        Public Sub RunReplay_TwentyFiveBars_MultiConfluence_ReturnsZeroTrades()
            Dim config = MakeConfig(StrategyConditionType.MultiConfluence)
            Dim bars = MakeBars(25)

            Dim result = _sut.RunReplay(config, bars, CancellationToken.None)

            Assert.NotNull(result)
            Assert.Empty(result.Trades)
        End Sub

        ' ══════════════════════════════════════════════════════════════════
        ' Under-warmup — AtrFilter enabled

        ''' <summary>
        ''' 10 bars with AtrFilter=True.
        ''' The ATR series is fully NaN; the engine must not throw and must return
        ''' 0 closed trades.
        ''' </summary>
        <Fact>
        Public Sub RunReplay_TenBars_AtrFilterEnabled_ReturnsZeroTrades()
            Dim config = MakeConfig(useAtr:=True)
            Dim bars = MakeBars(10)

            Dim result = _sut.RunReplay(config, bars, CancellationToken.None)

            Assert.NotNull(result)
            Assert.Empty(result.Trades)
        End Sub

            ' ══════════════════════════════════════════════════════════════════
            ' BUG-26: DoubleBubbleButt P&L must differ between personas

            ''' <summary>
            ''' BUG-26: Two configs with different SlAtrMultiple/TpAtrMultiple (simulating
            ''' two personas) must produce different TotalPnL when the DoubleBubbleButt
            ''' strategy fires trades.  Identical P&L indicated a hardcoded ATR multiplier
            ''' was ignoring per-persona config values.
            ''' </summary>
            <Fact>
            Public Sub RunReplay_DoubleBubbleButt_DifferentPersonaMultiples_ProduceDifferentPnL()
                ' Use enough bars to clear the 25-bar warm-up and trigger at least one entry.
                ' Bars are constructed so close repeatedly crosses above the inner upper BB band.
                Dim t0 = New DateTimeOffset(2026, 1, 6, 9, 30, 0, TimeSpan.Zero)
                Dim bars As New List(Of MarketBar)(200)
                For i As Integer = 0 To 199
                    Dim basePrice = 5000D + i * 0.5D
                    bars.Add(New MarketBar With {
                        .ContractId = "MES",
                        .Timeframe = BarTimeframe.OneMinute,
                        .Timestamp = t0.AddMinutes(i),
                        .Open = basePrice,
                        .High = basePrice + 15D,
                        .Low = basePrice - 5D,
                        .Close = basePrice + 10D,
                        .Volume = 200L,
                        .VWAP = basePrice + 5D
                    })
                Next

                Dim configLewis As New BacktestConfiguration() With {
                    .ContractId = "MES",
                    .PointValue = 5.0D,
                    .TickSize = 0.25D,
                    .StrategyCondition = StrategyConditionType.DoubleBubbleButt,
                    .UseAtrMode = True,
                    .SlAtrMultiple = 1.5D,
                    .TpAtrMultiple = 3.0D
                }

                Dim configJoe As New BacktestConfiguration() With {
                    .ContractId = "MES",
                    .PointValue = 5.0D,
                    .TickSize = 0.25D,
                    .StrategyCondition = StrategyConditionType.DoubleBubbleButt,
                    .UseAtrMode = True,
                    .SlAtrMultiple = 0.75D,
                    .TpAtrMultiple = 2.0D
                }

                Dim resultLewis = _sut.RunReplay(configLewis, bars, CancellationToken.None)
                Dim resultJoe   = _sut.RunReplay(configJoe,   bars, CancellationToken.None)

                ' If both run produced trades, P&L must differ (different SL/TP multiples).
                ' If neither produced trades the indicator bands never fired — that is an
                ' environment issue, not the bug; assert at least one side traded.
                Assert.True(resultLewis.TotalTrades > 0 OrElse resultJoe.TotalTrades > 0,
                            "Expected at least one persona to produce trades on the synthetic bars.")

                If resultLewis.TotalTrades > 0 AndAlso resultJoe.TotalTrades > 0 Then
                    Assert.NotEqual(resultLewis.TotalPnL, resultJoe.TotalPnL)
                End If
            End Sub

        End Class

    End Namespace

