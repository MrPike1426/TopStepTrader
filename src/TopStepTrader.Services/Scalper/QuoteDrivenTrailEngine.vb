Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Scalper

    ''' <summary>
    ''' FEAT-64: Quote-driven two-phase trailing stop. See <see cref="IScalperTrailEngine"/>
    ''' for the phase logic. Pure function — owns no state beyond the supplied
    ''' <see cref="ScalperTrailState"/> instance; safe to register as a singleton.
    ''' </summary>
    Public Class QuoteDrivenTrailEngine
        Implements IScalperTrailEngine

        Public Function Initialise(state As ScalperTrailState) As Decimal Implements IScalperTrailEngine.Initialise
            If state Is Nothing Then Throw New ArgumentNullException(NameOf(state))
            If state.TickSize <= 0D Then Throw New ArgumentException("TickSize must be > 0", NameOf(state))
            If state.DollarsPerTick <= 0D Then Throw New ArgumentException("DollarsPerTick must be > 0", NameOf(state))

            Dim distanceTicks = DistanceInTicks(state.InitialStopDollars, state.DollarsPerTick)
            Dim distance = distanceTicks * state.TickSize
            state.CurrentStopPrice = If(state.Side = OrderSide.Buy,
                                        RoundDownToTick(state.EntryPrice - distance, state.TickSize),
                                        RoundUpToTick(state.EntryPrice + distance, state.TickSize))
            state.PeakFavorablePrice = state.EntryPrice
            state.HasBreakevenSnapped = False
            state.ThrottleWindowStartUtc = DateTime.MinValue
            state.EditsThisSecond = 0
            Return state.CurrentStopPrice
        End Function

        Public Function OnQuote(state As ScalperTrailState,
                                lastPrice As Decimal,
                                nowUtc As DateTime,
                                minSlEditStepTicks As Integer,
                                maxSlEditsPerSecond As Integer) As ScalperTrailUpdate _
            Implements IScalperTrailEngine.OnQuote
            If state Is Nothing Then Throw New ArgumentNullException(NameOf(state))
            Dim update = New ScalperTrailUpdate With {
                .NewStopPrice = state.CurrentStopPrice,
                .StopAdvanced = False,
                .ExitRequested = False,
                .BrokerEditShouldFire = False
            }

            If lastPrice <= 0D Then
                update.Reason = "non-positive price"
                Return update
            End If

            ' --- Track peak favorable price ---
            If state.Side = OrderSide.Buy Then
                If lastPrice > state.PeakFavorablePrice Then state.PeakFavorablePrice = lastPrice
            Else
                If state.PeakFavorablePrice = 0D OrElse lastPrice < state.PeakFavorablePrice Then state.PeakFavorablePrice = lastPrice
            End If

            ' --- Phase transition: BE snap ---
            Dim favDollars As Decimal = ComputeFavorableDollars(state, lastPrice)
            If Not state.HasBreakevenSnapped AndAlso favDollars >= state.BreakevenSnapDollars Then
                Dim beStop = RoundToTickToward(state.EntryPrice, state.TickSize, state.Side, away:=False)
                If IsAdvance(state, beStop) Then
                    update.NewStopPrice = beStop
                    update.StopAdvanced = True
                    update.Reason = $"BE snap @ +${favDollars:F2}"
                End If
                state.HasBreakevenSnapped = True
            End If

            ' --- Phase 2: continuous trail ---
            If state.HasBreakevenSnapped Then
                Dim trailDistanceTicks = DistanceInTicks(state.TrailDistanceDollars, state.DollarsPerTick)
                Dim trailDistance = trailDistanceTicks * state.TickSize
                Dim trailCandidate As Decimal
                If state.Side = OrderSide.Buy Then
                    trailCandidate = RoundDownToTick(state.PeakFavorablePrice - trailDistance, state.TickSize)
                Else
                    trailCandidate = RoundUpToTick(state.PeakFavorablePrice + trailDistance, state.TickSize)
                End If
                If IsAdvance(state, trailCandidate) Then
                    update.NewStopPrice = trailCandidate
                    update.StopAdvanced = True
                    update.Reason = $"Trail to {trailCandidate} (peak {state.PeakFavorablePrice})"
                End If
            End If

            ' --- Apply advance to state ---
            If update.StopAdvanced Then
                ' Throttle: count advances per second.
                Dim windowStart = New DateTime(nowUtc.Ticks - (nowUtc.Ticks Mod TimeSpan.TicksPerSecond), DateTimeKind.Utc)
                If state.ThrottleWindowStartUtc <> windowStart Then
                    state.ThrottleWindowStartUtc = windowStart
                    state.EditsThisSecond = 0
                End If

                Dim stepTicks = TicksBetween(state.CurrentStopPrice, update.NewStopPrice, state.TickSize)
                Dim stepSatisfied = stepTicks >= minSlEditStepTicks
                Dim rateSatisfied = state.EditsThisSecond < maxSlEditsPerSecond
                update.BrokerEditShouldFire = stepSatisfied AndAlso rateSatisfied

                state.CurrentStopPrice = update.NewStopPrice
                If update.BrokerEditShouldFire Then state.EditsThisSecond += 1
            End If

            ' --- Exit decision: price crossed through current SL ---
            If state.Side = OrderSide.Buy Then
                If lastPrice <= state.CurrentStopPrice Then
                    update.ExitRequested = True
                    update.Reason = $"Stop hit @ {lastPrice} (SL {state.CurrentStopPrice})"
                End If
            Else
                If lastPrice >= state.CurrentStopPrice Then
                    update.ExitRequested = True
                    update.Reason = $"Stop hit @ {lastPrice} (SL {state.CurrentStopPrice})"
                End If
            End If

            Return update
        End Function

        ' ── Helpers ─────────────────────────────────────────────────────────

        Private Shared Function ComputeFavorableDollars(state As ScalperTrailState, lastPrice As Decimal) As Decimal
            Dim move = If(state.Side = OrderSide.Buy,
                          lastPrice - state.EntryPrice,
                          state.EntryPrice - lastPrice)
            If move <= 0D Then Return 0D
            Dim ticks = move / state.TickSize
            Return ticks * state.DollarsPerTick
        End Function

        Private Shared Function IsAdvance(state As ScalperTrailState, candidate As Decimal) As Boolean
            If state.Side = OrderSide.Buy Then
                Return candidate > state.CurrentStopPrice
            Else
                Return candidate < state.CurrentStopPrice
            End If
        End Function

        Private Shared Function DistanceInTicks(dollars As Decimal, dollarsPerTick As Decimal) As Integer
            If dollarsPerTick <= 0D Then Return 0
            Return CInt(Math.Ceiling(CDbl(dollars / dollarsPerTick)))
        End Function

        Private Shared Function RoundDownToTick(value As Decimal, tickSize As Decimal) As Decimal
            Dim ticks = Math.Floor(value / tickSize)
            Return ticks * tickSize
        End Function

        Private Shared Function RoundUpToTick(value As Decimal, tickSize As Decimal) As Decimal
            Dim ticks = Math.Ceiling(value / tickSize)
            Return ticks * tickSize
        End Function

        ''' <summary>Round price to the nearest tick, optionally biased away from entry for safety.</summary>
        Private Shared Function RoundToTickToward(price As Decimal, tickSize As Decimal,
                                                   side As OrderSide, away As Boolean) As Decimal
            If away Then
                Return If(side = OrderSide.Buy, RoundDownToTick(price, tickSize), RoundUpToTick(price, tickSize))
            Else
                Return Math.Round(price / tickSize) * tickSize
            End If
        End Function

        Private Shared Function TicksBetween(a As Decimal, b As Decimal, tickSize As Decimal) As Integer
            Return CInt(Math.Abs((a - b) / tickSize))
        End Function

    End Class

End Namespace
