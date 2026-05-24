Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' FEAT-64: Mutable per-position state for the Ultimate Scalper quote-driven trail engine.
    ''' Lives on the orchestrator for the duration of a single live position; created on entry,
    ''' updated on every <c>MarketHubClient.QuoteReceived</c> tick, discarded on exit.
    '''
    ''' The trail is monotonic — <see cref="CurrentStopPrice"/> only advances in the favorable
    ''' direction. Throttle counters guard broker SL-edit rate limits.
    ''' </summary>
    Public Class ScalperTrailState

        ''' <summary>Root symbol the trail belongs to ("MES" / "MNQ" / "MGC").</summary>
        Public Property Symbol As String = String.Empty

        ''' <summary>Buy = long, Sell = short. Drives the sign of all favorable-direction comparisons.</summary>
        Public Property Side As OrderSide

        ''' <summary>Entry fill price reported by the broker.</summary>
        Public Property EntryPrice As Decimal

        ''' <summary>Instrument tick size (e.g. 0.25 for MES).</summary>
        Public Property TickSize As Decimal

        ''' <summary>Dollar value of one tick per contract (e.g. 1.25 for MES).</summary>
        Public Property DollarsPerTick As Decimal

        ''' <summary>Initial risk per contract in USD (from the per-instrument risk profile).</summary>
        Public Property InitialStopDollars As Decimal

        ''' <summary>Favorable excursion in USD that triggers the break-even snap.</summary>
        Public Property BreakevenSnapDollars As Decimal

        ''' <summary>Distance behind peak favorable price (USD per contract) for the trail.</summary>
        Public Property TrailDistanceDollars As Decimal

        ''' <summary>Current resting stop-loss price. Updated monotonically.</summary>
        Public Property CurrentStopPrice As Decimal

        ''' <summary>Peak favorable price observed (max for long, min for short).</summary>
        Public Property PeakFavorablePrice As Decimal

        ''' <summary>True once the BE snap has fired — phase transition flag.</summary>
        Public Property HasBreakevenSnapped As Boolean

        ''' <summary>Wall-clock second window for the broker SL-edit throttle.</summary>
        Public Property ThrottleWindowStartUtc As DateTime = DateTime.MinValue

        ''' <summary>Edits emitted in the current throttle window.</summary>
        Public Property EditsThisSecond As Integer

    End Class

End Namespace
