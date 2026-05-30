Imports TopStepTrader.API.Models.Responses
Imports TopStepTrader.Data.Entities
Imports TopStepTrader.Services.Trades
Imports Xunit

Namespace TopStepTrader.Tests.Services.Trades

    ''' <summary>
    ''' BUG-92: unit tests for the broker-fill ⇒ exit-price reconciliation helpers in
    ''' <c>TradeRecordService</c>. The pure Friend Shared helpers let us drive every
    ''' fill-shape scenario without a DB, broker mock, or DI container.
    ''' </summary>
    Public Class TradeRecordServiceReconciliationTests

        ' ── BuildContractMatcher ─────────────────────────────────────────────────

        <Fact>
        Public Sub BuildContractMatcher_RootPrefixSupplied_MatchesPxResolvedContractId()
            Dim matcher = TradeRecordService.BuildContractMatcher("MNQ", "CON.F.US.MNQ.")
            Assert.True(matcher("CON.F.US.MNQ.U26"))
            Assert.True(matcher("con.f.us.mnq.h27"))  ' case-insensitive
            Assert.False(matcher("CON.F.US.MES.U26"))  ' different root
            Assert.False(matcher(""))
            Assert.False(matcher(Nothing))
        End Sub

        <Fact>
        Public Sub BuildContractMatcher_NoRootPrefix_FallsBackToLiteralEquality()
            Dim matcher = TradeRecordService.BuildContractMatcher("CUSTOM_ID", Nothing)
            Assert.True(matcher("CUSTOM_ID"))
            Assert.True(matcher("custom_id"))  ' case-insensitive
            Assert.False(matcher("DIFFERENT"))
        End Sub

        <Fact>
        Public Sub TryResolveContractRootPrefix_KnownSymbol_ReturnsRootPrefix()
            Assert.Equal("CON.F.US.MNQ.", TradeRecordService.TryResolveContractRootPrefix("MNQ"))
            Assert.Equal("CON.F.US.MES.", TradeRecordService.TryResolveContractRootPrefix("MES"))
            Assert.Equal("CON.F.US.M2K.", TradeRecordService.TryResolveContractRootPrefix("M2K"))
        End Sub

        <Fact>
        Public Sub TryResolveContractRootPrefix_UnknownSymbol_ReturnsNothing()
            Assert.Null(TradeRecordService.TryResolveContractRootPrefix("ZZZ"))
            Assert.Null(TradeRecordService.TryResolveContractRootPrefix(""))
            Assert.Null(TradeRecordService.TryResolveContractRootPrefix(Nothing))
        End Sub

        ' ── ComputeExitReconciliation ────────────────────────────────────────────

        Private Shared Function Rec(direction As String, entryPrice As Decimal, sizes As Integer,
                                     symbol As String) As LiveTradeRecordEntity
            Return New LiveTradeRecordEntity With {
                .Id = 110L,
                .Symbol = symbol,
                .ContractId = symbol,
                .Direction = direction,
                .EntryPrice = entryPrice,
                .Sizes = sizes,
                .EntryTime = New DateTimeOffset(2026, 5, 22, 14, 0, 31, TimeSpan.Zero),
                .IsOpen = False
            }
        End Function

        Private Shared Function Fill(orderId As Long, side As Integer, price As Double,
                                      size As Integer, tsMs As Long,
                                      Optional contractId As String = "CON.F.US.MNQ.U26") As PXTradeDto
            Return New PXTradeDto With {
                .Id = orderId * 10L,
                .OrderId = orderId,
                .Side = side,
                .Price = price,
                .Size = size,
                .ContractId = contractId,
                .CreationTimestamp = tsMs.ToString()
            }
        End Function

        Private Shared Function MnqMatcher() As Func(Of String, Boolean)
            Return TradeRecordService.BuildContractMatcher("MNQ", "CON.F.US.MNQ.")
        End Function

        <Fact>
        Public Sub ComputeExitReconciliation_SingleClosingFill_LongLoss_UsesBrokerPrice()
            ' Mirrors trade #110 from the 2026-05-22 post-mortem: Long 4 @ 29616.5 → broker exit 29591.5 = -$200
            Dim entity = Rec("Long", 29616.5D, 4, "MNQ")
            Dim entryMs = entity.EntryTime.ToUnixTimeMilliseconds()
            Dim fills As IList(Of PXTradeDto) = New List(Of PXTradeDto) From {
                Fill(orderId:=3019266675L, side:=1, price:=29591.5, size:=4, tsMs:=entryMs + 17000L)
            }

            Dim plan = TradeRecordService.ComputeExitReconciliation(entity, fills, Nothing, MnqMatcher())

            Assert.NotNull(plan)
            Assert.Equal(29591.5D, plan.ExitPrice)
            Assert.Equal(-200.0D, plan.PnL)   ' (29591.5 - 29616.5) × 4 × $2/pt
            Assert.Equal(3019266675L, plan.ExitOrderId)
            Assert.True(plan.ExitTime.HasValue)
        End Sub

        <Fact>
        Public Sub ComputeExitReconciliation_SingleClosingFill_LongWin_PositivePnL()
            Dim entity = Rec("Long", 29570.5D, 4, "MNQ")
            Dim entryMs = entity.EntryTime.ToUnixTimeMilliseconds()
            Dim fills As IList(Of PXTradeDto) = New List(Of PXTradeDto) From {
                Fill(orderId:=3019364351L, side:=1, price:=29580.0, size:=4, tsMs:=entryMs + 30000L)
            }

            Dim plan = TradeRecordService.ComputeExitReconciliation(entity, fills, Nothing, MnqMatcher())

            Assert.NotNull(plan)
            Assert.Equal(29580.0D, plan.ExitPrice)
            Assert.Equal(76.0D, plan.PnL)   ' (29580 - 29570.5) × 4 × $2/pt = +$76
        End Sub

        <Fact>
        Public Sub ComputeExitReconciliation_ShortTrade_UsesOppositeSide()
            Dim entity = Rec("Short", 100.0D, 1, "MES")
            entity.ContractId = "MES"
            Dim entryMs = entity.EntryTime.ToUnixTimeMilliseconds()
            ' Short exits via Buy-side (Side=0). A Sell-side fill must NOT match.
            Dim sellMatcher = TradeRecordService.BuildContractMatcher("MES", "CON.F.US.MES.")
            Dim fills As IList(Of PXTradeDto) = New List(Of PXTradeDto) From {
                Fill(orderId:=1L, side:=1, price:=999.0, size:=1, tsMs:=entryMs + 5000L, contractId:="CON.F.US.MES.U26"),
                Fill(orderId:=2L, side:=0, price:=95.0, size:=1, tsMs:=entryMs + 10000L, contractId:="CON.F.US.MES.U26")
            }

            Dim plan = TradeRecordService.ComputeExitReconciliation(entity, fills, Nothing, sellMatcher)

            Assert.NotNull(plan)
            Assert.Equal(95.0D, plan.ExitPrice)
            Assert.Equal(2L, plan.ExitOrderId)
        End Sub

        <Fact>
        Public Sub ComputeExitReconciliation_PartialFills_UsesVolumeWeightedAverage()
            Dim entity = Rec("Long", 100.0D, 6, "MES")
            entity.ContractId = "MES"
            Dim entryMs = entity.EntryTime.ToUnixTimeMilliseconds()
            ' Two partial close fills: 4 @ 99.5 + 2 @ 98.0 → VWAP = (4*99.5 + 2*98) / 6 = 99.0
            Dim fills As IList(Of PXTradeDto) = New List(Of PXTradeDto) From {
                Fill(orderId:=42L, side:=1, price:=99.5, size:=4, tsMs:=entryMs + 5000L, contractId:="CON.F.US.MES.U26"),
                Fill(orderId:=42L, side:=1, price:=98.0, size:=2, tsMs:=entryMs + 6000L, contractId:="CON.F.US.MES.U26")
            }

            Dim plan = TradeRecordService.ComputeExitReconciliation(
                entity, fills, Nothing, TradeRecordService.BuildContractMatcher("MES", "CON.F.US.MES."))

            Assert.NotNull(plan)
            Assert.Equal(99.0D, plan.ExitPrice)
            Assert.Equal(42L, plan.ExitOrderId)   ' Shared OrderId on partials
        End Sub

        <Fact>
        Public Sub ComputeExitReconciliation_NoClosingFill_ReturnsNothing()
            Dim entity = Rec("Long", 100.0D, 1, "MNQ")
            Dim plan = TradeRecordService.ComputeExitReconciliation(
                entity, New List(Of PXTradeDto), Nothing, MnqMatcher())
            Assert.Null(plan)
        End Sub

        <Fact>
        Public Sub ComputeExitReconciliation_FillBeforeEntryTime_Ignored()
            Dim entity = Rec("Long", 100.0D, 1, "MNQ")
            Dim entryMs = entity.EntryTime.ToUnixTimeMilliseconds()
            ' A Sell fill from before EntryTime cannot be this trade's closing fill.
            Dim fills As IList(Of PXTradeDto) = New List(Of PXTradeDto) From {
                Fill(orderId:=1L, side:=1, price:=99.0, size:=1, tsMs:=entryMs - 60000L)
            }

            Dim plan = TradeRecordService.ComputeExitReconciliation(entity, fills, Nothing, MnqMatcher())
            Assert.Null(plan)
        End Sub

        <Fact>
        Public Sub ComputeExitReconciliation_DifferentContract_FilteredOut()
            Dim entity = Rec("Long", 100.0D, 1, "MNQ")
            Dim entryMs = entity.EntryTime.ToUnixTimeMilliseconds()
            ' A Sell fill on a different contract must not be picked.
            Dim fills As IList(Of PXTradeDto) = New List(Of PXTradeDto) From {
                Fill(orderId:=1L, side:=1, price:=99.0, size:=1, tsMs:=entryMs + 5000L,
                     contractId:="CON.F.US.MES.U26")
            }

            Dim plan = TradeRecordService.ComputeExitReconciliation(entity, fills, Nothing, MnqMatcher())
            Assert.Null(plan)
        End Sub

        ' ── BUG-102 F4-d: ResolveEntryPriceFromBrokerFills ────────────────────────

        Private Shared Function MnqEntry(entryOrderId As Long, direction As String,
                                          Optional entryPrice As Decimal = 0D) As LiveTradeRecordEntity
            Return New LiveTradeRecordEntity With {
                .Id = 200L,
                .Symbol = "MNQ",
                .ContractId = "MNQ",
                .Direction = direction,
                .EntryPrice = entryPrice,
                .Sizes = 1,
                .EntryOrderId = entryOrderId,
                .EntryTime = New DateTimeOffset(2026, 5, 20, 16, 27, 0, TimeSpan.Zero),
                .IsOpen = False
            }
        End Function

        <Fact>
        Public Sub ResolveEntryPriceFromBrokerFills_OrderAvgFillPrice_PreferredOverTrades()
            Dim entity = MnqEntry(entryOrderId:=4242L, direction:="Long")
            Dim orders As IList(Of PXOrderDto) = New List(Of PXOrderDto) From {
                New PXOrderDto With {
                    .Id = 4242L,
                    .ContractId = "CON.F.US.MNQ.U26",
                    .AvgFillPrice = 21345.5
                }
            }
            ' A stray trade fill at a different price; the order's AvgFillPrice should win.
            Dim trades As IList(Of PXTradeDto) = New List(Of PXTradeDto) From {
                New PXTradeDto With {
                    .OrderId = 4242L,
                    .Side = 0,
                    .Price = 99999.0,
                    .ContractId = "CON.F.US.MNQ.U26",
                    .CreationTimestamp = entity.EntryTime.AddSeconds(1).ToUnixTimeMilliseconds().ToString()
                }
            }

            Dim resolved = TradeRecordService.ResolveEntryPriceFromBrokerFills(entity, orders, trades)
            Assert.Equal(21345.5D, resolved)
        End Sub

        <Fact>
        Public Sub ResolveEntryPriceFromBrokerFills_NoOrder_FallsBackToTradeByOrderId()
            Dim entity = MnqEntry(entryOrderId:=4242L, direction:="Long")
            Dim trades As IList(Of PXTradeDto) = New List(Of PXTradeDto) From {
                New PXTradeDto With {
                    .OrderId = 4242L,
                    .Side = 0,
                    .Price = 21300.25,
                    .ContractId = "CON.F.US.MNQ.U26",
                    .CreationTimestamp = entity.EntryTime.AddSeconds(2).ToUnixTimeMilliseconds().ToString()
                }
            }

            Dim resolved = TradeRecordService.ResolveEntryPriceFromBrokerFills(entity, Nothing, trades)
            Assert.Equal(21300.25D, resolved)
        End Sub

        <Fact>
        Public Sub ResolveEntryPriceFromBrokerFills_NoEntryOrderId_FallsBackToFirstSideMatch()
            ' Reconciled-from-broker rows may not carry the entry order id. The matcher
            ' should still find the first long-side fill on the contract at/after EntryTime.
            Dim entity = MnqEntry(entryOrderId:=0L, direction:="Long")
            Dim entryMs = entity.EntryTime.ToUnixTimeMilliseconds()
            Dim trades As IList(Of PXTradeDto) = New List(Of PXTradeDto) From {
                New PXTradeDto With {
                    .OrderId = 1L,
                    .Side = 0,
                    .Price = 21500.0,
                    .ContractId = "CON.F.US.MNQ.U26",
                    .CreationTimestamp = (entryMs + 1000L).ToString()
                },
                New PXTradeDto With {
                    .OrderId = 2L,
                    .Side = 0,
                    .Price = 21550.0,
                    .ContractId = "CON.F.US.MNQ.U26",
                    .CreationTimestamp = (entryMs + 5000L).ToString()
                }
            }

            Dim resolved = TradeRecordService.ResolveEntryPriceFromBrokerFills(entity, Nothing, trades)
            Assert.Equal(21500.0D, resolved)   ' earliest matching fill wins
        End Sub

        <Fact>
        Public Sub ResolveEntryPriceFromBrokerFills_ShortTrade_UsesSellSide()
            Dim entity = MnqEntry(entryOrderId:=0L, direction:="Short")
            Dim entryMs = entity.EntryTime.ToUnixTimeMilliseconds()
            ' Short entries are Side=1 (Sell). A Buy-side fill must NOT match the fallback.
            Dim trades As IList(Of PXTradeDto) = New List(Of PXTradeDto) From {
                New PXTradeDto With {
                    .OrderId = 1L,
                    .Side = 0,
                    .Price = 99999.0,
                    .ContractId = "CON.F.US.MNQ.U26",
                    .CreationTimestamp = (entryMs + 1000L).ToString()
                },
                New PXTradeDto With {
                    .OrderId = 2L,
                    .Side = 1,
                    .Price = 21470.0,
                    .ContractId = "CON.F.US.MNQ.U26",
                    .CreationTimestamp = (entryMs + 2000L).ToString()
                }
            }

            Dim resolved = TradeRecordService.ResolveEntryPriceFromBrokerFills(entity, Nothing, trades)
            Assert.Equal(21470.0D, resolved)
        End Sub

        <Fact>
        Public Sub ResolveEntryPriceFromBrokerFills_FillBeforeEntryTime_NotUsed()
            Dim entity = MnqEntry(entryOrderId:=0L, direction:="Long")
            Dim entryMs = entity.EntryTime.ToUnixTimeMilliseconds()
            Dim trades As IList(Of PXTradeDto) = New List(Of PXTradeDto) From {
                New PXTradeDto With {
                    .OrderId = 1L,
                    .Side = 0,
                    .Price = 21000.0,
                    .ContractId = "CON.F.US.MNQ.U26",
                    .CreationTimestamp = (entryMs - 60000L).ToString()  ' 1 minute before entry
                }
            }

            Dim resolved = TradeRecordService.ResolveEntryPriceFromBrokerFills(entity, Nothing, trades)
            Assert.Equal(0D, resolved)
        End Sub

        <Fact>
        Public Sub ResolveEntryPriceFromBrokerFills_NoBrokerData_ReturnsZero()
            Dim entity = MnqEntry(entryOrderId:=4242L, direction:="Long")
            Assert.Equal(0D, TradeRecordService.ResolveEntryPriceFromBrokerFills(entity, Nothing, Nothing))
            Assert.Equal(0D, TradeRecordService.ResolveEntryPriceFromBrokerFills(
                entity, New List(Of PXOrderDto)(), New List(Of PXTradeDto)()))
        End Sub

        <Fact>
        Public Sub ResolveEntryPriceFromBrokerFills_DifferentContract_FilteredOut()
            Dim entity = MnqEntry(entryOrderId:=0L, direction:="Long")
            Dim entryMs = entity.EntryTime.ToUnixTimeMilliseconds()
            Dim trades As IList(Of PXTradeDto) = New List(Of PXTradeDto) From {
                New PXTradeDto With {
                    .OrderId = 1L,
                    .Side = 0,
                    .Price = 21500.0,
                    .ContractId = "CON.F.US.MES.U26",   ' wrong contract
                    .CreationTimestamp = (entryMs + 1000L).ToString()
                }
            }

            Dim resolved = TradeRecordService.ResolveEntryPriceFromBrokerFills(entity, Nothing, trades)
            Assert.Equal(0D, resolved)
        End Sub

        <Fact>
        Public Sub ComputeExitReconciliation_OrdersFilter_NarrowsToClosingOrderIds()
            ' When orders are supplied, only fills whose OrderId is a closing market order
            ' (OrderType=2, opposite side, after EntryTime) should be used. Stray Sell fills
            ' from an unrelated order must be filtered out.
            Dim entity = Rec("Long", 100.0D, 1, "MNQ")
            Dim entryMs = entity.EntryTime.ToUnixTimeMilliseconds()
            Dim closingOrder As New PXOrderDto With {
                .Id = 999L,
                .ContractId = "CON.F.US.MNQ.U26",
                .Side = 1,
                .OrderType = 2,
                .CreationTimestamp = (entryMs + 1000L).ToString()
            }
            Dim orders As IList(Of PXOrderDto) = New List(Of PXOrderDto) From {closingOrder}
            Dim fills As IList(Of PXTradeDto) = New List(Of PXTradeDto) From {
                Fill(orderId:=999L, side:=1, price:=98.0, size:=1, tsMs:=entryMs + 1500L),
                Fill(orderId:=111L, side:=1, price:=50.0, size:=1, tsMs:=entryMs + 2000L)   ' unrelated Sell
            }

            Dim plan = TradeRecordService.ComputeExitReconciliation(entity, fills, orders, MnqMatcher())

            Assert.NotNull(plan)
            Assert.Equal(98.0D, plan.ExitPrice)   ' VWAP'd only over OrderId=999
            Assert.Equal(999L, plan.ExitOrderId)
        End Sub

    End Class

End Namespace
