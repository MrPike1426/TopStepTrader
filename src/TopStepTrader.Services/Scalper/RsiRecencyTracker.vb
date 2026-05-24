Namespace TopStepTrader.Services.Scalper

    ''' <summary>
    ''' FEAT-64: Incremental Wilder RSI with bookkeeping for "bars since last midline cross".
    '''
    ''' Mirrors Pine Script's <c>ta.rsi(close, period)</c> +
    ''' <c>ta.cross(rsi, mid) and rsi &gt; mid</c> / <c>... and rsi &lt; mid</c> +
    ''' <c>bar_index - lastAboveMidBar</c>. Both cross counters are exposed so the detector
    ''' can apply the recency filter ("RSI must have crossed up through 50 within the last N
    ''' bars before going oversold" / mirror for bearish).
    '''
    ''' Warmup: returns NaN for <c>Rsi</c> until <c>period + 1</c> closes have been observed.
    ''' </summary>
    Public Class RsiRecencyTracker

        Private ReadOnly _period As Integer
        Private ReadOnly _midline As Double
        Private _previousClose As Decimal?
        Private _avgGain As Double
        Private _avgLoss As Double
        Private _sampleCount As Integer
        Private _seedGainSum As Double
        Private _seedLossSum As Double
        Private _rsi As Double = Double.NaN
        Private _previousRsi As Double = Double.NaN
        Private _barsSinceCrossAbove As Integer = Int32.MaxValue
        Private _barsSinceCrossBelow As Integer = Int32.MaxValue

        Public Sub New(period As Integer, midline As Double)
            If period < 2 Then Throw New ArgumentOutOfRangeException(NameOf(period), "period must be >= 2")
            _period = period
            _midline = midline
        End Sub

        ''' <summary>Advance one bar with the given close price.</summary>
        Public Sub AddClose(closePrice As Decimal)
            ' --- Compute gain/loss vs previous close ---
            If _previousClose Is Nothing Then
                _previousClose = closePrice
                Return
            End If

            Dim diff = CDbl(closePrice - _previousClose.Value)
            Dim gain = If(diff > 0, diff, 0)
            Dim loss = If(diff < 0, -diff, 0)
            _previousClose = closePrice

            _sampleCount += 1

            ' --- Seed phase: first 'period' diffs accumulate; finalise on the period-th diff ---
            If _sampleCount < _period Then
                _seedGainSum += gain
                _seedLossSum += loss
                Return
            ElseIf _sampleCount = _period Then
                _seedGainSum += gain
                _seedLossSum += loss
                _avgGain = _seedGainSum / _period
                _avgLoss = _seedLossSum / _period
            Else
                ' --- Wilder smoothing ---
                _avgGain = (_avgGain * (_period - 1) + gain) / _period
                _avgLoss = (_avgLoss * (_period - 1) + loss) / _period
            End If

            ' --- Compute RSI ---
            _previousRsi = _rsi
            If _avgLoss = 0 Then
                _rsi = 100.0
            Else
                Dim rs = _avgGain / _avgLoss
                _rsi = 100.0 - (100.0 / (1.0 + rs))
            End If

            ' --- Cross bookkeeping (only valid once previous RSI is known) ---
            If Not Double.IsNaN(_previousRsi) Then
                Dim crossedAbove = (_previousRsi <= _midline) AndAlso (_rsi > _midline)
                Dim crossedBelow = (_previousRsi >= _midline) AndAlso (_rsi < _midline)

                ' Increment first so the cross bar itself reads as 0.
                If _barsSinceCrossAbove < Int32.MaxValue Then _barsSinceCrossAbove += 1
                If _barsSinceCrossBelow < Int32.MaxValue Then _barsSinceCrossBelow += 1

                If crossedAbove Then _barsSinceCrossAbove = 0
                If crossedBelow Then _barsSinceCrossBelow = 0
            End If
        End Sub

        ''' <summary>Current RSI, or <c>Double.NaN</c> during warmup.</summary>
        Public ReadOnly Property Rsi As Double
            Get
                Return _rsi
            End Get
        End Property

        ''' <summary>True once RSI has been computed at least once.</summary>
        Public ReadOnly Property IsWarm As Boolean
            Get
                Return Not Double.IsNaN(_rsi)
            End Get
        End Property

        ''' <summary>Bars since RSI most-recently crossed up through midline; <c>Int32.MaxValue</c> = never.</summary>
        Public ReadOnly Property BarsSinceCrossAbove As Integer
            Get
                Return _barsSinceCrossAbove
            End Get
        End Property

        ''' <summary>Bars since RSI most-recently crossed down through midline; <c>Int32.MaxValue</c> = never.</summary>
        Public ReadOnly Property BarsSinceCrossBelow As Integer
            Get
                Return _barsSinceCrossBelow
            End Get
        End Property

    End Class

End Namespace
