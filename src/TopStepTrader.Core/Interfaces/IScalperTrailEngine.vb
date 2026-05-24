Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' FEAT-64: Quote-driven two-phase trailing stop. Phase 1 (Risk) holds the initial SL
    ''' at <c>entry ∓ InitialStopDollars</c>. Phase 2 (Trail) snaps SL to entry once favorable
    ''' excursion reaches <c>BreakevenSnapDollars</c>, then trails at
    ''' <c>TrailDistanceDollars</c> behind peak favorable price. Monotonic — SL never retraces.
    ''' </summary>
    Public Interface IScalperTrailEngine

        ''' <summary>
        ''' Initialises <paramref name="state"/> in place: sets <c>CurrentStopPrice</c> to
        ''' <c>entry ∓ initialRisk</c> rounded away from entry to the nearest tick, sets
        ''' <c>PeakFavorablePrice = EntryPrice</c>, returns the initial SL price.
        ''' </summary>
        Function Initialise(state As ScalperTrailState) As Decimal

        ''' <summary>
        ''' Process a quote tick: update peak favorable, run the BE-snap / trail logic,
        ''' decide whether the broker SL edit should fire (subject to step + rate throttles),
        ''' and report whether the trail has been violated. Mutates <paramref name="state"/>
        ''' in place; returns a decision record.
        ''' </summary>
        Function OnQuote(state As ScalperTrailState,
                         lastPrice As Decimal,
                         nowUtc As DateTime,
                         minSlEditStepTicks As Integer,
                         maxSlEditsPerSecond As Integer) As ScalperTrailUpdate

    End Interface

End Namespace
