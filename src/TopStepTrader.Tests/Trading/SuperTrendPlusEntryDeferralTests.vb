Imports System.Collections.Concurrent
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>STRAT-40 F5-c — exercises the deferred-candidate queue state machine that
    ''' <see cref="UI.ViewModels.SuperTrendPlusViewModel.EvaluateSlotEntriesAsync"/> drives.
    ''' The VM itself has heavy DI (SignalR hubs, EF DbContexts, broker services) so this
    ''' integration scaffold runs the F3 queue + F2 primitive directly:
    '''
    '''   Tick 1 — fresh strategy-TF flip + lower-TF disagrees → candidate enqueued
    '''   Tick 2 — lower-TF still disagrees → still enqueued (same queue entry)
    '''   Tick 3 — lower-TF flips into agreement → entry promoted, queue empty
    ''' </summary>
    Public Class SuperTrendPlusEntryDeferralTests

        Private Const ContractId As String = "MNQ"
        Private Const MaxDeferAgeMinutes As Integer = 10

        ''' <summary>Mirrors the queue entry record on the VM.</summary>
        Private Class DeferredCandidate
            Public Property ContractId As String
            Public Property Side As String
            Public Property FirstDeferredUtc As DateTimeOffset
            Public Property AdxAtSignal As Single
        End Class

        Private Shared Function MakeUptrendLowerTfBars() As IList(Of MarketBar)
            Dim bars As New List(Of MarketBar)
            For i = 0 To 29
                Dim close = 100.0F + 0.5F * i
                bars.Add(New MarketBar With {
                    .Timestamp = New DateTimeOffset(2026, 5, 20, 0, 0, 0, TimeSpan.Zero).AddMinutes(3 * i),
                    .Open = close - 0.05F, .High = close + 0.10F, .Low = close - 0.10F,
                    .Close = close, .Volume = 1000
                })
            Next
            Return bars
        End Function

        Private Shared Function MakeDowntrendLowerTfBars() As IList(Of MarketBar)
            Dim bars As New List(Of MarketBar)
            For i = 0 To 29
                Dim close = 200.0F - 0.5F * i
                bars.Add(New MarketBar With {
                    .Timestamp = New DateTimeOffset(2026, 5, 20, 0, 0, 0, TimeSpan.Zero).AddMinutes(3 * i),
                    .Open = close + 0.05F, .High = close + 0.10F, .Low = close - 0.10F,
                    .Close = close, .Volume = 1000
                })
            Next
            Return bars
        End Function

        ''' <summary>Simulates one tick of the VM's F4 path: given the current lower-TF
        ''' bars, mutate the queue per the F3 rules and return the side that should fire.
        ''' Returns Nothing when no entry fires this tick (deferred or no signal).</summary>
        Private Shared Function ProcessTick(queue As ConcurrentDictionary(Of String, DeferredCandidate),
                                              freshIsLong As Boolean,
                                              freshIsShort As Boolean,
                                              freshAdx As Single,
                                              lowerTfBars As IList(Of MarketBar),
                                              nowUtc As DateTimeOffset) As String

            ' Step A — re-evaluate any deferred candidate first (the VM top-of-loop check).
            Dim deferred As DeferredCandidate = Nothing
            If queue.TryGetValue(ContractId, deferred) Then
                Dim isLongDeferred = String.Equals(deferred.Side, "Buy", StringComparison.OrdinalIgnoreCase)
                Dim dirMatches As Boolean =
                    (isLongDeferred AndAlso freshIsLong) OrElse
                    (Not isLongDeferred AndAlso freshIsShort)
                Dim age = (nowUtc - deferred.FirstDeferredUtc).TotalMinutes

                If Not dirMatches Then
                    Dim dropped As DeferredCandidate = Nothing
                    queue.TryRemove(ContractId, dropped)
                ElseIf age > MaxDeferAgeMinutes Then
                    Dim dropped As DeferredCandidate = Nothing
                    queue.TryRemove(ContractId, dropped)
                Else
                    Dim ltRes = EntryQualityGate.EvaluateLowerTfAgreement(
                        lowerTfBars, isLongDeferred, stPeriod:=10, stMultiplier:=3.0R)
                    If Not ltRes.IsBlocked Then
                        Dim dropped As DeferredCandidate = Nothing
                        queue.TryRemove(ContractId, dropped)
                        Return deferred.Side
                    Else
                        ' Still deferred — skip fresh evaluation this tick.
                        Return Nothing
                    End If
                End If
            End If

            ' Step B — fresh evaluation (matching the VM's F4 order).
            If Not (freshIsLong OrElse freshIsShort) Then Return Nothing
            Dim freshSide As String = If(freshIsLong, "Buy", "Sell")
            Dim freshRes = EntryQualityGate.EvaluateLowerTfAgreement(
                lowerTfBars, freshIsLong, stPeriod:=10, stMultiplier:=3.0R)
            If Not freshRes.IsBlocked Then
                Return freshSide
            End If

            ' Disagrees — enqueue (overwrites any earlier entry deliberately).
            queue(ContractId) = New DeferredCandidate With {
                .ContractId = ContractId,
                .Side = freshSide,
                .FirstDeferredUtc = nowUtc,
                .AdxAtSignal = freshAdx
            }
            Return Nothing
        End Function

        <Fact>
        Public Sub F5c_DisagreeThenAgree_DefersThenPromotes()
            Dim queue As New ConcurrentDictionary(Of String, DeferredCandidate)(StringComparer.OrdinalIgnoreCase)
            Dim now0 = New DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero)

            ' Tick 1: fresh LONG flip, lower-TF is downtrend → expected enqueue, no entry.
            Dim t1Fire = ProcessTick(queue, freshIsLong:=True, freshIsShort:=False, freshAdx:=35.0F,
                                       lowerTfBars:=MakeDowntrendLowerTfBars(), nowUtc:=now0)
            Assert.Null(t1Fire)
            Assert.Single(queue)
            Assert.Equal("Buy", queue(ContractId).Side)
            Dim firstDeferredUtc = queue(ContractId).FirstDeferredUtc

            ' Tick 2: same strategy-TF direction, lower-TF still downtrend → stays deferred.
            Dim t2Fire = ProcessTick(queue, freshIsLong:=True, freshIsShort:=False, freshAdx:=36.0F,
                                       lowerTfBars:=MakeDowntrendLowerTfBars(), nowUtc:=now0.AddSeconds(15))
            Assert.Null(t2Fire)
            Assert.Single(queue)
            ' The queue entry's first-deferred timestamp must NOT advance on a re-defer
            ' (the age cap is measured from the original signal time, not the latest tick).
            Assert.Equal(firstDeferredUtc, queue(ContractId).FirstDeferredUtc)

            ' Tick 3: lower TF now agrees (uptrend) → entry fires, queue empties.
            Dim t3Fire = ProcessTick(queue, freshIsLong:=True, freshIsShort:=False, freshAdx:=36.0F,
                                       lowerTfBars:=MakeUptrendLowerTfBars(), nowUtc:=now0.AddSeconds(30))
            Assert.Equal("Buy", t3Fire)
            Assert.Empty(queue)
        End Sub

        <Fact>
        Public Sub F5c_StrategyDirectionEvaporates_DropsDeferred()
            Dim queue As New ConcurrentDictionary(Of String, DeferredCandidate)(StringComparer.OrdinalIgnoreCase)
            Dim now0 = New DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero)

            ' Tick 1: fresh LONG candidate deferred.
            Dim t1Fire = ProcessTick(queue, freshIsLong:=True, freshIsShort:=False, freshAdx:=35.0F,
                                       lowerTfBars:=MakeDowntrendLowerTfBars(), nowUtc:=now0)
            Assert.Null(t1Fire)
            Assert.Single(queue)

            ' Tick 2: strategy-TF has flipped to SHORT (LONG signal evaporated). Lower-TF
            ' is downtrend, which AGREES with SHORT — so a fresh SHORT entry fires AND the
            ' stale LONG queue entry is discarded.
            Dim t2Fire = ProcessTick(queue, freshIsLong:=False, freshIsShort:=True, freshAdx:=35.0F,
                                       lowerTfBars:=MakeDowntrendLowerTfBars(), nowUtc:=now0.AddSeconds(15))
            Assert.Equal("Sell", t2Fire)
            Assert.Empty(queue)
        End Sub

        <Fact>
        Public Sub F5c_DeferredAgesOut_Discarded()
            Dim queue As New ConcurrentDictionary(Of String, DeferredCandidate)(StringComparer.OrdinalIgnoreCase)
            Dim now0 = New DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero)

            Dim t1Fire = ProcessTick(queue, freshIsLong:=True, freshIsShort:=False, freshAdx:=35.0F,
                                       lowerTfBars:=MakeDowntrendLowerTfBars(), nowUtc:=now0)
            Assert.Null(t1Fire)
            Assert.Single(queue)

            ' Tick after the max-age cap. Lower-TF still disagrees, so on its own the
            ' candidate would stay deferred — but the age check fires first and drops it.
            ' The fresh-signal path then re-evaluates and re-defers a brand-new candidate
            ' (semantically a "new attempt" rather than the original aged-out signal).
            Dim afterCap = now0.AddMinutes(MaxDeferAgeMinutes + 1)
            Dim t2Fire = ProcessTick(queue, freshIsLong:=True, freshIsShort:=False, freshAdx:=35.0F,
                                       lowerTfBars:=MakeDowntrendLowerTfBars(), nowUtc:=afterCap)
            Assert.Null(t2Fire)
            ' Queue has been re-populated with a *new* entry whose FirstDeferredUtc is afterCap,
            ' not the original now0 — proving the aged-out entry was discarded.
            Assert.Single(queue)
            Assert.Equal(afterCap, queue(ContractId).FirstDeferredUtc)
        End Sub

    End Class

End Namespace
