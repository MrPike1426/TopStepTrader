Imports Microsoft.EntityFrameworkCore
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data.Repositories

    ''' <summary>
    ''' Persists and queries real trade outcomes for the ML feedback loop.
    ''' </summary>
    Public Class TradeOutcomeRepository

        Private ReadOnly _db As AppDbContext

        Public Sub New(db As AppDbContext)
            _db = db
        End Sub

        ''' <summary>Save a new (initially open) trade outcome. Returns the assigned Id.</summary>
        Public Async Function SaveOutcomeAsync(outcome As TradeOutcome) As Task(Of Long)
            Dim entity As New TradeOutcomeEntity With {
                .SignalId = outcome.SignalId,
                .OrderId = outcome.OrderId,
                .ContractId = outcome.ContractId,
                .Timeframe = outcome.Timeframe,
                .SignalType = outcome.SignalType,
                .SignalConfidence = outcome.SignalConfidence,
                .ModelVersion = outcome.ModelVersion,
                .EntryTime = outcome.EntryTime,
                .EntryPrice = outcome.EntryPrice,
                .IsOpen = True,
                .ExitReason = String.Empty
            }
            _db.TradeOutcomes.Add(entity)
            Await _db.SaveChangesAsync()
            Return entity.Id
        End Function

        ''' <summary>Update an open outcome with the resolved exit data.</summary>
        Public Async Function ResolveOutcomeAsync(id As Long,
                                                   exitTime As DateTimeOffset,
                                                   exitPrice As Decimal,
                                                   pnl As Decimal,
                                                   isWinner As Boolean,
                                                   exitReason As String) As Task
            Dim entity = Await _db.TradeOutcomes.FindAsync(id)
            If entity Is Nothing Then Return

            entity.ExitTime = exitTime
            entity.ExitPrice = exitPrice
            entity.PnL = pnl
            entity.IsWinner = isWinner
            entity.ExitReason = exitReason
            entity.IsOpen = False

            Await _db.SaveChangesAsync()
        End Function

        ''' <summary>All outcomes still marked open (not yet resolved).</summary>
        ''' <remarks>
        ''' UAT-BUG-003 (ORDER BY extension): EF Core SQLite cannot translate DateTimeOffset
        ''' in ORDER BY clauses. Fix: order by Id (Long PK) which increases monotonically with
        ''' insertion time, giving the same chronological order as EntryTime.
        ''' </remarks>
        Public Async Function GetOpenOutcomesAsync() As Task(Of List(Of TradeOutcomeEntity))
            Return Await _db.TradeOutcomes _
                            .AsNoTracking() _
                            .Where(Function(o) o.IsOpen) _
                            .OrderBy(Function(o) o.Id) _
                            .ToListAsync()
        End Function

        ''' <summary>Resolved outcomes from the given lookback period, ordered by entry time.</summary>
        ''' <remarks>
        ''' UAT-BUG-002: VB.NET "Not booleanProperty" → OnesComplement → EF Core can't translate.
        '''   Fix: o.IsOpen = False (ExpressionType.Equal → WHERE IsOpen = 0).
        ''' UAT-BUG-003: EF Core SQLite cannot translate DateTimeOffset >= in a LINQ WHERE clause
        '''   because the value converter (DateTimeOffsetToStringConverter) is not applied to the
        '''   right-hand parameter when building the SQL predicate.
        '''   Fix: pre-format the cutoff to TsFmt and use FromSqlInterpolated — same pattern as
        '''   BarRepository (UAT-BUG-001).
        ''' </remarks>
        Public Async Function GetResolvedOutcomesAsync(from As DateTimeOffset) As Task(Of List(Of TradeOutcomeEntity))
            ' Format to the EF Core SQLite DateTimeOffset TEXT format so the string comparison
            ' in SQLite gives the same result as a numeric date comparison.
            Const TsFmt As String = "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz"
            Dim fromStr As String = from.ToString(TsFmt)

            ' OrderBy cannot use EntryTime (DateTimeOffset) in EF Core SQLite ORDER BY clauses.
            ' Fetch unordered from SQL, then sort in-memory via LINQ-to-Objects.
            Dim entities = Await _db.TradeOutcomes _
                                    .FromSqlInterpolated(
                                        $"SELECT * FROM TradeOutcomes WHERE IsOpen = 0 AND EntryTime >= {fromStr}") _
                                    .AsNoTracking() _
                                    .ToListAsync()
            Return entities.OrderBy(Function(o) o.EntryTime).ToList()
        End Function

        ''' <summary>Rolling win-rate over the most recent N resolved outcomes.</summary>
        ''' <remarks>
        ''' UAT-BUG-002: Not o.IsOpen → o.IsOpen = False (OnesComplement fix).
        ''' UAT-BUG-003: OrderByDescending(EntryTime) replaced with OrderByDescending(Id) —
        '''   DateTimeOffset cannot be used in EF Core SQLite ORDER BY clauses.
        '''   Id is a monotonically increasing PK so it gives the same "most recent" ordering.
        ''' </remarks>
        Public Async Function GetRollingWinRateAsync(windowSize As Integer) As Task(Of Single)
            Dim recent = Await _db.TradeOutcomes _
                                   .AsNoTracking() _
                                   .Where(Function(o) o.IsOpen = False AndAlso o.IsWinner.HasValue) _
                                   .OrderByDescending(Function(o) o.Id) _
                                   .Take(windowSize) _
                                   .ToListAsync()
            If recent.Count = 0 Then Return 0
            Dim wins = recent.Where(Function(o) o.IsWinner = True).Count()
            Return CSng(wins) / CSng(recent.Count)
        End Function

        ''' <summary>Total number of outcomes (open + resolved).</summary>
        Public Async Function GetCountAsync() As Task(Of Integer)
            Return Await _db.TradeOutcomes.CountAsync()
        End Function

    End Class

End Namespace
