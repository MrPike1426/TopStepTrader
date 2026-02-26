Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' Result of a quick EMA + RSI trend analysis on recent bars.
    ''' Provides Up/Down probability percentages based on combined indicator confluence.
    ''' </summary>
    Public Class TrendAnalysisResult

        ''' <summary>Probability (0–100) that price will move UP.</summary>
        Public Property UpProbability As Double

        ''' <summary>Probability (0–100) that price will move DOWN.</summary>
        Public Property DownProbability As Double

        ''' <summary>Current EMA 21 value.</summary>
        Public Property EMA21 As Single

        ''' <summary>Current EMA 50 value.</summary>
        Public Property EMA50 As Single

        ''' <summary>Current RSI 14 value.</summary>
        Public Property RSI14 As Single

        ''' <summary>Current close price of the most recent bar.</summary>
        Public Property LastClose As Decimal

        ''' <summary>Human-readable summary of the trend analysis.</summary>
        Public Property Summary As String = String.Empty

        ''' <summary>Number of bars analysed.</summary>
        Public Property BarsAnalysed As Integer

        ''' <summary>Timestamp of the analysis.</summary>
        Public Property AnalysedAt As DateTimeOffset = DateTimeOffset.UtcNow

        ''' <summary>Individual indicator signals used to compute the final probability.</summary>
        Public Property Signals As New List(Of String)()

    End Class

End Namespace
