Namespace TopStepTrader.Core.Events

    ''' <summary>
    ''' BUG-94 F2/F3/F4: payload raised by <c>TradeReconciliationWorker</c> when its periodic
    ''' scan finds a broker-reported open position with no matching open <c>LiveTradeRecord</c>.
    ''' Consumed by the UI to surface a toast/banner alarm and (when AutoStopLossEnabled is on)
    ''' to confirm the protective Stop Market that the worker has already placed.
    ''' </summary>
    Public Class OrphanPositionDetectedEventArgs
        Inherits EventArgs

        ''' <summary>Broker position id.</summary>
        Public Property PositionId As Long
        ''' <summary>PX contract id reported by the broker (e.g. "CON.F.US.MES.M26").</summary>
        Public Property ContractId As String = String.Empty
        ''' <summary>Root symbol resolved via FavouriteContracts (e.g. "MES"). Falls back to ContractId.</summary>
        Public Property Symbol As String = String.Empty
        ''' <summary>"Long" or "Short".</summary>
        Public Property Side As String = String.Empty
        ''' <summary>Absolute contract count.</summary>
        Public Property Size As Integer
        ''' <summary>Broker-reported avg fill price.</summary>
        Public Property NetPrice As Decimal
        ''' <summary>Broker-reported open P&amp;L (USD).</summary>
        Public Property OpenPnLUsd As Decimal
        ''' <summary>Position age at detection (best-effort; may be 0 when broker omits CreationTimestamp).</summary>
        Public Property Age As TimeSpan
        ''' <summary>True when the worker placed a protective SL after detecting the orphan.</summary>
        Public Property AutoSlApplied As Boolean
        ''' <summary>SL price written when <see cref="AutoSlApplied"/> is True.</summary>
        Public Property AutoSlPriceApplied As Decimal
        ''' <summary>
        ''' Populated when auto-SL was deliberately not applied. Documented values:
        '''   "Disabled" — SafetyNetSettings.AutoStopLossEnabled = False.
        '''   "ExistingBracketDetected" — an SL is already resting on the contract.
        '''   "UnknownContract" — FavouriteContracts cannot resolve the contract id.
        '''   "EditFailed" — EditPositionSlTpAsync returned False / threw.
        ''' </summary>
        Public Property AutoSlSkippedReason As String = String.Empty
        ''' <summary>Time the orphan was detected (UTC).</summary>
        Public Property DetectedAtUtc As DateTimeOffset = DateTimeOffset.UtcNow
    End Class

End Namespace
