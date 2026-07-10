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
                                         exitReason As String,
                                         Optional closeFillSource As String = Nothing) As Task _
            Implements ILiveTradeRecordRepository.CloseAsync
            Dim entity = Await _db.LiveTradeRecords.FindAsync(id)
            If entity Is Nothing Then Return
            entity.ExitTime = exitTime
            entity.ExitPrice = exitPrice
            entity.PnL = pnl
            entity.ExitReason = exitReason
            entity.IsOpen = False
            ' BUG-100: persist broker-fill provenance only when the caller supplied it.
            ' Pre-BUG-100 callers (RecoverOpenTradesAsync, BUG-94 reconciliation) leave
            ' the column NULL — which is the expected "unknown source" sentinel.
            If Not String.IsNullOrEmpty(closeFillSource) Then
                entity.CloseFillSource = closeFillSource
            End If
            entity.UpdatedAt = DateTimeOffset.UtcNow
            Await _db.SaveChangesAsync()
        End Function

        Public Async Function ReconcileExitAsync(id As Long,
                                                 exitPrice As Decimal,
                                                 pnl As Decimal,
                                                 exitOrderId As Long,
                                                 exitTime As DateTimeOffset?) As Task _
            Implements ILiveTradeRecordRepository.ReconcileExitAsync
            Dim entity = Await _db.LiveTradeRecords.FindAsync(id)
            If entity Is Nothing Then Return
            entity.ExitPrice = exitPrice
            entity.PnL = pnl
            entity.ExitOrderId = exitOrderId
            If exitTime.HasValue Then entity.ExitTime = exitTime.Value
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

        Public Async Function FindByEntryOrderIdAsync(externalOrderId As Long) As Task(Of LiveTradeRecordEntity) _
            Implements ILiveTradeRecordRepository.FindByEntryOrderIdAsync
            If externalOrderId = 0L Then Return Nothing
            Return Await _db.LiveTradeRecords _
                .Where(Function(r) r.EntryOrderId = externalOrderId) _
                .OrderByDescending(Function(r) r.Id) _
                .FirstOrDefaultAsync()
        End Function

        Public Async Function FindOpenByContractIdAsync(contractId As String) As Task(Of LiveTradeRecordEntity) _
            Implements ILiveTradeRecordRepository.FindOpenByContractIdAsync
            If String.IsNullOrEmpty(contractId) Then Return Nothing
            ' BUG-98: literal equality on ContractId. The caller is responsible for normalising
            ' to the persisted shape — strategy-attributed rows store the short root symbol
            ' (e.g. "MES"), but BrokerFill-Unattributed rows store the full PX id (e.g.
            ' "CON.F.US.MES.U26"). TradeReconciliationWorker.ScanOrphansAsync issues two
            ' lookups (resolved root then literal id) to cover both shapes.
            Return Await _db.LiveTradeRecords _
                .Where(Function(r) r.IsOpen AndAlso r.ContractId = contractId) _
                .OrderByDescending(Function(r) r.Id) _
                .FirstOrDefaultAsync()
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

        Public Async Function SumRealisedPnlSinceAsync(sinceUtc As DateTimeOffset) As Task(Of Decimal) _
            Implements ILiveTradeRecordRepository.SumRealisedPnlSinceAsync
            ' EF Core's SQLite provider stores DateTimeOffset as TEXT in a format whose
            ' parameter-binding round-trip does not sort-compare reliably against the
            ' stored representation. Project (Id, PnL, ExitTime) into memory then filter
            ' on the materialised DateTimeOffset values. Set is bounded by one trading
            ' day's closed trades so the in-memory pass is negligible.
            Dim rows = Await _db.LiveTradeRecords _
                .Where(Function(r) Not r.IsOpen AndAlso
                                   r.PnL.HasValue AndAlso
                                   r.ExitTime.HasValue) _
                .Select(Function(r) New With {Key .PnL = r.PnL, Key .ExitTime = r.ExitTime}) _
                .ToListAsync()
            Dim total As Decimal = 0D
            For Each r In rows
                If r.PnL.HasValue AndAlso r.ExitTime.HasValue AndAlso
                   r.ExitTime.Value >= sinceUtc Then
                    total += r.PnL.Value
                End If
            Next
            Return total
        End Function

        Public Async Function GetDailyCloseStatsAsync(sinceUtc As DateTimeOffset) As Task(Of DailyCloseStats) _
            Implements ILiveTradeRecordRepository.GetDailyCloseStatsAsync
            ' Same SQLite DateTimeOffset caveat as SumRealisedPnlSinceAsync: project the
            ' closed rows into memory, then filter/sort on materialised values. The set
            ' is bounded by one trading day's closed trades.
            Dim rows = Await _db.LiveTradeRecords _
                .Where(Function(r) Not r.IsOpen AndAlso
                                   r.PnL.HasValue AndAlso
                                   r.ExitTime.HasValue) _
                .Select(Function(r) New With {
                    Key .PnL = r.PnL,
                    Key .ExitTime = r.ExitTime,
                    Key .Commission = r.CommissionUsd,
                    Key .Fees = r.FeesUsd}) _
                .ToListAsync()

            Dim dayRows = rows _
                .Where(Function(r) r.PnL.HasValue AndAlso r.ExitTime.HasValue AndAlso
                                   r.ExitTime.Value >= sinceUtc) _
                .OrderBy(Function(r) r.ExitTime.Value) _
                .ToList()

            Dim stats As New DailyCloseStats With {.TradeCount = dayRows.Count}
            For Each r In dayRows
                stats.GrossPnl += r.PnL.Value
                stats.NetPnlAfterFees += r.PnL.Value - r.Commission - r.Fees
            Next
            ' Consecutive losers: newest backwards, counting PnL <= 0 until the first winner.
            For i = dayRows.Count - 1 To 0 Step -1
                If dayRows(i).PnL.Value <= 0D Then
                    stats.ConsecutiveLosers += 1
                Else
                    Exit For
                End If
            Next
            Return stats
        End Function

    End Class

End Namespace
