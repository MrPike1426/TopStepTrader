Namespace TopStepTrader.Core.Enums

    ''' <summary>Integer values are persisted — append only, never renumber.</summary>
    Public Enum RiskHaltReason As Byte
        None = 0
        DailyLossLimit = 1
        MaxDrawdown = 2
        MaxPositionSize = 3
        ManualHalt = 4
        ConnectionLost = 5
        ' FEAT-73 combine guard:
        DailyProfitLock = 6
        ConsecutiveLosses = 7
        MaxTradesPerDay = 8
    End Enum

End Namespace
