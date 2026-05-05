Namespace TopStepTrader.Core.Models.Debug

    Public Enum DebugEventType
        Heartbeat
        BarClose
        SlAdjust
        AiCheck
        PartialFill
        [Exit]
    End Enum

    Public Class DebugSnapshotRecord
        Public Property TradeId As String = String.Empty
        Public Property Timestamp As String = String.Empty
        Public Property EventType As String = "Heartbeat"
        Public Property LastPrice As Nullable(Of Decimal)
        Public Property Bid As Nullable(Of Decimal)
        Public Property Ask As Nullable(Of Decimal)
        Public Property CurrentSL As Nullable(Of Decimal)
        Public Property CurrentTP As Nullable(Of Decimal)
        Public Property UnrealizedPnLTicks As Nullable(Of Decimal)
        Public Property UnrealizedPnLDollars As Nullable(Of Decimal)
        Public Property Mfe As Nullable(Of Decimal)
        Public Property Mae As Nullable(Of Decimal)
        Public Property BarOpen As Nullable(Of Decimal)
        Public Property BarHigh As Nullable(Of Decimal)
        Public Property BarLow As Nullable(Of Decimal)
        Public Property BarClose As Nullable(Of Decimal)
        Public Property SuperTrendValue As Nullable(Of Decimal)
        Public Property SuperTrendDirection As String
        Public Property Atr As Nullable(Of Decimal)
        Public Property Adx As Nullable(Of Single)
        Public Property StopPhase As String
        Public Property Notes As String
    End Class

End Namespace
