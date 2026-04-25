Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    ''' <summary>
    ''' Full indicator + context snapshot captured at signal time.
    ''' Linked to the TradeOutcomes row via TradeOutcomeId.
    ''' </summary>
    <Table("TradeSetupSnapshots")>
    Public Class TradeSetupSnapshotEntity

        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.Identity)>
        Public Property Id As Long

        Public Property TradeOutcomeId As Long

        Public Property CapturedAt As DateTimeOffset = DateTimeOffset.UtcNow

        ' ── Ichimoku ────────────────────────────────────────────────────
        <Column(TypeName:="decimal(18,4)")>
        Public Property Tenkan As Decimal = 0D
        <Column(TypeName:="decimal(18,4)")>
        Public Property Kijun As Decimal = 0D
        <Column(TypeName:="decimal(18,4)")>
        Public Property Cloud1 As Decimal = 0D
        <Column(TypeName:="decimal(18,4)")>
        Public Property Cloud2 As Decimal = 0D

        ' ── EMAs ────────────────────────────────────────────────────────
        <Column(TypeName:="decimal(18,4)")>
        Public Property Ema21 As Decimal = 0D
        <Column(TypeName:="decimal(18,4)")>
        Public Property Ema50 As Decimal = 0D

        ' ── MACD ────────────────────────────────────────────────────────
        Public Property MacdHist As Single = 0F
        Public Property MacdHistPrev As Single = 0F

        ' ── StochRSI / DMI ──────────────────────────────────────────────
        Public Property StochRsiK As Single = 0F
        Public Property PlusDI As Single = 0F
        Public Property MinusDI As Single = 0F
        Public Property AdxValue As Single = 0F

        ' ── RSI / VIDYA ─────────────────────────────────────────────────
        Public Property Rsi14 As Single = 0F
        <Column(TypeName:="decimal(18,4)")>
        Public Property VidyaValue As Decimal = 0D
        Public Property CmoValue As Single = 0F
        Public Property DeltaVol As Single = 0F

        ' ── Confluence score ─────────────────────────────────────────────
        Public Property LongCount As Integer = 0
        Public Property ShortCount As Integer = 0
        Public Property TotalConditions As Integer = 0
        Public Property UpPct As Integer = 0
        Public Property DownPct As Integer = 0

        ' ── Signal bar OHLCV ─────────────────────────────────────────────
        <Column(TypeName:="decimal(18,4)")>
        Public Property SignalBarOpen As Decimal = 0D
        <Column(TypeName:="decimal(18,4)")>
        Public Property SignalBarHigh As Decimal = 0D
        <Column(TypeName:="decimal(18,4)")>
        Public Property SignalBarLow As Decimal = 0D
        <Column(TypeName:="decimal(18,4)")>
        Public Property SignalBarClose As Decimal = 0D
        Public Property SignalBarVolume As Long = 0L

        ' ── Volatility / session ─────────────────────────────────────────
        <Column(TypeName:="decimal(18,4)")>
        Public Property AtrValue As Decimal = 0D
        Public Property SessionWindow As String = String.Empty
        Public Property DayOfWeek As Integer = 0
        Public Property HourOfDay As Integer = 0

        ' ── Strategy identity ────────────────────────────────────────────
        <MaxLength(100)>
        Public Property StrategyName As String = String.Empty
        <MaxLength(50)>
        Public Property PersonaName As String = String.Empty
        Public Property SlMultiple As Single = 0F
        Public Property TpMultiple As Single = 0F
        Public Property TimeframeMinutes As Integer = 0

    End Class

End Namespace
