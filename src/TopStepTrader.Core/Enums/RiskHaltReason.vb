Namespace TopStepTrader.Core.Enums

    Public Enum RiskHaltReason As Byte
        None = 0
        DailyLossLimit = 1
        MaxDrawdown = 2
        MaxPositionSize = 3
        ManualHalt = 4
        ConnectionLost = 5
    End Enum

End Namespace
