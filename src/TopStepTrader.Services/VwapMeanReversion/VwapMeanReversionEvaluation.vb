Namespace TopStepTrader.Services.VwapMeanReversion

    ''' <summary>
    ''' FEAT-75: Fade direction decided by the signal detector. <c>None</c> indicates
    ''' no fresh signal this evaluation.
    ''' </summary>
    Public Enum VwapMeanReversionSignalSide
        None = 0
        Bullish = 1
        Bearish = 2
    End Enum

    ''' <summary>
    ''' FEAT-75: Per-tick evaluation result for one watchlist instrument. Mirrors
    ''' <c>BreakAndBounceEvaluation</c>: carries the raw readout plus per-condition
    ''' pass/fail so the UI status grid can explain near-misses.
    ''' </summary>
    Public Class VwapMeanReversionEvaluation
        Public Property Symbol As String = String.Empty
        Public Property AsOf As DateTimeOffset
        Public Property Signal As VwapMeanReversionSignalSide = VwapMeanReversionSignalSide.None
        Public Property RejectionReason As String = String.Empty

        ' ── Readout ────────────────────────────────────────────────────────
        Public Property LastClose As Decimal
        Public Property Vwap As Decimal
        Public Property Sd As Decimal

        ''' <summary>Signed distance of the 5-min close from VWAP in SD units (negative = below).</summary>
        Public Property DeviationSd As Double = Double.NaN

        ''' <summary>15-min ADX(14) at the last closed veto-TF bar. NaN = warmup.</summary>
        Public Property Adx As Double = Double.NaN

        ''' <summary>Wilder ATR(14) on the 5-min at the last closed bar. 0 = warmup.</summary>
        Public Property Atr As Decimal

        ' ── Per-condition status (UI grid) ─────────────────────────────────

        ''' <summary>(a) 5-min close beyond ±SdEntryThreshold from the session VWAP.</summary>
        Public Property BeyondEntryBand As Boolean

        ''' <summary>Too-far-gone veto tripped: excursion beyond SdTooFarThreshold.</summary>
        Public Property TooFarGone As Boolean

        ''' <summary>(b) 15-min ADX(14) below the trend-veto threshold.</summary>
        Public Property AdxVetoPassed As Boolean

        ''' <summary>(c) 1-min reversal candle confirmed.</summary>
        Public Property ConfirmationCandle As Boolean

        ''' <summary>Which confirmation fired: "Reversal", "BullishEngulfing", "BearishEngulfing".</summary>
        Public Property ConfirmationPattern As String = String.Empty

        ''' <summary>False when the entry cutoff (90 min before the 21:10 UTC close) blocks entries.</summary>
        Public Property InEntryWindow As Boolean

        ' ── Trade plan (populated when Signal <> None) ─────────────────────
        Public Property SuggestedInitialStopPrice As Decimal

        ''' <summary>T1 — the session VWAP (half close).</summary>
        Public Property T1Price As Decimal

        ''' <summary>T2 — opposite 1 SD band or Tp2StopMultiple × stop distance, whichever is closer.</summary>
        Public Property T2Price As Decimal

        ' ── Warmup bookkeeping ─────────────────────────────────────────────
        Public Property IsWarm As Boolean
        Public Property BarsAvailable As Integer
        Public Property AdxBarsAvailable As Integer
        Public Property ConfirmBarsAvailable As Integer
    End Class

End Namespace
