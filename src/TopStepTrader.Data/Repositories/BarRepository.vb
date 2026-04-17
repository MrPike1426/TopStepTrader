Option Compare Binary
Option Strict On
Option Explicit On

Imports System.Data
Imports System.Threading
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data.Repositories

    Public Class BarRepository

        Private ReadOnly _context As AppDbContext
        Private ReadOnly _logger As ILogger(Of BarRepository)

        Public Sub New(context As AppDbContext, logger As ILogger(Of BarRepository))
            _context = context
            _logger = logger
        End Sub

        ' EF Core 9 SQLite stores DateTimeOffset via DateTimeOffsetToStringConverter
        ' using the format "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz" (space not T, capital F = no
        ' trailing zeros in fractional part, zzz = signed offset like "+00:00").
        ' All raw SQL and FromSqlInterpolated timestamp literals must use this exact format.
        Private Const TsFmt As String = "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz"

        ' DateTime columns (no offset) use lowercase "f" — always emits all 7 fractional digits.
        Private Const CreatedFmt As String = "yyyy-MM-dd HH:mm:ss.fffffff"

        ''' <summary>Get bars for ML training or backtest. Returns oldest-first.</summary>
        ''' <remarks>
        ''' Uses FromSqlInterpolated instead of LINQ .Where() to bypass the VB.NET / EF Core
        ''' string-comparison incompatibility (UAT-BUG-001): VB.NET compiles String = String
        ''' inside expression trees to String.Compare(), which EF Core SQLite cannot translate.
        ''' FromSqlInterpolated passes ContractId as a parameterised SQL literal, bypassing
        ''' the expression tree entirely.  Timestamps are pre-formatted to TsFmt so SQLite's
        ''' text ordering gives the same result as a numeric date comparison.
        '''
        ''' UAT-BUG-005: ordering is done in-memory using LINQ-to-Objects on the Timestamp
        ''' property, NOT by Id.  Id order does not guarantee Timestamp order when bars are
        ''' stored in descending-timestamp batches (e.g. the ProjectX API returns newest-first
        ''' and BulkInsertAsync stores them in that order, so newer bars have lower Ids).
        ''' Using .OrderBy(b.Id) then gave the backtest engine reverse-chronological bars,
        ''' causing exit timestamps to be earlier than entry timestamps and massively inflated
        ''' metrics due to look-ahead bias.
        ''' </remarks>
        Public Async Function GetBarsAsync(contractId As String,
                                            timeframe As BarTimeframe,
                                            from As DateTimeOffset,
                                            [to] As DateTimeOffset,
                                            Optional cancel As CancellationToken = Nothing) As Task(Of List(Of MarketBar))
            Dim tfCode As Integer = CInt(timeframe)
            Dim fromStr As String = from.ToString(TsFmt)
            Dim toStr As String = [to].ToString(TsFmt)

            Dim entities = Await _context.Bars _
                .FromSqlInterpolated(
                    $"SELECT * FROM Bars WHERE ContractId = {contractId} AND Timeframe = {tfCode} AND Timestamp >= {fromStr} AND Timestamp <= {toStr}") _
                .AsNoTracking() _
                .ToListAsync(cancel)

            ' Sort in-memory by Timestamp (LINQ-to-Objects) to guarantee chronological
            ' oldest-first order regardless of storage/insertion order.
            ' EF Core SQLite cannot use DateTimeOffset in ORDER BY clauses (UAT-BUG-003);
            ' in-memory sorting avoids that restriction and is always reliable.
            Return entities.OrderBy(Function(b) b.Timestamp).Select(AddressOf MapToModel).ToList()
        End Function

        ''' <summary>Get the N most recent bars for a contract/timeframe, returned oldest-first.</summary>
        ''' <remarks>
        ''' See GetBarsAsync for the FromSqlInterpolated rationale (UAT-BUG-001).
        ''' UAT-BUG-005: selects the N most recent rows by Timestamp in-memory — cannot rely
        ''' on Id ordering (see GetBarsAsync remarks).
        ''' </remarks>
        Public Async Function GetRecentBarsAsync(contractId As String,
                                                  timeframe As BarTimeframe,
                                                  count As Integer,
                                                  Optional cancel As CancellationToken = Nothing) As Task(Of List(Of MarketBar))
            Dim tfCode As Integer = CInt(timeframe)

            Dim entities = Await _context.Bars _
                .FromSqlInterpolated(
                    $"SELECT * FROM Bars WHERE ContractId = {contractId} AND Timeframe = {tfCode}") _
                .AsNoTracking() _
                .ToListAsync(cancel)

            ' Select the N most-recent bars by Timestamp then return them oldest-first.
            Return entities.OrderByDescending(Function(b) b.Timestamp) _
                           .Take(count) _
                           .OrderBy(Function(b) b.Timestamp) _
                           .Select(AddressOf MapToModel).ToList()
        End Function

        ''' <summary>Get the timestamp of the latest stored bar (used to know where to fetch from).</summary>
        ''' <remarks>
        ''' See GetBarsAsync for the FromSqlInterpolated rationale (UAT-BUG-001).
        ''' UAT-BUG-005: uses in-memory Max(Timestamp) rather than OrderByDescending(Id) —
        ''' Id does not reliably map to Timestamp order when bars are stored newest-first.
        ''' </remarks>
        Public Async Function GetLatestTimestampAsync(contractId As String,
                                                       timeframe As BarTimeframe,
                                                       Optional cancel As CancellationToken = Nothing) As Task(Of DateTimeOffset?)
            Dim tfCode As Integer = CInt(timeframe)

            Dim entities = Await _context.Bars _
                .FromSqlInterpolated(
                    $"SELECT * FROM Bars WHERE ContractId = {contractId} AND Timeframe = {tfCode}") _
                .AsNoTracking() _
                .ToListAsync(cancel)

            If entities.Count = 0 Then Return Nothing
            Return CType(entities.Max(Function(b) b.Timestamp), DateTimeOffset?)
        End Function

        ''' <summary>
        ''' Bulk upsert bars — inserts new ones, silently skips duplicates.
        '''
        ''' WHY raw ADO.NET instead of EF Core AddRange/SaveChanges:
        '''   EF Core's LINQ translator for SQLite stores DateTimeOffset as TEXT using the
        '''   format "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz".  The old code compared timestamps with
        '''   b.Timestamp.Equals(entity.Timestamp) inside AnyAsync — EF Core cannot reliably
        '''   translate DateTimeOffset.Equals() for SQLite, causing the duplicate check to
        '''   always return False and SaveChangesAsync to throw a unique-constraint violation
        '''   (which was swallowed), so zero bars were ever persisted.
        '''
        '''   INSERT OR IGNORE delegates deduplication to SQLite's native unique index on
        '''   (ContractId, Timeframe, Timestamp), which is always correct and runs in a
        '''   single transaction for performance.
        ''' </summary>
        Public Async Function BulkInsertAsync(bars As IEnumerable(Of MarketBar),
                                               timeframe As BarTimeframe,
                                               Optional cancel As CancellationToken = Nothing) As Task(Of Integer)
            Dim entities = bars.Select(Function(b) MapToEntity(b, timeframe)).ToList()
            If entities.Count = 0 Then Return 0

            ' Class-level TsFmt / CreatedFmt constants are used here so that raw INSERT
            ' values are stored in the same TEXT format that FromSqlInterpolated range
            ' queries and EF Core LINQ date comparisons expect.
            Const Sql As String =
                "INSERT OR IGNORE INTO Bars " &
                "(ContractId, Timeframe, Timestamp, OpenPrice, HighPrice, LowPrice, ClosePrice, Volume, VWAP, CreatedAt) " &
                "VALUES (@contractId, @timeframe, @timestamp, @open, @high, @low, @close, @volume, @vwap, @createdAt)"

            ' Obtain the connection EF Core is using.  Open it ourselves only when EF Core
            ' has not already opened it (e.g. during an enclosing transaction).
            Dim conn = _context.Database.GetDbConnection()
            Dim mustClose = (conn.State <> ConnectionState.Open)
            If mustClose Then Await conn.OpenAsync(cancel)

            Dim inserted = 0
            Dim txn = conn.BeginTransaction()
            Try
                For Each entity In entities
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = txn
                        cmd.CommandText = Sql

                        Dim tsText = entity.Timestamp.ToString(TsFmt)
                        Dim createdText = entity.CreatedAt.ToString(CreatedFmt)

                        ' Helper: create and add a named parameter
                        Dim addP = Sub(name As String, value As Object)
                                       Dim p = cmd.CreateParameter()
                                       p.ParameterName = name
                                       p.Value = If(value Is Nothing, DBNull.Value, value)
                                       cmd.Parameters.Add(p)
                                   End Sub

                        addP("@contractId", entity.ContractId)
                        addP("@timeframe", entity.Timeframe)
                        addP("@timestamp", tsText)
                        addP("@open", entity.OpenPrice)
                        addP("@high", entity.HighPrice)
                        addP("@low", entity.LowPrice)
                        addP("@close", entity.ClosePrice)
                        addP("@volume", entity.Volume)
                        addP("@vwap", If(entity.VWAP.HasValue, CObj(entity.VWAP.Value), Nothing))
                        addP("@createdAt", createdText)

                        ' ExecuteNonQuery returns 1 for an actual insert, 0 when IGNORE fires
                        inserted += Await cmd.ExecuteNonQueryAsync(cancel)
                    End Using
                Next

                txn.Commit()

            Catch ex As Exception
                txn.Rollback()
                _logger.LogError(ex, "BulkInsertAsync: transaction rolled back ({N} bars for {Contract})",
                                 entities.Count, entities.FirstOrDefault()?.ContractId)
                Throw

            Finally
                txn.Dispose()
                If mustClose Then conn.Close()
            End Try

            _logger.LogDebug("BulkInsertAsync: {Count} inserted, {Skipped} duplicates skipped ({Contract})",
                             inserted, entities.Count - inserted,
                             entities.FirstOrDefault()?.ContractId)
            Return inserted
        End Function

        ' ── Private helpers ──────────────────────────────────────────────────────

        Private Function MapToModel(entity As BarEntity) As MarketBar
            Return New MarketBar With {
                .Id = entity.Id,
                .ContractId = entity.ContractId,
                .Timestamp = entity.Timestamp,
                .Timeframe = CType(entity.Timeframe, BarTimeframe),
                .Open = entity.OpenPrice,
                .High = entity.HighPrice,
                .Low = entity.LowPrice,
                .Close = entity.ClosePrice,
                .Volume = entity.Volume,
                .VWAP = entity.VWAP
            }
        End Function

        Private Function MapToEntity(bar As MarketBar, timeframe As BarTimeframe) As BarEntity
            Return New BarEntity With {
                .ContractId = bar.ContractId,
                .Timeframe = CInt(timeframe),
                .Timestamp = bar.Timestamp,
                .OpenPrice = bar.Open,
                .HighPrice = bar.High,
                .LowPrice = bar.Low,
                .ClosePrice = bar.Close,
                .Volume = bar.Volume,
                .VWAP = bar.VWAP
            }
        End Function

    End Class

End Namespace
