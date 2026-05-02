Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.Core.Models


    ''' <summary>
    ''' Plain state object representing a single concurrent position slot.
    ''' Replaces the PersonaBoxVm identity concept — slots are index-based,
    ''' not person-based.
    ''' </summary>
    Public Class PositionSlot

        Public Property SlotIndex As Integer

        Public Property Instrument As String = String.Empty

        Public Property Side As String = String.Empty

        Public Property EntryPrice As Decimal

        Public Property EntryBarTime As DateTimeOffset = DateTimeOffset.MinValue

        Public Property EntryAdx As Single

        Public Property StopPrice As Decimal

        Public Property TakeProfitPrice As Decimal

        Public Property PositionId As Long?

        Public Property AccountId As Long

        Public Property Contracts As Integer = 1

        Public Property IsOpen As Boolean

        Public Property Health As SlotHealth = SlotHealth.Healthy

        Public Property EntryReason As String = String.Empty

        Public Property EntryTime As DateTime = DateTime.MinValue

        Public Property MissCount As Integer

        Public Property UnrealizedPnl As Decimal

        ''' <summary>14-period ATR value at the time this slot was opened.</summary>
        Public Property EntryAtr As Decimal

        ''' <summary>Initial risk in price — |entry - stop| at the moment of entry confirmation.</summary>
        Public Property InitialRisk As Decimal

        ''' <summary>Current phased stop phase. Only advances forward.</summary>
        Public Property StopPhase As StopPhase = StopPhase.Initial

        ''' <summary>
        ''' Number of consecutive bars on which the exit engine scored Exiting health
        ''' (score >= 6, no E1 flip). Exit only fires when this reaches 2, preventing
        ''' single-bar consolidation at trend highs from triggering premature closure.
        ''' Resets to zero whenever the bar scores Warning or Healthy.
        ''' </summary>
        Public Property ConsecutiveExitBars As Integer = 0

        ''' <summary>
        ''' True between TryOpenSlot succeeding and the FireEntryAsync completing (order
        ''' accepted or rejected). Guards against the 15-second re-evaluation tick
        ''' re-opening the same instrument while the first PlaceOrder call is in-flight.
        ''' </summary>
        Public Property IsEntryInFlight As Boolean = False

    End Class

End Namespace
