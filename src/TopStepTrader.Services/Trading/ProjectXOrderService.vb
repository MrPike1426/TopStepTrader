Imports System.IO
Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.API.Adapters
Imports TopStepTrader.API.Hubs
Imports TopStepTrader.API.Http.ProjectX
Imports TopStepTrader.API.Models.Requests
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Data.Repositories

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' Implements IOrderService for TopStepX / ProjectX.
    ''' Uses PXOrderClient for all order, position, and trade operations.
    '''
    ''' Bracket strategy:
    '''   • PlaceOrderAsync       → submits both stopLossBracket (type=4) and takeProfitBracket (type=1).
    '''                             Platform-level OCO links the pair: whichever fills first auto-cancels the other.
    '''   • EditPositionSlTpAsync → modifies the resting SL bracket order (type=4) in-place (stop price).
    '''   • FlattenContractAsync  → cancels ALL bracket orders (type=4 SL and type=1 TP) before closing
    '''                             so neither can fire after the position is flat.
    ''' </summary>
    Public Class ProjectXOrderService
        Implements IOrderService

        Private ReadOnly _orderClient As PXOrderClient
        Private ReadOnly _orderRepo As OrderRepository
        Private ReadOnly _accountService As IAccountService
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _catalog As TopStepXInstrumentCatalog
        Private ReadOnly _logger As ILogger(Of ProjectXOrderService)
        Private ReadOnly _hubClient As UserHubClient

        Public Event OrderFilled As EventHandler(Of OrderFilledEventArgs) Implements IOrderService.OrderFilled
        Public Event OrderRejected As EventHandler(Of OrderRejectedEventArgs) Implements IOrderService.OrderRejected
        Public Event PositionUpdated As EventHandler(Of Core.Events.PositionUpdateEventArgs) Implements IOrderService.PositionUpdated

        Public Sub New(orderClient As PXOrderClient,
                       orderRepo As OrderRepository,
                       accountService As IAccountService,
                       session As ITradingSessionContext,
                       catalog As TopStepXInstrumentCatalog,
                       logger As ILogger(Of ProjectXOrderService),
                       hubClient As UserHubClient)
            _orderClient = orderClient
            _orderRepo = orderRepo
            _accountService = accountService
            _session = session
            _catalog = catalog
            _logger = logger
            _hubClient = hubClient
            ' Bridge the SignalR real-time position stream into the IOrderService.PositionUpdated event.
            ' The REST searchOpen endpoint returns openPnl=0; the hub push carries the live value.
            AddHandler _hubClient.PositionUpdated, AddressOf OnHubPositionUpdated
        End Sub

        ''' <summary>
        ''' Translates a SignalR GatewayUserPosition push into the IOrderService.PositionUpdated event
        ''' so engine and UI consumers receive live P&amp;L without polling the REST endpoint.
        ''' </summary>
        Private Sub OnHubPositionUpdated(sender As Object, e As PXPositionUpdateEventArgs)
            Dim data = e?.PositionData
            If data Is Nothing OrElse String.IsNullOrEmpty(data.ContractId) Then Return
            RaiseEvent PositionUpdated(Me, New Core.Events.PositionUpdateEventArgs(
                data.ContractId, data.NetPos, CDec(data.NetPrice), CDec(data.OpenPnL)))
        End Sub

        Public Async Function PlaceOrderAsync(order As Order) As Task(Of Order) _
            Implements IOrderService.PlaceOrderAsync

            order.PlacedAt = DateTimeOffset.UtcNow
            order.Status = OrderStatus.Pending
            order.Id = Await _orderRepo.SaveOrderAsync(order)

            ' Prefer the account ID already on the order (set by the calling ViewModel).
            ' Fall back to session/API lookup only when the caller did not supply one.
            Dim accountId As Long = If(order.AccountId > 0, order.AccountId, Await GetActiveAccountIdAsync())
            _logger.LogInformation("TopStepX PlaceOrder: using accountId={AccId} (order.AccountId={OrderAccId})",
                                   accountId, order.AccountId)

            ' Default to 1 contract when Quantity is not explicitly set (test phase)
            Dim contractSize = If(order.Quantity > 0, order.Quantity, 1)

            ' Resolve to active front-month contract ID (handles roll-overs and symbol→PX translation)
            Dim resolvedContractId = Await ResolveToActivePxContractIdAsync(order.ContractId)
            If Not String.Equals(resolvedContractId, order.ContractId, StringComparison.OrdinalIgnoreCase) Then
                _logger.LogInformation(
                    "TopStepX PlaceOrder: resolved contract {Old} → {New}", order.ContractId, resolvedContractId)
            End If

            ' TopStepX bracket tick sign convention (UAT-confirmed):
            '   Ticks are SIGNED relative to entry price direction.
            '   Long  (Buy):  SL below entry → negative ticks; TP above entry → positive ticks.
            '   Short (Sell): SL above entry → positive ticks; TP below entry → negative ticks.
            Dim isBuy = order.Side = OrderSide.Buy

            ' ── Tick SL bracket ─────────────────────────────────────────────────────
            Dim slBracket As PXBracketOrder = Nothing
            If order.InitialStopTicks.HasValue AndAlso order.InitialStopTicks.Value > 0 Then
                Dim validatedTicks = Await _catalog.ClampStopTicksAsync(resolvedContractId, order.InitialStopTicks.Value)
                ' Long SL is below entry → negative; short SL is above entry → positive
                Dim signedSlTicks = If(isBuy, -validatedTicks, validatedTicks)
                slBracket = New PXBracketOrder With {
                    .Ticks = signedSlTicks,
                    .OrderType = 4   ' Stop Market — only type supported by ProjectX
                }
                _logger.LogInformation(
                    "TopStepX SL bracket: {Contract} requested={Req} validated={Val} signed={Signed} ticks (isBuy={Buy})",
                    resolvedContractId, order.InitialStopTicks.Value, validatedTicks, signedSlTicks, isBuy)
            End If

            ' ── Tick TP bracket ─────────────────────────────────────────────────────
            Dim tpBracket As PXBracketOrder = Nothing
            If order.InitialTakeProfitTicks.HasValue AndAlso order.InitialTakeProfitTicks.Value > 0 Then
                Dim validatedTpTicks = Await _catalog.ClampStopTicksAsync(resolvedContractId, order.InitialTakeProfitTicks.Value)
                ' Long TP is above entry → positive; short TP is below entry → negative
                Dim signedTpTicks = If(isBuy, validatedTpTicks, -validatedTpTicks)
                tpBracket = New PXBracketOrder With {
                    .Ticks = signedTpTicks,
                    .OrderType = 1   ' Limit take-profit
                }
                _logger.LogInformation(
                    "TopStepX TP bracket: {Contract} requested={Req} validated={Val} signed={Signed} ticks (isBuy={Buy})",
                    resolvedContractId, order.InitialTakeProfitTicks.Value, validatedTpTicks, signedTpTicks, isBuy)
            End If

            Dim req = New PXPlaceOrderRequest With {
                .AccountId = accountId,
                .ContractId = resolvedContractId,
                .OrderType = MapOrderType(order.OrderType),
                .Side = If(order.Side = OrderSide.Buy, 0, 1),
                .Size = contractSize,
                .LimitPrice = If(order.LimitPrice.HasValue, CDbl(order.LimitPrice.Value), CType(Nothing, Double?)),
                .StopPrice = If(order.StopPrice.HasValue, CDbl(order.StopPrice.Value), CType(Nothing, Double?)),
                .StopLossBracket = slBracket,
                .TakeProfitBracket = tpBracket
            }

            Dim slDesc = If(slBracket IsNot Nothing, $"{slBracket.Ticks} ticks", "none")
            Dim tpDesc = If(tpBracket IsNot Nothing, $"{tpBracket.Ticks} ticks", "none")
            _logger.LogInformation(
                "Placing TopStepX {Side} x{Qty} {Contract} (accountId={AccId}) SL={SL} TP={TP}",
                order.Side, contractSize, resolvedContractId, accountId, slDesc, tpDesc)

            Dim caughtEx As Exception = Nothing
            Try
                Dim resp = Await _orderClient.PlaceOrderAsync(req)

                If resp.Success Then
                    order.ExternalOrderId = resp.OrderId
                    order.Status = OrderStatus.Working
                    Await _orderRepo.UpdateOrderStatusAsync(order.Id, order.Status, order.ExternalOrderId)
                    _logger.LogInformation("TopStepX order accepted. orderId={Id}", resp.OrderId)
                    If OperatingSystem.IsWindows() Then System.Console.Beep(880, 200)

                    ' Naked-entry guard: if we submitted an SL bracket, schedule a background check
                    ' to confirm it was accepted by the broker. TopStepX can silently drop bracket legs
                    ' that violate exchange risk rules (e.g. excessive tick distance), leaving the entry
                    ' position completely unmanaged. Clamping in ClampStopTicksAsync is the primary
                    ' defence; this is a secondary safety net for any rejection path we missed.
                    If slBracket IsNot Nothing Then
                        Dim verifyContractId = resolvedContractId
                        Dim verifyAccountId = accountId
                        Task.Run(Async Function()
                            Try
                                Await Task.Delay(TimeSpan.FromSeconds(7))
                                Dim ordersResp = Await _orderClient.SearchOpenOrdersAsync(verifyAccountId)
                                Dim hasSl = ordersResp?.Orders IsNot Nothing AndAlso
                                            ordersResp.Orders.Any(
                                                Function(o) String.Equals(o.ContractId, verifyContractId,
                                                                           StringComparison.OrdinalIgnoreCase) AndAlso
                                                            o.OrderType = 4)
                                If Not hasSl Then
                                    _logger.LogError(
                                        "🚨 NAKED ENTRY: no SL bracket found for {Contract} 7s after fill — " &
                                        "flattening position to prevent unmanaged risk.",
                                        verifyContractId)
                                    Await _orderClient.CloseContractAsync(verifyAccountId, verifyContractId)
                                End If
                            Catch ex As Exception
                                _logger.LogWarning(ex,
                                    "TopStepX naked-entry guard check failed for {Contract}", verifyContractId)
                            End Try
                        End Function)
                    End If
                Else
                    order.Status = OrderStatus.Rejected
                    Await _orderRepo.UpdateOrderStatusAsync(order.Id, order.Status)
                    Dim rejectMsg = If(Not String.IsNullOrEmpty(resp.ErrorMessage),
                                      resp.ErrorMessage,
                                      $"errorCode={resp.ErrorCode}")
                    _logger.LogWarning("TopStepX order rejected: {Msg}", rejectMsg)
                    RaiseEvent OrderRejected(Me, New OrderRejectedEventArgs(order, rejectMsg))
                End If
            Catch ex As Exception
                caughtEx = ex
            End Try

            If caughtEx IsNot Nothing Then
                order.Status = OrderStatus.Rejected
                Await _orderRepo.UpdateOrderStatusAsync(order.Id, order.Status)
                _logger.LogError(caughtEx, "Exception placing TopStepX order")
                RaiseEvent OrderRejected(Me, New OrderRejectedEventArgs(order, caughtEx.Message))
            End If

            Return order
        End Function

        Public Async Function CancelOrderAsync(orderId As Long) As Task(Of Boolean) _
            Implements IOrderService.CancelOrderAsync

            Dim accountId = Await GetActiveAccountIdAsync()
            Dim caughtEx As Exception = Nothing
            Dim success = False
            Try
                ' Try to close as open position first, then cancel pending order
                Dim posResp = Await _orderClient.SearchOpenPositionsAsync(accountId)
                Dim pos = posResp?.Positions?.FirstOrDefault(Function(p) p.Id = orderId)

                If pos IsNot Nothing Then
                    Dim closeResp = Await _orderClient.CloseContractAsync(accountId, pos.ContractId)
                    success = closeResp.Success
                Else
                    Dim cancelResp = Await _orderClient.CancelOrderAsync(accountId, orderId)
                    success = cancelResp.Success
                End If
            Catch ex As Exception
                caughtEx = ex
            End Try

            If caughtEx IsNot Nothing Then
                _logger.LogWarning(caughtEx, "TopStepX cancel/close failed for orderId={Id}", orderId)
                Return False
            End If

            If success Then
                Dim dbOrder = (Await _orderRepo.GetOpenOrdersAsync()).
                    FirstOrDefault(Function(o) o.ExternalOrderId = orderId)
                If dbOrder IsNot Nothing Then
                    Await _orderRepo.UpdateOrderStatusAsync(dbOrder.Id, OrderStatus.Cancelled)
                End If
            End If
            Return success
        End Function

        Public Async Function CancelAllOpenOrdersAsync() As Task _
            Implements IOrderService.CancelAllOpenOrdersAsync

            Dim accountId = Await GetActiveAccountIdAsync()
            _logger.LogWarning("TopStepX FlattenAll: closing all open positions for accountId={Id}", accountId)

            Dim posResp = Await _orderClient.SearchOpenPositionsAsync(accountId)
            If posResp?.Positions Is Nothing Then Return

            Dim tasks = posResp.Positions.
                Select(Function(p) _orderClient.CloseContractAsync(accountId, p.ContractId))
            Await Task.WhenAll(tasks)
        End Function

        Public Async Function GetOpenOrdersAsync(accountId As Long) As Task(Of IEnumerable(Of Order)) _
            Implements IOrderService.GetOpenOrdersAsync
            Return Await _orderRepo.GetOpenOrdersAsync(accountId)
        End Function

        Public Async Function GetOrderHistoryAsync(accountId As Long, from As DateTime, [to] As DateTime) _
            As Task(Of IEnumerable(Of Order)) Implements IOrderService.GetOrderHistoryAsync
            Return Await _orderRepo.GetOrderHistoryAsync(accountId, from, [to])
        End Function

        Public Async Function TryGetOrderFillPriceAsync(externalOrderId As Long, accountId As Long,
                                                         Optional cancel As CancellationToken = Nothing) _
            As Task(Of Decimal?) Implements IOrderService.TryGetOrderFillPriceAsync
            Try
                ' Search recent orders (last 10 minutes) to find this order's avgFillPrice.
                Dim fromTs = DateTimeOffset.UtcNow.AddMinutes(-10)
                Dim resp = Await _orderClient.SearchOrdersAsync(
                    accountId,
                    startTimestamp:=fromTs.ToUnixTimeMilliseconds(),
                    cancel:=cancel)
                If resp?.Orders Is Nothing Then Return Nothing
                Dim match = resp.Orders.FirstOrDefault(Function(o) o.Id = externalOrderId)
                If match Is Nothing Then Return Nothing
                If match.AvgFillPrice.HasValue AndAlso match.AvgFillPrice.Value > 0 Then
                    Return CDec(match.AvgFillPrice.Value)
                End If
                Return Nothing
            Catch ex As Exception
                _logger.LogWarning(ex, "TryGetOrderFillPriceAsync failed for orderId={Id}", externalOrderId)
                Return Nothing
            End Try
        End Function

        ''' <summary>
        ''' Returns the stop price from the companion Stop Market bracket order placed at entry.
        ''' Searches open orders for a type=4 (Stop Market) order on the same contract.
        ''' Returns Nothing when no bracket is found.
        ''' </summary>
        Public Async Function TryGetBracketStopPriceAsync(accountId As Long, contractId As String,
                                                           Optional cancel As CancellationToken = Nothing) As Task(Of Decimal?) Implements IOrderService.TryGetBracketStopPriceAsync
            Try
                Dim resolvedId = Await ResolveToActivePxContractIdAsync(contractId, cancel)
                Dim resp = Await _orderClient.SearchOpenOrdersAsync(accountId, cancel)
                If resp?.Orders Is Nothing Then Return Nothing
                Dim stopOrder = resp.Orders.FirstOrDefault(
                    Function(o) String.Equals(o.ContractId, resolvedId, StringComparison.OrdinalIgnoreCase) AndAlso
                                o.OrderType = 4 AndAlso   ' Stop Market
                                o.StopPrice.HasValue AndAlso o.StopPrice.Value > 0)
                If stopOrder IsNot Nothing Then Return CDec(stopOrder.StopPrice.Value)
                ' Root-symbol fallback for contract rolls
                Dim fav = FavouriteContracts.TryGetBySymbolResolved(contractId)
                If fav IsNot Nothing AndAlso Not String.IsNullOrEmpty(fav.PxRootSymbol) Then
                    Dim rootPrefix = $"CON.F.US.{fav.PxRootSymbol}."
                    stopOrder = resp.Orders.FirstOrDefault(
                        Function(o) o.ContractId.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) AndAlso
                                    o.OrderType = 4 AndAlso
                                    o.StopPrice.HasValue AndAlso o.StopPrice.Value > 0)
                    If stopOrder IsNot Nothing Then Return CDec(stopOrder.StopPrice.Value)
                End If
                Return Nothing
            Catch ex As Exception
                _logger.LogWarning(ex, "TryGetBracketStopPriceAsync failed for {Contract}", contractId)
                Return Nothing
            End Try
        End Function

        Public Async Function GetLiveWorkingOrdersAsync(accountId As Long, contractId As String,
                                                         Optional cancel As CancellationToken = Nothing) _
            As Task(Of IEnumerable(Of Order)) Implements IOrderService.GetLiveWorkingOrdersAsync
            Try
                Dim resolvedId = Await ResolveToActivePxContractIdAsync(contractId, cancel)
                Dim resp = Await _orderClient.SearchOpenOrdersAsync(accountId, cancel)
                If resp?.Success <> True OrElse resp.Orders Is Nothing Then
                    Return Enumerable.Empty(Of Order)()
                End If
                Return resp.Orders.
                    Where(Function(o) String.Equals(o.ContractId, resolvedId, StringComparison.OrdinalIgnoreCase)).
                    Select(Function(o) BrokerModelAdapter.FromPX(o)).
                    ToList()
            Catch ex As Exception
                _logger.LogWarning(ex, "GetLiveWorkingOrders failed for {Contract}", contractId)
                Return Enumerable.Empty(Of Order)()
            End Try
        End Function

        Public Async Function GetLivePositionSnapshotAsync(accountId As Long, contractId As String,
                                                            Optional positionId As Long? = Nothing,
                                                            Optional cancel As CancellationToken = Nothing) _
            As Task(Of LivePositionSnapshot) Implements IOrderService.GetLivePositionSnapshotAsync
            Try
                Dim resolvedId = Await ResolveToActivePxContractIdAsync(contractId, cancel)
                Dim resp = Await _orderClient.SearchOpenPositionsAsync(accountId, cancel)
                If resp?.Positions Is Nothing Then Return Nothing

                ' When a specific positionId is supplied, search across ALL positions by ID —
                ' this bypasses contract-ID resolution and prevents false-Nothing returns that
                ' occur during quarterly contract rolls (e.g. MGC M26 vs Q26 mismatch).
                Dim allMatches = If(positionId.HasValue,
                    resp.Positions.Where(Function(p) p.Id = positionId.Value).ToList(),
                    resp.Positions.Where(Function(p) String.Equals(p.ContractId, resolvedId, StringComparison.OrdinalIgnoreCase)).ToList())

                ' Root-symbol fallback: when the exact contractId match fails (typically due to a
                ' quarterly roll where the static/cached ID is stale — e.g. M26 expired but the live
                ' position is on Q26), retry using the CME root prefix "CON.F.US.MGC." so we match
                ' ANY active expiry without requiring a code change on each roll.
                ' Uses the same StartsWith pattern as FavouriteContracts.TryGetBySymbol.
                If allMatches.Count = 0 AndAlso Not positionId.HasValue Then
                    Dim favFb = FavouriteContracts.TryGetBySymbolResolved(contractId)
                    If favFb IsNot Nothing AndAlso Not String.IsNullOrEmpty(favFb.PxRootSymbol) Then
                        Dim rootPrefix = $"CON.F.US.{favFb.PxRootSymbol}."
                        allMatches = resp.Positions.Where(
                            Function(p) p.ContractId.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)).ToList()
                        If allMatches.Count > 0 Then
                            _logger.LogInformation(
                                "GetLivePositionSnapshot: exact match for '{Resolved}' failed; root-symbol fallback matched {Count} position(s) for prefix '{Prefix}' — contract roll detected",
                                resolvedId, allMatches.Count, rootPrefix)
                        End If
                    End If
                End If
                If allMatches.Count = 0 Then Return Nothing

                ' Exclude working bracket orders (netPos=0 = resting/unfilled SL or TP bracket).
                ' TopStepX /api/Position/searchOpen returns SL and TP bracket orders alongside
                ' the actual position with the same contractId but netPos=0 and netPrice=0.
                ' Without this filter the weighted-average entry price collapses to 0
                ' producing OpenRate=0 → "fill price unavailable" in CreateBracket / Nudge.
                Dim matches = allMatches.Where(Function(p) Math.Abs(p.NetPos) > 0).ToList()
                If matches.Count = 0 Then Return Nothing

                Dim rep = matches.First()
                Dim totalUnits As Decimal = matches.Sum(Function(p) CDec(Math.Abs(p.NetPos)))
                Dim weightedAvg As Decimal = If(totalUnits > 0,
                    CDec(matches.Sum(Function(p) p.NetPrice * Math.Abs(p.NetPos))) / totalUnits,
                    CDec(rep.NetPrice))

                ' ProjectX standard sign convention:
                '   netPos > 0  →  long (bought) position   (e.g. BUY 3 contracts → netPos = +3)
                '   netPos < 0  →  short (sold) position
                ' UAT-confirmed 2026-03-30: manual BUY 3× BTC returned netPos = +3 → isBuy = True.
                Dim isBuyPosition As Boolean = rep.NetPos > 0
                _logger.LogInformation("GetLivePositionSnapshot: contractId={Id} netPos={Pos} → IsBuy={Buy} (totalUnits={Units})",
                                       resolvedId, rep.NetPos, isBuyPosition, totalUnits)

                Dim rawPnl = CDec(matches.Sum(Function(p) p.OpenPnL))
                _logger.LogInformation(
                    "GetLivePositionSnapshot: netPos={Pos} openPnl={Pnl} netPrice={Price} totalUnits={Units}",
                    rep.NetPos, rawPnl, rep.NetPrice, totalUnits)

                Return New LivePositionSnapshot With {
                    .PositionId = rep.Id,
                    .UnrealizedPnlUsd = rawPnl,
                    .OpenedAtUtc = DateTimeOffset.UtcNow,
                    .IsBuy = isBuyPosition,
                    .OpenRate = weightedAvg,
                    .Amount = totalUnits,    ' contract count — used by tile display for futures
                    .Units = totalUnits,
                    .PositionCount = matches.Count
                }
            Catch ex As Exception
                _logger.LogWarning(ex, "GetLivePositionSnapshot failed for {Contract}", contractId)
                Return Nothing
            End Try
        End Function

        ''' <summary>
        ''' Synthetic OCO flatten: cancels any working stop orders for this contract
        ''' (the resting SL bracket) before sending the position close.
        ''' This prevents the SL from triggering after the position is already closed.
        ''' </summary>
        Public Async Function FlattenContractAsync(accountId As Long, contractId As String,
                                                    Optional cancel As CancellationToken = Nothing) _
            As Task(Of Boolean) Implements IOrderService.FlattenContractAsync
            Try
                Dim resolvedId = Await ResolveToActivePxContractIdAsync(contractId, cancel)
                FlattenDiag($"BEGIN flatten: input='{contractId}' resolved='{resolvedId}' account={accountId}")

                ' ── Cancel both bracket legs before closing ──────────────────────────
                ' With platform-level OCO enabled, both a Stop (type=4) SL and a Limit (type=1) TP
                ' are resting as open orders.  We must cancel BOTH before CloseContract so that:
                '   • The SL cannot fire as a second close after our market close fills.
                '   • The TP Limit cannot open a new position in the opposite direction after the
                '     position is flat (a resting Limit on a flat account would do exactly that).
                ' Without OCO (legacy behaviour) only the SL existed; this code is harmless —
                ' attempting to cancel an already-filled or non-existent order is a no-op.
                Dim openOrdersResp = Await _orderClient.SearchOpenOrdersAsync(accountId, cancel)
                If openOrdersResp?.Orders IsNot Nothing Then
                    Dim allForAccount = openOrdersResp.Orders.Count
                    Dim bracketOrders = openOrdersResp.Orders.Where(
                        Function(o) String.Equals(o.ContractId, resolvedId, StringComparison.OrdinalIgnoreCase) AndAlso
                                    (o.OrderType = 4 OrElse o.OrderType = 1))  ' 4=Stop SL, 1=Limit TP
                    FlattenDiag($"open orders total={allForAccount} matching resolvedId={bracketOrders.Count()}")
                    For Each bracketOrder In bracketOrders
                        Dim typeLabel = If(bracketOrder.OrderType = 4, "SL-Stop", "TP-Limit")
                        Dim cancelResp = Await _orderClient.CancelOrderAsync(accountId, bracketOrder.Id, cancel)
                        Dim cancelLine = $"cancel {typeLabel} orderId={bracketOrder.Id} ok={cancelResp.Success} code={cancelResp.ErrorCode} msg={cancelResp.ErrorMessage}"
                        FlattenDiag(cancelLine)
                        _logger.LogInformation(
                            "TopStepX OCO: cancelled {Type} order {Id} for {Contract} — result={Ok}",
                            typeLabel, bracketOrder.Id, resolvedId, cancelResp.Success)
                    Next
                Else
                    FlattenDiag("open orders response was null or empty")
                End If

                ' ── Close position ──────────────────────────────────────────────────
                Dim resp = Await _orderClient.CloseContractAsync(accountId, resolvedId, cancel)
                Dim closeLine = $"closeContract contract='{resolvedId}' ok={resp.Success} code={resp.ErrorCode} msg={resp.ErrorMessage}"
                FlattenDiag(closeLine)
                _logger.LogInformation(
                    "TopStepX FlattenContract: closed {Contract} — success={Ok}", resolvedId, resp.Success)
                Return resp.Success
            Catch ex As Exception
                FlattenDiag($"EXCEPTION: {ex.GetType().Name}: {ex.Message}")
                _logger.LogWarning(ex, "FlattenContract failed for {Contract}", contractId)
                Return False
            End Try
        End Function

        ''' <summary>
        ''' Writes a timestamped line to DebugLog (UI execution log) and appends it to
        ''' Documents\TopStepTrader_Diagnostics\flatten_debug.txt for offline inspection.
        ''' </summary>
        Private Shared Sub FlattenDiag(message As String)
            Dim ts = DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)
            Dim line = $"[FLATTEN {ts}] {message}"
            Try : Core.Logging.DebugLog.Log(line) : Catch : End Try
            Try
                Dim dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                       "TopStepTrader_Diagnostics")
                Directory.CreateDirectory(dir)
                File.AppendAllText(Path.Combine(dir, "flatten_debug.txt"),
                                   line & Environment.NewLine)
            Catch
            End Try
        End Sub

        ''' <summary>
        ''' Modifies the resting SL and/or TP bracket orders for an open position.
        ''' Either <paramref name="slRate"/> or <paramref name="tpRate"/> (or both) may be supplied;
        ''' Nothing means "leave that leg unchanged".
        ''' SL = type-4 Stop order; TP = type-1 Limit order.
        ''' Open orders are fetched once and both modifications are applied in that single pass.
        ''' </summary>
        Public Async Function EditPositionSlTpAsync(positionId As Long, slRate As Decimal?, tpRate As Decimal?,
                                                    Optional enableTsl As Boolean = False,
                                                    Optional cancel As CancellationToken = Nothing) _
            As Task(Of Boolean) Implements IOrderService.EditPositionSlTpAsync

            If Not slRate.HasValue AndAlso Not tpRate.HasValue Then
                _logger.LogDebug("EditPositionSlTp: neither SL nor TP rate provided — nothing to modify.")
                Return True
            End If

            Try
                Dim accountId = Await GetActiveAccountIdAsync()

                ' Find the position to get contractId and direction
                Dim posResp = Await _orderClient.SearchOpenPositionsAsync(accountId, cancel)
                Dim pos = posResp?.Positions?.FirstOrDefault(Function(p) p.Id = positionId)
                If pos Is Nothing Then
                    _logger.LogWarning("EditPositionSlTp: position {PosId} not found", positionId)
                    Return False
                End If

                ' Fetch open orders once — used for both SL and TP lookups
                Dim ordersResp = Await _orderClient.SearchOpenOrdersAsync(accountId, cancel)
                Dim info = Await _catalog.GetInfoAsync(pos.ContractId, cancel)
                Dim entryPrice = CDec(pos.NetPrice)
                ' ProjectX standard sign convention: netPos > 0 → long; netPos < 0 → short
                Dim isBuy = pos.NetPos > 0
                Dim overallSuccess = True

                ' Diagnostic: log every resting order for this contract so we can confirm
                ' brackets are present and see their exact type/status values.
                If ordersResp?.Orders IsNot Nothing Then
                    Dim contractOrders = ordersResp.Orders.
                        Where(Function(o) String.Equals(o.ContractId, pos.ContractId, StringComparison.OrdinalIgnoreCase)).
                        ToList()
                    _logger.LogDebug("EditPositionSlTp: {Count} open order(s) for {Contract}: {Detail}",
                        contractOrders.Count, pos.ContractId,
                        String.Join(", ", contractOrders.Select(Function(o) $"id={o.Id} type={o.OrderType} stop={o.StopPrice} limit={o.LimitPrice}")))
                End If

                ' ── SL modification (type=4 Stop) — all resting brackets ────────────
                If slRate.HasValue Then
                    Dim slOrders = ordersResp?.Orders?.Where(
                        Function(o) String.Equals(o.ContractId, pos.ContractId, StringComparison.OrdinalIgnoreCase) AndAlso
                                    o.OrderType = 4)?.ToList()

                    If slOrders Is Nothing OrElse slOrders.Count = 0 Then
                        _logger.LogWarning("EditPositionSlTp: no resting SL order (type=4) for {Contract} pos {PosId}",
                                           pos.ContractId, positionId)
                        overallSuccess = False
                    Else
                        Dim newSlTicks = TickMath.StopTicksFromPrice(entryPrice, slRate.Value, info.TickSize)
                        Dim validatedTicks = TickMath.ClampToMinStop(newSlTicks, info.MinStopTicks)
                        If validatedTicks <> newSlTicks Then
                            ' Min-stop clamping was applied: push the stop further away from entry using the
                            ' directional tick helper (long stop moves further below; short stop further above).
                            _logger.LogWarning("EditPositionSlTp: SL clamped {Req}→{Val} ticks for {Contract}",
                                               newSlTicks, validatedTicks, pos.ContractId)
                            slRate = TickMath.PriceFromTicks(entryPrice, validatedTicks, info.TickSize, isBuy, isStop:=True)
                        ElseIf info.TickSize > 0D Then
                            ' No clamping needed: snap the requested price to the nearest tick boundary and
                            ' send it as-is. Do NOT reconstruct via PriceFromTicks — that helper always
                            ' places the price on the unfavourable side of entry (long→below, short→above),
                            ' which is wrong once the ratchet has advanced the stop to breakeven or above.
                            slRate = Math.Round(slRate.Value / info.TickSize, MidpointRounding.AwayFromZero) * info.TickSize
                        End If
                        For Each slOrder In slOrders
                            Dim slMod = New PXModifyOrderRequest With {
                                .AccountId = accountId,
                                .OrderId = slOrder.Id,
                                .OrderType = slOrder.OrderType,   ' Required by TopStepX modify: echo back the order type (4=Stop)
                                .StopPrice = CDbl(slRate.Value)
                            }
                            Dim slResp = Await _orderClient.ModifyOrderAsync(slMod, cancel)
                            _logger.LogInformation("EditPositionSlTp: SL order {Id} → {Price} ({Ticks}t) success={Ok}",
                                                   slOrder.Id, slRate.Value, validatedTicks, slResp.Success)
                            If Not slResp.Success Then
                                ' Specific broker rejection reasons — log clearly so we can diagnose
                                Dim errMsg = If(slResp.ErrorMessage, String.Empty)
                                If errMsg.IndexOf("Too late", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                                   errMsg.IndexOf("price too close", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                                   errMsg.IndexOf("Invalid Price", StringComparison.OrdinalIgnoreCase) >= 0 Then
                                    _logger.LogWarning("EditPositionSlTp: SL modify rejected ({Err}) — price={Price} orderId={Id}. Will retry next tick.",
                                                       errMsg, slRate.Value, slOrder.Id)
                                Else
                                    _logger.LogWarning("EditPositionSlTp: SL modify failed — errorCode={Code} msg={Msg}",
                                                       slResp.ErrorCode, errMsg)
                                End If
                                overallSuccess = False
                            End If
                        Next
                    End If
                End If

                ' ── TP modification (type=1 Limit) — all resting brackets ───────────
                If tpRate.HasValue Then
                    Dim tpOrders = ordersResp?.Orders?.Where(
                        Function(o) String.Equals(o.ContractId, pos.ContractId, StringComparison.OrdinalIgnoreCase) AndAlso
                                    o.OrderType = 1)?.ToList()

                    If tpOrders Is Nothing OrElse tpOrders.Count = 0 Then
                        _logger.LogWarning("EditPositionSlTp: no resting TP order (type=1) for {Contract} pos {PosId}",
                                           pos.ContractId, positionId)
                        ' TP order missing is non-fatal — position still managed by SL trail
                    Else
                        For Each tpOrder In tpOrders
                            Dim tpMod = New PXModifyOrderRequest With {
                                .AccountId = accountId,
                                .OrderId = tpOrder.Id,
                                .OrderType = tpOrder.OrderType,   ' Required by TopStepX modify: echo back the order type (1=Limit)
                                .LimitPrice = CDbl(tpRate.Value)
                            }
                            Dim tpResp = Await _orderClient.ModifyOrderAsync(tpMod, cancel)
                            _logger.LogInformation("EditPositionSlTp: TP order {Id} → {Price} success={Ok}",
                                                   tpOrder.Id, tpRate.Value, tpResp.Success)
                            If Not tpResp.Success Then
                                Dim errMsg = If(tpResp.ErrorMessage, String.Empty)
                                If errMsg.IndexOf("Too late", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                                   errMsg.IndexOf("price too close", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                                   errMsg.IndexOf("Invalid Price", StringComparison.OrdinalIgnoreCase) >= 0 Then
                                    _logger.LogWarning("EditPositionSlTp: TP modify rejected ({Err}) — price={Price} orderId={Id}. Will retry next tick.",
                                                       errMsg, tpRate.Value, tpOrder.Id)
                                Else
                                    _logger.LogWarning("EditPositionSlTp: TP modify failed — errorCode={Code} msg={Msg}",
                                                       tpResp.ErrorCode, errMsg)
                                End If
                            End If
                            ' TP miss is non-fatal for overall success
                        Next
                    End If
                End If

                Return overallSuccess

            Catch ex As Exception
                _logger.LogWarning(ex, "EditPositionSlTp failed for positionId={PosId}", positionId)
                Return False
            End Try
        End Function

        ' ── Helpers ─────────────────────────────────────────────────────────────────

        Private Async Function GetActiveAccountIdAsync() As Task(Of Long)
            ' Use the session account when set (user chose on Dashboard).
            ' Note: TopStepX practice/sim accounts may have CanTrade=False from the API even though
            ' they accept orders — do not gate on CanTrade here; accept any non-zero Id.
            If _session.SelectedAccount IsNot Nothing AndAlso _session.SelectedAccount.Id > 0 Then
                Return _session.SelectedAccount.Id
            End If
            Dim accounts = Await _accountService.GetActiveAccountsAsync()
            ' Prefer a CanTrade account; fall back to any TopStepX account if none flagged.
            Dim acc = accounts?.FirstOrDefault(Function(a) a.CanTrade AndAlso a.Broker = BrokerType.TopStepX)
            If acc Is Nothing Then
                acc = accounts?.FirstOrDefault(Function(a) a.Broker = BrokerType.TopStepX)
            End If
            Return If(acc IsNot Nothing, acc.Id, 0L)
        End Function

        ''' <summary>
        ''' Translates a caller-supplied <paramref name="contractId"/> to the live front-month
        ''' ProjectX contract ID.  Handles two cases:
        ''' <list type="bullet">
        ''' <item>Display symbol (e.g. "GOLD.24-7") — caller used the favourite's symbol name</item>
        ''' <item>Stale PX ID (e.g. "CON.F.US.MGC.J26") — monthly roll-over has expired it</item>
        ''' </list>
        ''' Falls back to the supplied value when no matching favourite is found.
        ''' </summary>
        Private Async Function ResolveToActivePxContractIdAsync(
            contractId As String,
            Optional cancel As CancellationToken = Nothing) As Task(Of String)

            Dim fav = FavouriteContracts.TryGetBySymbolResolved(contractId)
            If fav Is Nothing Then Return contractId
            Dim resolved = Await _catalog.GetResolvedContractIdAsync(fav, cancel)
            Return If(Not String.IsNullOrEmpty(resolved), resolved, contractId)
        End Function

        Private Shared Function MapOrderType(t As OrderType) As Integer
            Select Case t
                Case OrderType.Limit : Return 1
                Case OrderType.Market : Return 2
                Case OrderType.StopOrder, OrderType.StopLimit : Return 4  ' ProjectX only supports Stop Market (type=4)
                Case Else : Return 2
            End Select
        End Function

        ''' <summary>
        ''' Partially closes an open position by the specified number of contracts using the
        ''' ProjectX /api/Position/partialCloseContract endpoint.
        ''' Returns True when the API call succeeds.
        ''' </summary>
        Public Async Function PartialCloseContractAsync(accountId As Long, contractId As String, size As Integer,
                                                         Optional cancel As CancellationToken = Nothing) _
            As Task(Of Boolean) Implements IOrderService.PartialCloseContractAsync
            Try
                Dim resolvedId = Await ResolveToActivePxContractIdAsync(contractId, cancel)
                Dim resp = Await _orderClient.PartialCloseAsync(accountId, resolvedId, size, cancel)
                _logger.LogInformation(
                    "TopStepX PartialClose: {Contract} size={Size} success={Ok} code={Code} msg={Msg}",
                    resolvedId, size, resp.Success, resp.ErrorCode, resp.ErrorMessage)
                Return resp.Success
            Catch ex As Exception
                _logger.LogWarning(ex, "PartialCloseContractAsync failed for {Contract}", contractId)
                Return False
            End Try
        End Function

    End Class

End Namespace
