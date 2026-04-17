Imports System.Threading
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data.Repositories

    ''' <summary>
    ''' Persists and retrieves backtest run results, including per-trade detail.
    ''' </summary>
    Public Class BacktestRepository

        Private ReadOnly _context As AppDbContext
        Private ReadOnly _logger As ILogger(Of BacktestRepository)

        Public Sub New(context As AppDbContext, logger As ILogger(Of BacktestRepository))
            _context = context
            _logger = logger
        End Sub

        ''' <summary>Save a completed backtest run (including child trades). Returns the new Run ID.</summary>
        Public Async Function SaveRunAsync(entity As BacktestRunEntity,
                                            Optional cancel As CancellationToken = Nothing) As Task(Of Long)
            entity.Status = 1  ' Complete
            entity.CompletedAt = DateTimeOffset.UtcNow
            _context.BacktestRuns.Add(entity)
            Await _context.SaveChangesAsync(cancel)
            _logger.LogInformation("Backtest run saved: Id={Id}, Trades={N}", entity.Id, entity.Trades.Count)
            Return entity.Id
        End Function

        ''' <summary>Returns the N most recent completed backtest runs (without trade detail).</summary>
        Public Async Function GetRecentRunsAsync(Optional maxRuns As Integer = 20,
                                                  Optional cancel As CancellationToken = Nothing) _
            As Task(Of IList(Of BacktestRunEntity))
            Return Await _context.BacktestRuns.
                Where(Function(r) r.Status = 1).
                OrderByDescending(Function(r) r.CompletedAt).
                Take(maxRuns).
                AsNoTracking().
                ToListAsync(cancel)
        End Function

        ''' <summary>Returns a single run with all its child trades.</summary>
        Public Async Function GetRunWithTradesAsync(runId As Long,
                                                     Optional cancel As CancellationToken = Nothing) _
            As Task(Of BacktestRunEntity)
            Return Await _context.BacktestRuns.
                Include(Function(r) r.Trades).
                AsNoTracking().
                FirstOrDefaultAsync(Function(r) r.Id = runId, cancel)
        End Function

    End Class

End Namespace
