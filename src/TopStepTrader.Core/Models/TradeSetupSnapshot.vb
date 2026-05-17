Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' FEAT-58: Full indicator + context snapshot captured at signal time.
    ''' Core-side POCO that mirrors <c>TopStepTrader.Data.Entities.TradeSetupSnapshotEntity</c> so the
    ''' service interface in Core does not have to reference the Data project. Mapped to the entity
    ''' inside <c>TradeRecordService.SaveSetupSnapshotAsync</c>.
    '''
    ''' Forward-compatibility: fields that are not currently consumed by SuperTrend+ Autopilot
    ''' (Ichimoku, EMA21/50, MACD, StochRSI, VIDYA, CMO, ΔVolume) are left at default 0 by the
    ''' producer and are reserved for the Multi-Confluence strategy snapshot.
    ''' </summary>
    Public Class TradeSetupSnapshot
        Public Property Id As Long
        Public Property TradeOutcomeId As Long
        Public Property CapturedAt As DateTimeOffset = DateTimeOffset.UtcNow

        ' Ichimoku
        Public Property Tenkan As Decimal = 0D
        Public Property Kijun As Decimal = 0D
        Public Property Cloud1 As Decimal = 0D
        Public Property Cloud2 As Decimal = 0D

        ' EMAs
        Public Property Ema21 As Decimal = 0D
        Public Property Ema50 As Decimal = 0D

        ' MACD
        Public Property MacdHist As Single = 0F
        Public Property MacdHistPrev As Single = 0F

        ' StochRSI / DMI
        Public Property StochRsiK As Single = 0F
        Public Property PlusDI As Single = 0F
        Public Property MinusDI As Single = 0F
        Public Property AdxValue As Single = 0F

        ' RSI / VIDYA
        Public Property Rsi14 As Single = 0F
        Public Property VidyaValue As Decimal = 0D
        Public Property CmoValue As Single = 0F
        Public Property DeltaVol As Single = 0F

        ' Confluence score (SuperTrend+ maps LongCount/ShortCount from ADX-band)
        Public Property LongCount As Integer = 0
        Public Property ShortCount As Integer = 0
        Public Property TotalConditions As Integer = 0
        Public Property UpPct As Integer = 0
        Public Property DownPct As Integer = 0

        ' Signal bar OHLCV
        Public Property SignalBarOpen As Decimal = 0D
        Public Property SignalBarHigh As Decimal = 0D
        Public Property SignalBarLow As Decimal = 0D
        Public Property SignalBarClose As Decimal = 0D
        Public Property SignalBarVolume As Long = 0L

        ' Volatility / session
        Public Property AtrValue As Decimal = 0D
        Public Property SessionWindow As String = String.Empty
        Public Property DayOfWeek As Integer = 0
        Public Property HourOfDay As Integer = 0

        ' Strategy identity
        Public Property StrategyName As String = String.Empty
        Public Property PersonaName As String = String.Empty
        Public Property SlMultiple As Single = 0F
        Public Property TpMultiple As Single = 0F
        Public Property TimeframeMinutes As Integer = 0
    End Class

End Namespace
