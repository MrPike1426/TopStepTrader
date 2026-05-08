Imports System.IO
Imports Microsoft.Data.Sqlite
Imports TopStepTrader.Core.Models.Debug

Namespace TopStepTrader.Data.Debug

    Public Class DebugTradeDbContext
        Implements IDisposable

        Private ReadOnly _connectionString As String
        Private ReadOnly _maxTradeCount As Integer
        Private _disposed As Boolean = False

        Public Sub New(Optional connectionString As String = Nothing, Optional maxTradeCount As Integer = 100)
            _maxTradeCount = maxTradeCount
            If String.IsNullOrEmpty(connectionString) Then
                Dim dir = ResolveDiagnosticsFolder()
                Dim dbPath = Path.Combine(dir, "debug_trades.db")
                _connectionString = $"Data Source={dbPath}"
            Else
                _connectionString = connectionString
            End If
        End Sub

        Public Shared Function ResolveDiagnosticsFolder() As String
            Return _diagnosticsFolder.Value
        End Function

        ' BUG-65: cache the resolved path; production-safe fallback to %LOCALAPPDATA%.
        Private Shared ReadOnly _diagnosticsFolder As New Lazy(Of String)(AddressOf ComputeDiagnosticsFolder)

        Private Shared Function ComputeDiagnosticsFolder() As String
            ' Walk up from AppContext.BaseDirectory to find the .sln (dev builds).
            Try
                Dim baseDir = AppContext.BaseDirectory
                Dim current = New DirectoryInfo(baseDir)
                Dim hops = 0
                While current IsNot Nothing AndAlso hops < 8
                    If Directory.GetFiles(current.FullName, "*.sln").Length > 0 Then
                        Dim diagnosticsDir = Path.Combine(current.FullName, "TopStepTrader_Diagnostics")
                        Try
                            Directory.CreateDirectory(diagnosticsDir)
                            Return diagnosticsDir
                        Catch
                            ' fall through to %LOCALAPPDATA%
                            Exit While
                        End Try
                    End If
                    current = current.Parent
                    hops += 1
                End While
            Catch
                ' fall through
            End Try

            ' Production-safe fallback: per-user writable location.
            Dim localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            Dim fallbackDir = Path.Combine(localAppData, "TopStepTrader", "Diagnostics")
            Directory.CreateDirectory(fallbackDir)
            Return fallbackDir
        End Function

        Public Async Function EnsureSchemaAsync() As Task
            Using conn = New SqliteConnection(_connectionString)
                Await conn.OpenAsync()
                Using walCmd = conn.CreateCommand()
                    walCmd.CommandText = "PRAGMA journal_mode=WAL"
                    Await walCmd.ExecuteScalarAsync()
                End Using
                Using cmd = conn.CreateCommand()
                    cmd.CommandText =
                        "CREATE TABLE IF NOT EXISTS DebugTrades (" &
                        "  TradeId              TEXT PRIMARY KEY," &
                        "  SlotIndex            INTEGER NOT NULL," &
                        "  Persona              TEXT NOT NULL," &
                        "  Instrument           TEXT NOT NULL," &
                        "  TimeFrame            TEXT NOT NULL," &
                        "  EntryMode            TEXT NOT NULL," &
                        "  Direction            TEXT NOT NULL," &
                        "  EntryPrice           REAL NOT NULL," &
                        "  EntryTime            TEXT NOT NULL," &
                        "  InitialSL            REAL NOT NULL," &
                        "  InitialTP            REAL NOT NULL," &
                        "  ContractCount        INTEGER NOT NULL," &
                        "  SuperTrendConfigJson TEXT NOT NULL," &
                        "  AiCheckResult        TEXT," &
                        "  AiCheckReason        TEXT," &
                        "  ActualFillPrice      REAL," &
                        "  FillConfirmedTime    TEXT," &
                        "  RealisedPnLDollars   REAL," &
                        "  ClosedAt             TEXT," &
                        "  CreatedAt            TEXT NOT NULL" &
                        ");" &
                        "CREATE INDEX IF NOT EXISTS IX_DebugTrades_CreatedAt ON DebugTrades(CreatedAt);" &
                        "CREATE TABLE IF NOT EXISTS DebugSnapshots (" &
                        "  Id                   INTEGER PRIMARY KEY AUTOINCREMENT," &
                        "  TradeId              TEXT NOT NULL," &
                        "  Timestamp            TEXT NOT NULL," &
                        "  EventType            TEXT NOT NULL," &
                        "  LastPrice            REAL," &
                        "  Bid                  REAL," &
                        "  Ask                  REAL," &
                        "  CurrentSL            REAL," &
                        "  CurrentTP            REAL," &
                        "  UnrealizedPnLTicks   REAL," &
                        "  UnrealizedPnLDollars REAL," &
                        "  Mfe                  REAL," &
                        "  Mae                  REAL," &
                        "  BarOpen              REAL," &
                        "  BarHigh              REAL," &
                        "  BarLow               REAL," &
                        "  BarClose             REAL," &
                        "  SuperTrendValue      REAL," &
                        "  SuperTrendDirection  TEXT," &
                        "  Atr                  REAL," &
                        "  Adx                  REAL," &
                        "  StopPhase            TEXT," &
                        "  Notes                TEXT," &
                        "  FOREIGN KEY (TradeId) REFERENCES DebugTrades(TradeId)" &
                        ");" &
                        "CREATE INDEX IF NOT EXISTS IX_DebugSnapshots_TradeId_Time ON DebugSnapshots(TradeId, Timestamp);"
                    Await cmd.ExecuteNonQueryAsync()
                End Using

                ' Idempotent migration: add new columns to existing databases
                Await AddColumnIfMissingAsync(conn, "DebugTrades", "ActualFillPrice", "REAL")
                Await AddColumnIfMissingAsync(conn, "DebugTrades", "FillConfirmedTime", "TEXT")
                Await AddColumnIfMissingAsync(conn, "DebugTrades", "RealisedPnLDollars", "REAL")
                Await AddColumnIfMissingAsync(conn, "DebugSnapshots", "Adx", "REAL")
                Await AddColumnIfMissingAsync(conn, "DebugSnapshots", "StopPhase", "TEXT")
            End Using
        End Function

        Private Shared Async Function AddColumnIfMissingAsync(conn As SqliteConnection, tableName As String, columnName As String, columnType As String) As Task
            Dim exists As Boolean = False
            Using infoCmd = conn.CreateCommand()
                infoCmd.CommandText = $"PRAGMA table_info({tableName})"
                Using reader = Await infoCmd.ExecuteReaderAsync()
                    While Await reader.ReadAsync()
                        If String.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase) Then
                            exists = True
                            Exit While
                        End If
                    End While
                End Using
            End Using
            If Not exists Then
                Using alterCmd = conn.CreateCommand()
                    alterCmd.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnType} DEFAULT NULL"
                    Await alterCmd.ExecuteNonQueryAsync()
                End Using
            End If
        End Function

        Public Async Function WriteBatchAsync(
                headers As IReadOnlyList(Of DebugTradeRecord),
                snapshots As IReadOnlyList(Of DebugSnapshotRecord),
                endTrades As IReadOnlyList(Of (TradeId As String, ClosedUtc As DateTime, RealisedPnl As Nullable(Of Decimal))),
                Optional fillUpdates As IReadOnlyList(Of (TradeId As String, FillPrice As Decimal, FillConfirmedTime As DateTime)) = Nothing) As Task
            If headers.Count = 0 AndAlso snapshots.Count = 0 AndAlso endTrades.Count = 0 AndAlso
               (fillUpdates Is Nothing OrElse fillUpdates.Count = 0) Then Return
            Using conn = New SqliteConnection(_connectionString)
                Await conn.OpenAsync()
                Using tx = conn.BeginTransaction()
                    For Each h In headers
                        Using cmd = conn.CreateCommand()
                            cmd.Transaction = tx
                            cmd.CommandText =
                                "INSERT OR IGNORE INTO DebugTrades " &
                                "(TradeId,SlotIndex,Persona,Instrument,TimeFrame,EntryMode,Direction," &
                                " EntryPrice,EntryTime,InitialSL,InitialTP,ContractCount," &
                                " SuperTrendConfigJson,AiCheckResult,AiCheckReason," &
                                " ActualFillPrice,FillConfirmedTime,RealisedPnLDollars,ClosedAt,CreatedAt)" &
                                " VALUES " &
                                "(@TradeId,@SlotIndex,@Persona,@Instrument,@TimeFrame,@EntryMode,@Direction," &
                                " @EntryPrice,@EntryTime,@InitialSL,@InitialTP,@ContractCount," &
                                " @SuperTrendConfigJson,@AiCheckResult,@AiCheckReason," &
                                " @ActualFillPrice,@FillConfirmedTime,@RealisedPnLDollars,@ClosedAt,@CreatedAt)"
                            cmd.Parameters.AddWithValue("@TradeId", h.TradeId)
                            cmd.Parameters.AddWithValue("@SlotIndex", h.SlotIndex)
                            cmd.Parameters.AddWithValue("@Persona", h.Persona)
                            cmd.Parameters.AddWithValue("@Instrument", h.Instrument)
                            cmd.Parameters.AddWithValue("@TimeFrame", h.TimeFrame)
                            cmd.Parameters.AddWithValue("@EntryMode", h.EntryMode)
                            cmd.Parameters.AddWithValue("@Direction", h.Direction)
                            cmd.Parameters.AddWithValue("@EntryPrice", CDbl(h.EntryPrice))
                            cmd.Parameters.AddWithValue("@EntryTime", h.EntryTime)
                            cmd.Parameters.AddWithValue("@InitialSL", CDbl(h.InitialSL))
                            cmd.Parameters.AddWithValue("@InitialTP", CDbl(h.InitialTP))
                            cmd.Parameters.AddWithValue("@ContractCount", h.ContractCount)
                            cmd.Parameters.AddWithValue("@SuperTrendConfigJson", h.SuperTrendConfigJson)
                            cmd.Parameters.AddWithValue("@AiCheckResult", If(h.AiCheckResult IsNot Nothing, CObj(h.AiCheckResult), DBNull.Value))
                            cmd.Parameters.AddWithValue("@AiCheckReason", If(h.AiCheckReason IsNot Nothing, CObj(h.AiCheckReason), DBNull.Value))
                            cmd.Parameters.AddWithValue("@ActualFillPrice", If(h.ActualFillPrice.HasValue, CObj(CDbl(h.ActualFillPrice.Value)), DBNull.Value))
                            cmd.Parameters.AddWithValue("@FillConfirmedTime", If(h.FillConfirmedTime IsNot Nothing, CObj(h.FillConfirmedTime), DBNull.Value))
                            cmd.Parameters.AddWithValue("@RealisedPnLDollars", If(h.RealisedPnLDollars.HasValue, CObj(CDbl(h.RealisedPnLDollars.Value)), DBNull.Value))
                            cmd.Parameters.AddWithValue("@ClosedAt", If(h.ClosedAt IsNot Nothing, CObj(h.ClosedAt), DBNull.Value))
                            cmd.Parameters.AddWithValue("@CreatedAt", h.CreatedAt)
                            Await cmd.ExecuteNonQueryAsync()
                        End Using
                    Next

                    For Each s In snapshots
                        Using cmd = conn.CreateCommand()
                            cmd.Transaction = tx
                            cmd.CommandText =
                                "INSERT INTO DebugSnapshots " &
                                "(TradeId,Timestamp,EventType,LastPrice,Bid,Ask,CurrentSL,CurrentTP," &
                                " UnrealizedPnLTicks,UnrealizedPnLDollars,Mfe,Mae," &
                                " BarOpen,BarHigh,BarLow,BarClose,SuperTrendValue,SuperTrendDirection," &
                                " Atr,Adx,StopPhase,Notes)" &
                                " VALUES " &
                                "(@TradeId,@Timestamp,@EventType,@LastPrice,@Bid,@Ask,@CurrentSL,@CurrentTP," &
                                " @UnrealizedPnLTicks,@UnrealizedPnLDollars,@Mfe,@Mae," &
                                " @BarOpen,@BarHigh,@BarLow,@BarClose,@SuperTrendValue,@SuperTrendDirection," &
                                " @Atr,@Adx,@StopPhase,@Notes)"
                            cmd.Parameters.AddWithValue("@TradeId", s.TradeId)
                            cmd.Parameters.AddWithValue("@Timestamp", s.Timestamp)
                            cmd.Parameters.AddWithValue("@EventType", s.EventType)
                            cmd.Parameters.AddWithValue("@LastPrice", NullableReal(s.LastPrice))
                            cmd.Parameters.AddWithValue("@Bid", NullableReal(s.Bid))
                            cmd.Parameters.AddWithValue("@Ask", NullableReal(s.Ask))
                            cmd.Parameters.AddWithValue("@CurrentSL", NullableReal(s.CurrentSL))
                            cmd.Parameters.AddWithValue("@CurrentTP", NullableReal(s.CurrentTP))
                            cmd.Parameters.AddWithValue("@UnrealizedPnLTicks", NullableReal(s.UnrealizedPnLTicks))
                            cmd.Parameters.AddWithValue("@UnrealizedPnLDollars", NullableReal(s.UnrealizedPnLDollars))
                            cmd.Parameters.AddWithValue("@Mfe", NullableReal(s.Mfe))
                            cmd.Parameters.AddWithValue("@Mae", NullableReal(s.Mae))
                            cmd.Parameters.AddWithValue("@BarOpen", NullableReal(s.BarOpen))
                            cmd.Parameters.AddWithValue("@BarHigh", NullableReal(s.BarHigh))
                            cmd.Parameters.AddWithValue("@BarLow", NullableReal(s.BarLow))
                            cmd.Parameters.AddWithValue("@BarClose", NullableReal(s.BarClose))
                            cmd.Parameters.AddWithValue("@SuperTrendValue", NullableReal(s.SuperTrendValue))
                            cmd.Parameters.AddWithValue("@SuperTrendDirection", If(s.SuperTrendDirection IsNot Nothing, CObj(s.SuperTrendDirection), DBNull.Value))
                            cmd.Parameters.AddWithValue("@Atr", NullableReal(s.Atr))
                            cmd.Parameters.AddWithValue("@Adx", If(s.Adx.HasValue, CObj(CDbl(s.Adx.Value)), DBNull.Value))
                            cmd.Parameters.AddWithValue("@StopPhase", If(s.StopPhase IsNot Nothing, CObj(s.StopPhase), DBNull.Value))
                            cmd.Parameters.AddWithValue("@Notes", If(s.Notes IsNot Nothing, CObj(s.Notes), DBNull.Value))
                            Await cmd.ExecuteNonQueryAsync()
                        End Using
                    Next

                    For Each kv In endTrades
                        Using cmd = conn.CreateCommand()
                            cmd.Transaction = tx
                            cmd.CommandText =
                                "UPDATE DebugTrades SET ClosedAt = @ClosedAt," &
                                " RealisedPnLDollars = COALESCE(@RealisedPnL, RealisedPnLDollars)" &
                                " WHERE TradeId = @TradeId"
                            cmd.Parameters.AddWithValue("@ClosedAt", kv.ClosedUtc.ToString("O"))
                            cmd.Parameters.AddWithValue("@RealisedPnL", If(kv.RealisedPnl.HasValue, CObj(CDbl(kv.RealisedPnl.Value)), DBNull.Value))
                            cmd.Parameters.AddWithValue("@TradeId", kv.TradeId)
                            Await cmd.ExecuteNonQueryAsync()
                        End Using
                    Next

                    If fillUpdates IsNot Nothing Then
                        For Each fu In fillUpdates
                            Using cmd = conn.CreateCommand()
                                cmd.Transaction = tx
                                cmd.CommandText =
                                    "UPDATE DebugTrades SET ActualFillPrice = @FillPrice," &
                                    " FillConfirmedTime = @FillTime WHERE TradeId = @TradeId"
                                cmd.Parameters.AddWithValue("@FillPrice", CDbl(fu.FillPrice))
                                cmd.Parameters.AddWithValue("@FillTime", fu.FillConfirmedTime.ToString("O"))
                                cmd.Parameters.AddWithValue("@TradeId", fu.TradeId)
                                Await cmd.ExecuteNonQueryAsync()
                            End Using
                        Next
                    End If

                    tx.Commit()
                End Using
            End Using
        End Function

        Public Async Function PurgeOldTradesAsync() As Task
            Dim cutoff = DateTime.UtcNow.AddDays(-30).ToString("O")
            Using conn = New SqliteConnection(_connectionString)
                Await conn.OpenAsync()
                Using tx = conn.BeginTransaction()
                    ' Delete snapshots for old trades first, then the trades themselves
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText =
                            "DELETE FROM DebugSnapshots WHERE TradeId IN " &
                            "(SELECT TradeId FROM DebugTrades WHERE CreatedAt < @Cutoff)"
                        cmd.Parameters.AddWithValue("@Cutoff", cutoff)
                        Await cmd.ExecuteNonQueryAsync()
                    End Using
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "DELETE FROM DebugTrades WHERE CreatedAt < @Cutoff"
                        cmd.Parameters.AddWithValue("@Cutoff", cutoff)
                        Await cmd.ExecuteNonQueryAsync()
                    End Using

                    ' Trim to most recent N trades
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText =
                            "DELETE FROM DebugSnapshots WHERE TradeId NOT IN " &
                            "(SELECT TradeId FROM DebugTrades ORDER BY CreatedAt DESC LIMIT @Max)"
                        cmd.Parameters.AddWithValue("@Max", _maxTradeCount)
                        Await cmd.ExecuteNonQueryAsync()
                    End Using
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText =
                            "DELETE FROM DebugTrades WHERE TradeId NOT IN " &
                            "(SELECT TradeId FROM DebugTrades ORDER BY CreatedAt DESC LIMIT @Max)"
                        cmd.Parameters.AddWithValue("@Max", _maxTradeCount)
                        Await cmd.ExecuteNonQueryAsync()
                    End Using

                    tx.Commit()
                End Using
            End Using
        End Function

        Public Async Function CountTradesAsync() As Task(Of Integer)
            Using conn = New SqliteConnection(_connectionString)
                Await conn.OpenAsync()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT COUNT(*) FROM DebugTrades"
                    Return CInt(Await cmd.ExecuteScalarAsync())
                End Using
            End Using
        End Function

        Public Async Function CountSnapshotsAsync(tradeId As String) As Task(Of Integer)
            Using conn = New SqliteConnection(_connectionString)
                Await conn.OpenAsync()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT COUNT(*) FROM DebugSnapshots WHERE TradeId = @TradeId"
                    cmd.Parameters.AddWithValue("@TradeId", tradeId)
                    Return CInt(Await cmd.ExecuteScalarAsync())
                End Using
            End Using
        End Function

        Public Async Function GetClosedAtAsync(tradeId As String) As Task(Of String)
            Using conn = New SqliteConnection(_connectionString)
                Await conn.OpenAsync()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT ClosedAt FROM DebugTrades WHERE TradeId = @TradeId"
                    cmd.Parameters.AddWithValue("@TradeId", tradeId)
                    Dim result = Await cmd.ExecuteScalarAsync()
                    Return If(result Is Nothing OrElse result Is DBNull.Value, Nothing, CStr(result))
                End Using
            End Using
        End Function

        Public Async Function GetActualFillPriceAsync(tradeId As String) As Task(Of Decimal?)
            Using conn = New SqliteConnection(_connectionString)
                Await conn.OpenAsync()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT ActualFillPrice FROM DebugTrades WHERE TradeId = @TradeId"
                    cmd.Parameters.AddWithValue("@TradeId", tradeId)
                    Dim result = Await cmd.ExecuteScalarAsync()
                    If result Is Nothing OrElse result Is DBNull.Value Then Return Nothing
                    Return CDec(CType(result, Double))
                End Using
            End Using
        End Function

        Public Async Function GetFillConfirmedTimeAsync(tradeId As String) As Task(Of String)
            Using conn = New SqliteConnection(_connectionString)
                Await conn.OpenAsync()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT FillConfirmedTime FROM DebugTrades WHERE TradeId = @TradeId"
                    cmd.Parameters.AddWithValue("@TradeId", tradeId)
                    Dim result = Await cmd.ExecuteScalarAsync()
                    Return If(result Is Nothing OrElse result Is DBNull.Value, Nothing, CStr(result))
                End Using
            End Using
        End Function

        Public Async Function GetRealisedPnLAsync(tradeId As String) As Task(Of Decimal?)
            Using conn = New SqliteConnection(_connectionString)
                Await conn.OpenAsync()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT RealisedPnLDollars FROM DebugTrades WHERE TradeId = @TradeId"
                    cmd.Parameters.AddWithValue("@TradeId", tradeId)
                    Dim result = Await cmd.ExecuteScalarAsync()
                    If result Is Nothing OrElse result Is DBNull.Value Then Return Nothing
                    Return CDec(CType(result, Double))
                End Using
            End Using
        End Function

        Public Async Function GetSnapshotAdxAndPhaseAsync(tradeId As String) As Task(Of (Adx As Double?, StopPhase As String)())
            Using conn = New SqliteConnection(_connectionString)
                Await conn.OpenAsync()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT Adx, StopPhase FROM DebugSnapshots WHERE TradeId = @TradeId"
                    cmd.Parameters.AddWithValue("@TradeId", tradeId)
                    Dim rows As New List(Of (Adx As Double?, StopPhase As String))()
                    Using reader = Await cmd.ExecuteReaderAsync()
                        While Await reader.ReadAsync()
                            Dim adx As Double? = If(reader.IsDBNull(0), CType(Nothing, Double?), reader.GetDouble(0))
                            Dim phase As String = If(reader.IsDBNull(1), Nothing, reader.GetString(1))
                            rows.Add((adx, phase))
                        End While
                    End Using
                    Return rows.ToArray()
                End Using
            End Using
        End Function

        Public Async Function GetAllTradesAsync() As Task(Of List(Of DebugTradeRecord))
            Dim result As New List(Of DebugTradeRecord)()
            Using conn = New SqliteConnection(_connectionString)
                Await conn.OpenAsync()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText =
                        "SELECT TradeId,SlotIndex,Persona,Instrument,TimeFrame,EntryMode,Direction," &
                        " EntryPrice,EntryTime,InitialSL,InitialTP,ContractCount," &
                        " SuperTrendConfigJson,AiCheckResult,AiCheckReason," &
                        " ActualFillPrice,FillConfirmedTime,RealisedPnLDollars,ClosedAt,CreatedAt" &
                        " FROM DebugTrades ORDER BY CreatedAt DESC"
                    Using reader = Await cmd.ExecuteReaderAsync()
                        While Await reader.ReadAsync()
                            Dim r As New DebugTradeRecord()
                            r.TradeId = reader.GetString(0)
                            r.SlotIndex = reader.GetInt32(1)
                            r.Persona = reader.GetString(2)
                            r.Instrument = reader.GetString(3)
                            r.TimeFrame = reader.GetString(4)
                            r.EntryMode = reader.GetString(5)
                            r.Direction = reader.GetString(6)
                            r.EntryPrice = CDec(reader.GetDouble(7))
                            r.EntryTime = reader.GetString(8)
                            r.InitialSL = CDec(reader.GetDouble(9))
                            r.InitialTP = CDec(reader.GetDouble(10))
                            r.ContractCount = reader.GetInt32(11)
                            r.SuperTrendConfigJson = reader.GetString(12)
                            r.AiCheckResult = If(reader.IsDBNull(13), Nothing, reader.GetString(13))
                            r.AiCheckReason = If(reader.IsDBNull(14), Nothing, reader.GetString(14))
                            r.ActualFillPrice = If(reader.IsDBNull(15), CType(Nothing, Decimal?), CDec(reader.GetDouble(15)))
                            r.FillConfirmedTime = If(reader.IsDBNull(16), Nothing, reader.GetString(16))
                            r.RealisedPnLDollars = If(reader.IsDBNull(17), CType(Nothing, Decimal?), CDec(reader.GetDouble(17)))
                            r.ClosedAt = If(reader.IsDBNull(18), Nothing, reader.GetString(18))
                            r.CreatedAt = reader.GetString(19)
                            result.Add(r)
                        End While
                    End Using
                End Using
            End Using
            Return result
        End Function

        Public Async Function GetSnapshotsAsync(tradeId As String) As Task(Of List(Of DebugSnapshotRecord))
            Dim result As New List(Of DebugSnapshotRecord)()
            Using conn = New SqliteConnection(_connectionString)
                Await conn.OpenAsync()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText =
                        "SELECT TradeId,Timestamp,EventType,LastPrice,Bid,Ask,CurrentSL,CurrentTP," &
                        " UnrealizedPnLTicks,UnrealizedPnLDollars,Mfe,Mae," &
                        " BarOpen,BarHigh,BarLow,BarClose,SuperTrendValue,SuperTrendDirection," &
                        " Atr,Adx,StopPhase,Notes" &
                        " FROM DebugSnapshots WHERE TradeId = @TradeId ORDER BY Timestamp ASC"
                    cmd.Parameters.AddWithValue("@TradeId", tradeId)
                    Using reader = Await cmd.ExecuteReaderAsync()
                        While Await reader.ReadAsync()
                            Dim s As New DebugSnapshotRecord()
                            s.TradeId = reader.GetString(0)
                            s.Timestamp = reader.GetString(1)
                            s.EventType = reader.GetString(2)
                            s.LastPrice = If(reader.IsDBNull(3), CType(Nothing, Decimal?), CDec(reader.GetDouble(3)))
                            s.Bid = If(reader.IsDBNull(4), CType(Nothing, Decimal?), CDec(reader.GetDouble(4)))
                            s.Ask = If(reader.IsDBNull(5), CType(Nothing, Decimal?), CDec(reader.GetDouble(5)))
                            s.CurrentSL = If(reader.IsDBNull(6), CType(Nothing, Decimal?), CDec(reader.GetDouble(6)))
                            s.CurrentTP = If(reader.IsDBNull(7), CType(Nothing, Decimal?), CDec(reader.GetDouble(7)))
                            s.UnrealizedPnLTicks = If(reader.IsDBNull(8), CType(Nothing, Decimal?), CDec(reader.GetDouble(8)))
                            s.UnrealizedPnLDollars = If(reader.IsDBNull(9), CType(Nothing, Decimal?), CDec(reader.GetDouble(9)))
                            s.Mfe = If(reader.IsDBNull(10), CType(Nothing, Decimal?), CDec(reader.GetDouble(10)))
                            s.Mae = If(reader.IsDBNull(11), CType(Nothing, Decimal?), CDec(reader.GetDouble(11)))
                            s.BarOpen = If(reader.IsDBNull(12), CType(Nothing, Decimal?), CDec(reader.GetDouble(12)))
                            s.BarHigh = If(reader.IsDBNull(13), CType(Nothing, Decimal?), CDec(reader.GetDouble(13)))
                            s.BarLow = If(reader.IsDBNull(14), CType(Nothing, Decimal?), CDec(reader.GetDouble(14)))
                            s.BarClose = If(reader.IsDBNull(15), CType(Nothing, Decimal?), CDec(reader.GetDouble(15)))
                            s.SuperTrendValue = If(reader.IsDBNull(16), CType(Nothing, Decimal?), CDec(reader.GetDouble(16)))
                            s.SuperTrendDirection = If(reader.IsDBNull(17), Nothing, reader.GetString(17))
                            s.Atr = If(reader.IsDBNull(18), CType(Nothing, Decimal?), CDec(reader.GetDouble(18)))
                            s.Adx = If(reader.IsDBNull(19), CType(Nothing, Single?), CSng(reader.GetDouble(19)))
                            s.StopPhase = If(reader.IsDBNull(20), Nothing, reader.GetString(20))
                            s.Notes = If(reader.IsDBNull(21), Nothing, reader.GetString(21))
                            result.Add(s)
                        End While
                    End Using
                End Using
            End Using
            Return result
        End Function

        Private Shared Function NullableReal(v As Nullable(Of Decimal)) As Object
            If v.HasValue Then Return CDbl(v.Value)
            Return DBNull.Value
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            _disposed = True
        End Sub

    End Class

End Namespace
