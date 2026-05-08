Namespace TopStepTrader.Core.Enums

    ''' <summary>
    ''' Bar timeframe in minutes. Values used as DB discriminator.
    ''' FifteenSecond = 0 is a special live-only value (never persisted to DB).
    ''' TwoSecond = -2 is a live-only value used for the 2-second position-management bar close.
    ''' </summary>
    Public Enum BarTimeframe As Integer
        TwoSecond = -2     ' Live-only — maps to TopStepX unit=1 (Second), unitNumber=2
        FiveSecond = -5    ' Live-only — maps to TopStepX unit=1 (Second), unitNumber=5
        FifteenSecond = 0  ' Live-only — maps to TopStepX unit=1 (Second), unitNumber=15
        OneMinute = 1
        ThreeMinute = 3
        FiveMinute = 5
        FifteenMinute = 15
        ThirtyMinute = 30
        OneHour = 60
        TwoHour = 120
        FourHour = 240
        Daily = 1440
    End Enum

End Namespace
