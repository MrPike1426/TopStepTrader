Imports TopStepTrader.API.Models.Responses
Imports TopStepTrader.Services.Trades
Imports Xunit

Namespace TopStepTrader.Tests.Services.Trades

    ''' <summary>
    ''' OBS-07 F4: regression tests for the snapshot-capture diagnostic instrumentation.
    ''' Drives the Friend Shared pure helpers extracted from
    ''' <c>TradeRecordService.CaptureClosingSnapshotsAsync</c> so every failure mode in
    ''' the OBS-07 ticket (B/C/D) has at least one dedicated guard.
    ''' </summary>
    Public Class CaptureClosingSnapshotsDiagnosticTests

        Private Const RecordId As Long = 110L

        Private Shared Function MnqMatcher() As Func(Of String, Boolean)
            Return TradeRecordService.BuildContractMatcher("MNQ", "CON.F.US.MNQ.")
        End Function

        Private Shared Function Order(id As Long, contractId As String) As PXOrderDto
            Return New PXOrderDto With {
                .Id = id,
                .ContractId = contractId,
                .OrderType = 2,
                .Side = 1,
                .Size = 4,
                .Status = 2,
                .CreationTimestamp = "1716386400000"
            }
        End Function

        Private Shared Function Position(id As Long, contractId As String) As PXPositionDto
            Return New PXPositionDto With {
                .Id = id,
                .ContractId = contractId,
                .PositionType = 1,
                .Size = 4,
                .AveragePrice = 29606.0,
                .OpenPnL = 0.0,
                .CreationTimestamp = "1716386400000"
            }
        End Function

        Private Shared Function Trade(id As Long, contractId As String,
                                       Optional price As Double = 29591.5,
                                       Optional size As Integer = 2) As PXTradeDto
            Return New PXTradeDto With {
                .Id = id,
                .OrderId = id,
                .ContractId = contractId,
                .Side = 1,
                .Price = price,
                .Size = size,
                .CreationTimestamp = "1716386431000"
            }
        End Function

        ' ── F4-a: matcher happy path — 3 orders / 1 position / 2 fills, all matching ──

        <Fact>
        Public Sub MapMatching_HappyPath_AllRowsPersistedWithMatchingContractId()
            Dim orders As IList(Of PXOrderDto) = New List(Of PXOrderDto) From {
                Order(1L, "CON.F.US.MNQ.U26"),
                Order(2L, "CON.F.US.MNQ.U26"),
                Order(3L, "CON.F.US.MNQ.U26")
            }
            Dim positions As IList(Of PXPositionDto) = New List(Of PXPositionDto) From {
                Position(10L, "CON.F.US.MNQ.U26")
            }
            Dim fills As IList(Of PXTradeDto) = New List(Of PXTradeDto) From {
                Trade(100L, "CON.F.US.MNQ.U26"),
                Trade(101L, "CON.F.US.MNQ.U26")
            }

            Dim entry = New DateTimeOffset(2026, 5, 22, 14, 0, 31, TimeSpan.Zero)
            Dim exitTime As DateTimeOffset? = entry.AddMinutes(2)
            Dim mappedOrders = TradeRecordService.MapMatchingOrders(orders, MnqMatcher(), RecordId)
            Dim mappedPositions = TradeRecordService.MapMatchingPositions(positions, MnqMatcher(), RecordId, entry, exitTime)
            Dim mappedFills = TradeRecordService.MapMatchingFills(fills, MnqMatcher(), RecordId)

            Assert.Equal(3, mappedOrders.Count)
            Assert.Equal(1, mappedPositions.Count)
            Assert.Equal(2, mappedFills.Count)
            Assert.All(mappedOrders, Sub(r) Assert.Equal(RecordId, r.LiveTradeRecordId))
            Assert.All(mappedPositions, Sub(r) Assert.Equal(RecordId, r.LiveTradeRecordId))
            Assert.All(mappedFills, Sub(r) Assert.Equal(RecordId, r.LiveTradeRecordId))
        End Sub

        ' ── F4-b: broker rows on a non-matching ContractId — matcher rejects all (hypothesis C) ──

        <Fact>
        Public Sub MapMatching_NonMatchingContractId_RejectsAllRows_AndDetectorSurfacesIt()
            Dim orders As IList(Of PXOrderDto) = New List(Of PXOrderDto) From {
                Order(1L, "CON.F.US.MES.U26"),
                Order(2L, "CON.F.US.MES.U26"),
                Order(3L, "CON.F.US.MES.U26")
            }
            Dim mappedOrders = TradeRecordService.MapMatchingOrders(orders, MnqMatcher(), RecordId)
            Assert.Empty(mappedOrders)

            Dim diag = TradeRecordService.DetectMatcherRejection(
                orders, MnqMatcher(), Function(o As PXOrderDto) o.ContractId)
            Assert.True(diag.Rejected)
            Assert.Equal(3, diag.Count)
            Assert.Contains("CON.F.US.MES.U26", diag.Sample)
        End Sub

        <Fact>
        Public Sub DetectMatcherRejection_AcceptsAtLeastOne_NotRejected()
            Dim orders As IList(Of PXOrderDto) = New List(Of PXOrderDto) From {
                Order(1L, "CON.F.US.MNQ.U26"),
                Order(2L, "CON.F.US.MES.U26")
            }
            Dim diag = TradeRecordService.DetectMatcherRejection(
                orders, MnqMatcher(), Function(o As PXOrderDto) o.ContractId)
            Assert.False(diag.Rejected)
        End Sub

        ' ── F4-c: accountId = 0 — explicit Warning instead of silent skip (hypothesis B) ──

        <Fact>
        Public Sub FormatAccountIdSkipWarning_NamesTheRecord_AndSurfacesAccountIdZero()
            Dim msg = TradeRecordService.FormatAccountIdSkipWarning(RecordId)
            Assert.Contains("skipped", msg, StringComparison.OrdinalIgnoreCase)
            Assert.Contains(RecordId.ToString(), msg)
            Assert.Contains("accountId=0", msg, StringComparison.OrdinalIgnoreCase)
            Assert.Contains("no SelectedAccount", msg, StringComparison.OrdinalIgnoreCase)
        End Sub

        ' ── F4-d: SafePxCallAsync returning Nothing (timeout) — empty paths still safe (hypothesis D) ──

        <Fact>
        Public Sub MapMatching_NothingResponses_ReturnEmptyListsWithoutThrowing()
            Dim orders = TradeRecordService.MapMatchingOrders(Nothing, MnqMatcher(), RecordId)
            Dim positions = TradeRecordService.MapMatchingPositions(Nothing, MnqMatcher(), RecordId,
                New DateTimeOffset(2026, 5, 22, 14, 0, 31, TimeSpan.Zero), Nothing)
            Dim fills = TradeRecordService.MapMatchingFills(Nothing, MnqMatcher(), RecordId)
            Assert.Empty(orders)
            Assert.Empty(positions)
            Assert.Empty(fills)

            Dim diag = TradeRecordService.DetectMatcherRejection(
                CType(Nothing, IList(Of PXOrderDto)),
                MnqMatcher(),
                Function(o As PXOrderDto) o.ContractId)
            Assert.False(diag.Rejected)
            Assert.Equal(0, diag.Count)
        End Sub

        <Fact>
        Public Sub MapMatching_OneRespEmptyOtherPopulated_StillWritesPopulatedRows()
            ' Mirrors a real partial-failure scenario: SearchOrdersAsync timed out (Nothing),
            ' but SearchTradesAsync returned 2 valid fills. The fills path must still produce
            ' rows so partial telemetry survives the timeout.
            Dim fills As IList(Of PXTradeDto) = New List(Of PXTradeDto) From {
                Trade(100L, "CON.F.US.MNQ.U26"),
                Trade(101L, "CON.F.US.MNQ.U26")
            }
            Dim mappedOrders = TradeRecordService.MapMatchingOrders(Nothing, MnqMatcher(), RecordId)
            Dim mappedFills = TradeRecordService.MapMatchingFills(fills, MnqMatcher(), RecordId)
            Assert.Empty(mappedOrders)
            Assert.Equal(2, mappedFills.Count)
        End Sub

    End Class

End Namespace
