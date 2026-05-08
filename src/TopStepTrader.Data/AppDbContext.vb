Imports System.Data
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
        Public Property TradeOutcomes As DbSet(Of TradeOutcomeEntity)
        Public Property TradeSetupSnapshots As DbSet(Of TradeSetupSnapshotEntity)
        Public Property TradeLifespanRecords As DbSet(Of TradeLifespanRecordEntity)
        Public Property AdaptiveParameters As DbSet(Of AdaptiveParametersEntity)
        Public Property BalanceHistory As DbSet(Of BalanceHistoryEntity)
        Public Property PersonaSettings As DbSet(Of PersonaSettingsEntity)
        Public Property ContractCache As DbSet(Of ContractCacheEntity)
        Public Property SuperTrendPlusConfig As DbSet(Of SuperTrendPlusConfigEntity)

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

            ' TradeOutcomes — index for resolution queries
            modelBuilder.Entity(Of TradeOutcomeEntity)() _
                .HasIndex(Function(o) New With {o.IsOpen, o.EntryTime}) _
                .HasDatabaseName("IX_TradeOutcomes_IsOpen_EntryTime")

            modelBuilder.Entity(Of TradeOutcomeEntity)() _
                .HasIndex(Function(o) o.SignalId) _
                .HasDatabaseName("IX_TradeOutcomes_SignalId")

            ' BalanceHistory — explicitly configure the table and index
            modelBuilder.Entity(Of BalanceHistoryEntity)() _
                .ToTable("BalanceHistory") _
                .HasKey(Function(b) b.Id)

            modelBuilder.Entity(Of BalanceHistoryEntity)() _
                .HasIndex(Function(b) New With {b.AccountId, b.RecordedDate}) _
                .HasDatabaseName("IX_BalanceHistory_AccountId_Date")

            ' PersonaSettings — one row per persona name, unique index enforces that
            modelBuilder.Entity(Of PersonaSettingsEntity)() _
                .ToTable("PersonaSettings") _
                .HasKey(Function(p) p.Id)

            modelBuilder.Entity(Of PersonaSettingsEntity)() _
                .HasIndex(Function(p) p.Name) _
                .IsUnique() _
                .HasDatabaseName("UQ_PersonaSettings_Name")

            ' SuperTrendPlusConfig — singleton row (id=1), no auto-increment
            modelBuilder.Entity(Of SuperTrendPlusConfigEntity)() _
                .ToTable("SuperTrendPlusConfig") _
                .HasKey(Function(c) c.Id)

        End Sub

        ''' <summary>
        ''' Idempotent schema migration for tables added after the initial DB was created.
        ''' Each CREATE TABLE / CREATE INDEX uses IF NOT EXISTS — safe to call on every startup.
        ''' </summary>
        Public Sub EnsureSchemaCurrent()
            Dim conn = Database.GetDbConnection()
            Dim mustClose = (conn.State <> ConnectionState.Open)
            If mustClose Then conn.Open()

            ' Enable WAL mode — makes SQLite resilient to concurrent access (e.g. OneDrive sync)
            ' running against the live database file without corrupting it.
            Using walCmd = conn.CreateCommand()
                walCmd.CommandText = "PRAGMA journal_mode=WAL;"
                walCmd.ExecuteNonQuery()
            End Using

            Try
                For Each ddl In New String() {
                    "CREATE TABLE IF NOT EXISTS ""TradeOutcomes"" (
                         ""Id""               INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                         ""SignalId""          INTEGER NOT NULL DEFAULT 0,
                         ""OrderId""           INTEGER,
                         ""ContractId""        TEXT    NOT NULL DEFAULT '',
                         ""Timeframe""         INTEGER NOT NULL DEFAULT 0,
                         ""SignalType""        TEXT    NOT NULL DEFAULT '',
                         ""SignalConfidence""  REAL    NOT NULL DEFAULT 0,
                         ""ModelVersion""      TEXT    NOT NULL DEFAULT '',
                         ""EntryTime""         TEXT    NOT NULL DEFAULT '',
                         ""EntryPrice""        TEXT    NOT NULL DEFAULT '0',
                         ""ExitTime""          TEXT,
                         ""ExitPrice""         TEXT,
                         ""PnL""               TEXT,
                         ""IsWinner""          INTEGER,
                         ""ExitReason""        TEXT    NOT NULL DEFAULT '',
                         ""IsOpen""            INTEGER NOT NULL DEFAULT 1,
                         ""CreatedAt""         TEXT    NOT NULL DEFAULT '')",
                    "CREATE INDEX IF NOT EXISTS ""IX_TradeOutcomes_IsOpen_EntryTime"" ON ""TradeOutcomes"" (""IsOpen"", ""EntryTime"")",
                    "CREATE INDEX IF NOT EXISTS ""IX_TradeOutcomes_SignalId"" ON ""TradeOutcomes"" (""SignalId"")",
                    "CREATE TABLE IF NOT EXISTS ""BacktestRuns"" (
                         ""Id""                  INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                         ""RunName""              TEXT    NOT NULL DEFAULT '',
                         ""ContractId""           TEXT    NOT NULL DEFAULT '',
                         ""Timeframe""            INTEGER NOT NULL DEFAULT 0,
                         ""StartDate""            TEXT    NOT NULL DEFAULT '',
                         ""EndDate""              TEXT    NOT NULL DEFAULT '',
                         ""InitialCapital""       TEXT    NOT NULL DEFAULT '0',
                         ""ModelVersion""         TEXT,
                         ""ParametersJson""       TEXT,
                         ""TotalTrades""          INTEGER NOT NULL DEFAULT 0,
                         ""WinningTrades""        INTEGER NOT NULL DEFAULT 0,
                         ""LosingTrades""         INTEGER NOT NULL DEFAULT 0,
                         ""TotalPnL""             TEXT    NOT NULL DEFAULT '0',
                         ""FinalCapital""         TEXT    NOT NULL DEFAULT '0',
                         ""MaxDrawdown""          TEXT    NOT NULL DEFAULT '0',
                         ""AveragePnLPerTrade""   TEXT    NOT NULL DEFAULT '0',
                         ""SharpeRatio""          REAL,
                         ""WinRate""              REAL,
                         ""Status""               INTEGER NOT NULL DEFAULT 0,
                         ""CompletedAt""          TEXT,
                         ""CreatedAt""            TEXT    NOT NULL DEFAULT '')",
                    "CREATE TABLE IF NOT EXISTS ""BacktestTrades"" (
                         ""Id""               INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                         ""BacktestRunId""    INTEGER NOT NULL,
                         ""EntryTime""        TEXT    NOT NULL DEFAULT '',
                         ""ExitTime""         TEXT,
                         ""Side""             TEXT    NOT NULL DEFAULT '',
                         ""EntryPrice""       TEXT    NOT NULL DEFAULT '0',
                         ""ExitPrice""        TEXT,
                         ""Quantity""         INTEGER NOT NULL DEFAULT 1,
                         ""PnL""              TEXT,
                         ""ExitReason""       TEXT,
                         ""SignalConfidence""  REAL,
                         CONSTRAINT ""FK_BacktestTrades_BacktestRuns_BacktestRunId""
                             FOREIGN KEY (""BacktestRunId"")
                             REFERENCES ""BacktestRuns"" (""Id"") ON DELETE CASCADE)",
                    "CREATE INDEX IF NOT EXISTS ""IX_BacktestTrades_RunId"" ON ""BacktestTrades"" (""BacktestRunId"")",
                    "CREATE TABLE IF NOT EXISTS ""RiskEvents"" (
                         ""Id""              INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                         ""OccurredAt""      TEXT    NOT NULL DEFAULT '',
                         ""EventType""       TEXT    NOT NULL DEFAULT '',
                         ""DailyPnLAtEvent"" TEXT,
                         ""DrawdownAtEvent"" TEXT,
                         ""RuleValue""       TEXT,
                         ""AccountId""       INTEGER,
                         ""DetailsJson""     TEXT,
                         ""Acknowledged""    INTEGER NOT NULL DEFAULT 0)"
                }
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = ddl
                        cmd.ExecuteNonQuery()
                    End Using
                Next
            Finally
                If mustClose Then conn.Close()
            End Try

            ' ── RC-5: add amount/leverage/SL/TP columns to Orders table ───────────────────────────────────────────
            ' SQLite does not support ALTER TABLE ... ADD COLUMN IF NOT EXISTS, so each
            ' statement is attempted individually and "duplicate column name" errors are
            ' silently swallowed, making this block fully idempotent on every startup.
            Dim mustClose2 = (conn.State <> ConnectionState.Open)
            If mustClose2 Then conn.Open()
            Try
                Dim orderAlters = New String() {
                    "ALTER TABLE ""Orders"" ADD COLUMN ""Amount"" TEXT",
                    "ALTER TABLE ""Orders"" ADD COLUMN ""Leverage"" INTEGER NOT NULL DEFAULT 1",
                    "ALTER TABLE ""Orders"" ADD COLUMN ""StopLossRate"" TEXT",
                    "ALTER TABLE ""Orders"" ADD COLUMN ""TakeProfitRate"" TEXT"
                }
                For Each ddl In orderAlters
                    Try
                        Using cmd = conn.CreateCommand()
                            cmd.CommandText = ddl
                            cmd.ExecuteNonQuery()
                        End Using
                    Catch ex As Exception
                        ' Ignore "duplicate column name" — column already present from a prior run.
                        If Not ex.Message.Contains("duplicate column") Then Throw
                    End Try
                Next
            Finally
                If mustClose2 Then conn.Close()
            End Try

            ' ── Persona settings table ───────────────────────────────────────────
            Dim mustCloseP = (conn.State <> ConnectionState.Open)
            If mustCloseP Then conn.Open()
            Try
                Dim personaDdl = New String() {
                    "CREATE TABLE IF NOT EXISTS ""PersonaSettings"" (
                         ""Id""                     INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                         ""Name""                   TEXT    NOT NULL DEFAULT '',
                         ""TradeAmount""             TEXT    NOT NULL DEFAULT '0',
                         ""Leverage""                INTEGER NOT NULL DEFAULT 1,
                         ""MaxScaleIns""             INTEGER NOT NULL DEFAULT 1,
                         ""SlMultipleOfN""           TEXT    NOT NULL DEFAULT '0',
                         ""LeveragedSlMultipleOfN""  TEXT    NOT NULL DEFAULT '0',
                         ""TpMultipleOfN""           TEXT    NOT NULL DEFAULT '0',
                         ""AdxThreshold""            REAL    NOT NULL DEFAULT 0,
                         ""DefaultConfidencePct""    INTEGER NOT NULL DEFAULT 70,
                         ""LastModifiedAt""          TEXT    NOT NULL DEFAULT '')",
                    "CREATE UNIQUE INDEX IF NOT EXISTS ""UQ_PersonaSettings_Name"" ON ""PersonaSettings"" (""Name"")"
                }
                For Each ddl In personaDdl
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = ddl
                        cmd.ExecuteNonQuery()
                    End Using
                Next

                ' ── Idempotent column additions to PersonaSettings ─────────────
                ' Entity class has properties not present in the original CREATE TABLE DDL.
                Dim personaAlters = New String() {
                    "ALTER TABLE ""PersonaSettings"" ADD COLUMN ""PositionSize""             INTEGER NOT NULL DEFAULT 1",
                    "ALTER TABLE ""PersonaSettings"" ADD COLUMN ""MacdHistMinAtrFraction""   REAL    NOT NULL DEFAULT 0.05"
                }
                For Each ddl In personaAlters
                    Try
                        Using cmd = conn.CreateCommand()
                            cmd.CommandText = ddl
                            cmd.ExecuteNonQuery()
                        End Using
                    Catch ex As Exception
                        If Not ex.Message.Contains("duplicate column") Then Throw
                    End Try
                Next
            Finally
                If mustCloseP Then conn.Close()
            End Try

            ' ── Scale-in support: add PositionGroupId to BacktestTrades ─────────
            ' Groups all legs of the same position (initial entry + scale-ins).
            ' Idempotent: "duplicate column name" errors are silently swallowed.
            Dim mustClose3 = (conn.State <> ConnectionState.Open)
            If mustClose3 Then conn.Open()
            Try
                Dim tradeAlters = New String() {
                    "ALTER TABLE ""BacktestTrades"" ADD COLUMN ""PositionGroupId"" INTEGER NOT NULL DEFAULT 0"
                }
                For Each ddl In tradeAlters
                    Try
                        Using cmd = conn.CreateCommand()
                            cmd.CommandText = ddl
                            cmd.ExecuteNonQuery()
                        End Using
                    Catch ex As Exception
                        If Not ex.Message.Contains("duplicate column") Then Throw
                    End Try
                Next
            Finally
                If mustClose3 Then conn.Close()
            End Try

            ' ── FEAT-01: extend TradeOutcomes + new tables ───────────────────────
            ' Idempotent ALTER TABLE columns; "duplicate column name" is swallowed.
            Dim mustClose5 = (conn.State <> ConnectionState.Open)
            If mustClose5 Then conn.Open()
            Try
                Dim tradeOutcomeAlters = New String() {
                    "ALTER TABLE ""TradeOutcomes"" ADD COLUMN ""RMultiple"" REAL",
                    "ALTER TABLE ""TradeOutcomes"" ADD COLUMN ""AiPostMortem"" TEXT",
                    "ALTER TABLE ""TradeOutcomes"" ADD COLUMN ""AiSetupQuality"" TEXT",
                    "ALTER TABLE ""TradeOutcomes"" ADD COLUMN ""AiExecutionQuality"" TEXT",
                    "ALTER TABLE ""TradeOutcomes"" ADD COLUMN ""AiPatternTag"" TEXT",
                    "ALTER TABLE ""TradeOutcomes"" ADD COLUMN ""AiRecommendation"" TEXT",
                    "ALTER TABLE ""TradeOutcomes"" ADD COLUMN ""AiPreTradeVerdict"" TEXT",
                    "ALTER TABLE ""TradeOutcomes"" ADD COLUMN ""AiPreTradeReasoning"" TEXT",
                    "ALTER TABLE ""TradeOutcomes"" ADD COLUMN ""MacroPostureAtEntry"" TEXT"
                }
                For Each ddl In tradeOutcomeAlters
                    Try
                        Using cmd = conn.CreateCommand()
                            cmd.CommandText = ddl
                            cmd.ExecuteNonQuery()
                        End Using
                    Catch ex As Exception
                        If Not ex.Message.Contains("duplicate column") Then Throw
                    End Try
                Next

                Dim newTableDdl = New String() {
                    "CREATE TABLE IF NOT EXISTS ""TradeSetupSnapshots"" (
                         ""Id""                   INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                         ""TradeOutcomeId""        INTEGER NOT NULL DEFAULT 0,
                         ""CapturedAt""            TEXT    NOT NULL DEFAULT '',
                         ""Tenkan""                TEXT    NOT NULL DEFAULT '0',
                         ""Kijun""                 TEXT    NOT NULL DEFAULT '0',
                         ""Cloud1""                TEXT    NOT NULL DEFAULT '0',
                         ""Cloud2""                TEXT    NOT NULL DEFAULT '0',
                         ""Ema21""                 TEXT    NOT NULL DEFAULT '0',
                         ""Ema50""                 TEXT    NOT NULL DEFAULT '0',
                         ""MacdHist""              REAL    NOT NULL DEFAULT 0,
                         ""MacdHistPrev""          REAL    NOT NULL DEFAULT 0,
                         ""StochRsiK""             REAL    NOT NULL DEFAULT 0,
                         ""PlusDI""                REAL    NOT NULL DEFAULT 0,
                         ""MinusDI""               REAL    NOT NULL DEFAULT 0,
                         ""AdxValue""              REAL    NOT NULL DEFAULT 0,
                         ""Rsi14""                 REAL    NOT NULL DEFAULT 0,
                         ""VidyaValue""            TEXT    NOT NULL DEFAULT '0',
                         ""CmoValue""              REAL    NOT NULL DEFAULT 0,
                         ""DeltaVol""              REAL    NOT NULL DEFAULT 0,
                         ""LongCount""             INTEGER NOT NULL DEFAULT 0,
                         ""ShortCount""            INTEGER NOT NULL DEFAULT 0,
                         ""TotalConditions""       INTEGER NOT NULL DEFAULT 0,
                         ""UpPct""                 INTEGER NOT NULL DEFAULT 0,
                         ""DownPct""               INTEGER NOT NULL DEFAULT 0,
                         ""SignalBarOpen""          TEXT    NOT NULL DEFAULT '0',
                         ""SignalBarHigh""          TEXT    NOT NULL DEFAULT '0',
                         ""SignalBarLow""           TEXT    NOT NULL DEFAULT '0',
                         ""SignalBarClose""         TEXT    NOT NULL DEFAULT '0',
                         ""SignalBarVolume""        INTEGER NOT NULL DEFAULT 0,
                         ""AtrValue""              TEXT    NOT NULL DEFAULT '0',
                         ""SessionWindow""          TEXT    NOT NULL DEFAULT '',
                         ""DayOfWeek""              INTEGER NOT NULL DEFAULT 0,
                         ""HourOfDay""              INTEGER NOT NULL DEFAULT 0,
                         ""StrategyName""           TEXT    NOT NULL DEFAULT '',
                         ""PersonaName""            TEXT    NOT NULL DEFAULT '',
                         ""SlMultiple""             REAL    NOT NULL DEFAULT 0,
                         ""TpMultiple""             REAL    NOT NULL DEFAULT 0,
                         ""TimeframeMinutes""       INTEGER NOT NULL DEFAULT 0)",
                    "CREATE INDEX IF NOT EXISTS ""IX_TradeSetupSnapshots_TradeOutcomeId"" ON ""TradeSetupSnapshots"" (""TradeOutcomeId"")",
                    "CREATE TABLE IF NOT EXISTS ""TradeLifespanRecords"" (
                         ""Id""                                INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                         ""TradeOutcomeId""                    INTEGER NOT NULL DEFAULT 0,
                         ""MaxAdverseExcursionDollars""        TEXT    NOT NULL DEFAULT '0',
                         ""MaxFavorableExcursionDollars""      TEXT    NOT NULL DEFAULT '0',
                         ""MaxAdverseExcursionTicks""          INTEGER NOT NULL DEFAULT 0,
                         ""MaxFavorableExcursionTicks""        INTEGER NOT NULL DEFAULT 0,
                         ""SlRatchetCount""                    INTEGER NOT NULL DEFAULT 0,
                         ""TpAdvanceCount""                    INTEGER NOT NULL DEFAULT 0,
                         ""FreeRideActivated""                 INTEGER NOT NULL DEFAULT 0,
                         ""FreeRideActivatedAtMinutes""        REAL    NOT NULL DEFAULT 0,
                         ""DurationMinutes""                   REAL    NOT NULL DEFAULT 0,
                         ""BarsInTrade""                       INTEGER NOT NULL DEFAULT 0,
                         ""EntrySessionWindow""                TEXT    NOT NULL DEFAULT '',
                         ""ExitSessionWindow""                 TEXT    NOT NULL DEFAULT '',
                         ""CrossedSessionBoundary""            INTEGER NOT NULL DEFAULT 0,
                         ""RMultiple""                         REAL    NOT NULL DEFAULT 0,
                         ""CreatedAt""                         TEXT    NOT NULL DEFAULT '',
                         ""UpdatedAt""                         TEXT    NOT NULL DEFAULT '')",
                    "CREATE INDEX IF NOT EXISTS ""IX_TradeLifespanRecords_TradeOutcomeId"" ON ""TradeLifespanRecords"" (""TradeOutcomeId"")",
                    "CREATE TABLE IF NOT EXISTS ""AdaptiveParameters"" (
                         ""Id""                INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                         ""StrategyName""       TEXT    NOT NULL DEFAULT '',
                         ""PersonaName""        TEXT    NOT NULL DEFAULT '',
                         ""ParameterName""      TEXT    NOT NULL DEFAULT '',
                         ""BaseValue""          REAL    NOT NULL DEFAULT 0,
                         ""AdjustmentValue""    REAL    NOT NULL DEFAULT 0,
                         ""EffectiveValue""     REAL    NOT NULL DEFAULT 0,
                         ""Rationale""          TEXT    NOT NULL DEFAULT '',
                         ""IsActive""           INTEGER NOT NULL DEFAULT 1,
                         ""SourceTradeCount""   INTEGER NOT NULL DEFAULT 0,
                         ""CreatedAt""          TEXT    NOT NULL DEFAULT '',
                         ""UpdatedAt""          TEXT    NOT NULL DEFAULT '')",
                    "CREATE UNIQUE INDEX IF NOT EXISTS ""UQ_AdaptiveParameters_Key"" ON ""AdaptiveParameters"" (""StrategyName"", ""PersonaName"", ""ParameterName"")",
                    "CREATE TABLE IF NOT EXISTS ""ContractCache"" (
                         ""RootSymbol""    TEXT NOT NULL PRIMARY KEY,
                         ""ContractId""   TEXT NOT NULL DEFAULT '',
                         ""LastUpdated""  TEXT NOT NULL DEFAULT '')"
                }
                For Each ddl In newTableDdl
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = ddl
                        cmd.ExecuteNonQuery()
                    End Using
                Next
            Finally
                If mustClose5 Then conn.Close()
            End Try

            ' ── FEAT-34: SuperTrend+ config persistence ──────────────────────────
            Dim mustClose6 = (conn.State <> ConnectionState.Open)
            If mustClose6 Then conn.Open()
            Try
                Dim stpDdl = New String() {
                    "CREATE TABLE IF NOT EXISTS ""SuperTrendPlusConfig"" (
                         ""Id""                              INTEGER NOT NULL PRIMARY KEY,
                         ""SelectedTpMultiple""              TEXT    NOT NULL DEFAULT '2×',
                         ""StMultiplier""                    REAL    NOT NULL DEFAULT 3.0,
                         ""SelectedTimeframe""               TEXT    NOT NULL DEFAULT '15min',
                         ""MaxSlots""                        INTEGER NOT NULL DEFAULT 3,
                         ""ContractsPerSlot""                INTEGER NOT NULL DEFAULT 1,
                         ""AdxWeakThreshold""                REAL    NOT NULL DEFAULT 25.0,
                         ""AdxModerateThreshold""            REAL    NOT NULL DEFAULT 40.0,
                         ""AdxStrongThreshold""              REAL    NOT NULL DEFAULT 60.0,
                         ""WarningScoreThreshold""           INTEGER NOT NULL DEFAULT 3,
                         ""ExitingScoreThreshold""           INTEGER NOT NULL DEFAULT 6,
                         ""EntryExitScoreBlockThreshold""    INTEGER NOT NULL DEFAULT 4)"
                }
                For Each ddl In stpDdl
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = ddl
                        cmd.ExecuteNonQuery()
                    End Using
                Next
            Finally
                If mustClose6 Then conn.Close()
            End Try

            ' ── FEAT-36: SuperTrend+ persona column ──────────────────────────────
            Dim mustClose7 = (conn.State <> ConnectionState.Open)
            If mustClose7 Then conn.Open()
            Try
                Try
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "ALTER TABLE ""SuperTrendPlusConfig"" ADD COLUMN ""ActivePersona"" TEXT NOT NULL DEFAULT 'Damian'"
                        cmd.ExecuteNonQuery()
                    End Using
                Catch ex As Exception
                    If Not ex.Message.Contains("duplicate column") Then Throw
                End Try
            Finally
                If mustClose7 Then conn.Close()
            End Try

            ' ── CHORE-01: Drop dead phase-ladder columns, add FEAT-46 threshold ──
            ' Idempotent — duplicate-column errors are swallowed; "no such column"
            ' errors from DROP COLUMN are swallowed (column already absent).
            Dim mustClose8 = (conn.State <> ConnectionState.Open)
            If mustClose8 Then conn.Open()
            Try
                Dim deadCols = New String() {
                    "BreakevenTriggerR", "ProfitLockTriggerR", "ProfitLockOffsetR",
                    "TrailAtrMultiple",  "ProfitTrailTriggerR",
                    "HarvestTriggerR",   "HarvestLockR",
                    "FreeRideTriggerR",  "FreeRideLockR"
                }
                For Each col In deadCols
                    Try
                        Using cmd = conn.CreateCommand()
                            cmd.CommandText = $"ALTER TABLE ""SuperTrendPlusConfig"" DROP COLUMN ""{col}"""
                            cmd.ExecuteNonQuery()
                        End Using
                    Catch ex As Exception
                        ' Column already absent or SQLite version < 3.35 — silently skip.
                        If Not ex.Message.Contains("no such column") AndAlso
                           Not ex.Message.Contains("no such table") Then
                            ' Unexpected error — rethrow.
                            Throw
                        End If
                    End Try
                Next

                Try
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "ALTER TABLE ""SuperTrendPlusConfig"" ADD COLUMN ""EntryExitScoreBlockThreshold"" INTEGER NOT NULL DEFAULT 4"
                        cmd.ExecuteNonQuery()
                    End Using
                Catch ex As Exception
                    If Not ex.Message.Contains("duplicate column") Then Throw
                End Try
            Finally
                If mustClose8 Then conn.Close()
            End Try
        End Sub

    End Class

End Namespace
