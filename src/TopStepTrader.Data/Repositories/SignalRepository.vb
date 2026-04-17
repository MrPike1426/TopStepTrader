Imports System.Threading
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
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

        ''' <remarks>
        ''' ContractId filtering uses FromSqlInterpolated to bypass the VB.NET / EF Core
        ''' string-comparison incompatibility (UAT-BUG-001): VB.NET compiles String = String
        ''' inside expression trees to String.Compare(), which EF Core SQLite cannot translate.
        ''' DateTime range filtering is chained in LINQ because DateTime comparisons do
        ''' translate correctly in EF Core SQLite.
        ''' </remarks>
        Public Async Function GetSignalHistoryAsync(contractId As String,
                                                     from As DateTime,
                                                     [to] As DateTime,
                                                     Optional cancel As CancellationToken = Nothing) As Task(Of List(Of TradeSignal))
            ' Only filter by ContractId when a non-empty value is supplied.
            Dim query As IQueryable(Of SignalEntity)
            If Not String.IsNullOrWhiteSpace(contractId) Then
                ' FromSqlInterpolated passes ContractId as a SQL parameter, bypassing the
                ' VB.NET expression-tree String.Compare() translation issue.
                query = _context.Signals _
                    .FromSqlInterpolated($"SELECT * FROM Signals WHERE ContractId = {contractId}")
            Else
                query = _context.Signals.AsQueryable()
            End If

            Dim entities = Await query _
                .Where(Function(s) s.GeneratedAt >= from AndAlso s.GeneratedAt <= [to]) _
                .OrderByDescending(Function(s) s.GeneratedAt) _
                .ToListAsync(cancel)

            Return entities.Select(AddressOf MapToModel).ToList()
        End Function

        ''' <remarks>See GetSignalHistoryAsync for the FromSqlInterpolated rationale (UAT-BUG-001).</remarks>
        Public Async Function GetRecentSignalsAsync(contractId As String,
                                                     count As Integer,
                                                     Optional cancel As CancellationToken = Nothing) As Task(Of List(Of TradeSignal))
            Dim entities = Await _context.Signals _
                .FromSqlInterpolated($"SELECT * FROM Signals WHERE ContractId = {contractId}") _
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
