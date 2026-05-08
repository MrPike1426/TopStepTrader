Imports Microsoft.EntityFrameworkCore
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data.Repositories

    Public Class LiveTradeRecordRepository
        Implements ILiveTradeRecordRepository

        Private ReadOnly _db As TradeHistoryDbContext

        Public Sub New(db As TradeHistoryDbContext)
            _db = db
        End Sub

        Public Async Function AddAsync(entity As LiveTradeRecordEntity) As Task(Of Long) _
            Implements ILiveTradeRecordRepository.AddAsync
            _db.LiveTradeRecords.Add(entity)
            Await _db.SaveChangesAsync()
            Return entity.Id
        End Function

        Public Async Function CloseAsync(id As Long, exitTime As DateTimeOffset,
                                         exitPrice As Decimal, pnl As Decimal,
                                         exitReason As String) As Task _
            Implements ILiveTradeRecordRepository.CloseAsync
            Dim entity = Await _db.LiveTradeRecords.FindAsync(id)
            If entity Is Nothing Then Return
            entity.ExitTime = exitTime
            entity.ExitPrice = exitPrice
            entity.PnL = pnl
            entity.ExitReason = exitReason
            entity.IsOpen = False
            entity.UpdatedAt = DateTimeOffset.UtcNow
            Await _db.SaveChangesAsync()
        End Function

        Public Async Function UpdateEntryPriceAsync(id As Long, entryPrice As Decimal) As Task _
            Implements ILiveTradeRecordRepository.UpdateEntryPriceAsync
            Dim entity = Await _db.LiveTradeRecords.FindAsync(id)
            If entity Is Nothing OrElse entity.EntryPrice = entryPrice Then Return
            entity.EntryPrice = entryPrice
            entity.UpdatedAt = DateTimeOffset.UtcNow
            Await _db.SaveChangesAsync()
        End Function

        Public Async Function ResolveTopStepXTradeIdAsync(id As Long, topStepXTradeId As Long) As Task _
            Implements ILiveTradeRecordRepository.ResolveTopStepXTradeIdAsync
            Dim entity = Await _db.LiveTradeRecords.FindAsync(id)
            If entity Is Nothing Then Return
            entity.TopStepXTradeId = topStepXTradeId
            entity.UpdatedAt = DateTimeOffset.UtcNow
            Await _db.SaveChangesAsync()
        End Function

        Public Async Function GetOpenRecordsAsync() As Task(Of IList(Of LiveTradeRecordEntity)) _
            Implements ILiveTradeRecordRepository.GetOpenRecordsAsync
            Return Await _db.LiveTradeRecords _
                .Where(Function(r) r.IsOpen) _
                .OrderBy(Function(r) r.EntryTime) _
                .ToListAsync()
        End Function

        Public Async Function GetByIdAsync(id As Long) As Task(Of LiveTradeRecordEntity) _
            Implements ILiveTradeRecordRepository.GetByIdAsync
            Return Await _db.LiveTradeRecords.FindAsync(id)
        End Function

        Public Async Function GetRecentAsync(count As Integer,
                                             Optional symbolFilter As String = Nothing,
                                             Optional strategyFilter As String = Nothing,
                                             Optional personaFilter As String = Nothing,
                                             Optional pnlFilter As PnLFilterType = PnLFilterType.All,
                                             Optional closedOnly As Boolean = False) As Task(Of IList(Of LiveTradeRecordEntity)) _
            Implements ILiveTradeRecordRepository.GetRecentAsync
            Dim q = _db.LiveTradeRecords.AsQueryable()

            If Not String.IsNullOrEmpty(symbolFilter) Then
                q = q.Where(Function(r) r.Symbol = symbolFilter)
            End If
            If Not String.IsNullOrEmpty(strategyFilter) Then
                q = q.Where(Function(r) r.StrategyName = strategyFilter)
            End If
            If Not String.IsNullOrEmpty(personaFilter) Then
                q = q.Where(Function(r) r.Persona = personaFilter)
            End If
            If closedOnly Then
                q = q.Where(Function(r) Not r.IsOpen)
            End If
            Select Case pnlFilter
                Case PnLFilterType.Winners
                    q = q.Where(Function(r) r.PnL.HasValue AndAlso r.PnL.Value > 0D)
                Case PnLFilterType.Losers
                    q = q.Where(Function(r) r.PnL.HasValue AndAlso r.PnL.Value < 0D)
            End Select

            Return Await q.OrderByDescending(Function(r) r.EntryTime) _
                          .Take(count) _
                          .ToListAsync()
        End Function

    End Class

End Namespace
