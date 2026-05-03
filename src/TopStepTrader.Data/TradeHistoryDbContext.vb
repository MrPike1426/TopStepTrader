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
