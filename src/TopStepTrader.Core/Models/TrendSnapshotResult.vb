Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' Output of the Naked Trader "Trend Snapshot" module.
    ''' Carries direction (UP / DOWN), confidence (LOW / MEDIUM / HIGH),
    ''' all raw indicator readings that drove the vote, and meta diagnostics.
    ''' </summary>
    Public Class TrendSnapshotResult

        ' ── Decision ──────────────────────────────────────────────────────────
        Public Property Direction As TrendDirection = TrendDirection.Up
        Public Property Confidence As TrendConfidence = TrendConfidence.Low
        Public Property Summary As String = String.Empty

        ' ── Indicator values (Single.NaN = warm-up incomplete) ────────────────
        Public Property Adx As Single = Single.NaN
        Public Property PlusDI As Single = Single.NaN
        Public Property MinusDI As Single = Single.NaN
        Public Property Ema9 As Single = Single.NaN
        Public Property Ema21 As Single = Single.NaN
        ''' <summary>MACD histogram (fast=8, slow=17, signal=9). May be NaN with short series.</summary>
        Public Property MacdHistogram As Single = Single.NaN
        ''' <summary>MACD line (fast EMA − slow EMA). Valid with fewer bars than histogram.</summary>
        Public Property MacdLine As Single = Single.NaN
        Public Property Vwap As Single = Single.NaN
        Public Property LastClose As Decimal = 0

        ' ── Vote tallies ──────────────────────────────────────────────────────
        Public Property UpVotes As Integer = 0
        Public Property DownVotes As Integer = 0
        ''' <summary>Count of indicators whose vote was counted (VWAP absent when volume unavailable).</summary>
        Public Property TotalVotes As Integer = 0

        ' ── Volume confirmation ───────────────────────────────────────────────
        ''' <summary>Nothing when volume data is absent; True/False when computable.</summary>
        Public Property IsVolumeOk As Boolean? = Nothing

        ' ── Meta ──────────────────────────────────────────────────────────────
        Public Property BarsAnalysed As Integer = 0
        Public Property AnalysedAt As DateTimeOffset = DateTimeOffset.UtcNow

    End Class

End Namespace
