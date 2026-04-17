Namespace TopStepTrader.Core.Enums

    ''' <summary>Bar timeframe in minutes. Values used as DB discriminator.</summary>
    Public Enum BarTimeframe As Integer
        OneMinute = 1
        ThreeMinute = 3
        FiveMinute = 5
        TenMinute = 10
        FifteenMinute = 15
        ThirtyMinute = 30
        OneHour = 60
        TwoHour = 120
        FourHour = 240
        Daily = 1440
    End Enum

End Namespace
