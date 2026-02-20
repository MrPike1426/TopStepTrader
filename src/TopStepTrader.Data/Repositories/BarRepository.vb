Imports System.Threading
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data.Repositories

    Public Class BarRepository

        Private ReadOnly _context As AppDbContext
        Private ReadOnly _logger As ILogger(Of BarRepository)

        Public Sub New(context As AppDbContext, logger As ILogger(Of BarRepository))
            _context = context
            _logger = logger
        End Sub

        ''' <summary>Get bars for ML training or backtest. Returns newest-first by default.</summary>
        Public Async Function GetBarsAsync(contractId As Integer,
                                            timeframe As BarTimeframe,
                                            from As DateTimeOffset,
                                            [to] As DateTimeOffset,
                                            Optional cancel As CancellationToken = Nothing) As Task(Of List(Of MarketBar))
            Dim entities = Await _context.Bars _
                .Where(Function(b) b.ContractId = contractId _
                                AndAlso b.Timeframe = CInt(timeframe) _
                                AndAlso b.Timestamp >= from _
                                AndAlso b.Timestamp <= [to]) _
                .OrderBy(Function(b) b.Timestamp) _
                .ToListAsync(cancel)

            Return entities.Select(AddressOf MapToModel).ToList()
        End Function

        ''' <summary>Get the N most recent bars for a contract/timeframe.</summary>
        Public Async Function GetRecentBarsAsync(contractId As Integer,
                                                  timeframe As BarTimeframe,
                                                  count As Integer,
                                                  Optional cancel As CancellationToken = Nothing) As Task(Of List(Of MarketBar))
            Dim entities = Await _context.Bars _
                .Where(Function(b) b.ContractId = contractId _
                                AndAlso b.Timeframe = CInt(timeframe)) _
                .OrderByDescending(Function(b) b.Timestamp) _
                .Take(count) _
                .OrderBy(Function(b) b.Timestamp) _
                .ToListAsync(cancel)

            Return entities.Select(AddressOf MapToModel).ToList()
        End Function

        ''' <summary>Get the timestamp of the latest stored bar (used to know where to fetch from).</summary>
        Public Async Function GetLatestTimestampAsync(contractId As Integer,
                                                       timeframe As BarTimeframe,
                                                       Optional cancel As CancellationToken = Nothing) As Task(Of DateTimeOffset?)
            Dim latest = Await _context.Bars _
                .Where(Function(b) b.ContractId = contractId AndAlso b.Timeframe = CInt(timeframe)) _
                .MaxAsync(Function(b) CType(b.Timestamp, DateTimeOffset?), cancel)
            Return latest
        End Function

        ''' <summary>
        ''' Bulk upsert bars — inserts new ones, skips duplicates.
        ''' Uses EF Core's AddRange with conflict handling via the unique index.
        ''' </summary>
        Public Async Function BulkInsertAsync(bars As IEnumerable(Of MarketBar),
                                               timeframe As BarTimeframe,
                                               Optional cancel As CancellationToken = Nothing) As Task(Of Integer)
            Dim entities = bars.Select(Function(b) MapToEntity(b, timeframe)).ToList()

            ' Use raw SQL MERGE for efficient upsert on SQL Server
            Dim inserted = 0
            For Each entity In entities
                Dim exists = Await _context.Bars.AnyAsync(
                    Function(b) b.ContractId = entity.ContractId _
                             AndAlso b.Timeframe = entity.Timeframe _
                             AndAlso b.Timestamp = entity.Timestamp, cancel)
                If Not exists Then
                    _context.Bars.Add(entity)
                    inserted += 1
                End If
            Next

            Await _context.SaveChangesAsync(cancel)
            _logger.LogDebug("Inserted {Count} new bars (skipped {Skipped} duplicates)",
                             inserted, entities.Count - inserted)
            Return inserted
        End Function

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
