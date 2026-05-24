Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' ARCH-20: strategy-agnostic exit execution pipeline extracted from
    ''' <c>SuperTrendPlusViewModel.ReleaseSlotAsync</c>. See
    ''' <see cref="IExitExecutionService"/> for the contract.
    '''
    ''' Singleton lifetime: stateless. Singleton is the natural lifetime to match the
    ''' singleton entry- and position-management services so a single DI graph wires
    ''' the same dependencies for every strategy ViewModel.
    ''' </summary>
    Public Class ExitExecutionService
        Implements IExitExecutionService

        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _tradeRecordService As ITradeRecordService
        Private ReadOnly _contractResolver As IContractResolutionService
        Private ReadOnly _logger As ILogger(Of ExitExecutionService)

        Public Sub New(orderService As IOrderService,
                       tradeRecordService As ITradeRecordService,
                       contractResolver As IContractResolutionService,
                       logger As ILogger(Of ExitExecutionService))
            _orderService = orderService
            _tradeRecordService = tradeRecordService
            _contractResolver = contractResolver
            _logger = logger
        End Sub

        Public Async Function CloseAsync(slot As PositionSlot,
                                          exitReason As String,
                                          trigger As String,
                                          timeframeMinutes As Integer,
                                          ct As CancellationToken) _
            As Task(Of ExitExecutionResult) Implements IExitExecutionService.CloseAsync

            ' BUG-90 F5: every release channel funnels through this single chokepoint
            ' and emits exactly one structured log line per release so the next stuck-slot
            ' incident is diagnosable from log scraping alone.
            _logger.LogInformation("ExitExec slot-release " &
                ReleaseLogFormatter.Format(
                    slot.SlotIndex, slot.Instrument, exitReason, trigger,
                    slot.LastSnapshotOkUtc, slot.MissCount, slot.NetPosLastSeen))

            ' Capture instrument identity BEFORE any side-effects so the result can carry
            ' the closing instrument to the VM for downstream cleanup (MarketHub unsub).
            Dim closingInstrument As String = slot.Instrument
            Dim closingSlotIndex As Integer = slot.SlotIndex
            Dim fcContext = FavouriteContracts.TryGetBySymbolResolved(closingInstrument, _contractResolver)
            Dim closingPxContractId As String = If(fcContext IsNot Nothing, fcContext.PxContractId, Nothing)

            ' BUG-82 F3: compute engine-derived exit price from unrealised P&L + tick metadata.
            Dim exitPx As Decimal? = Nothing
            If slot.EntryPrice <> 0D AndAlso fcContext IsNot Nothing AndAlso fcContext.PxTickSize > 0D Then
                Dim ticks = slot.UnrealizedPnl / (fcContext.PxTickValue * slot.Contracts)
                Dim direction = If(slot.Side = "Buy", 1D, -1D)
                exitPx = Math.Round(slot.EntryPrice + direction * ticks * fcContext.PxTickSize, 6)
            End If

            Dim closeTime As DateTimeOffset = DateTimeOffset.UtcNow
            Dim closePx As Decimal = If(exitPx.HasValue, exitPx.Value, 0D)
            Dim closePnl As Decimal = slot.UnrealizedPnl

            ' ── Persist close record before flattening (while slot data is still valid) ──
            If slot.TradeRecordId > 0 Then
                Try
                    Await _tradeRecordService.CloseTradeAsync(slot.TradeRecordId,
                                                              closeTime,
                                                              closePx,
                                                              closePnl,
                                                              exitReason)
                Catch ex As Exception
                    _logger.LogWarning(ex, "ExitExec [Slot {Idx}] failed to close trade record {Id}",
                                       slot.SlotIndex, slot.TradeRecordId)
                End Try
            End If

            ' FEAT-57 / FEAT-58: resolve outcome + lifespan record.
            Dim rMultiple As Decimal? = Nothing
            If slot.TradeOutcomeId > 0 Then
                Try
                    Await _tradeRecordService.ResolveOutcomeAsync(slot.TradeOutcomeId,
                                                                   closeTime,
                                                                   closePx,
                                                                   closePnl,
                                                                   closePnl > 0D,
                                                                   exitReason)
                Catch ex As Exception
                    _logger.LogWarning(ex, "ExitExec [Slot {Idx}] failed to resolve outcome {Id}",
                                       slot.SlotIndex, slot.TradeOutcomeId)
                End Try

                Try
                    Dim lifespan = BuildLifespan(slot, closePnl, timeframeMinutes, closeTime)
                    rMultiple = CDec(lifespan.RMultiple)
                    Await _tradeRecordService.SaveLifespanRecordAsync(slot.TradeOutcomeId, lifespan)
                Catch ex As Exception
                    _logger.LogWarning(ex, "ExitExec [Slot {Idx}] failed to save lifespan record for outcome {Id}",
                                       slot.SlotIndex, slot.TradeOutcomeId)
                End Try
            End If

            ' ── Close the live position on TopStepX ──
            ' Without this, the real position stays open on the exchange while the slot
            ' clears in-memory, causing EvaluateSlotEntriesAsync to re-enter the same
            ' instrument on the next tick (BUG-35).
            If slot.AccountId <> 0 AndAlso Not String.IsNullOrEmpty(closingInstrument) Then
                Try
                    Await _orderService.FlattenContractAsync(slot.AccountId, closingInstrument)
                    _logger.LogInformation("ExitExec [Slot {Idx}] flatten {Contract}: brackets cancelled + position closed",
                                           slot.SlotIndex, closingInstrument)
                Catch ex As Exception
                    _logger.LogWarning(ex, "ExitExec [Slot {Idx}] flatten failed for {Contract} — slot will be cleared anyway",
                                       slot.SlotIndex, closingInstrument)
                End Try
            End If

            Return New ExitExecutionResult With {
                .Released = True,
                .ExitPrice = exitPx,
                .RealizedPnlUsd = closePnl,
                .RMultiple = rMultiple,
                .ClosingInstrument = closingInstrument,
                .ClosingSlotIndex = closingSlotIndex,
                .ClosingPxContractId = closingPxContractId
            }
        End Function

        Private Function BuildLifespan(slot As PositionSlot,
                                        closePnl As Decimal,
                                        timeframeMinutes As Integer,
                                        closeTime As DateTimeOffset) As TradeLifespan
            Dim fcL = FavouriteContracts.TryGetBySymbolResolved(slot.Instrument, _contractResolver)
            Dim tickSize As Decimal = If(fcL IsNot Nothing, fcL.PxTickSize, 0D)
            Dim tickValue As Decimal = If(fcL IsNot Nothing, fcL.PxTickValue, 0D)
            Dim maeAbsUsd As Decimal = Math.Abs(slot.MaxAdverseExcursionUsd)
            Dim mfeUsd As Decimal = slot.MaxFavorableExcursionUsd
            Dim maeTicks As Integer = 0
            Dim mfeTicks As Integer = 0
            If tickValue > 0D AndAlso slot.Contracts > 0 Then
                maeTicks = CInt(Math.Round(maeAbsUsd / (tickValue * slot.Contracts)))
                mfeTicks = CInt(Math.Round(mfeUsd / (tickValue * slot.Contracts)))
            End If

            Dim durationMins As Single = 0F
            If slot.EntryTime <> DateTime.MinValue Then
                durationMins = CSng(Math.Max(0R, (DateTime.UtcNow - slot.EntryTime).TotalMinutes))
            End If
            Dim barsInTrade As Integer = If(timeframeMinutes > 0, CInt(Math.Floor(durationMins / timeframeMinutes)), 0)
            Dim rMultiple As Single = If(slot.InitialRiskDollars > 0D,
                                          CSng(closePnl / slot.InitialRiskDollars), 0F)
            Dim exitSession As String = ResolveSessionWindow(DateTime.UtcNow)
            Dim entrySession As String = If(String.IsNullOrEmpty(slot.EntrySessionWindow),
                                             exitSession, slot.EntrySessionWindow)

            Return New TradeLifespan With {
                .TradeOutcomeId = slot.TradeOutcomeId,
                .MaxAdverseExcursionDollars = maeAbsUsd,
                .MaxFavorableExcursionDollars = mfeUsd,
                .MaxAdverseExcursionTicks = maeTicks,
                .MaxFavorableExcursionTicks = mfeTicks,
                .SlRatchetCount = slot.SlRatchetCount,
                .TpAdvanceCount = 0,
                .FreeRideActivated = (slot.FreeRideActivatedAtMinutes > 0F),
                .FreeRideActivatedAtMinutes = slot.FreeRideActivatedAtMinutes,
                .DurationMinutes = durationMins,
                .BarsInTrade = barsInTrade,
                .EntrySessionWindow = entrySession,
                .ExitSessionWindow = exitSession,
                .CrossedSessionBoundary = Not String.Equals(entrySession, exitSession, StringComparison.OrdinalIgnoreCase),
                .RMultiple = rMultiple
            }
        End Function

        Private Shared Function ResolveSessionWindow(utc As DateTime) As String
            Dim h As Integer = utc.Hour
            Select Case h
                Case 0 To 6   : Return "Asia"
                Case 7 To 11  : Return "London"
                Case 12 To 13 : Return "US-Pre"
                Case 14 To 20 : Return "US-RTH"
                Case Else     : Return "US-Post"
            End Select
        End Function

    End Class

End Namespace
