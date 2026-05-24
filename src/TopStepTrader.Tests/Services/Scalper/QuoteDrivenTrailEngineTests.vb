Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Scalper
Imports Xunit

Namespace TopStepTrader.Tests.Services.Scalper

    Public Class QuoteDrivenTrailEngineTests

        ''' <summary>MES-like state for long-side tests. tick=0.25, $1.25/tick → 8 ticks = $10.</summary>
        Private Shared Function NewMesLongState(entry As Decimal,
                                                 Optional initialRisk As Decimal = 20D,
                                                 Optional beSnap As Decimal = 10D,
                                                 Optional trail As Decimal = 10D) As ScalperTrailState
            Return New ScalperTrailState With {
                .Symbol = "MES",
                .Side = OrderSide.Buy,
                .EntryPrice = entry,
                .TickSize = 0.25D,
                .DollarsPerTick = 1.25D,
                .InitialStopDollars = initialRisk,
                .BreakevenSnapDollars = beSnap,
                .TrailDistanceDollars = trail
            }
        End Function

        Private Shared Function NewMesShortState(entry As Decimal,
                                                  Optional initialRisk As Decimal = 20D,
                                                  Optional beSnap As Decimal = 10D,
                                                  Optional trail As Decimal = 10D) As ScalperTrailState
            Return New ScalperTrailState With {
                .Symbol = "MES",
                .Side = OrderSide.Sell,
                .EntryPrice = entry,
                .TickSize = 0.25D,
                .DollarsPerTick = 1.25D,
                .InitialStopDollars = initialRisk,
                .BreakevenSnapDollars = beSnap,
                .TrailDistanceDollars = trail
            }
        End Function

        <Fact>
        Public Sub Initialise_LongPlacesStopBelowEntryAtConfiguredRisk()
            ' $20 / $1.25 = 16 ticks. 16 × 0.25 = 4.00. SL = entry - 4.00.
            Dim engine = New QuoteDrivenTrailEngine()
            Dim state = NewMesLongState(entry:=4500D, initialRisk:=20D)
            Dim sl = engine.Initialise(state)
            Assert.Equal(4496D, sl)
            Assert.Equal(4500D, state.PeakFavorablePrice)
            Assert.False(state.HasBreakevenSnapped)
        End Sub

        <Fact>
        Public Sub Initialise_ShortPlacesStopAboveEntryAtConfiguredRisk()
            Dim engine = New QuoteDrivenTrailEngine()
            Dim state = NewMesShortState(entry:=4500D, initialRisk:=20D)
            Dim sl = engine.Initialise(state)
            Assert.Equal(4504D, sl)
            Assert.Equal(4500D, state.PeakFavorablePrice)
        End Sub

        <Fact>
        Public Sub Long_BeSnap_FiresExactlyWhenFavorableDollarsCrossesThreshold()
            ' beSnap=$10 → 8 ticks favorable → +2.00 price move.
            Dim engine = New QuoteDrivenTrailEngine()
            Dim state = NewMesLongState(entry:=4500D)
            engine.Initialise(state)

            ' Just below threshold: 7 ticks favorable = $8.75 — no snap.
            Dim u1 = engine.OnQuote(state, lastPrice:=4501.75D, nowUtc:=DateTime.UtcNow,
                                     minSlEditStepTicks:=1, maxSlEditsPerSecond:=5)
            Assert.False(state.HasBreakevenSnapped)
            Assert.False(u1.StopAdvanced)
            Assert.Equal(4496D, state.CurrentStopPrice)

            ' Exactly at threshold: 8 ticks = $10.00 — snap fires.
            Dim u2 = engine.OnQuote(state, lastPrice:=4502.00D, nowUtc:=DateTime.UtcNow,
                                     minSlEditStepTicks:=1, maxSlEditsPerSecond:=5)
            Assert.True(state.HasBreakevenSnapped)
            Assert.True(u2.StopAdvanced)
            Assert.Equal(4500D, state.CurrentStopPrice)
        End Sub

        <Fact>
        Public Sub Long_TrailRatchets_ForwardAndNeverRetracts()
            Dim engine = New QuoteDrivenTrailEngine()
            Dim state = NewMesLongState(entry:=4500D, beSnap:=10D, trail:=10D)
            engine.Initialise(state)
            ' Advance to BE.
            engine.OnQuote(state, 4502.00D, DateTime.UtcNow, 1, 5)
            ' Push to a new peak: 4510.00. Trail = 8 ticks behind = 4508.00.
            Dim u = engine.OnQuote(state, 4510.00D, DateTime.UtcNow, 1, 5)
            Assert.True(u.StopAdvanced)
            Assert.Equal(4508D, state.CurrentStopPrice)

            ' Pullback to 4505 — stop must NOT retreat.
            Dim u2 = engine.OnQuote(state, 4505.00D, DateTime.UtcNow, 1, 5)
            Assert.False(u2.StopAdvanced)
            Assert.Equal(4508D, state.CurrentStopPrice)

            ' New peak 4512: trail to 4510.00. Advances.
            Dim u3 = engine.OnQuote(state, 4512.00D, DateTime.UtcNow, 1, 5)
            Assert.True(u3.StopAdvanced)
            Assert.Equal(4510D, state.CurrentStopPrice)
        End Sub

        <Fact>
        Public Sub Long_PriceCrossingStop_ExitRequestedFires()
            Dim engine = New QuoteDrivenTrailEngine()
            Dim state = NewMesLongState(entry:=4500D)
            engine.Initialise(state)
            ' Push to BE + trail.
            engine.OnQuote(state, 4502D, DateTime.UtcNow, 1, 5)
            engine.OnQuote(state, 4510D, DateTime.UtcNow, 1, 5)
            ' Now drop to current stop.
            Dim sl = state.CurrentStopPrice
            Dim u = engine.OnQuote(state, sl, DateTime.UtcNow, 1, 5)
            Assert.True(u.ExitRequested)
        End Sub

        <Fact>
        Public Sub Throttle_SuppressesEditWhenStepBelowMinTicks()
            Dim engine = New QuoteDrivenTrailEngine()
            Dim state = NewMesLongState(entry:=4500D, beSnap:=10D, trail:=10D)
            engine.Initialise(state)
            engine.OnQuote(state, 4502D, DateTime.UtcNow, 1, 5)
            engine.OnQuote(state, 4510D, DateTime.UtcNow, 1, 5)
            Dim slBefore = state.CurrentStopPrice
            ' Tiny advance: peak ticks up 1 → trail advances 1 tick. Min step = 2 ticks.
            Dim u = engine.OnQuote(state, 4510.25D, DateTime.UtcNow, minSlEditStepTicks:=2, maxSlEditsPerSecond:=5)
            Assert.True(u.StopAdvanced)
            Assert.False(u.BrokerEditShouldFire)
            ' State still advances locally so the next bigger move can clear the step.
            Assert.NotEqual(slBefore, state.CurrentStopPrice)
        End Sub

        <Fact>
        Public Sub Throttle_SuppressesEditWhenRateExceeded()
            Dim engine = New QuoteDrivenTrailEngine()
            Dim state = NewMesLongState(entry:=4500D, beSnap:=10D, trail:=10D)
            engine.Initialise(state)
            engine.OnQuote(state, 4502D, DateTime.UtcNow, 1, 5)
            Dim same = New DateTime(2026, 5, 20, 14, 0, 0, DateTimeKind.Utc)
            ' Burn through max edits this second.
            Dim emitted = 0
            For step_ = 1 To 10
                Dim u = engine.OnQuote(state, 4502D + CDec(step_) * 0.25D, same,
                                        minSlEditStepTicks:=1, maxSlEditsPerSecond:=3)
                If u.BrokerEditShouldFire Then emitted += 1
            Next
            Assert.Equal(3, emitted)
        End Sub

        <Fact>
        Public Sub Short_TrailRatchets_FavorablyDownward()
            ' MES short, entry 4500. trail=$10 → 8 ticks → 2.00 in price.
            ' BE snap at 4498 (price moved -$10 favorable). SL → 4500.
            ' Peak fav at 4490 (much further favorable). Trail SL = peak + 2.00 = 4492.
            ' Bounce to 4491.50 (still below SL): peak stays 4490, SL stays 4492.
            Dim engine = New QuoteDrivenTrailEngine()
            Dim state = NewMesShortState(entry:=4500D)
            engine.Initialise(state)

            engine.OnQuote(state, 4498D, DateTime.UtcNow, 1, 5)
            Assert.True(state.HasBreakevenSnapped)
            Assert.Equal(4500D, state.CurrentStopPrice)

            Dim u2 = engine.OnQuote(state, 4490D, DateTime.UtcNow, 1, 5)
            Assert.True(u2.StopAdvanced)
            Assert.Equal(4492D, state.CurrentStopPrice)

            ' Bounce to a price still below SL: no retreat, no exit.
            Dim u3 = engine.OnQuote(state, 4491.5D, DateTime.UtcNow, 1, 5)
            Assert.False(u3.StopAdvanced)
            Assert.False(u3.ExitRequested)
            Assert.Equal(4492D, state.CurrentStopPrice)
        End Sub

        <Fact>
        Public Sub Short_PriceCrossingStop_ExitRequestedFires()
            Dim engine = New QuoteDrivenTrailEngine()
            Dim state = NewMesShortState(entry:=4500D)
            engine.Initialise(state)
            engine.OnQuote(state, 4498D, DateTime.UtcNow, 1, 5)
            engine.OnQuote(state, 4490D, DateTime.UtcNow, 1, 5)
            Dim sl = state.CurrentStopPrice  ' = 4492
            ' Rally back to SL — short stop trips when price rises to or above SL.
            Dim u = engine.OnQuote(state, sl, DateTime.UtcNow, 1, 5)
            Assert.True(u.ExitRequested)
        End Sub

    End Class

End Namespace
