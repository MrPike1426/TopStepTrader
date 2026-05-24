Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' FEAT-65: Raised by <c>UltimateScalperOrchestrator.ScanCompleted</c> after every
    ''' completion of the per-symbol scan loop. Drives the header status line in the
    ''' Ultimate Scalper tab: per-symbol bar counts during warmup, then the scan-tick
    ''' heartbeat timestamp once every watchlist symbol is warm.
    ''' </summary>
    Public Class ScannerScanCompletedEventArgs
        Inherits EventArgs

        ''' <summary>UTC wall-clock time at which the scan loop completed.</summary>
        Public Property AsOfUtc As DateTime

        ''' <summary>
        ''' Per-symbol bars-available counts captured from the most recent evaluation in this
        ''' scan. Symbols that threw during evaluation are absent from this dictionary; the
        ''' consumer should treat absent entries as 0.
        ''' </summary>
        Public Property BarsAvailable As IReadOnlyDictionary(Of String, Integer)

    End Class

End Namespace
