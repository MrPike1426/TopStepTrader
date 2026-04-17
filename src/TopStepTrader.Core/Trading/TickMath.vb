Namespace TopStepTrader.Core.Trading

    ''' <summary>
    ''' Pure tick ↔ price helpers for futures instruments.
    ''' All arithmetic uses Decimal to avoid floating-point rounding errors common with
    ''' small tick sizes (e.g. MNQ 0.25, MES 0.25, MGC 0.10).
    ''' </summary>
    Public Module TickMath

        ''' <summary>
        ''' Convert a tick count to an absolute price level.
        ''' </summary>
        ''' <param name="entryPrice">Fill price of the entry order.</param>
        ''' <param name="ticks">Number of ticks away from entry.</param>
        ''' <param name="tickSize">Minimum price increment for the instrument.</param>
        ''' <param name="isBuy">True for a long position, False for short.</param>
        ''' <param name="isStop">True = stop (unfavourable side), False = target (favourable side).</param>
        Public Function PriceFromTicks(entryPrice As Decimal,
                                       ticks As Integer,
                                       tickSize As Decimal,
                                       isBuy As Boolean,
                                       isStop As Boolean) As Decimal
            If tickSize <= 0D Then Return entryPrice
            Dim delta As Decimal = ticks * tickSize
            ' Long stop is below entry; long target is above.
            ' Short stop is above entry; short target is below.
            If isBuy Then
                Return If(isStop, entryPrice - delta, entryPrice + delta)
            Else
                Return If(isStop, entryPrice + delta, entryPrice - delta)
            End If
        End Function

        ''' <summary>
        ''' Count ticks between two prices (signed: positive when toPrice > fromPrice).
        ''' Round half-away-from-zero so partial ticks never silently vanish.
        ''' </summary>
        Public Function TicksBetween(fromPrice As Decimal,
                                     toPrice As Decimal,
                                     tickSize As Decimal) As Integer
            If tickSize <= 0D Then Return 0
            Return CInt(Math.Round((toPrice - fromPrice) / tickSize,
                                   MidpointRounding.AwayFromZero))
        End Function

        ''' <summary>
        ''' Return the number of ticks between <paramref name="entryPrice"/> and
        ''' <paramref name="stopPrice"/>, always as a positive integer regardless of side.
        ''' </summary>
        Public Function StopTicksFromPrice(entryPrice As Decimal,
                                           stopPrice As Decimal,
                                           tickSize As Decimal) As Integer
            If tickSize <= 0D Then Return 0
            Return Math.Abs(TicksBetween(entryPrice, stopPrice, tickSize))
        End Function

        ''' <summary>
        ''' Enforce a minimum stop distance. Bumps <paramref name="requestedTicks"/> up to
        ''' <paramref name="minStopTicks"/> when the minimum is known and larger.
        ''' </summary>
        Public Function ClampToMinStop(requestedTicks As Integer,
                                       minStopTicks As Integer?) As Integer
            If minStopTicks.HasValue AndAlso requestedTicks < minStopTicks.Value Then
                Return minStopTicks.Value
            End If
            Return requestedTicks
        End Function

        ''' <summary>
        ''' Approximate tick size for an instrument from its last-known price.
        ''' Used as a last resort when the catalog has not yet been loaded.
        ''' </summary>
        Public Function ApproximateTickSize(lastPrice As Decimal) As Decimal
            Select Case lastPrice
                Case > 50000D : Return 5.0D      ' BTC-class
                Case > 3000D  : Return 0.25D     ' Gold / NQ-class
                Case > 1000D  : Return 0.25D     ' S&P class
                Case > 100D   : Return 0.01D     ' Oil / MGC class
                Case Else     : Return 0.01D
            End Select
        End Function

    End Module

End Namespace
