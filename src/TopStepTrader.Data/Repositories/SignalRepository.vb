Imports System.Threading
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data.Repositories

    Public Class SignalRepository

        Private ReadOnly _context As AppDbContext
        Private ReadOnly _logger As ILogger(Of SignalRepository)

        Public Sub New(context As AppDbContext, logger As ILogger(Of SignalRepository))
            _context = context
            _logger = logger
        End Sub

        Public Async Function SaveSignalAsync(signal As TradeSignal,
                                              Optional cancel As CancellationToken = Nothing) As Task(Of Long)
            Dim entity = New SignalEntity With {
                .ContractId = signal.ContractId,
                .GeneratedAt = signal.GeneratedAt,
                .SignalType = CByte(signal.SignalType),
                .Confidence = signal.Confidence,
                .ModelVersion = signal.ModelVersion,
                .SuggestedEntry = signal.SuggestedEntryPrice,
                .SuggestedStop = signal.SuggestedStopLoss,
                .SuggestedTarget = signal.SuggestedTakeProfit,
                .ReasoningJson = If(signal.ReasoningTags.Any(),
                                    String.Join(",", signal.ReasoningTags), Nothing)
            }
            _context.Signals.Add(entity)
            Await _context.SaveChangesAsync(cancel)
            Return entity.Id
        End Function

        Public Async Function GetSignalHistoryAsync(contractId As Integer,
                                                     from As DateTime,
                                                     [to] As DateTime,
                                                     Optional cancel As CancellationToken = Nothing) As Task(Of List(Of TradeSignal))
            Dim entities = Await _context.Signals _
                .Where(Function(s) s.ContractId = contractId _
                               AndAlso s.GeneratedAt >= from _
                               AndAlso s.GeneratedAt <= [to]) _
                .OrderByDescending(Function(s) s.GeneratedAt) _
                .ToListAsync(cancel)

            Return entities.Select(AddressOf MapToModel).ToList()
        End Function

        Public Async Function GetRecentSignalsAsync(contractId As Integer,
                                                     count As Integer,
                                                     Optional cancel As CancellationToken = Nothing) As Task(Of List(Of TradeSignal))
            Dim entities = Await _context.Signals _
                .Where(Function(s) s.ContractId = contractId) _
                .OrderByDescending(Function(s) s.GeneratedAt) _
                .Take(count) _
                .ToListAsync(cancel)

            Return entities.Select(AddressOf MapToModel).ToList()
        End Function

        Private Function MapToModel(entity As SignalEntity) As TradeSignal
            Return New TradeSignal With {
                .Id = entity.Id,
                .ContractId = entity.ContractId,
                .GeneratedAt = entity.GeneratedAt,
                .SignalType = CType(entity.SignalType, SignalType),
                .Confidence = entity.Confidence,
                .ModelVersion = entity.ModelVersion,
                .SuggestedEntryPrice = entity.SuggestedEntry,
                .SuggestedStopLoss = entity.SuggestedStop,
                .SuggestedTakeProfit = entity.SuggestedTarget,
                .ReasoningTags = If(String.IsNullOrEmpty(entity.ReasoningJson),
                                    New List(Of String)(),
                                    entity.ReasoningJson.Split(","c).ToList())
            }
        End Function

    End Class

End Namespace
