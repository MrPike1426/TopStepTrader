Imports TopStepTrader.API.Models.Responses
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.API.Adapters

    ''' <summary>
    ''' Translates broker-specific API response models into the shared Core domain models
    ''' used by services and ViewModels. Keeps all downstream code broker-agnostic.
    ''' </summary>
    Public Module BrokerModelAdapter

        ' ── Account ─────────────────────────────────────────────────────────────────

        ''' <summary>Maps a ProjectX account DTO to the shared Account model.</summary>
        Public Function FromPX(dto As PXAccountDto) As Account
            Return New Account With {
                .Id = dto.Id,
                .Name = dto.Name,
                .Balance = dto.Balance,
                .CanTrade = dto.CanTrade,
                .IsLive = Not dto.Simulated,
                .Broker = BrokerType.TopStepX
            }
        End Function

        ' ── Orders ──────────────────────────────────────────────────────────────────

        ''' <summary>Maps a ProjectX order DTO to the shared Order model.</summary>
        Public Function FromPX(dto As PXOrderDto) As Order
            Return New Order With {
                .Id = dto.Id,
                .AccountId = dto.AccountId,
                .ContractId = dto.ContractId,
                .PlacedAt = DateTimeOffset.FromUnixTimeMilliseconds(dto.CreationTimestamp),
                .OrderType = MapPXOrderType(dto.OrderType),
                .Side = MapPXSide(dto.Side),
                .Quantity = dto.Size,
                .LimitPrice = If(dto.LimitPrice.HasValue, CDec(dto.LimitPrice.Value), CType(Nothing, Decimal?)),
                .StopPrice = If(dto.StopPrice.HasValue, CDec(dto.StopPrice.Value), CType(Nothing, Decimal?)),
                .Status = MapPXOrderStatus(dto.Status),
                .FillPrice = If(dto.AvgFillPrice.HasValue, CDec(dto.AvgFillPrice.Value), CType(Nothing, Decimal?))
            }
        End Function

        ' ── Bars ────────────────────────────────────────────────────────────────────

        ''' <summary>Maps a ProjectX BarDto to the shared MarketBar model.</summary>
        Public Function FromPX(dto As BarDto, contractId As String) As MarketBar
            Return New MarketBar With {
                .ContractId = contractId,
                .Timestamp = ParseBarTimestamp(dto.Timestamp),
                .Open = CDec(dto.Open),
                .High = CDec(dto.High),
                .Low = CDec(dto.Low),
                .Close = CDec(dto.Close),
                .Volume = dto.Volume
            }
        End Function

        ' ── Helpers ─────────────────────────────────────────────────────────────────

        Private Function ParseBarTimestamp(raw As String) As DateTimeOffset
            Dim result As DateTimeOffset
            If DateTimeOffset.TryParse(raw, result) Then Return result
            Return DateTimeOffset.UtcNow
        End Function

        Private Function MapPXOrderType(pxType As Integer) As OrderType
            Select Case pxType
                Case 1 : Return OrderType.Limit
                Case 2 : Return OrderType.Market
                Case 4 : Return OrderType.StopOrder
                Case Else : Return OrderType.Market
            End Select
        End Function

        Private Function MapPXSide(pxSide As Integer) As OrderSide
            Return If(pxSide = 0, OrderSide.Buy, OrderSide.Sell)
        End Function

        Private Function MapPXOrderStatus(pxStatus As Integer) As OrderStatus
            ' ProjectX status codes: 1=Working, 2=Filled, 3=Cancelled, 4=Rejected
            Select Case pxStatus
                Case 1 : Return OrderStatus.Pending
                Case 2 : Return OrderStatus.Filled
                Case 3 : Return OrderStatus.Cancelled
                Case 4 : Return OrderStatus.Rejected
                Case Else : Return OrderStatus.Pending
            End Select
        End Function

    End Module

End Namespace
