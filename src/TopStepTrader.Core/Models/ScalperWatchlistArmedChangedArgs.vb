Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' FEAT-69: Raised by <c>UltimateScalperOrchestrator.WatchlistArmedChanged</c> when the
    ''' stop-entry manager arms or disarms a primed order. Drives the per-row "Armed" indicator
    ''' and the header status line's armed-symbols summary in the Ultimate Scalper tab.
    ''' </summary>
    Public Class ScalperWatchlistArmedChangedArgs
        Inherits EventArgs

        Public Property Symbol As String

        ''' <summary>True = symbol just transitioned to Primed; False = disarmed (or filled).</summary>
        Public Property IsArmed As Boolean

        ''' <summary>Direction of the armed order. <c>None</c> when <see cref="IsArmed"/> is False.</summary>
        Public Property Side As UltimateScalperSignalSide = UltimateScalperSignalSide.None

        ''' <summary>Broker trigger price of the armed stop-entry. 0 when <see cref="IsArmed"/> is False.</summary>
        Public Property TriggerPrice As Decimal

    End Class

End Namespace
