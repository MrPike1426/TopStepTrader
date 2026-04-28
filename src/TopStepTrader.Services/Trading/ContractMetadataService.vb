Imports Microsoft.Extensions.Logging
Imports TopStepTrader.API.Http.ProjectX
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' TopStepX-only implementation of IContractMetadataService.
    ''' Uses PXContractClient (POST /api/Contract/search and /searchById).
    ''' FavouriteContracts is always consulted first as a fast local cache.
    ''' Legacy ContractClient removed — not registered or injected.
    ''' </summary>
    Public Class ContractMetadataService
        Implements IContractMetadataService

        Private ReadOnly _pxClient As PXContractClient
        Private ReadOnly _logger As ILogger(Of ContractMetadataService)

        Public Sub New(pxClient As PXContractClient,
                       logger As ILogger(Of ContractMetadataService))
            _pxClient = pxClient
            _logger = logger
        End Sub

        Public Async Function ResolveContractIdAsync(symbol As String) As Task(Of String) _
            Implements IContractMetadataService.ResolveContractIdAsync

            ' Fast path: check FavouriteContracts first
            Dim fav = FavouriteContracts.TryGetBySymbol(symbol)
            If fav IsNot Nothing Then
                Return fav.PxContractId
            End If

            ' Slow path: query TopStepX
            Try
                Dim resp = Await _pxClient.SearchContractsAsync(symbol)
                Dim match = resp?.Contracts?.FirstOrDefault(
                    Function(c) String.Equals(c.ContractId, symbol, StringComparison.OrdinalIgnoreCase) OrElse
                                c.Name.IndexOf(symbol, StringComparison.OrdinalIgnoreCase) >= 0)
                Return If(match IsNot Nothing, match.ContractId, Nothing)
            Catch ex As Exception
                _logger.LogWarning(ex, "ResolveContractId failed for {Symbol}", symbol)
                Return Nothing
            End Try
        End Function

        Public Async Function GetContractAsync(contractId As String) As Task(Of Contract) _
            Implements IContractMetadataService.GetContractAsync

            ' Check FavouriteContracts first
            Dim fav = FavouriteContracts.TryGetBySymbol(contractId)
            If fav IsNot Nothing Then
                Return New Contract With {
                    .Id = fav.PxContractId,
                    .FriendlyName = fav.Name,
                    .TickSize = fav.PxTickSize,
                    .TickValue = fav.PxTickValue
                }
            End If

            Try
                Dim resp = Await _pxClient.SearchByIdAsync(contractId)
                Dim dto = resp?.Contracts?.FirstOrDefault()
                If dto Is Nothing Then Return Nothing
                Return New Contract With {
                    .Id = dto.ContractId,
                    .FriendlyName = dto.Name,
                    .TickSize = dto.TickSize,
                    .TickValue = dto.TickValue
                }
            Catch ex As Exception
                _logger.LogWarning(ex, "GetContract failed for {ContractId}", contractId)
                Return Nothing
            End Try
        End Function

        Public Async Function SearchContractsAsync(searchText As String) As Task(Of IEnumerable(Of Contract)) _
            Implements IContractMetadataService.SearchContractsAsync
            Try
                Dim resp = Await _pxClient.SearchContractsAsync(searchText)
                If resp?.Contracts Is Nothing Then Return Enumerable.Empty(Of Contract)()
                Return resp.Contracts.Select(Function(c) New Contract With {
                    .Id = c.ContractId,
                    .FriendlyName = c.Name,
                    .TickSize = c.TickSize,
                    .TickValue = c.TickValue
                })
            Catch ex As Exception
                _logger.LogWarning(ex, "SearchContracts failed for '{Text}'", searchText)
                Return Enumerable.Empty(Of Contract)()
            End Try
        End Function

    End Class

End Namespace
