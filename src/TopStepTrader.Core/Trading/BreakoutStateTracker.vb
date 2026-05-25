Imports System.Collections.Concurrent
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings

Namespace TopStepTrader.Core.Trading

    ''' <summary>
    ''' FEAT-62: Per-contract 15-minute breakout-direction state machine for the
    ''' Break and Bounce strategy. <c>Update</c> is called once per closed 15m bar
    ''' with the previous day's reference range; <c>Direction</c> reads the current
    ''' bias (-1 short, 0 flat, +1 long).
    '''
    ''' Invalidation:
    '''   • <see cref="BreakAndBounceConfig.InvalidateDirOnCounterBreakout"/> — a long
    '''     bias is cleared when a subsequent 15m bar closes below <c>prevLow</c>
    '''     (mirror for shorts). The bias does NOT flip; the orchestrator must wait
    '''     for the next forward breakout.
    '''   • <see cref="BreakAndBounceConfig.InvalidateDirOnWindowExpiry"/> —
    '''     <see cref="OnEntryWindowExpired"/> clears all state when invoked by the
    '''     orchestrator once per session.
    '''   • <see cref="OnNewTradingDay"/> unconditionally clears everything.
    ''' </summary>
    Public Class BreakoutStateTracker

        Private ReadOnly _config As BreakAndBounceConfig
        Private ReadOnly _state As New ConcurrentDictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)

        Public Sub New(config As BreakAndBounceConfig)
            If config Is Nothing Then Throw New ArgumentNullException(NameOf(config))
            _config = config
        End Sub

        ''' <summary>Apply a closed 15m bar to the tracker for <paramref name="symbol"/>.</summary>
        Public Sub Update(symbol As String,
                          closedBar As MarketBar,
                          prevHigh As Decimal,
                          prevLow As Decimal)
            If closedBar Is Nothing OrElse String.IsNullOrEmpty(symbol) Then Return
            Dim current As Integer = 0
            _state.TryGetValue(symbol, current)

            ' Counter-breakout invalidation takes precedence over a fresh opposite
            ' breakout: a standing long bias hit by a sub-prevLow close clears to 0,
            ' not -1. The orchestrator must wait for the next forward breakout.
            If _config.InvalidateDirOnCounterBreakout Then
                If current = 1 AndAlso closedBar.Close < prevLow Then
                    _state(symbol) = 0
                    Return
                End If
                If current = -1 AndAlso closedBar.Close > prevHigh Then
                    _state(symbol) = 0
                    Return
                End If
            End If

            If closedBar.Close > prevHigh Then
                _state(symbol) = 1
                Return
            End If
            If closedBar.Close < prevLow Then
                _state(symbol) = -1
            End If
        End Sub

        ''' <summary>Current bias for <paramref name="symbol"/>: -1, 0, or +1.</summary>
        Public Function GetDirection(symbol As String) As Integer
            If String.IsNullOrEmpty(symbol) Then Return 0
            Dim v As Integer = 0
            _state.TryGetValue(symbol, v)
            Return v
        End Function

        ''' <summary>Idempotent: clears all bias state when entry window expires for the day.</summary>
        Public Sub OnEntryWindowExpired()
            If Not _config.InvalidateDirOnWindowExpiry Then Return
            _state.Clear()
        End Sub

        ''' <summary>Hard reset — called at start of each trading day or on prev-range refresh.</summary>
        Public Sub OnNewTradingDay()
            _state.Clear()
        End Sub

    End Class

End Namespace
