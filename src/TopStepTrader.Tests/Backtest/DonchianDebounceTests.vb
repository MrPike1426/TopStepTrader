Imports System.Threading
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Backtest
Imports Xunit

Namespace TopStepTrader.Tests.Backtest

    ''' <summary>
    ''' BUG-06: DonchianBreakout exit de-bounce.
    '''
    ''' Verifies that after a mid-cross NeutralExit the engine suppresses Donchian re-entry
    ''' for exactly 3 bars.  Uses BacktestEngine.RunReplay (Friend) so tests are fully
    ''' in-memory with no repository or database dependencies.
    ''' </summary>
    Public Class DonchianDebounceTests

        ' ─── helpers ────────────────────────────────────────────────────────────────

        Private Shared Function MakeEngine() As BacktestEngine
            ' Repos are not called by RunReplay — passing Nothing is safe.
            Return New BacktestEngine(Nothing, Nothing, NullLogger(Of BacktestEngine).Instance)
        End Function

        Private Shared Function MakeConfig() As BacktestConfiguration
            Return New BacktestConfiguration With {
                .RunName          = "DonchianDebounceTest",
                .ContractId       = "CON.F.US.MES.H26",
                .StartDate        = New Date(2025, 1, 1),
                .EndDate          = New Date(2025, 6, 1),
                .InitialCapital   = 50000D,
                .TickSize         = 0.25D,
                .PointValue       = 5D,
                .Quantity         = 1,
                .MaxScaleIns      = 0,
                .UseAtrMode       = False,       ' ATR-relative stops off — only NeutralExit matters
                .SlippageTicks    = 0,
                .MinSignalConfidence = 0.0F,
                .StrategyCondition = StrategyConditionType.DonchianBreakout
            }
        End Function

        ''' <summary>
        ''' Build a synthetic 80-bar series for the DonchianBreakout de-bounce test.
        ''' Layout (all bars 5-min, UTC, starting 2025-01-02 09:00):
        '''
        '''   Bars 0–56  (57 warmup bars): flat — Close=5000, High=5005, Low=4995.
        '''   Bar  57   : breakout — Close=5011 &gt; DonchianUpper(56)=5005.  Buy signal fires.
        '''   Bar  58   : fill bar — Open=5001 (entry fills here). Close=4998 &lt; mid → NeutralExit.
        '''               lastDonchianExitBarIndex is set to 58.
        '''   Bars 59-61: debounce window (i − 58 = 1,2,3 ≤ 3).  Close=5015 (would-be breakout)
        '''               but the de-bounce guard suppresses all three signals.
        '''   Bars 62-79: flat — no further signals.
        '''
        ''' Expected result: exactly 1 trade (bar 58 fill + NeutralExit).
        ''' </summary>
        Private Shared Function BuildDebounceBars() As IReadOnlyList(Of MarketBar)
            Dim bars As New List(Of MarketBar)()
            Dim t0 = New DateTimeOffset(2025, 1, 2, 9, 0, 0, TimeSpan.Zero)

            For i = 0 To 79
                Dim ts = t0.AddMinutes(i * 5)
                Dim bar As New MarketBar With {
                    .ContractId = "CON.F.US.MES.H26",
                    .Timestamp  = ts,
                    .Timeframe  = BarTimeframe.FiveMinute,
                    .Volume     = 100
                }

                If i <= 56 Then
                    ' Flat warmup bars
                    bar.Open  = 5000D : bar.High = 5005D : bar.Low = 4995D : bar.Close = 5000D
                ElseIf i = 57 Then
                    ' Breakout bar: Close > DonchianUpper(56) = 5005 → Buy signal
                    bar.Open  = 5001D : bar.High = 5011D : bar.Low = 4999D : bar.Close = 5011D
                ElseIf i = 58 Then
                    ' Fill bar: position opens at Open=5001. Close=4998 < DonchianMid(57)≈5003 → NeutralExit
                    bar.Open  = 5001D : bar.High = 5006D : bar.Low = 4997D : bar.Close = 4998D
                ElseIf i >= 59 AndAlso i <= 61 Then
                    ' Debounce window: would-be breakout closes (5015 > DonchianUpper ≈ 5011)
                    ' but de-bounce guard (i − 58 ≤ 3) suppresses the signal.
                    bar.Open  = 5000D : bar.High = 5015D : bar.Low = 4990D : bar.Close = 5015D
                Else
                    ' Quiet tail: no signals
                    bar.Open  = 5000D : bar.High = 5005D : bar.Low = 4995D : bar.Close = 5000D
                End If

                bars.Add(bar)
            Next
            Return bars.AsReadOnly()
        End Function

        ' ─── tests ──────────────────────────────────────────────────────────────────

        ''' <summary>
        ''' After a Donchian mid-cross NeutralExit the engine must not re-enter for 3 bars.
        ''' The test bar series has three clear breakout bars (59–61) inside the debounce
        ''' window.  Without the guard those three would each generate a trade; with the
        ''' guard the total should remain exactly 1.
        ''' </summary>
        <Fact>
        Public Sub DonchianDebounce_NeutralExitFollowedByThreeBreakouts_ProducesExactlyOneTrade()
            Dim engine = MakeEngine()
            Dim config = MakeConfig()
            Dim bars   = BuildDebounceBars()

            Dim result = engine.RunReplay(config, bars, CancellationToken.None)

            Assert.Equal(1, result.TotalTrades)
        End Sub

        ''' <summary>
        ''' After the 3-bar debounce window expires (bar 62+), a new Donchian breakout
        ''' signal must be allowed through.  This test appends a second breakout at bar 63
        ''' (i − 58 = 5 &gt; 3) and confirms the engine enters a second trade.
        ''' </summary>
        <Fact>
        Public Sub DonchianDebounce_BreakoutAfterDebounceWindow_AllowsReEntry()
            Dim engine = MakeEngine()
            Dim config = MakeConfig()

            ' Reuse the base bar list but promote bar 63 to a breakout.
            Dim bars   = BuildDebounceBars().ToList()
            ' DonchianUpper at bar 62 = max(High[43..62]).  Bars 59–61 in the base series
            ' have High=5015, so the channel has risen to 5015.  Close=5020 clears it.
            bars(63) = New MarketBar With {
                .ContractId = "CON.F.US.MES.H26",
                .Timestamp  = bars(63).Timestamp,
                .Timeframe  = BarTimeframe.FiveMinute,
                .Volume     = 100,
                .Open       = 5001D,
                .High       = 5020D,
                .Low        = 4999D,
                .Close      = 5020D
            }

            Dim result = engine.RunReplay(config, bars.AsReadOnly(), CancellationToken.None)

            ' First trade from bar 57 signal / bar 58 fill+exit, second from bar 63 signal.
            Assert.True(result.TotalTrades >= 2,
                        $"Expected ≥ 2 trades after debounce expires; got {result.TotalTrades}")
        End Sub

        ''' <summary>
        ''' Oscillating price that crosses the mid every bar generates an unbounded trade
        ''' stream without de-bounce.  With the guard the trade count must stay within the
        ''' theoretical ceiling: 1 trade per (1 signal + 1 fill + 3 debounce) = 5-bar cycle.
        ''' For an 80-bar series with 25 active bars (warmUp=55), max trades ≤ 5.
        ''' </summary>
        <Fact>
        Public Sub DonchianDebounce_OscillatingAroundMid_TradeCountIsBounded()
            Dim engine = MakeEngine()
            Dim config = MakeConfig()

            Dim bars As New List(Of MarketBar)()
            Dim t0 = New DateTimeOffset(2025, 1, 2, 9, 0, 0, TimeSpan.Zero)

            For i = 0 To 79
                Dim ts = t0.AddMinutes(i * 5)
                Dim bar As New MarketBar With {
                    .ContractId = "CON.F.US.MES.H26",
                    .Timestamp  = ts,
                    .Timeframe  = BarTimeframe.FiveMinute,
                    .Volume     = 100
                }

                If i <= 56 Then
                    bar.Open  = 5000D : bar.High = 5005D : bar.Low = 4995D : bar.Close = 5000D
                ElseIf i Mod 2 = 1 Then
                    ' Odd bars: breakout above upper (5011 > 5005 warmup upper)
                    bar.Open  = 5001D : bar.High = 5012D : bar.Low = 4999D : bar.Close = 5011D
                Else
                    ' Even bars: close below mid → triggers NeutralExit if long
                    bar.Open  = 5001D : bar.High = 5006D : bar.Low = 4990D : bar.Close = 4990D
                End If

                bars.Add(bar)
            Next

            Dim result = engine.RunReplay(config, bars.AsReadOnly(), CancellationToken.None)

            ' 25 active bars / 5-bar minimum cycle = max 5 trades with debounce.
            Assert.True(result.TotalTrades <= 5,
                        $"De-bounce should cap trades to ≤ 5 in 25 active bars; got {result.TotalTrades}")
        End Sub

    End Class

End Namespace
