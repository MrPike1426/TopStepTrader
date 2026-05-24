Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Scalper

    ''' <summary>
    ''' FEAT-64: Volume-weighted average price accumulator scoped to a single trading session.
    ''' Mirrors Pine Script's <c>ta.vwap(hlc3)</c>: typical price is (High + Low + Close) / 3
    ''' weighted by Volume. Session boundary detection is the caller's responsibility — call
    ''' <see cref="Reset"/> when a new session begins (e.g. on a > 30 min gap between bar
    ''' timestamps for equity-futures micros).
    '''
    ''' v1 does not consult <c>ITradingSessionContext</c>; the orchestrator drives resets from
    ''' bar-timestamp gaps. If the strategy later needs RTH-only behaviour, integrate the
    ''' session resolver upstream — the calculator itself stays pure.
    ''' </summary>
    Public Class SessionVwapCalculator

        Private _sumTypicalVolume As Decimal
        Private _sumVolume As Decimal
        Private _barCount As Integer

        ''' <summary>Adds a closed bar's contribution to the running VWAP.</summary>
        Public Sub AddBar(bar As MarketBar)
            If bar Is Nothing Then Return
            Dim typical = (bar.High + bar.Low + bar.Close) / 3D
            Dim vol = CDec(bar.Volume)
            _sumTypicalVolume += typical * vol
            _sumVolume += vol
            _barCount += 1
        End Sub

        ''' <summary>Clears the accumulator. Called at session open by the orchestrator.</summary>
        Public Sub Reset()
            _sumTypicalVolume = 0D
            _sumVolume = 0D
            _barCount = 0
        End Sub

        ''' <summary>Current session VWAP, or 0 when no bars have been added.</summary>
        Public ReadOnly Property Vwap As Decimal
            Get
                If _sumVolume = 0D Then Return 0D
                Return _sumTypicalVolume / _sumVolume
            End Get
        End Property

        ''' <summary>Number of bars currently contributing to the running VWAP.</summary>
        Public ReadOnly Property BarCount As Integer
            Get
                Return _barCount
            End Get
        End Property

    End Class

End Namespace
