Imports Microsoft.EntityFrameworkCore
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data

    Public Class AppDbContext
        Inherits DbContext

        Public Sub New(options As DbContextOptions(Of AppDbContext))
            MyBase.New(options)
        End Sub

        Public Property Bars As DbSet(Of BarEntity)
        Public Property Signals As DbSet(Of SignalEntity)
        Public Property Orders As DbSet(Of OrderEntity)
        Public Property BacktestRuns As DbSet(Of BacktestRunEntity)
        Public Property BacktestTrades As DbSet(Of BacktestTradeEntity)
        Public Property RiskEvents As DbSet(Of RiskEventEntity)

        Protected Overrides Sub OnModelCreating(modelBuilder As ModelBuilder)
            MyBase.OnModelCreating(modelBuilder)

            ' Bars — unique constraint on (ContractId, Timeframe, Timestamp) to prevent duplicates
            modelBuilder.Entity(Of BarEntity)() _
                .HasIndex(Function(b) New With {b.ContractId, b.Timeframe, b.Timestamp}) _
                .IsUnique() _
                .HasDatabaseName("UQ_Bars_ContractTimeframeTimestamp")

            modelBuilder.Entity(Of BarEntity)() _
                .HasIndex(Function(b) New With {b.ContractId, b.Timeframe, b.Timestamp}) _
                .HasDatabaseName("IX_Bars_ContractTimeframe_Timestamp")

            ' Signals — index for history queries
            modelBuilder.Entity(Of SignalEntity)() _
                .HasIndex(Function(s) New With {s.ContractId, s.GeneratedAt}) _
                .HasDatabaseName("IX_Signals_ContractId_GeneratedAt")

            ' Orders — index for account history queries
            modelBuilder.Entity(Of OrderEntity)() _
                .HasIndex(Function(o) New With {o.AccountId, o.PlacedAt}) _
                .HasDatabaseName("IX_Orders_AccountId_PlacedAt")

            ' BacktestTrades — index for run lookup
            modelBuilder.Entity(Of BacktestTradeEntity)() _
                .HasIndex(Function(t) t.BacktestRunId) _
                .HasDatabaseName("IX_BacktestTrades_RunId")

            ' BacktestTrades → BacktestRun cascade delete
            modelBuilder.Entity(Of BacktestRunEntity)() _
                .HasMany(Function(r) r.Trades) _
                .WithOne(Function(t) t.BacktestRun) _
                .HasForeignKey(Function(t) t.BacktestRunId) _
                .OnDelete(DeleteBehavior.Cascade)

            ' Orders → Signal (optional FK, no cascade)
            modelBuilder.Entity(Of OrderEntity)() _
                .HasOne(Function(o) o.SourceSignal) _
                .WithMany() _
                .HasForeignKey(Function(o) o.SourceSignalId) _
                .OnDelete(DeleteBehavior.SetNull)
        End Sub

    End Class

End Namespace
