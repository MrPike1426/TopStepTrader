Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.API.Http.ProjectX
Imports TopStepTrader.API.Models.Responses
Imports TopStepTrader.Core.Models.Debug
Imports TopStepTrader.Data.Debug

Namespace TopStepTrader.Services.Debug

    ''' <summary>
    ''' FEAT-56: reconcile stale "Open" debug trades against TopStepX so the
    ''' Post-Mortem tab is authoritative even when the engine missed an exit
    ''' callback (SL hit / TP hit / manual flatten while app was closed).
    ''' </summary>
    Public Interface IDebugTradeReconciliationService
        Function ReconcileOpenTradesAsync(Optional cancel As CancellationToken = Nothing) As Task(Of Integer)
    End Interface

    Public Class DebugTradeReconciliationService
        Implements IDebugTradeReconciliationService

        Private ReadOnly _db As DebugTradeDbContext
        Private ReadOnly _orderClient As PXOrderClient
        Private ReadOnly _logger As ILogger(Of DebugTradeReconciliationService)

        Public Sub New(db As DebugTradeDbContext, orderClient As PXOrderClient, logger As ILogger(Of DebugTradeReconciliationService))
            _db = db
            _orderClient = orderClient
            _logger = logger
        End Sub

        Public Async Function ReconcileOpenTradesAsync(Optional cancel As CancellationToken = Nothing) As Task(Of Integer) _
            Implements IDebugTradeReconciliationService.ReconcileOpenTradesAsync

            Dim trades = Await _db.GetAllTradesAsync()
            Dim openTrades = trades.Where(Function(t) String.IsNullOrEmpty(t.ClosedAt)).ToList()
            If openTrades.Count = 0 Then Return 0

            ' Group by AccountId to minimise REST round-trips. Trades captured before
            ' AccountId was persisted (AccountId = 0) cannot be reconciled.
            Dim updatedCount As Integer = 0
            Dim byAccount = openTrades.Where(Function(t) t.AccountId <> 0).
                                       GroupBy(Function(t) t.AccountId).
                                       ToList()

            For Each grp In byAccount
                cancel.ThrowIfCancellationRequested()
                Dim accountId = grp.Key
                Dim accountTrades = grp.ToList()

                Dim openPositions As List(Of PXPositionDto) = Nothing
                Dim positionsFailed As Boolean = False
                Try
                    Dim posResp = Await _orderClient.SearchOpenPositionsAsync(accountId, cancel)
                    openPositions = If(posResp?.Positions, New List(Of PXPositionDto)())
                Catch ex As Exception
                    _logger.LogWarning(ex, "DebugReconciliation: SearchOpenPositions failed for account {Acct}", accountId)
                    positionsFailed = True
                End Try

                If positionsFailed Then
                    For Each t In accountTrades
                        Try
                            Await _db.ApplyReconciliationAsync(t.TradeId, "Failed", DateTime.UtcNow)
                        Catch
                        End Try
                    Next
                    Continue For
                End If

                ' Pull historical trades since the earliest entry to find the closing fill.
                Dim earliestEntry = accountTrades.Min(Function(t) ParseUtc(t.EntryTime, DateTime.UtcNow.AddDays(-7)))
                Dim startTs As Long? = New DateTimeOffset(earliestEntry.AddMinutes(-1)).ToUnixTimeMilliseconds()
                Dim historicalTrades As List(Of PXTradeDto) = Nothing
                Try
                    Dim tradeResp = Await _orderClient.SearchTradesAsync(accountId, startTs, Nothing, cancel)
                    historicalTrades = If(tradeResp?.Trades, New List(Of PXTradeDto)())
                Catch ex As Exception
                    _logger.LogWarning(ex, "DebugReconciliation: SearchTrades failed for account {Acct}", accountId)
                    historicalTrades = New List(Of PXTradeDto)()
                End Try

                For Each t In accountTrades
                    cancel.ThrowIfCancellationRequested()
                    Dim perTradeFailed As Boolean = False
                    Try
                        Dim stillOpen = openPositions.Any(
                            Function(p) String.Equals(p.ContractId, t.Instrument, StringComparison.OrdinalIgnoreCase) AndAlso
                                        p.Size > 0)
                        If stillOpen Then
                            Await _db.ApplyReconciliationAsync(t.TradeId, "StillOpen", DateTime.UtcNow)
                            Continue For
                        End If

                        ' Find the closing fill: opposite-side trade on same contract, after entry.
                        Dim entryUtc = ParseUtc(t.EntryTime, DateTime.MinValue)
                        Dim closingSide As Integer = If(String.Equals(t.Direction, "Long", StringComparison.OrdinalIgnoreCase), 1, 0)
                        Dim closingFill = historicalTrades.
                            Where(Function(x) String.Equals(x.ContractId, t.Instrument, StringComparison.OrdinalIgnoreCase) AndAlso
                                              x.Side = closingSide AndAlso
                                              ParseUtc(x.CreationTimestamp, DateTime.MinValue) >= entryUtc).
                            OrderBy(Function(x) ParseUtc(x.CreationTimestamp, DateTime.MaxValue)).
                            FirstOrDefault()

                        If closingFill IsNot Nothing Then
                            Dim closedUtc = ParseUtc(closingFill.CreationTimestamp, DateTime.UtcNow)
                            Dim exitPrice As Decimal = CDec(closingFill.Price)
                            Dim reason = ClassifyExitReason(t, exitPrice)

                            Await _db.ApplyReconciliationAsync(t.TradeId,
                                                               "Reconciled",
                                                               DateTime.UtcNow,
                                                               closedUtc,
                                                               exitPrice,
                                                               reason)

                            ' Synthesise an authoritative "Closed" action from the broker fill.
                            Dim payload = New List(Of DebugTradeAction) From {
                                New DebugTradeAction With {
                                    .TradeId = t.TradeId,
                                    .TimestampUtc = closedUtc.ToString("O"),
                                    .ActionType = "Closed",
                                    .Price = exitPrice,
                                    .Quantity = closingFill.Size,
                                    .OrderId = closingFill.OrderId,
                                    .Reason = reason,
                                    .Source = "Api"
                                }
                            }
                            Try
                                Await _db.WriteBatchAsync(
                                    New List(Of DebugTradeRecord)(),
                                    New List(Of DebugSnapshotRecord)(),
                                    New List(Of (TradeId As String, ClosedUtc As DateTime, RealisedPnl As Nullable(Of Decimal)))(),
                                    Nothing,
                                    payload)
                            Catch
                            End Try
                            updatedCount += 1
                        Else
                            Await _db.ApplyReconciliationAsync(t.TradeId, "Unreconciled", DateTime.UtcNow)
                        End If
                    Catch ex As Exception
                        _logger.LogWarning(ex, "DebugReconciliation: failed for TradeId={TradeId}", t.TradeId)
                        perTradeFailed = True
                    End Try

                    If perTradeFailed Then
                        Try
                            Await _db.ApplyReconciliationAsync(t.TradeId, "Failed", DateTime.UtcNow)
                        Catch
                        End Try
                    End If
                Next
            Next

            Return updatedCount
        End Function

        Private Shared Function ParseUtc(text As String, fallback As DateTime) As DateTime
            If String.IsNullOrEmpty(text) Then Return fallback
            Dim parsed As DateTime
            If DateTime.TryParse(text, Nothing, Globalization.DateTimeStyles.AssumeUniversal Or Globalization.DateTimeStyles.AdjustToUniversal, parsed) Then
                Return parsed
            End If
            Return fallback
        End Function

        Private Shared Function ClassifyExitReason(t As DebugTradeRecord, exitPrice As Decimal) As String
            ' Best-effort classification: compare exit price to the last-known SL.
            ' Without the full action history we cannot tell SL from TP definitively,
            ' but SL proximity is the most actionable signal for the post-mortem UI.
            Try
                If t.InitialSL <> 0D Then
                    Dim sl As Decimal = t.InitialSL
                    Dim tolerance As Decimal = Math.Max(Math.Abs(t.EntryPrice) * 0.0005D, 0.5D)
                    If Math.Abs(exitPrice - sl) <= tolerance Then Return "SL Hit"
                End If
            Catch
            End Try
            Return "Manual / Engine Flatten"
        End Function

    End Class

End Namespace
