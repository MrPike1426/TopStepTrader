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
        Public Property RiskEvents As DbSet(Of RiskEventEntity)
        Public Property TradeOutcomes As DbSet(Of TradeOutcomeEntity)
        Public Property TradeSetupSnapshots As DbSet(Of TradeSetupSnapshotEntity)
        Public Property TradeLifespanRecords As DbSet(Of TradeLifespanRecordEntity)
        Public Property AdaptiveParameters As DbSet(Of AdaptiveParametersEntity)
        Public Property BalanceHistory As DbSet(Of BalanceHistoryEntity)
        Public Property PersonaSettings As DbSet(Of PersonaSettingsEntity)
        Public Property ContractCache As DbSet(Of ContractCacheEntity)
        Public Property SuperTrendPlusConfig As DbSet(Of SuperTrendPlusConfigEntity)
        Public Property UltimateScalperConfig As DbSet(Of UltimateScalperConfigEntity)
        Public Property SlipStreamConfig As DbSet(Of SlipStreamConfigEntity)
        Public Property BreakAndBounceConfig As DbSet(Of BreakAndBounceConfigEntity)

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

            ' BacktestRuns / BacktestTrades entities removed (ARCH-17). The tables are
            ' dropped on startup by EnsureSchemaCurrent for backwards-compatibility.

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

            ' UltimateScalperConfig — singleton row (id=1), no auto-increment (FEAT-64)
            modelBuilder.Entity(Of UltimateScalperConfigEntity)() _
                .ToTable("UltimateScalperConfig") _
                .HasKey(Function(c) c.Id)

            ' SlipStreamConfig — singleton row (id=1), no auto-increment (FEAT-70)
            modelBuilder.Entity(Of SlipStreamConfigEntity)() _
                .ToTable("SlipStreamConfig") _
                .HasKey(Function(c) c.Id)

            ' BreakAndBounceConfig — singleton row (id=1), no auto-increment (FEAT-62)
            modelBuilder.Entity(Of BreakAndBounceConfigEntity)() _
                .ToTable("BreakAndBounceConfig") _
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

            ' ── ARCH-17: drop legacy backtest tables from existing user databases ──────
            ' BacktestRuns / BacktestTrades and the entire backtest subsystem were
            ' removed (ARCH-17). Idempotent DROP IF EXISTS keeps existing DBs clean.
            Dim mustCloseDrop = (conn.State <> ConnectionState.Open)
            If mustCloseDrop Then conn.Open()
            Try
                For Each ddl In New String() {
                    "DROP TABLE IF EXISTS ""BacktestTrades""",
                    "DROP TABLE IF EXISTS ""BacktestRuns"""
                }
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = ddl
                        cmd.ExecuteNonQuery()
                    End Using
                Next
            Finally
                If mustCloseDrop Then conn.Close()
            End Try

            ' ── Scale-in support placeholder (legacy column add no longer needed) ─────

            ' ── FEAT-01: extend TradeOutcomes + new tables ────────────────────
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

            ' ── FEAT-63: $-denominated TP ladder column ──────────────────────────
            Dim mustClose9 = (conn.State <> ConnectionState.Open)
            If mustClose9 Then conn.Open()
            Try
                Try
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "ALTER TABLE ""SuperTrendPlusConfig"" ADD COLUMN ""LadderTpDollars"" TEXT NOT NULL DEFAULT '0'"
                        cmd.ExecuteNonQuery()
                    End Using
                Catch ex As Exception
                    If Not ex.Message.Contains("duplicate column") Then Throw
                End Try
            Finally
                If mustClose9 Then conn.Close()
            End Try

            ' ── FEAT-64: Ultimate Scalper config singleton table ──────────────────
            Dim mustClose10 = (conn.State <> ConnectionState.Open)
            If mustClose10 Then conn.Open()
            Try
                Dim scalperDdl = New String() {
                    "CREATE TABLE IF NOT EXISTS ""UltimateScalperConfig"" (
                         ""Id""                          INTEGER NOT NULL PRIMARY KEY,
                         ""MaLength""                    INTEGER NOT NULL DEFAULT 200,
                         ""RsiLength""                   INTEGER NOT NULL DEFAULT 14,
                         ""RsiOverbought""               REAL    NOT NULL DEFAULT 70.0,
                         ""RsiOversold""                 REAL    NOT NULL DEFAULT 30.0,
                         ""MaxBarsSinceMidlineCross""    INTEGER NOT NULL DEFAULT 10,
                         ""SafetyCeilingTpDollars""      TEXT    NOT NULL DEFAULT '200',
                         ""MinSlEditStepTicks""          INTEGER NOT NULL DEFAULT 1,
                         ""MaxSlEditsPerSecond""         INTEGER NOT NULL DEFAULT 5,
                         ""MesInitialStopDollars""       TEXT    NOT NULL DEFAULT '20',
                         ""MesBreakevenSnapDollars""     TEXT    NOT NULL DEFAULT '7',
                         ""MesTrailDistanceDollars""     TEXT    NOT NULL DEFAULT '7',
                         ""MnqInitialStopDollars""       TEXT    NOT NULL DEFAULT '40',
                         ""MnqBreakevenSnapDollars""     TEXT    NOT NULL DEFAULT '12',
                         ""MnqTrailDistanceDollars""     TEXT    NOT NULL DEFAULT '12',
                         ""MgcInitialStopDollars""       TEXT    NOT NULL DEFAULT '30',
                         ""MgcBreakevenSnapDollars""     TEXT    NOT NULL DEFAULT '10',
                         ""MgcTrailDistanceDollars""     TEXT    NOT NULL DEFAULT '10')"
                }
                For Each ddl In scalperDdl
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = ddl
                        cmd.ExecuteNonQuery()
                    End Using
                Next

                ' Idempotent ALTER for installs created before the Leverage column existed.
                Try
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "ALTER TABLE ""UltimateScalperConfig"" ADD COLUMN ""Leverage"" INTEGER NOT NULL DEFAULT 1"
                        cmd.ExecuteNonQuery()
                    End Using
                Catch ex As Exception
                    If Not ex.Message.Contains("duplicate column") Then Throw
                End Try

                ' FEAT-69: idempotent ALTERs for the pre-staged stop-entry knobs.
                Dim feat69Columns = New (Name As String, Ddl As String)() {
                    ("PreStagedEntriesEnabled", "ALTER TABLE ""UltimateScalperConfig"" ADD COLUMN ""PreStagedEntriesEnabled"" INTEGER NOT NULL DEFAULT 1"),
                    ("EntryTriggerOffsetTicks", "ALTER TABLE ""UltimateScalperConfig"" ADD COLUMN ""EntryTriggerOffsetTicks"" INTEGER NOT NULL DEFAULT 1"),
                    ("RepriceThresholdTicks", "ALTER TABLE ""UltimateScalperConfig"" ADD COLUMN ""RepriceThresholdTicks"" INTEGER NOT NULL DEFAULT 2"),
                    ("ArmStaleMinutes", "ALTER TABLE ""UltimateScalperConfig"" ADD COLUMN ""ArmStaleMinutes"" INTEGER NOT NULL DEFAULT 30"),
                    ("ReArmDebounceSeconds", "ALTER TABLE ""UltimateScalperConfig"" ADD COLUMN ""ReArmDebounceSeconds"" INTEGER NOT NULL DEFAULT 30"),
                    ("MaxBrokerCallsPerMinute", "ALTER TABLE ""UltimateScalperConfig"" ADD COLUMN ""MaxBrokerCallsPerMinute"" INTEGER NOT NULL DEFAULT 80"),
                    ("MaxConcurrentPositions", "ALTER TABLE ""UltimateScalperConfig"" ADD COLUMN ""MaxConcurrentPositions"" INTEGER NOT NULL DEFAULT 1")
                }
                For Each col In feat69Columns
                    Try
                        Using cmd = conn.CreateCommand()
                            cmd.CommandText = col.Ddl
                            cmd.ExecuteNonQuery()
                        End Using
                    Catch ex As Exception
                        If Not ex.Message.Contains("duplicate column") Then Throw
                    End Try
                Next
            Finally
                If mustClose10 Then conn.Close()
            End Try

            ' ── FEAT-70: SlipStream config singleton table ────────────────────────
            Dim mustClose11 = (conn.State <> ConnectionState.Open)
            If mustClose11 Then conn.Open()
            Try
                Dim slipDdl = New String() {
                    "CREATE TABLE IF NOT EXISTS ""SlipStreamConfig"" (
                         ""Id""                          INTEGER NOT NULL PRIMARY KEY,
                         ""HtfTimeframe""                TEXT    NOT NULL DEFAULT '60min',
                         ""UseHtfFilter""                INTEGER NOT NULL DEFAULT 1,
                         ""HtfEmaLength""                INTEGER NOT NULL DEFAULT 50,
                         ""EmaFastLength""               INTEGER NOT NULL DEFAULT 21,
                         ""EmaSlowLength""               INTEGER NOT NULL DEFAULT 200,
                         ""RsiLength""                   INTEGER NOT NULL DEFAULT 14,
                         ""RsiLongMin""                  REAL    NOT NULL DEFAULT 55.0,
                         ""RsiShortMax""                 REAL    NOT NULL DEFAULT 45.0,
                         ""AdxLength""                   INTEGER NOT NULL DEFAULT 14,
                         ""AdxMin""                      REAL    NOT NULL DEFAULT 22.0,
                         ""AtrLength""                   INTEGER NOT NULL DEFAULT 14,
                         ""AtrPercentLookback""          INTEGER NOT NULL DEFAULT 100,
                         ""AtrPercentMin""               REAL    NOT NULL DEFAULT 30.0,
                         ""ExtendBars""                  INTEGER NOT NULL DEFAULT 3,
                         ""ExtendAtrMult""               REAL    NOT NULL DEFAULT 0.5,
                         ""RiskPct""                     REAL    NOT NULL DEFAULT 0.5,
                         ""AtrSLmult""                   REAL    NOT NULL DEFAULT 1.5,
                         ""AtrTP1mult""                  REAL    NOT NULL DEFAULT 1.0,
                         ""Tp1Pct""                      REAL    NOT NULL DEFAULT 50.0,
                         ""TrailMult""                   REAL    NOT NULL DEFAULT 1.5,
                         ""TrailOffsetMult""             REAL    NOT NULL DEFAULT 1.0,
                         ""MaxBarsInTrade""              INTEGER NOT NULL DEFAULT 40,
                         ""UseSession""                  INTEGER NOT NULL DEFAULT 1,
                         ""SessionWindow""               TEXT    NOT NULL DEFAULT '0830-1500',
                         ""FlatWindow""                  TEXT    NOT NULL DEFAULT '1450-1500',
                         ""CooldownBars""                INTEGER NOT NULL DEFAULT 3,
                         ""EnableLong""                  INTEGER NOT NULL DEFAULT 1,
                         ""EnableShort""                 INTEGER NOT NULL DEFAULT 1,
                         ""MinSlEditStepTicks""          INTEGER NOT NULL DEFAULT 1,
                         ""MaxSlEditsPerSecond""         INTEGER NOT NULL DEFAULT 5,
                         ""MaxConcurrentPositions""      INTEGER NOT NULL DEFAULT 1,
                         ""MesAtrSLmultOverride""        REAL,
                         ""MesAtrTP1multOverride""       REAL,
                         ""MesTrailMultOverride""        REAL,
                         ""MesTrailOffsetMultOverride""  REAL,
                         ""MnqAtrSLmultOverride""        REAL,
                         ""MnqAtrTP1multOverride""       REAL,
                         ""MnqTrailMultOverride""        REAL,
                         ""MnqTrailOffsetMultOverride""  REAL,
                         ""MgcAtrSLmultOverride""        REAL,
                         ""MgcAtrTP1multOverride""       REAL,
                         ""MgcTrailMultOverride""        REAL,
                         ""MgcTrailOffsetMultOverride""  REAL)"
                }
                For Each ddl In slipDdl
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = ddl
                        cmd.ExecuteNonQuery()
                    End Using
                Next
            Finally
                If mustClose11 Then conn.Close()
            End Try

            ' ── FEAT-62: BreakAndBounce config singleton table ───────────────────
            Dim mustClose12 = (conn.State <> ConnectionState.Open)
            If mustClose12 Then conn.Open()
            Try
                Dim bbDdl = New String() {
                    "CREATE TABLE IF NOT EXISTS ""BreakAndBounceConfig"" (
                         ""Id""                              INTEGER NOT NULL PRIMARY KEY,
                         ""EntryWindow""                     TEXT    NOT NULL DEFAULT '0830-1100',
                         ""FlatWindow""                      TEXT    NOT NULL DEFAULT '1450-1500',
                         ""BreakoutTimeframe""               TEXT    NOT NULL DEFAULT '15min',
                         ""RetestTimeframe""                 TEXT    NOT NULL DEFAULT '5min',
                         ""MinimumStopDistanceTicks""        INTEGER NOT NULL DEFAULT 8,
                         ""MinimumStopAtrFraction""          REAL    NOT NULL DEFAULT 0.5,
                         ""AtrLength""                       INTEGER NOT NULL DEFAULT 14,
                         ""InvalidateDirOnCounterBreakout""  INTEGER NOT NULL DEFAULT 1,
                         ""InvalidateDirOnWindowExpiry""     INTEGER NOT NULL DEFAULT 1,
                         ""ContractsPerEntry""               INTEGER NOT NULL DEFAULT 1,
                         ""AiVetoEnabled""                   INTEGER NOT NULL DEFAULT 1,
                         ""EnableLong""                      INTEGER NOT NULL DEFAULT 1,
                         ""EnableShort""                     INTEGER NOT NULL DEFAULT 1,
                         ""MinSlEditStepTicks""              INTEGER NOT NULL DEFAULT 1,
                         ""MaxConcurrentPositions""          INTEGER NOT NULL DEFAULT 1)"
                }
                For Each ddl In bbDdl
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = ddl
                        cmd.ExecuteNonQuery()
                    End Using
                Next
            Finally
                If mustClose12 Then conn.Close()
            End Try
        End Sub

    End Class

End Namespace
