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
                Dim dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TopStepTrader")
                Directory.CreateDirectory(dir)
                Dim dbPath = Path.Combine(dir, "debug_trades.db")
                _connectionString = $"Data Source={dbPath}"
            Else
                _connectionString = connectionString
            End If
        End Sub

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
                        "  VolumeSinceLastSnap  INTEGER," &
                        "  Notes                TEXT," &
                        "  FOREIGN KEY (TradeId) REFERENCES DebugTrades(TradeId)" &
                        ");" &
                        "CREATE INDEX IF NOT EXISTS IX_DebugSnapshots_TradeId_Time ON DebugSnapshots(TradeId, Timestamp);"
                    Await cmd.ExecuteNonQueryAsync()
                End Using
            End Using
        End Function

        Public Async Function WriteBatchAsync(
                headers As IReadOnlyList(Of DebugTradeRecord),
                snapshots As IReadOnlyList(Of DebugSnapshotRecord),
                endTrades As IReadOnlyList(Of KeyValuePair(Of String, DateTime))) As Task
            If headers.Count = 0 AndAlso snapshots.Count = 0 AndAlso endTrades.Count = 0 Then Return
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
                                " SuperTrendConfigJson,AiCheckResult,AiCheckReason,ClosedAt,CreatedAt)" &
                                " VALUES " &
                                "(@TradeId,@SlotIndex,@Persona,@Instrument,@TimeFrame,@EntryMode,@Direction," &
                                " @EntryPrice,@EntryTime,@InitialSL,@InitialTP,@ContractCount," &
                                " @SuperTrendConfigJson,@AiCheckResult,@AiCheckReason,@ClosedAt,@CreatedAt)"
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
                                " Atr,VolumeSinceLastSnap,Notes)" &
                                " VALUES " &
                                "(@TradeId,@Timestamp,@EventType,@LastPrice,@Bid,@Ask,@CurrentSL,@CurrentTP," &
                                " @UnrealizedPnLTicks,@UnrealizedPnLDollars,@Mfe,@Mae," &
                                " @BarOpen,@BarHigh,@BarLow,@BarClose,@SuperTrendValue,@SuperTrendDirection," &
                                " @Atr,@VolumeSinceLastSnap,@Notes)"
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
                            cmd.Parameters.AddWithValue("@VolumeSinceLastSnap", If(s.VolumeSinceLastSnap.HasValue, CObj(s.VolumeSinceLastSnap.Value), DBNull.Value))
                            cmd.Parameters.AddWithValue("@Notes", If(s.Notes IsNot Nothing, CObj(s.Notes), DBNull.Value))
                            Await cmd.ExecuteNonQueryAsync()
                        End Using
                    Next

                    For Each kv In endTrades
                        Using cmd = conn.CreateCommand()
                            cmd.Transaction = tx
                            cmd.CommandText = "UPDATE DebugTrades SET ClosedAt = @ClosedAt WHERE TradeId = @TradeId"
                            cmd.Parameters.AddWithValue("@ClosedAt", kv.Value.ToString("O"))
                            cmd.Parameters.AddWithValue("@TradeId", kv.Key)
                            Await cmd.ExecuteNonQueryAsync()
                        End Using
                    Next

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

        Private Shared Function NullableReal(v As Nullable(Of Decimal)) As Object
            If v.HasValue Then Return CDbl(v.Value)
            Return DBNull.Value
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            _disposed = True
        End Sub

    End Class

End Namespace
