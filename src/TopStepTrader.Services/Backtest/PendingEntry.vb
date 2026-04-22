Namespace TopStepTrader.Services.Backtest

    ''' <summary>
    ''' Bundles all next-bar pending entry state into a single object.
    ''' A signal fires at bar[i]; the engine fills at bar[i+1].Open.
    ''' Setting this reference to Nothing clears all pending state atomically,
    ''' preventing stale values from a prior bar leaking into the next signal.
    ''' </summary>
    Friend Class PendingEntry
        Public Property Side As String          ' "Buy" or "Sell"
        Public Property GroupId As Integer
        Public Property Confidence As Single
        Public Property IsScaleIn As Boolean
        Public Property StopDelta As Decimal    ' ATR-relative entry→SL distance
        Public Property TpDelta As Decimal      ' ATR-relative entry→TP distance
        ' SuperTrend: absolute SL/TP levels set at signal time (indicator-anchored)
        Public Property AbsStSl As Decimal
        Public Property AbsStTp As Decimal
        Public Property StIsLong As Boolean
        ' Indicator-channel exits (strategy-specific; 0 = not set for this strategy)
        Public Property DonMid As Decimal       ' DonchianBreakout mid-channel exit
        Public Property DonIsLong As Boolean
        Public Property BbMid As Decimal        ' BbRsiMeanReversion BB-middle exit
        Public Property BbIsLong As Boolean
        Public Property DbbInner As Decimal     ' DoubleBubbleButt inner-band exit
        Public Property DbbIsLong As Boolean
        ''' <summary>True when the signal has partial conviction (8/9 MultiConfluence). Entry uses half quantity.</summary>
        Public Property IsPartialSignal As Boolean
    End Class

End Namespace
