Imports System.IO
Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Models.Debug
Imports TopStepTrader.Data.Debug
Imports TopStepTrader.Services.Debug
Imports Xunit

Namespace TopStepTrader.Tests.Debug

    Public Class DebugTradeCaptureServiceTests

        Private Shared Function MakeTempDb() As (DbContext As DebugTradeDbContext, Service As DebugTradeCaptureService)
            Dim dbPath = Path.Combine(Path.GetTempPath(), $"test_debug_{Guid.NewGuid():N}.db")
            Dim connString = $"Data Source={dbPath}"
            Dim db = New DebugTradeDbContext(connString, maxTradeCount:=100)
            db.EnsureSchemaAsync().GetAwaiter().GetResult()
            Dim svc = New DebugTradeCaptureService(db, NullLogger(Of DebugTradeCaptureService).Instance)
            Return (db, svc)
        End Function

        Private Shared Function MakeHeader(Optional tradeId As String = Nothing) As DebugTradeRecord
            Return New DebugTradeRecord With {
                .TradeId = If(tradeId, Guid.NewGuid().ToString("D")),
                .SlotIndex = 0,
                .Persona = "Damian",
                .Instrument = "MES",
                .TimeFrame = "15min",
                .EntryMode = "BarClose",
                .Direction = "Long",
                .EntryPrice = 5000D,
                .EntryTime = DateTime.UtcNow.ToString("O"),
                .InitialSL = 4990D,
                .InitialTP = 0D,
                .ContractCount = 1,
                .SuperTrendConfigJson = "{}",
                .CreatedAt = DateTime.UtcNow.ToString("O")
            }
        End Function

        Private Shared Function MakeSnap(tradeId As String, eventType As String) As DebugSnapshotRecord
            Return New DebugSnapshotRecord With {
                .TradeId = tradeId,
                .Timestamp = DateTime.UtcNow.ToString("O"),
                .EventType = eventType,
                .LastPrice = 5010D
            }
        End Function

        ' ── When disabled, BeginTrade/RecordSnapshot are no-ops (no DB rows) ──

        <Fact>
        Public Async Function WhenDisabled_BeginTrade_WritesNoRows() As Task
            Dim pair = MakeTempDb()
            Using pair.Service
                pair.Service.IsEnabled = False
                Dim header = MakeHeader()
                pair.Service.BeginTrade(header)
                Await Task.Delay(2200)
                Dim count = Await pair.DbContext.CountTradesAsync()
                Assert.Equal(0, count)
            End Using
        End Function

        <Fact>
        Public Async Function WhenDisabled_RecordSnapshot_WritesNoRows() As Task
            Dim pair = MakeTempDb()
            Using pair.Service
                pair.Service.IsEnabled = False
                Dim tid = Guid.NewGuid().ToString("D")
                pair.Service.RecordSnapshot(MakeSnap(tid, "Heartbeat"))
                Await Task.Delay(2200)
                Dim count = Await pair.DbContext.CountSnapshotsAsync(tid)
                Assert.Equal(0, count)
            End Using
        End Function

        ' ── When enabled: BeginTrade + 3x RecordSnapshot + EndTrade produces correct rows ──

        <Fact>
        Public Async Function WhenEnabled_FullLifecycle_WritesHeaderAndSnapshots() As Task
            Dim pair = MakeTempDb()
            Using pair.Service
                pair.Service.IsEnabled = True
                Dim tid = Guid.NewGuid().ToString("D")
                Dim header = MakeHeader(tid)
                pair.Service.BeginTrade(header)
                pair.Service.RecordSnapshot(MakeSnap(tid, "Heartbeat"))
                pair.Service.RecordSnapshot(MakeSnap(tid, "Heartbeat"))
                pair.Service.RecordSnapshot(MakeSnap(tid, "SlAdjust"))
                pair.Service.EndTrade(tid, DateTime.UtcNow)
                Await Task.Delay(2200)
                Assert.Equal(1, Await pair.DbContext.CountTradesAsync())
                Assert.Equal(3, Await pair.DbContext.CountSnapshotsAsync(tid))
                Dim closedAt = Await pair.DbContext.GetClosedAtAsync(tid)
                Assert.NotNull(closedAt)
            End Using
        End Function

        ' ── SQLite write failure does NOT throw out of RecordSnapshot ──

        <Fact>
        Public Sub WriteFailure_DoesNotThrowFromRecordSnapshot()
            Dim badConn = "Data Source=Z:\\nonexistent\\invalid_path\\test.db"
            Dim db = New DebugTradeDbContext(badConn)
            Using svc = New DebugTradeCaptureService(db, NullLogger(Of DebugTradeCaptureService).Instance)
                svc.IsEnabled = True
                Dim ex As Exception = Nothing
                Try
                    svc.RecordSnapshot(MakeSnap(Guid.NewGuid().ToString("D"), "Heartbeat"))
                Catch e As Exception
                    ex = e
                End Try
                Assert.Null(ex)
            End Using
        End Sub

        ' ── FEAT-41: UpdateFill writes ActualFillPrice and FillConfirmedTime ──

        <Fact>
        Public Async Function UpdateFill_WritesActualFillPriceAndConfirmedTime() As Task
            Dim pair = MakeTempDb()
            Using pair.Service
                pair.Service.IsEnabled = True
                Dim tid = Guid.NewGuid().ToString("D")
                pair.Service.BeginTrade(MakeHeader(tid))
                pair.Service.UpdateFill(tid, 5012.5D, DateTime.UtcNow)
                Await Task.Delay(2200)
                Dim fillPrice = Await pair.DbContext.GetActualFillPriceAsync(tid)
                Dim fillTime = Await pair.DbContext.GetFillConfirmedTimeAsync(tid)
                Assert.NotNull(fillPrice)
                Assert.Equal(5012.5D, fillPrice.Value)
                Assert.NotNull(fillTime)
            End Using
        End Function

        ' ── FEAT-41: EndTrade with realisedPnl writes RealisedPnLDollars ──

        <Fact>
        Public Async Function EndTrade_WithRealisedPnl_WritesRealisedPnLDollars() As Task
            Dim pair = MakeTempDb()
            Using pair.Service
                pair.Service.IsEnabled = True
                Dim tid = Guid.NewGuid().ToString("D")
                pair.Service.BeginTrade(MakeHeader(tid))
                pair.Service.EndTrade(tid, DateTime.UtcNow, 125.5D)
                Await Task.Delay(2200)
                Dim pnl = Await pair.DbContext.GetRealisedPnLAsync(tid)
                Assert.NotNull(pnl)
                Assert.Equal(125.5D, pnl.Value)
            End Using
        End Function

        ' ── FEAT-41: Snapshots include non-null Adx and StopPhase ──

        <Fact>
        Public Async Function Snapshot_WithAdxAndStopPhase_WritesNonNullValues() As Task
            Dim pair = MakeTempDb()
            Using pair.Service
                pair.Service.IsEnabled = True
                Dim tid = Guid.NewGuid().ToString("D")
                pair.Service.BeginTrade(MakeHeader(tid))
                Dim snap = MakeSnap(tid, "Heartbeat")
                snap.Adx = 28.5F
                snap.StopPhase = "Breakeven"
                pair.Service.RecordSnapshot(snap)
                Await Task.Delay(2200)
                Dim rows = Await pair.DbContext.GetSnapshotAdxAndPhaseAsync(tid)
                Assert.Single(rows)
                Assert.NotNull(rows(0).Adx)
                Assert.InRange(rows(0).Adx.Value, 28.4, 28.6)
                Assert.Equal("Breakeven", rows(0).StopPhase)
            End Using
        End Function

        ' ── FEAT-41: EnsureSchemaAsync is idempotent (calling twice causes no error) ──

        <Fact>
        Public Async Function EnsureSchema_IsIdempotent_NoErrorOnDoubleCall() As Task
            Dim dbPath = Path.Combine(Path.GetTempPath(), $"test_idempotent_{Guid.NewGuid():N}.db")
            Dim connString = $"Data Source={dbPath}"
            Dim db = New DebugTradeDbContext(connString)
            Await db.EnsureSchemaAsync()
            Dim ex As Exception = Nothing
            Try
                Await db.EnsureSchemaAsync()
            Catch e As Exception
                ex = e
            End Try
            Assert.Null(ex)
            db.Dispose()
        End Function

        <Fact>
        Public Async Function Purge_RemovesOldRowsAndTrimsToMax() As Task
            Dim dbPath = Path.Combine(Path.GetTempPath(), $"test_purge_{Guid.NewGuid():N}.db")
            Dim connString = $"Data Source={dbPath}"
            Dim db = New DebugTradeDbContext(connString, maxTradeCount:=3)
            Await db.EnsureSchemaAsync()

            ' Insert 5 trades: 2 old (>30 days), 3 recent
            Dim headers As New List(Of DebugTradeRecord)()
            For i = 1 To 5
                Dim ts = If(i <= 2,
                    DateTime.UtcNow.AddDays(-35).AddSeconds(i).ToString("O"),
                    DateTime.UtcNow.AddSeconds(i).ToString("O"))
                headers.Add(New DebugTradeRecord With {
                    .TradeId = Guid.NewGuid().ToString("D"),
                    .SlotIndex = 0,
                    .Persona = "Damian",
                    .Instrument = "MES",
                    .TimeFrame = "15min",
                    .EntryMode = "BarClose",
                    .Direction = "Long",
                    .EntryPrice = 5000D,
                    .EntryTime = ts,
                    .InitialSL = 4990D,
                    .InitialTP = 0D,
                    .ContractCount = 1,
                    .SuperTrendConfigJson = "{}",
                    .CreatedAt = ts
                })
            Next

            Await db.WriteBatchAsync(headers,
                                     New List(Of DebugSnapshotRecord)(),
                                     New List(Of (TradeId As String, ClosedUtc As DateTime, RealisedPnl As Nullable(Of Decimal)))())

            Assert.Equal(5, Await db.CountTradesAsync())

            Await db.PurgeOldTradesAsync()

            ' 2 old deleted + trim to 3 recent → 3 remain
            Assert.Equal(3, Await db.CountTradesAsync())
            db.Dispose()
        End Function

    End Class

End Namespace
