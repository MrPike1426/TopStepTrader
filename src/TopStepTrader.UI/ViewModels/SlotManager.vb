Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' Owns the array of PositionSlots and enforces open/close rules
    ''' based on ADX band targeting.
    ''' </summary>
    Public Class SlotManager

        Private ReadOnly _config As SuperTrendPlusConfig
        Private ReadOnly _slots As PositionSlot()

        Public Sub New(config As SuperTrendPlusConfig)
            _config = config
            _slots = New PositionSlot(_config.MaxSlots - 1) {}
            For i = 0 To _config.MaxSlots - 1
                _slots(i) = New PositionSlot With {.SlotIndex = i}
            Next
        End Sub

        ''' <summary>All slots (read-only view).</summary>
        Public ReadOnly Property Slots As IReadOnlyList(Of PositionSlot)
            Get
                Return _slots
            End Get
        End Property

        Public ReadOnly Property OpenSlotCount As Integer
            Get
                Return _slots.Count(Function(s) s.IsOpen)
            End Get
        End Property

        ''' <summary>
        ''' Returns the target number of open slots for a given ADX value.
        ''' ADX &lt; AdxWeakThreshold → 0
        ''' ADX in [Weak, Moderate)  → 1
        ''' ADX in [Moderate, Strong) → 2
        ''' ADX ≥ Strong             → 3
        ''' </summary>
        Public Function TargetSlotCount(adx As Single) As Integer
            If adx < _config.AdxWeakThreshold Then Return 0
            If adx < _config.AdxModerateThreshold Then Return 1
            If adx < _config.AdxStrongThreshold Then Return 2
            Return _config.MaxSlots
        End Function

        ''' <summary>
        ''' Tries to open the next available slot, enforcing all slot-open rules.
        ''' Returns the opened slot, or Nothing if rules prevent opening.
        ''' </summary>
        Public Function TryOpenSlot(instrument As String,
                                    side As String,
                                    entryAdx As Single,
                                    barTime As DateTimeOffset,
                                    stLine As Decimal,
                                    lastClose As Decimal) As PositionSlot
            ' Rule: no slot open when any existing slot is Exiting
            If _slots.Any(Function(s) s.IsOpen AndAlso s.Health = SlotHealth.Exiting) Then
                Return Nothing
            End If

            ' Rule: no new slot for an instrument that already has an open slot (one per instrument)
            If _slots.Any(Function(s) s.IsOpen AndAlso
                          String.Equals(s.Instrument, instrument, StringComparison.OrdinalIgnoreCase)) Then
                Return Nothing
            End If

            ' Rule: no counter-trend on the SAME instrument (cross-instrument conflicts are allowed)
            Dim sameInstrumentSlots = _slots.Where(Function(s) s.IsOpen AndAlso
                String.Equals(s.Instrument, instrument, StringComparison.OrdinalIgnoreCase)).ToList()
            If sameInstrumentSlots.Any() AndAlso sameInstrumentSlots.First().Side <> side Then
                Return Nothing
            End If

            ' Rule: no new slot if same instrument has a Warning or Exiting slot
            If _slots.Any(Function(s) s.IsOpen AndAlso
                          String.Equals(s.Instrument, instrument, StringComparison.OrdinalIgnoreCase) AndAlso
                          (s.Health = SlotHealth.Warning OrElse s.Health = SlotHealth.Exiting)) Then
                Return Nothing
            End If

            ' Rule: total concurrent slot cap
            If OpenSlotCount >= _config.MaxSlots Then Return Nothing

            ' Rule: ADX must meet weak threshold to open any slot
            If TargetSlotCount(entryAdx) = 0 Then Return Nothing

            ' Rule: bar timestamp must be newer than the most recently opened slot's EntryBarTime
            ' for the same instrument (prevents double-open on the same bar)
            Dim lastOpened = _slots _
                .Where(Function(s) s.IsOpen AndAlso
                       String.Equals(s.Instrument, instrument, StringComparison.OrdinalIgnoreCase)) _
                .OrderByDescending(Function(s) s.EntryBarTime) _
                .FirstOrDefault()
            If lastOpened IsNot Nothing AndAlso barTime <= lastOpened.EntryBarTime Then
                Return Nothing
            End If

            Dim slot = _slots.FirstOrDefault(Function(s) Not s.IsOpen)
            If slot Is Nothing Then Return Nothing

            slot.Instrument          = instrument
            slot.Side                = side
            slot.EntryAdx            = entryAdx
            slot.EntryBarTime        = barTime
            slot.StopPrice           = stLine
            slot.EntryPrice          = 0D
            slot.TakeProfitPrice     = 0D
            slot.Contracts           = ContractsForAdx(entryAdx)
            slot.IsOpen              = True
            slot.Health              = SlotHealth.Healthy
            slot.MissCount           = 0
            slot.UnrealizedPnl       = 0D
            slot.EntryReason         = FormatEntryReason(entryAdx)
            slot.ConsecutiveExitBars = 0
            slot.IsEntryInFlight     = True   ' cleared by FireEntryAsync on accept or reject
            Return slot
        End Function

        Public Sub CloseSlot(index As Integer)
            If index >= 0 AndAlso index < _slots.Length Then
                Dim s = _slots(index)
                s.IsOpen       = False
                s.Health       = SlotHealth.Healthy
                s.Instrument   = String.Empty
                s.Side         = String.Empty
                s.EntryPrice   = 0D
                s.EntryBarTime = DateTimeOffset.MinValue
                s.EntryAdx     = 0F
                s.StopPrice    = 0D
                s.TakeProfitPrice = 0D
                s.PositionId   = Nothing
                s.AccountId    = 0
                s.MissCount    = 0
                s.UnrealizedPnl = 0D
                s.EntryReason  = String.Empty
                s.EntryTime    = DateTime.MinValue
                s.EntryAtr     = 0D
                s.InitialRisk  = 0D
                s.StopPhase          = Core.Enums.StopPhase.Initial
                s.ConsecutiveExitBars = 0
                s.IsEntryInFlight    = False
            End If
        End Sub

        Public Sub CloseAllForInstrument(instrument As String)
            For i = 0 To _slots.Length - 1
                If _slots(i).IsOpen AndAlso _slots(i).Instrument = instrument Then
                    CloseSlot(i)
                End If
            Next
        End Sub

        Public Sub ResetAll()
            For i = 0 To _slots.Length - 1
                CloseSlot(i)
            Next
        End Sub

        ''' <summary>Returns the number of contracts to place based on ADX strength band.</summary>
        Public Function ContractsForAdx(adx As Single) As Integer
            If adx >= _config.AdxStrongThreshold Then Return Math.Min(3, _config.ContractsPerSlot * 3)
            If adx >= _config.AdxModerateThreshold Then Return Math.Min(2, _config.ContractsPerSlot * 2)
            Return _config.ContractsPerSlot
        End Function

        Private Shared Function FormatEntryReason(adx As Single) As String
            Dim adxInt = CInt(Math.Round(adx))
            If adx >= 60 Then Return $"ADX {adxInt} — L3: Espresso"
            If adx >= 40 Then Return $"ADX {adxInt} — L2: Latte"
            Return $"ADX {adxInt} — L1: Decaff"
        End Function

    End Class

End Namespace
