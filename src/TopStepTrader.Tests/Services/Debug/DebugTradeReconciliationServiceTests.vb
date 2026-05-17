Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Data.Sqlite
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.API.Models.Responses
Imports TopStepTrader.Core.Models.Debug
Imports TopStepTrader.Data.Debug
Imports TopStepTrader.Services.Debug
Imports Xunit

Namespace TopStepTrader.Tests.Services.Debug

    ''' <summary>BUG-83 F8 acceptance tests for DebugTradeReconciliationService.</summary>
    Public Class DebugTradeReconciliationServiceTests
        Implements IDisposable

        Private ReadOnly _dbPath As String
        Private ReadOnly _connStr As String
        Private ReadOnly _db As DebugTradeDbContext

        Public Sub New()
            _dbPath = Path.Combine(Path.GetTempPath(), $"bug83_{Guid.NewGuid():N}.db")
            _connStr = $"Data Source={_dbPath}"
            _db = New DebugTradeDbContext(_connStr)
            _db.EnsureSchemaAsync().GetAwaiter().GetResult()
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            Try
                _db.Dispose()
                SqliteConnection.ClearAllPools()
                If File.Exists(_dbPath) Then File.Delete(_dbPath)
            Catch
            End Try
        End Sub

        ' -- Fixtures -------------------------------------------------------------

        Private Class StubOrderClient
            Implements IDebugReconciliationOrderClient

            Public Positions As New List(Of PXPositionDto)()
            Public Trades As New List(Of PXTradeDto)()
            Public Orders As New List(Of PXOrderDto)()
            Public OrdersCalls As Integer
            Public TradesCalls As Integer
            Public PositionsCalls As Integer

            Public Function SearchOpenPositionsAsync(accountId As Long, cancel As CancellationToken) _
                As Task(Of PXPositionSearchResponse) Implements IDebugReconciliationOrderClient.SearchOpenPositionsAsync
                PositionsCalls += 1
                Return Task.FromResult(New PXPositionSearchResponse With {.Positions = Positions})
            End Function

            Public Function SearchTradesAsync(accountId As Long, startTimestamp As Long?, endTimestamp As Long?, cancel As CancellationToken) _
                As Task(Of PXTradeSearchResponse) Implements IDebugReconciliationOrderClient.SearchTradesAsync
                TradesCalls += 1
                Return Task.FromResult(New PXTradeSearchResponse With {.Trades = Trades})
            End Function

            Public Function SearchOrdersAsync(accountId As Long, startTimestamp As Long?, endTimestamp As Long?, cancel As CancellationToken) _
                As Task(Of PXOrderSearchResponse) Implements IDebugReconciliationOrderClient.SearchOrdersAsync
                OrdersCalls += 1
                Return Task.FromResult(New PXOrderSearchResponse With {.Orders = Orders})
            End Function
        End Class

        Private Async Function SeedTradeAsync(
                tradeId As String,
                accountId As Long,
                instrument As String,
                direction As String,
                entryPrice As Decimal,
                initialSl As Decimal,
                initialTp As Decimal,
                Optional entryTime As String = Nothing,
                Optional closedAt As String = Nothing) As Task
            Dim t As New DebugTradeRecord With {
                .TradeId = tradeId,
                .SlotIndex = 0,
                .Persona = "Test",
                .Instrument = instrument,
                .TimeFrame = "5m",
                .EntryMode = "Test",
                .Direction = direction,
                .EntryPrice = entryPrice,
                .EntryTime = If(entryTime, DateTime.UtcNow.AddMinutes(-30).ToString("O")),
                .InitialSL = initialSl,
                .InitialTP = initialTp,
                .ContractCount = 1,
                .SuperTrendConfigJson = "{}",
                .ClosedAt = closedAt,
                .CreatedAt = DateTime.UtcNow.ToString("O"),
                .AccountId = accountId
            }
            Await _db.WriteBatchAsync(
                New List(Of DebugTradeRecord) From {t},
                New List(Of DebugSnapshotRecord)(),
                New List(Of (TradeId As String, ClosedUtc As DateTime, RealisedPnl As Nullable(Of Decimal)))())
        End Function

        Private Async Function SeedActionAsync(tradeId As String, actionType As String, newValue As Decimal,
                                                Optional timestampUtc As String = Nothing,
                                                Optional source As String = "Local",
                                                Optional orderId As Long? = Nothing,
                                                Optional price As Decimal? = Nothing) As Task
            Dim a As New DebugTradeAction With {
                .TradeId = tradeId,
                .TimestampUtc = If(timestampUtc, DateTime.UtcNow.ToString("O")),
                .ActionType = actionType,
                .NewValue = newValue,
                .Source = source,
                .OrderId = orderId,
                .Price = price
            }
            Await _db.WriteBatchAsync(
                New List(Of DebugTradeRecord)(),
                New List(Of DebugSnapshotRecord)(),
                New List(Of (TradeId As String, ClosedUtc As DateTime, RealisedPnl As Nullable(Of Decimal)))(),
                Nothing,
                New List(Of DebugTradeAction) From {a})
        End Function

        Private Function NewSvc(stub As IDebugReconciliationOrderClient) As DebugTradeReconciliationService
            Return New DebugTradeReconciliationService(_db, stub, NullLogger(Of DebugTradeReconciliationService).Instance)
        End Function

        Private Async Function GetTradeAsync(tradeId As String) As Task(Of DebugTradeRecord)
            Dim all = Await _db.GetAllTradesAsync()
            Return all.FirstOrDefault(Function(x) x.TradeId = tradeId)
        End Function

        ' -- Tests ----------------------------------------------------------------

        <Fact>
        Public Async Function SlHit_ByOrderType_Stop_ReturnsSlHit() As Task
            Await SeedTradeAsync("T1", 100, "MES", "Long", 5000D, 4990D, 5020D)

            Dim stub As New StubOrderClient()
            stub.Trades.Add(New PXTradeDto With {
                .Id = 1, .OrderId = 10, .ContractId = "MES", .Side = 1,
                .Price = 4985, .Size = 1, .CreationTimestamp = DateTime.UtcNow.ToString("O")
            })
            stub.Orders.Add(New PXOrderDto With {.Id = 10, .OrderType = 4, .ContractId = "MES"})

            Await NewSvc(stub).ReconcileOpenTradesAsync()

            Dim t = Await GetTradeAsync("T1")
            Assert.Equal("Reconciled", t.ReconciliationStatus)
            Assert.Equal("SL Hit", t.ExitReason)
        End Function

        <Fact>
        Public Async Function TpHit_ByOrderType_LimitAtTp_ReturnsTpHit() As Task
            Await SeedTradeAsync("T2", 100, "MES", "Long", 5000D, 4990D, 5020D)

            Dim stub As New StubOrderClient()
            stub.Trades.Add(New PXTradeDto With {
                .Id = 1, .OrderId = 11, .ContractId = "MES", .Side = 1,
                .Price = 5020, .Size = 1, .CreationTimestamp = DateTime.UtcNow.ToString("O")
            })
            stub.Orders.Add(New PXOrderDto With {.Id = 11, .OrderType = 1, .LimitPrice = 5020.0R, .ContractId = "MES"})

            Await NewSvc(stub).ReconcileOpenTradesAsync()

            Dim t = Await GetTradeAsync("T2")
            Assert.Equal("TP Hit", t.ExitReason)
        End Function

        <Fact>
        Public Async Function TpStyleLimit_NotAtTp_ReturnsManualLimit() As Task
            Await SeedTradeAsync("T3", 100, "MES", "Long", 5000D, 4990D, 5020D)

            Dim stub As New StubOrderClient()
            stub.Trades.Add(New PXTradeDto With {
                .Id = 1, .OrderId = 12, .ContractId = "MES", .Side = 1,
                .Price = 5010, .Size = 1, .CreationTimestamp = DateTime.UtcNow.ToString("O")
            })
            stub.Orders.Add(New PXOrderDto With {.Id = 12, .OrderType = 1, .LimitPrice = 5010.0R, .ContractId = "MES"})

            Await NewSvc(stub).ReconcileOpenTradesAsync()

            Dim t = Await GetTradeAsync("T3")
            Assert.Equal("Manual / Engine Flatten (limit)", t.ExitReason)
        End Function

        <Fact>
        Public Async Function ManualFlatten_MarketOrder_ReturnsManual() As Task
            Await SeedTradeAsync("T4", 100, "MES", "Long", 5000D, 4990D, 5020D)

            Dim stub As New StubOrderClient()
            stub.Trades.Add(New PXTradeDto With {
                .Id = 1, .OrderId = 13, .ContractId = "MES", .Side = 1,
                .Price = 5005, .Size = 1, .CreationTimestamp = DateTime.UtcNow.ToString("O")
            })
            stub.Orders.Add(New PXOrderDto With {.Id = 13, .OrderType = 2, .ContractId = "MES"})

            Await NewSvc(stub).ReconcileOpenTradesAsync()

            Dim t = Await GetTradeAsync("T4")
            Assert.Equal("Manual / Engine Flatten", t.ExitReason)
        End Function

        <Fact>
        Public Async Function LatestSlFallback_NoParentOrder_UsesActionLog() As Task
            ' Entry 5000, InitialSL 4990, but SL was ratcheted up to 5005.
            ' Closing fill at 5005 should classify as SL Hit (not 4990 proximity).
            Await SeedTradeAsync("T5", 100, "MES", "Long", 5000D, 4990D, 5020D)
            Await SeedActionAsync("T5", "StopLossModified", 5005D)

            Dim stub As New StubOrderClient()
            ' No matching order in Orders list - parent order lookup returns Nothing.
            stub.Trades.Add(New PXTradeDto With {
                .Id = 1, .OrderId = 99, .ContractId = "MES", .Side = 1,
                .Price = 5005, .Size = 1, .CreationTimestamp = DateTime.UtcNow.ToString("O")
            })

            Await NewSvc(stub).ReconcileOpenTradesAsync()

            Dim t = Await GetTradeAsync("T5")
            Assert.Equal("SL Hit", t.ExitReason)
        End Function

        <Fact>
        Public Async Function AlreadyClosedTrade_IsSkipped() As Task
            Await SeedTradeAsync("T6", 100, "MES", "Long", 5000D, 4990D, 5020D,
                                 closedAt:=DateTime.UtcNow.ToString("O"))

            Dim stub As New StubOrderClient()
            Dim updated = Await NewSvc(stub).ReconcileOpenTradesAsync()

            Assert.Equal(0, stub.PositionsCalls)
            Assert.Equal(0, updated)
            Dim t = Await GetTradeAsync("T6")
            Assert.Null(t.ReconciliationStatus)
        End Function

        <Fact>
        Public Async Function NoFills_ReturnsUnreconciled() As Task
            Await SeedTradeAsync("T7", 100, "MES", "Long", 5000D, 4990D, 5020D)

            Dim stub As New StubOrderClient()
            Await NewSvc(stub).ReconcileOpenTradesAsync()

            Dim t = Await GetTradeAsync("T7")
            Assert.Equal("Unreconciled", t.ReconciliationStatus)
        End Function

        <Fact>
        Public Async Function AccountIdZero_TaggedAndNoBrokerCalls() As Task
            Await SeedTradeAsync("T8", 0, "MES", "Long", 5000D, 4990D, 5020D)

            Dim stub As New StubOrderClient()
            Await NewSvc(stub).ReconcileOpenTradesAsync()

            Dim t = Await GetTradeAsync("T8")
            Assert.Equal("Unreconciled - no AccountId", t.ReconciliationStatus)
            Assert.Equal(0, stub.PositionsCalls)
            Assert.Equal(0, stub.TradesCalls)
            Assert.Equal(0, stub.OrdersCalls)
        End Function

        <Fact>
        Public Async Function BadEntryTime_TaggedAndIsolated() As Task
            ' Bad trade poisons nothing - other trade on same account still reconciles.
            Await SeedTradeAsync("BAD", 100, "MES", "Long", 5000D, 4990D, 5020D, entryTime:="not-a-date")
            Await SeedTradeAsync("GOOD", 100, "MES", "Long", 5000D, 4990D, 5020D)

            Dim stub As New StubOrderClient()
            stub.Trades.Add(New PXTradeDto With {
                .Id = 1, .OrderId = 20, .ContractId = "MES", .Side = 1,
                .Price = 5020, .Size = 1, .CreationTimestamp = DateTime.UtcNow.ToString("O")
            })
            stub.Orders.Add(New PXOrderDto With {.Id = 20, .OrderType = 1, .LimitPrice = 5020.0R, .ContractId = "MES"})

            Await NewSvc(stub).ReconcileOpenTradesAsync()

            Dim bad = Await GetTradeAsync("BAD")
            Dim good = Await GetTradeAsync("GOOD")
            Assert.Equal("Failed - bad EntryTime", bad.ReconciliationStatus)
            Assert.Null(bad.ClosedAt)
            Assert.Equal("Reconciled", good.ReconciliationStatus)
        End Function

        <Fact>
        Public Async Function LocalPlusApi_Merge_ProducesSingleClosedRow() As Task
            Await SeedTradeAsync("T9", 100, "MES", "Long", 5000D, 4990D, 5020D)
            ' Pre-seed a local Closed action with matching OrderId.
            Dim closedTs = DateTime.UtcNow.ToString("O")
            Await SeedActionAsync("T9", "Closed", 0D, timestampUtc:=closedTs,
                                  source:="Local", orderId:=50L, price:=4999D)

            Dim stub As New StubOrderClient()
            stub.Trades.Add(New PXTradeDto With {
                .Id = 1, .OrderId = 50, .ContractId = "MES", .Side = 1,
                .Price = 5000, .Size = 1, .CreationTimestamp = closedTs
            })
            stub.Orders.Add(New PXOrderDto With {.Id = 50, .OrderType = 2, .ContractId = "MES"})

            Await NewSvc(stub).ReconcileOpenTradesAsync()

            Dim actions = Await _db.GetActionsAsync("T9")
            Dim closedRows = actions.Where(Function(a) a.ActionType = "Closed").ToList()
            Assert.Single(closedRows)
            Assert.Equal("Local+Api", closedRows(0).Source)
        End Function

        <Fact>
        Public Async Function StillOpen_PositionPresent_NoSyntheticClosed() As Task
            Await SeedTradeAsync("T10", 100, "MES", "Long", 5000D, 4990D, 5020D)

            Dim stub As New StubOrderClient()
            stub.Positions.Add(New PXPositionDto With {.ContractId = "MES", .Size = 1, .PositionType = 1})

            Await NewSvc(stub).ReconcileOpenTradesAsync()

            Dim t = Await GetTradeAsync("T10")
            Assert.Equal("StillOpen", t.ReconciliationStatus)
            Dim actions = Await _db.GetActionsAsync("T10")
            Assert.Empty(actions.Where(Function(a) a.ActionType = "Closed"))
        End Function

    End Class

End Namespace
