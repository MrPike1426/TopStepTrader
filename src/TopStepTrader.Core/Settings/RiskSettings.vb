Namespace TopStepTrader.Core.Settings

    Public Class RiskSettings
        ''' <summary>Daily loss limit in dollars. Negative value e.g. -1500</summary>
        Public Property DailyLossLimitDollars As Decimal = -1500D
        ''' <summary>Maximum drawdown in dollars. Negative value e.g. -2000</summary>
        Public Property MaxDrawdownDollars As Decimal = -2000D
        Public Property MaxPositionSizeContracts As Integer = 3
        Public Property MaxOrdersPerMinute As Integer = 10
        ''' <summary>Minimum ML confidence (0-1) required to execute auto-trade</summary>
        Public Property MinSignalConfidence As Single = 0.65F
        ''' <summary>MUST be explicitly set to True in Settings UI before auto-trading</summary>
        Public Property AutoExecutionEnabled As Boolean = False
    End Class

End Namespace
