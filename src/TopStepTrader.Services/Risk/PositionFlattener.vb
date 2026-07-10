Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Interfaces

Namespace TopStepTrader.Services.Risk

    ''' <summary>
    ''' FEAT-73 F5: force-flatten sweep. Resolves a scoped <see cref="IOrderService"/>
    ''' via <see cref="IServiceScopeFactory"/> (same pattern the guard uses for
    ''' repository access), closes every open position with a broker-confirmed fill
    ''' (<c>FlattenContractWithFillAsync</c> also cancels both resting bracket legs),
    ''' then sweeps any pre-staged orders (e.g. scalper stop entries) with
    ''' <c>CancelAllOpenOrdersAsync</c>. A failure on one contract never aborts the others.
    ''' </summary>
    Public Class PositionFlattener
        Implements IPositionFlattener

        Private ReadOnly _scopeFactory As IServiceScopeFactory
        Private ReadOnly _logger As ILogger(Of PositionFlattener)

        Public Sub New(scopeFactory As IServiceScopeFactory,
                       logger As ILogger(Of PositionFlattener))
            _scopeFactory = scopeFactory
            _logger = logger
        End Sub

        Public Async Function FlattenAllAsync(accountId As Long) As Task(Of FlattenAllResult) _
            Implements IPositionFlattener.FlattenAllAsync
            Dim result As New FlattenAllResult()
            If accountId <= 0 Then
                _logger?.LogError(
                    "PositionFlattener: no active account (accountId={AccountId}) — cannot flatten", accountId)
                Return result
            End If

            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim orders = scope.ServiceProvider.GetRequiredService(Of IOrderService)()

                    Dim positions = (Await orders.GetOpenPositionsAsync(accountId)).ToList()
                    result.AttemptedContracts = positions.Count
                    _logger?.LogWarning(
                        "PositionFlattener: flattening {Count} open position(s) on account {AccountId}",
                        positions.Count, accountId)

                    For Each pos In positions
                        Try
                            Dim outcome = Await orders.FlattenContractWithFillAsync(accountId, pos.ContractId)
                            If outcome.Success Then
                                result.FlattenedContracts += 1
                                _logger?.LogWarning(
                                    "PositionFlattener: {ContractId} flattened (fill={FillPrice})",
                                    pos.ContractId,
                                    If(outcome.Fill IsNot Nothing, outcome.Fill.FillPrice.ToString("F2"), "unconfirmed"))
                            Else
                                result.FailedContractIds.Add(pos.ContractId)
                                _logger?.LogError(
                                    "PositionFlattener: flatten FAILED for {ContractId} on account {AccountId}",
                                    pos.ContractId, accountId)
                            End If
                        Catch ex As Exception
                            result.FailedContractIds.Add(pos.ContractId)
                            _logger?.LogError(ex,
                                "PositionFlattener: flatten threw for {ContractId} on account {AccountId}",
                                pos.ContractId, accountId)
                        End Try
                    Next

                    Try
                        Await orders.CancelAllOpenOrdersAsync()
                        result.OrdersCancelled = True
                    Catch ex As Exception
                        _logger?.LogError(ex,
                            "PositionFlattener: cancel-all-open-orders sweep failed on account {AccountId}", accountId)
                    End Try
                End Using
            Catch ex As Exception
                _logger?.LogError(ex, "PositionFlattener: sweep failed on account {AccountId}", accountId)
            End Try

            If Not result.Complete Then
                _logger?.LogError(
                    "PositionFlattener: INCOMPLETE flatten on account {AccountId} — attempted={Attempted}, flattened={Flattened}, failed=[{Failed}], ordersCancelled={OrdersCancelled}. Manual check required.",
                    accountId, result.AttemptedContracts, result.FlattenedContracts,
                    String.Join(",", result.FailedContractIds), result.OrdersCancelled)
            End If
            Return result
        End Function

    End Class

End Namespace
