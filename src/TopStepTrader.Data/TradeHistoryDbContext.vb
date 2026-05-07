Imports Microsoft.EntityFrameworkCore
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data

    ''' <summary>
    ''' Separate SQLite context for live trade history (TradeHistory.db).
    ''' Kept isolated from AppDbContext so trade records are independently queryable.
    ''' </summary>
    Public Class TradeHistoryDbContext
        Inherits DbContext

        Public Sub New(options As DbContextOptions(Of TradeHistoryDbContext))
            MyBase.New(options)
        End Sub

        Public Property LiveTradeRecords As DbSet(Of LiveTradeRecordEntity)
        Public Property TradeStopAdjustments As DbSet(Of TradeStopAdjustmentEntity)

        ''' <summary>Idempotent ALTER TABLE statements for columns added after initial EnsureCreated.</summary>
        Public Sub EnsureSchemaCurrent()
            Dim conn = Database.GetDbConnection()
            If conn.State <> System.Data.ConnectionState.Open Then conn.Open()
            Using cmd = conn.CreateCommand()
                ' Create TradeStopAdjustments table idempotently
                cmd.CommandText = "CREATE TABLE IF NOT EXISTS TradeStopAdjustments (" &
                    "Id INTEGER PRIMARY KEY AUTOINCREMENT, " &
                    "LiveTradeRecordId INTEGER NOT NULL, " &
                    "Timestamp TEXT NOT NULL, " &
                    "OldStop TEXT NOT NULL, " &
                    "NewStop TEXT NOT NULL, " &
                    "TriggerReason TEXT NOT NULL, " &
                    "Notes TEXT)"
                cmd.ExecuteNonQuery()
                cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_TradeStopAdjustments_RecordId " &
                    "ON TradeStopAdjustments (LiveTradeRecordId, Timestamp)"
                cmd.ExecuteNonQuery()

                For Each sql In New String() {
                    "ALTER TABLE ""LiveTradeRecords"" ADD COLUMN ""Timeframe"" TEXT NOT NULL DEFAULT ''"
                }
                    cmd.CommandText = sql
                    Try
                        cmd.ExecuteNonQuery()
                    Catch ex As Exception When ex.Message.Contains("duplicate column")
                    End Try
                Next
            End Using
        End Sub

        Protected Overrides Sub OnModelCreating(modelBuilder As ModelBuilder)
            MyBase.OnModelCreating(modelBuilder)

            modelBuilder.Entity(Of LiveTradeRecordEntity)() _
                .HasIndex(Function(r) New With {r.IsOpen, r.EntryTime}) _
                .HasDatabaseName("IX_LiveTradeRecords_IsOpen_EntryTime")

            modelBuilder.Entity(Of LiveTradeRecordEntity)() _
                .HasIndex(Function(r) r.EntryTime) _
                .HasDatabaseName("IX_LiveTradeRecords_EntryTime")
        End Sub

    End Class

End Namespace
