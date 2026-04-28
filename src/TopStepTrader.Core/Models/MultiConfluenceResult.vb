Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' Result returned by the Multi-Confluence evaluator for one bar.
    ''' </summary>
    Public Class MultiConfluenceResult
        ''' <summary>Trade direction signalled, or Nothing when no signal fires.</summary>
        Public Property Side As OrderSide? = Nothing
        ''' <summary>Percentage of bullish confluence conditions met (0–100).</summary>
        Public Property BullScore As Integer = 0
        ''' <summary>Percentage of bearish confluence conditions met (0–100).</summary>
        Public Property BearScore As Integer = 0
        ''' <summary>ATR(14) value — drives dynamic SL/TP sizing.</summary>
        Public Property AtrValue As Decimal = 0D
        ''' <summary>
        ''' Ichimoku cloud edge to use as the SL candidate price.
        ''' Cloud bottom for Long entries; cloud top for Short entries.
        ''' Nothing when cloud values are not yet available.
        ''' </summary>
        Public Property CloudEdgeSl As Decimal? = Nothing
        ''' <summary>Single-line summary of all indicator values for the execution log.</summary>
        Public Property StatusLine As String = String.Empty
        ''' <summary>Raw ADX(14) value at bar-check time.</summary>
        Public Property AdxValue As Single = 0F
        ' ── Extended indicator snapshot for the Hydra grid display ──────────────
        Public Property Cloud1 As Decimal = 0D         ' higher of SpanA / SpanB
        Public Property Cloud2 As Decimal = 0D         ' lower of SpanA / SpanB
        Public Property Tenkan As Decimal = 0D
        Public Property Kijun As Decimal = 0D
        Public Property Ema21 As Decimal = 0D
        Public Property Ema50 As Decimal = 0D
        Public Property PlusDI As Single = 0F
        Public Property MinusDI As Single = 0F
        Public Property MacdHist As Single = 0F
        Public Property MacdHistPrev As Single = 0F
        Public Property StochRsiK As Single = 0F
        Public Property LongCount As Integer = 0
        Public Property ShortCount As Integer = 0
        ''' <summary>Graduated confidence score [0–1]. 0 when no signal fires.</summary>
        Public Property Confidence As Single = 0F
        ''' <summary>True when 8/9 conditions align but not all 9.</summary>
        Public Property IsPartialSignal As Boolean = False
        ''' <summary>Names of conditions that failed on this evaluation.</summary>
        Public Property FailedConditions As New List(Of String)()
    End Class

End Namespace
