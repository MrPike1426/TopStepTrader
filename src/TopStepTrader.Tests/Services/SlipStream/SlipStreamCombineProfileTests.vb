Imports System.Threading
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging.Abstractions
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.BreakAndBounce
Imports TopStepTrader.Services.Market
Imports TopStepTrader.Services.Risk
Imports TopStepTrader.Services.Scalper
Imports TopStepTrader.Services.SlipStream
Imports TopStepTrader.Services.VwapMeanReversion
Imports Xunit

Namespace TopStepTrader.Tests.Services.SlipStream

    ''' <summary>
    ''' STRAT-45 acceptance: SlipStream combine sizing (F1), micro-only watchlist
    ''' restriction (F2), engine disablement in combine mode (F3), and force-flatten
    ''' idempotence (F4).
    ''' </summary>
    Public Class SlipStreamCombineProfileTests

        Private Shared Function CombineOn(Optional maxContracts As Integer = 5,
                                           Optional slipStreamRiskPct As Double = 0.4,
                                           Optional microsOnly As Boolean = True) As CombineSettings
            Return New CombineSettings With {
                .Enabled = True,
                .MaxContracts = maxContracts,
                .SlipStreamRiskPct = slipStreamRiskPct,
                .MicrosOnly = microsOnly
            }
        End Function

        ' ── F1 — Combine sizing ───────────────────────────────────────────────

        <Fact>
        Public Sub F1a_CombineOff_RiskPctIsConfigValue()
            Dim config As New SlipStreamConfig With {.RiskPct = 0.5}
            Assert.Equal(0.5, SlipStreamOrchestrator.EffectiveRiskPct(config, New CombineSettings()))
            Assert.Equal(0.5, SlipStreamOrchestrator.EffectiveRiskPct(config, Nothing))
        End Sub

        <Fact>
        Public Sub F1b_CombineOn_ProfileCapsRiskPct()
            ' User at the 0.5 default → combine profile 0.4 wins ($200 on $50k).
            Dim config As New SlipStreamConfig With {.RiskPct = 0.5}
            Assert.Equal(0.4, SlipStreamOrchestrator.EffectiveRiskPct(config, CombineOn()))
        End Sub

        <Fact>
        Public Sub F1c_CombineOn_TighterUserValueIsRespected()
            Dim config As New SlipStreamConfig With {.RiskPct = 0.25}
            Assert.Equal(0.25, SlipStreamOrchestrator.EffectiveRiskPct(config, CombineOn()))
        End Sub

        <Fact>
        Public Sub F1d_RiskCashOnFiftyK_IsAboutTwoHundred()
            ' $50k × 0.4% = $200 risk; MES pointValue $5/pt; 8-pt stop → floor(200/40) = 5,
            ' at the MaxContracts cap. A 10-pt stop → floor(200/50) = 4, under the cap.
            Assert.Equal(5, SlipStreamOrchestrator.ComputeContracts(50000D, 0.4, 8D, 5D, CombineOn()))
            Assert.Equal(4, SlipStreamOrchestrator.ComputeContracts(50000D, 0.4, 10D, 5D, CombineOn()))
        End Sub

        <Fact>
        Public Sub F1e_QuantityClampedToMaxContracts()
            ' Tiny stop → raw quantity 40, clamped to the combine MaxContracts.
            Assert.Equal(5, SlipStreamOrchestrator.ComputeContracts(50000D, 0.4, 1D, 5D, CombineOn(maxContracts:=5)))
            Assert.Equal(3, SlipStreamOrchestrator.ComputeContracts(50000D, 0.4, 1D, 5D, CombineOn(maxContracts:=3)))
        End Sub

        <Fact>
        Public Sub F1f_CombineOff_ParanoiaCeilingUnchanged()
            ' Combine off → legacy behaviour: cap 100, min 1.
            Assert.Equal(40, SlipStreamOrchestrator.ComputeContracts(50000D, 0.4, 1D, 5D, New CombineSettings()))
            Assert.Equal(1, SlipStreamOrchestrator.ComputeContracts(50000D, 0.4, 500D, 5D, New CombineSettings()))
        End Sub

        <Fact>
        Public Sub F1g_MinimumOneContractSurvivesClamp()
            Assert.Equal(1, SlipStreamOrchestrator.ComputeContracts(50000D, 0.4, 500D, 5D, CombineOn()))
            Assert.Equal(1, SlipStreamOrchestrator.ComputeContracts(0D, 0.4, 10D, 5D, CombineOn()))
        End Sub

        ' ── F2 — Micro-only watchlist restriction ────────────────────────────

        <Fact>
        Public Sub F2a_IsMicro_KnowsTheTicketList()
            For Each root In {"MES", "MNQ", "MGC", "MCL", "MCLE", "M2K", "M6E", "MBT", "MYM", "M6B", "M6A", "MHG"}
                Assert.True(InstrumentUniverse.IsMicro(root), $"{root} should be micro")
            Next
            For Each root In {"ES", "NQ", "GC", "CL", "RTY", "", Nothing}
                Assert.False(InstrumentUniverse.IsMicro(root), $"{root} should NOT be micro")
            Next
        End Sub

        <Fact>
        Public Sub F2b_UniverseFilter_DropsMinisWhenCombineOn()
            Dim universe As IList(Of UniverseEntry) = InstrumentUniverse.GetAll()
            ' Plant a synthetic full-size ES entry to prove the filter bites.
            universe.Add(New UniverseEntry With {
                .Contract = New FavouriteContract("ES", "S&P 500 mini", "CON.F.US.EP.U26", 0.25D, 12.5D, 50D, 0.3D, 100D) With {.PxRootSymbol = "ES"},
                .Tier = UniverseTier.Experimental
            })

            Dim filtered = AdaptiveWatchlistService.FilterUniverseMicrosOnly(universe, CombineOn())
            Assert.DoesNotContain(filtered, Function(e) e.Contract.PxRootSymbol = "ES")
            Assert.Contains(filtered, Function(e) e.Contract.PxRootSymbol = "MES")
            Assert.Equal(universe.Count - 1, filtered.Count)
        End Sub

        <Fact>
        Public Sub F2c_UniverseFilter_PassThroughWhenCombineOffOrMinisAllowed()
            Dim universe As IList(Of UniverseEntry) = InstrumentUniverse.GetAll()
            universe.Add(New UniverseEntry With {
                .Contract = New FavouriteContract("ES", "S&P 500 mini", "CON.F.US.EP.U26", 0.25D, 12.5D, 50D, 0.3D, 100D) With {.PxRootSymbol = "ES"},
                .Tier = UniverseTier.Experimental
            })

            Assert.Equal(universe.Count,
                         AdaptiveWatchlistService.FilterUniverseMicrosOnly(universe, New CombineSettings()).Count)
            Assert.Equal(universe.Count,
                         AdaptiveWatchlistService.FilterUniverseMicrosOnly(universe, CombineOn(microsOnly:=False)).Count)
        End Sub

        <Fact>
        Public Sub F2d_FallbackListFilter_MicrosOnly()
            Dim contracts As IReadOnlyList(Of FavouriteContract) = New List(Of FavouriteContract) From {
                FavouriteContracts.GetDefaults().First(Function(f) f.PxRootSymbol = "MES"),
                New FavouriteContract("ES", "S&P 500 mini", "CON.F.US.EP.U26", 0.25D, 12.5D, 50D, 0.3D, 100D) With {.PxRootSymbol = "ES"}
            }
            Dim filtered = AdaptiveWatchlistService.FilterMicrosOnly(contracts, CombineOn())
            Assert.Single(filtered)
            Assert.Equal("MES", filtered(0).PxRootSymbol)
        End Sub

        ' ── F3 — Engine disablement in combine mode ──────────────────────────

        <Fact>
        Public Sub F3a_BreakAndBounce_EnableRefusedInCombineMode()
            Dim orch As New BreakAndBounceOrchestrator(
                Nothing, Nothing, Nothing, Nothing, Nothing, Nothing,
                NullLogger(Of BreakAndBounceOrchestrator).Instance,
                combineOptions:=Options.Create(CombineOn()))
            Dim raised As New List(Of Boolean)
            AddHandler orch.EnabledChanged, Sub(s, enabled) raised.Add(enabled)

            orch.Enable()

            Assert.False(orch.IsEnabled)
            ' EnabledChanged(False) re-raised so a UI toggle reverts.
            Assert.Contains(False, raised)
            Assert.DoesNotContain(True, raised)
        End Sub

        <Fact>
        Public Sub F3b_BreakAndBounce_EnableWorksWithCombineOff()
            Dim orch As New BreakAndBounceOrchestrator(
                Nothing, Nothing, Nothing, Nothing, Nothing, Nothing,
                NullLogger(Of BreakAndBounceOrchestrator).Instance,
                combineOptions:=Options.Create(New CombineSettings()))
            orch.Enable()
            Assert.True(orch.IsEnabled)
        End Sub

        <Fact>
        Public Sub F3c_UltimateScalper_EnableRefusedInCombineMode()
            Dim orch As New UltimateScalperOrchestrator(
                Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing,
                NullLogger(Of UltimateScalperOrchestrator).Instance,
                combineOptions:=Options.Create(CombineOn()))
            Dim raised As New List(Of Boolean)
            AddHandler orch.EnabledChanged, Sub(s, enabled) raised.Add(enabled)

            orch.Enable()

            Assert.False(orch.IsEnabled)
            Assert.Contains(False, raised)
            Assert.DoesNotContain(True, raised)
        End Sub

        <Fact>
        Public Sub F3d_UltimateScalper_EnableWorksWithCombineOff()
            Dim orch As New UltimateScalperOrchestrator(
                Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing,
                NullLogger(Of UltimateScalperOrchestrator).Instance,
                combineOptions:=Options.Create(New CombineSettings()))
            orch.Enable()
            Assert.True(orch.IsEnabled)
        End Sub

        <Fact>
        Public Sub F3e_VwapMeanReversion_EnableRefusedInCombineMode()
            ' BUG-104: FEAT-75 shipped after STRAT-45 without the combine gate.
            Dim orch As New VwapMeanReversionOrchestrator(
                Nothing, Nothing, Nothing, Nothing, Nothing, Nothing,
                NullLogger(Of VwapMeanReversionOrchestrator).Instance,
                combineOptions:=Options.Create(CombineOn()))
            Dim raised As New List(Of Boolean)
            AddHandler orch.EnabledChanged, Sub(s, enabled) raised.Add(enabled)

            orch.Enable()

            Assert.False(orch.IsEnabled)
            ' EnabledChanged(False) re-raised so a UI toggle reverts.
            Assert.Contains(False, raised)
            Assert.DoesNotContain(True, raised)
        End Sub

        <Fact>
        Public Sub F3f_VwapMeanReversion_EnableWorksWithCombineOff()
            Dim orch As New VwapMeanReversionOrchestrator(
                Nothing, Nothing, Nothing, Nothing, Nothing, Nothing,
                NullLogger(Of VwapMeanReversionOrchestrator).Instance,
                combineOptions:=Options.Create(New CombineSettings()))
            orch.Enable()
            Assert.True(orch.IsEnabled)
        End Sub

        ' ── F4 — Force-flatten idempotence ────────────────────────────────────

        ''' <summary>Both the SlipStream FlatWindow close and the guard's force-flatten
        ''' sweep may fire on the same position. The flattener re-queries open positions
        ''' every sweep, so a second sweep must see none and submit zero broker closes.</summary>
        <Fact>
        Public Async Function F4a_SecondFlattenSweep_SeesNoPositionsAndNoOps() As Task
            Dim orderSvc As New StatefulFlattenOrderService()
            orderSvc.OpenPositions.Add(New LivePositionSnapshot With {
                .PositionId = 1L, .ContractId = "CON.F.US.MES.U26", .Units = 2D, .IsBuy = True
            })

            Dim services As New ServiceCollection()
            services.AddSingleton(Of IOrderService)(orderSvc)
            Dim provider = services.BuildServiceProvider()
            Dim flattener As New PositionFlattener(
                provider.GetRequiredService(Of IServiceScopeFactory)(),
                NullLogger(Of PositionFlattener).Instance)

            Dim first = Await flattener.FlattenAllAsync(accountId:=42)
            Assert.Equal(1, first.AttemptedContracts)
            Assert.Equal(1, first.FlattenedContracts)
            Assert.Equal(1, orderSvc.FlattenCalls)
            Assert.True(first.Complete)

            ' Second sweep: position book already empty — clean no-op.
            Dim second = Await flattener.FlattenAllAsync(accountId:=42)
            Assert.Equal(0, second.AttemptedContracts)
            Assert.Equal(0, second.FlattenedContracts)
            Assert.Equal(1, orderSvc.FlattenCalls)  ' no new broker close
            Assert.True(second.Complete)
        End Function

        ' ── Fakes ─────────────────────────────────────────────────────────────

        ''' <summary>IOrderService fake whose position book empties when flattened, so a
        ''' second sweep observes a flat account (F4).</summary>
        Private Class StatefulFlattenOrderService
            Implements IOrderService

            Public ReadOnly OpenPositions As New List(Of LivePositionSnapshot)
            Public FlattenCalls As Integer = 0

            Public Function GetOpenPositionsAsync(accountId As Long,
                                                   Optional cancel As CancellationToken = Nothing) As Task(Of IEnumerable(Of LivePositionSnapshot)) _
                Implements IOrderService.GetOpenPositionsAsync
                Return Task.FromResult(Of IEnumerable(Of LivePositionSnapshot))(OpenPositions.ToList())
            End Function

            Public Function FlattenContractWithFillAsync(accountId As Long, contractId As String,
                                                          Optional cancel As CancellationToken = Nothing) As Task(Of (Success As Boolean, Fill As BrokerCloseFill)) _
                Implements IOrderService.FlattenContractWithFillAsync
                FlattenCalls += 1
                OpenPositions.RemoveAll(Function(p) String.Equals(p.ContractId, contractId, StringComparison.OrdinalIgnoreCase))
                Return Task.FromResult((True, CType(Nothing, BrokerCloseFill)))
            End Function

            ' ── Uninvolved members ────────────────────────────────────────────
            Public Event OrderFilled As EventHandler(Of OrderFilledEventArgs) Implements IOrderService.OrderFilled
            Public Event OrderRejected As EventHandler(Of OrderRejectedEventArgs) Implements IOrderService.OrderRejected
            Public Event PositionUpdated As EventHandler(Of PositionUpdateEventArgs) Implements IOrderService.PositionUpdated

            Public Function GetLivePositionSnapshotAsync(accountId As Long, contractId As String,
                                                          Optional positionId As Long? = Nothing,
                                                          Optional bypassCache As Boolean = False,
                                                          Optional cancel As CancellationToken = Nothing) As Task(Of LivePositionSnapshot) _
                Implements IOrderService.GetLivePositionSnapshotAsync
                Return Task.FromResult(Of LivePositionSnapshot)(Nothing)
            End Function
            Public Function PlaceOrderAsync(order As Order) As Task(Of Order) Implements IOrderService.PlaceOrderAsync
                Return Task.FromResult(Of Order)(Nothing)
            End Function
            Public Function CancelOrderAsync(orderId As Long) As Task(Of Boolean) Implements IOrderService.CancelOrderAsync
                Return Task.FromResult(False)
            End Function
            Public Function CancelAllOpenOrdersAsync() As Task Implements IOrderService.CancelAllOpenOrdersAsync
                Return Task.CompletedTask
            End Function
            Public Function GetOpenOrdersAsync(accountId As Long) As Task(Of IEnumerable(Of Order)) Implements IOrderService.GetOpenOrdersAsync
                Return Task.FromResult(Of IEnumerable(Of Order))(Array.Empty(Of Order)())
            End Function
            Public Function GetOrderHistoryAsync(accountId As Long, fromUtc As DateTime, toUtc As DateTime) As Task(Of IEnumerable(Of Order)) Implements IOrderService.GetOrderHistoryAsync
                Return Task.FromResult(Of IEnumerable(Of Order))(Array.Empty(Of Order)())
            End Function
            Public Function TryGetOrderFillPriceAsync(externalOrderId As Long, accountId As Long,
                                                       Optional cancel As CancellationToken = Nothing) As Task(Of Decimal?) _
                Implements IOrderService.TryGetOrderFillPriceAsync
                Return Task.FromResult(Of Decimal?)(Nothing)
            End Function
            Public Function TryGetBracketStopPriceAsync(accountId As Long, contractId As String,
                                                         Optional cancel As CancellationToken = Nothing) As Task(Of Decimal?) _
                Implements IOrderService.TryGetBracketStopPriceAsync
                Return Task.FromResult(Of Decimal?)(Nothing)
            End Function
            Public Function GetLiveWorkingOrdersAsync(accountId As Long, contractId As String,
                                                       Optional cancel As CancellationToken = Nothing) As Task(Of IEnumerable(Of Order)) _
                Implements IOrderService.GetLiveWorkingOrdersAsync
                Return Task.FromResult(Of IEnumerable(Of Order))(Array.Empty(Of Order)())
            End Function
            Public Function FlattenContractAsync(accountId As Long, contractId As String,
                                                  Optional cancel As CancellationToken = Nothing) As Task(Of Boolean) _
                Implements IOrderService.FlattenContractAsync
                Return Task.FromResult(True)
            End Function
            Public Function EditPositionSlTpAsync(positionId As Long, slRate As Decimal?, tpRate As Decimal?,
                                                   Optional enableTsl As Boolean = False,
                                                   Optional cancel As CancellationToken = Nothing) As Task(Of Boolean) _
                Implements IOrderService.EditPositionSlTpAsync
                Return Task.FromResult(True)
            End Function
            Public Function PartialCloseContractAsync(accountId As Long, contractId As String, size As Integer,
                                                       Optional cancel As CancellationToken = Nothing) As Task(Of Boolean) _
                Implements IOrderService.PartialCloseContractAsync
                Return Task.FromResult(True)
            End Function
        End Class

    End Class

End Namespace
