Namespace TopStepTrader.Core.Settings

    ''' <summary>
    ''' Unified configuration for SuperTrend+ Autopilot.
    ''' Replaces the three per-persona profile settings (Joe/Damian/Lewis).
    ''' </summary>
    Public Class SuperTrendPlusConfig

        ''' <summary>Maximum concurrent position slots per instrument.</summary>
        Public Property MaxSlots As Integer = 3

        ''' <summary>Contracts opened per slot entry.</summary>
        Public Property ContractsPerSlot As Integer = 1

        ''' <summary>Minimum ADX required to open any slot.</summary>
        Public Property AdxWeakThreshold As Single = 25.0F

        ''' <summary>ADX required to open slot 2 (index 1).</summary>
        Public Property AdxModerateThreshold As Single = 40.0F

        ''' <summary>ADX required to open slot 3 (index 2).</summary>
        Public Property AdxStrongThreshold As Single = 60.0F

        ''' <summary>Reward:risk ratio for take-profit calculation.</summary>
        Public Property TpMultiple As Decimal = 2.0D

        ''' <summary>SuperTrend ATR multiplier.</summary>
        Public Property StMultiplier As Double = 3.0

        ''' <summary>Chart timeframe label shown in the UI (e.g. "5min").</summary>
        Public Property BarTimeframe As String = "5min"

    End Class

End Namespace
