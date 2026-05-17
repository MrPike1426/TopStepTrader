Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.API.Models.Responses
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models.Debug
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Data.Debug

Namespace TopStepTrader.Services.Debug

    ''' <summary>
    ''' FEAT-56: reconcile stale "Open" debug trades against TopStepX so the
    ''' Post-Mortem tab is authoritative even when the engine missed an exit
    ''' callback (SL hit / TP hit / manual flatten while app was closed).
    '''
    ''' BUG-83 hardens the original implementation: real exit-reason classification
    ''' via SearchOrdersAsync, explicit AccountId=0 handling, fail-loud EntryTime
    ''' parsing, retry-on-failure for synthetic Closed writes, Local+Api merge,
    ''' resolver-aware contract-ID matching, and Information-level success logging.
    ''' </summary>
    Public Interface IDebugTradeReconciliationService
        Function ReconcileOpenTradesAsync(Optional cancel As CancellationToken = Nothing) As Task(Of Integer)
    End Interface

    Public Class DebugTradeReconciliationService
        Implements IDebugTradeReconciliationService

        Private ReadOnly _db As DebugTradeDbContext
        Private ReadOnly _orderClient As IDebugReconciliationOrderClient
        Private ReadOnly _logger As ILogger(Of DebugTradeReconciliationService)
        Private ReadOnly _contractResolver As IContractResolutionService

        Public Sub New(db As DebugTradeDbContext,
                       orderClient As IDebugReconciliationOrderClient,
                       logger As ILogger(Of DebugTradeReconciliationService),
                       Optional contractResolver As IContractResolutionService = Nothing)
            _db = db
            _orderClient = orderClient
            _logger = logger
            _contractResolver = contractResolver
        End Sub

        Public Async Function ReconcileOpenTradesAsync(Optional cancel As CancellationToken = Nothing) As Task(Of Integer) _
            Implements IDebugTradeReconciliationService.ReconcileOpenTradesAsync

            Dim trades = Await _db.GetAllTradesAsync()
            Dim openTrades = trades.Where(Function(t) String.IsNullOrEmpty(t.ClosedAt)).ToList()
            If openTrades.Count = 0 Then Return 0

            Dim updatedCount As Integer = 0

            ' BUG-83 F2: trades captured before AccountId was persisted (AccountId = 0)
            ' cannot be reconciled. Tag them explicitly so the user sees a status
            ' instead of an indefinite "Open" with no explanation.
            Dim unaccountable = openTrades.Where(Function(t) t.AccountId = 0).ToList()
            For Each t In unaccountable
                Try
                    Await _db.ApplyReconciliationAsync(t.TradeId, "Unreconciled - no AccountId", DateTime.UtcNow)
                    LogResult(t.TradeId, "Unreconciled - no AccountId", Nothing, Nothing, Nothing)
                Catch ex As Exception
                    _logger.LogWarning(ex, "DebugReconciliation: failed to tag unaccountable TradeId={TradeId}", t.TradeId)
                End Try
            Next

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
                            LogResult(t.TradeId, "Failed", Nothing, Nothing, Nothing)
                        Catch
                        End Try
                    Next
                    Continue For
                End If

                ' BUG-83 F3: trades with malformed EntryTime are tagged and skipped *before*
                ' computing the earliestEntry window - otherwise a single bad row would
                ' poison the whole account by collapsing the window to DateTime.MinValue.
                Dim parseableTrades As New List(Of DebugTradeRecord)()
                For Each t In accountTrades
                    Dim parsed = TryParseUtc(t.EntryTime)
                    If parsed.HasValue Then
                        parseableTrades.Add(t)
                    Else
                        Try
                            Await _db.ApplyReconciliationAsync(t.TradeId, "Failed - bad EntryTime", DateTime.UtcNow)
                            _logger.LogWarning("DebugReconciliation: TradeId={Id} has unparseable EntryTime={ET}; skipped",
                                               t.TradeId, t.EntryTime)
                            LogResult(t.TradeId, "Failed - bad EntryTime", Nothing, Nothing, Nothing)
                        Catch ex As Exception
                            _logger.LogWarning(ex, "DebugReconciliation: failed to tag bad-EntryTime TradeId={Id}", t.TradeId)
                        End Try
                    End If
                Next

                If parseableTrades.Count = 0 Then Continue For

                Dim earliestEntry = parseableTrades.Min(Function(t) TryParseUtc(t.EntryTime).Value)
                Dim startTs As Long? = New DateTimeOffset(earliestEntry.AddMinutes(-1)).ToUnixTimeMilliseconds()

                Dim historicalTrades As List(Of PXTradeDto) = Nothing
                Try
                    Dim tradeResp = Await _orderClient.SearchTradesAsync(accountId, startTs, Nothing, cancel)
                    historicalTrades = If(tradeResp?.Trades, New List(Of PXTradeDto)())
                Catch ex As Exception
                    _logger.LogWarning(ex, "DebugReconciliation: SearchTrades failed for account {Acct}", accountId)
                    historicalTrades = New List(Of PXTradeDto)()
                End Try

                ' BUG-83 F1: pull historical orders so we can classify SL/TP by order type
                ' (Stop=4 -> SL Hit, Limit=1 at TP price -> TP Hit) instead of relying on
                ' brittle price-proximity-to-InitialSL.
                Dim historicalOrders As List(Of PXOrderDto) = Nothing
                Try
                    Dim orderResp = Await _orderClient.SearchOrdersAsync(accountId, startTs, Nothing, cancel)
                    historicalOrders = If(orderResp?.Orders, New List(Of PXOrderDto)())
                Catch ex As Exception
                    _logger.LogWarning(ex, "DebugReconciliation: SearchOrders failed for account {Acct}", accountId)
                    historicalOrders = New List(Of PXOrderDto)()
                End Try

                Dim unparseableFillsLogged As Boolean = False

                For Each t In parseableTrades
                    cancel.ThrowIfCancellationRequested()
                    Dim perTradeFailed As Boolean = False
                    Try
                        Dim stillOpen = openPositions.Any(
                            Function(p) ContractIdsMatch(p.ContractId, t.Instrument) AndAlso p.Size > 0)
                        If stillOpen Then
                            Await _db.ApplyReconciliationAsync(t.TradeId, "StillOpen", DateTime.UtcNow)
                            LogResult(t.TradeId, "StillOpen", Nothing, Nothing, Nothing)
                            Continue For
                        End If

                        Dim entryUtc = TryParseUtc(t.EntryTime).Value
                        Dim closingSide As Integer = If(String.Equals(t.Direction, "Long", StringComparison.OrdinalIgnoreCase), 1, 0)

                        Dim candidateFills =
                            historicalTrades.
                                Where(Function(x) ContractIdsMatch(x.ContractId, t.Instrument) AndAlso x.Side = closingSide).
                                Select(Function(x) New With {.Fill = x, .Ts = TryParseUtc(x.CreationTimestamp)}).
                                ToList()

                        If Not unparseableFillsLogged AndAlso candidateFills.Any(Function(c) Not c.Ts.HasValue) Then
                            _logger.LogWarning("DebugReconciliation: account {Acct} returned fills with unparseable timestamps (suppressing further warnings this pass)", accountId)
                            unparseableFillsLogged = True
                        End If

                        Dim closingFill = candidateFills.
                            Where(Function(c) c.Ts.HasValue AndAlso c.Ts.Value >= entryUtc).
                            OrderBy(Function(c) c.Ts.Value).
                            Select(Function(c) c.Fill).
                            FirstOrDefault()

                        If closingFill IsNot Nothing Then
                            Dim closedUtcMaybe = TryParseUtc(closingFill.CreationTimestamp)
                            Dim closedUtc As DateTime
                            If closedUtcMaybe.HasValue Then
                                closedUtc = closedUtcMaybe.Value
                            Else
                                closedUtc = DateTime.UtcNow
                                _logger.LogWarning("DebugReconciliation: TradeId={Id} closing fill has unparseable timestamp={Ts}; using UtcNow",
                                                   t.TradeId, closingFill.CreationTimestamp)
                            End If

                            Dim exitPrice As Decimal = CDec(closingFill.Price)
                            Dim closingOrder = historicalOrders.FirstOrDefault(Function(o) o.Id = closingFill.OrderId)
                            Dim reason = Await ClassifyExitReasonAsync(t, closingFill, closingOrder, cancel)

                            ' BUG-83 F4: write the synthetic Closed action FIRST. If this fails
                            ' ClosedAt is left null so the next pass retries; nothing is silently lost.
                            ' F5: use UpsertClosedActionAsync so a pre-existing local Closed is merged
                            ' into a single Local+Api row.
                            Dim closedActionFailed As Boolean = False
                            Try
                                Await _db.UpsertClosedActionAsync(
                                    t.TradeId,
                                    closingFill.OrderId,
                                    closedUtc,
                                    exitPrice,
                                    closingFill.Size,
                                    reason)
                            Catch ex As Exception
                                _logger.LogWarning(ex, "DebugReconciliation: failed to insert/merge synthetic Closed action for TradeId={TradeId}", t.TradeId)
                                closedActionFailed = True
                            End Try

                            If closedActionFailed Then
                                Try
                                    Await _db.ApplyReconciliationAsync(t.TradeId, "Failed - action insert", DateTime.UtcNow)
                                    LogResult(t.TradeId, "Failed - action insert", Nothing, Nothing, Nothing)
                                Catch
                                End Try
                                Continue For
                            End If

                            Await _db.ApplyReconciliationAsync(t.TradeId,
                                                               "Reconciled",
                                                               DateTime.UtcNow,
                                                               closedUtc,
                                                               exitPrice,
                                                               reason)
                            LogResult(t.TradeId, "Reconciled", closedUtc, exitPrice, reason)
                            updatedCount += 1
                        Else
                            Await _db.ApplyReconciliationAsync(t.TradeId, "Unreconciled", DateTime.UtcNow)
                            LogResult(t.TradeId, "Unreconciled", Nothing, Nothing, Nothing)
                        End If
                    Catch ex As Exception
                        _logger.LogWarning(ex, "DebugReconciliation: failed for TradeId={TradeId}", t.TradeId)
                        perTradeFailed = True
                    End Try

                    If perTradeFailed Then
                        Try
                            Await _db.ApplyReconciliationAsync(t.TradeId, "Failed", DateTime.UtcNow)
                            LogResult(t.TradeId, "Failed", Nothing, Nothing, Nothing)
                        Catch
                        End Try
                    End If
                Next
            Next

            Return updatedCount
        End Function

        ' -- Helpers --------------------------------------------------------------

        ''' <summary>
        ''' BUG-83 F3: parse an ISO timestamp into UTC, returning Nothing on failure
        ''' so callers can react explicitly rather than silently substituting MinValue.
        ''' </summary>
        Friend Shared Function TryParseUtc(text As String) As Nullable(Of DateTime)
            If String.IsNullOrEmpty(text) Then Return Nothing
            Dim parsed As DateTime
            If DateTime.TryParse(text,
                                 Nothing,
                                 Globalization.DateTimeStyles.AssumeUniversal Or
                                 Globalization.DateTimeStyles.AdjustToUniversal,
                                 parsed) Then
                Return parsed
            End If
            Return Nothing
        End Function

        ''' <summary>
        ''' BUG-83 F1: classify the exit primarily by the closing order's type.
        ''' Falls back to price proximity against the *latest* SL from the action log
        ''' (not InitialSL - by the time of close the SL has usually been ratcheted).
        ''' </summary>
        Friend Async Function ClassifyExitReasonAsync(
                t As DebugTradeRecord,
                closingFill As PXTradeDto,
                closingOrder As PXOrderDto,
                cancel As CancellationToken) As Task(Of String)

            If closingOrder IsNot Nothing Then
                ' OrderType 4 = Stop, 5 = TrailingStop -> SL Hit (regardless of price proximity).
                If closingOrder.OrderType = 4 OrElse closingOrder.OrderType = 5 Then Return "SL Hit"
                ' OrderType 1 = Limit -> TP Hit if price matches the bracket TP candidate.
                If closingOrder.OrderType = 1 Then
                    Dim tpCandidate As Decimal = Await GetLatestTpCandidateAsync(t)
                    Dim limitPrice As Decimal = If(closingOrder.LimitPrice.HasValue,
                                                   CDec(closingOrder.LimitPrice.Value),
                                                   CDec(closingFill.Price))
                    Dim tolerance As Decimal = Math.Max(Math.Abs(t.EntryPrice) * 0.0005D, 0.5D)
                    If tpCandidate <> 0D AndAlso Math.Abs(limitPrice - tpCandidate) <= tolerance Then
                        Return "TP Hit"
                    End If
                    Return "Manual / Engine Flatten (limit)"
                End If
                Return "Manual / Engine Flatten"
            End If

            ' Fallback: no parent order found - use SL proximity against latest SL from actions.
            Dim latestSl As Decimal = Await GetLatestStopFromActionsAsync(t)
            If latestSl <> 0D Then
                Dim slTol As Decimal = Math.Max(Math.Abs(t.EntryPrice) * 0.0005D, 0.5D)
                If Math.Abs(CDec(closingFill.Price) - latestSl) <= slTol Then Return "SL Hit"
            End If
            Return "Manual / Engine Flatten"
        End Function

        Private Async Function GetLatestStopFromActionsAsync(t As DebugTradeRecord) As Task(Of Decimal)
            Try
                Dim actions = Await _db.GetActionsAsync(t.TradeId)
                Dim latest = actions.
                    Where(Function(a) (String.Equals(a.ActionType, "StopLossModified", StringComparison.OrdinalIgnoreCase) OrElse
                                       String.Equals(a.ActionType, "StopLossPlaced", StringComparison.OrdinalIgnoreCase)) AndAlso
                                      a.NewValue.HasValue).
                    OrderByDescending(Function(a) a.TimestampUtc).
                    FirstOrDefault()
                If latest IsNot Nothing Then Return latest.NewValue.Value
            Catch
            End Try
            Return t.InitialSL
        End Function

        Private Async Function GetLatestTpCandidateAsync(t As DebugTradeRecord) As Task(Of Decimal)
            Try
                Dim actions = Await _db.GetActionsAsync(t.TradeId)
                Dim latest = actions.
                    Where(Function(a) (String.Equals(a.ActionType, "TakeProfitModified", StringComparison.OrdinalIgnoreCase) OrElse
                                       String.Equals(a.ActionType, "TakeProfitPlaced", StringComparison.OrdinalIgnoreCase)) AndAlso
                                      a.NewValue.HasValue).
                    OrderByDescending(Function(a) a.TimestampUtc).
                    FirstOrDefault()
                If latest IsNot Nothing Then Return latest.NewValue.Value
            Catch
            End Try
            Return t.InitialTP
        End Function

        ''' <summary>
        ''' BUG-83 F6: contract-ID compare that survives quarterly-roll drift between
        ''' the local Instrument string and the API ContractId by resolving both through
        ''' the favourite-contract catalog.
        ''' </summary>
        Private Function ContractIdsMatch(a As String, b As String) As Boolean
            If String.Equals(a, b, StringComparison.OrdinalIgnoreCase) Then Return True
            Try
                Dim fa = FavouriteContracts.TryGetBySymbolResolved(a, _contractResolver)
                Dim fb = FavouriteContracts.TryGetBySymbolResolved(b, _contractResolver)
                If fa IsNot Nothing AndAlso fb IsNot Nothing Then
                    Return String.Equals(fa.PxContractId, fb.PxContractId, StringComparison.OrdinalIgnoreCase)
                End If
            Catch
            End Try
            Return False
        End Function

        ''' <summary>BUG-83 F7: one Information line per trade per pass.</summary>
        Private Sub LogResult(tradeId As String,
                              status As String,
                              closedUtc As Nullable(Of DateTime),
                              exitPrice As Nullable(Of Decimal),
                              exitReason As String)
            _logger.LogInformation(
                "DebugReconciliation: TradeId={TradeId} Status={Status} ClosedUtc={ClosedUtc} ExitPrice={ExitPrice} ExitReason={ExitReason}",
                tradeId,
                status,
                If(closedUtc.HasValue, CObj(closedUtc.Value.ToString("O")), "-"),
                If(exitPrice.HasValue, CObj(exitPrice.Value), "-"),
                If(exitReason, "-"))
        End Sub

    End Class

End Namespace
