Imports TopStepTrader.UI.ViewModels
Imports Xunit

Namespace TopStepTrader.Tests.ViewModels

    ''' <summary>
    ''' STRAT-35: Verifies that the strongest-ADX candidate is selected over
    ''' a first-past-the-post (Lewis-first) candidate, and that the Joe-priority
    ''' tiebreaker is applied when ADX values are equal.
    ''' </summary>
    Public Class SuperTrendPlusBestSignalTests

        ' Helper that replicates the selection logic from EvaluatePersonasAsync
        Private Shared Function SelectBest(
            candidates As List(Of Tuple(Of PersonaBoxVm, String, String, Decimal, Single)),
            allBoxes As PersonaBoxVm()
        ) As Tuple(Of PersonaBoxVm, String, String, Decimal, Single)

            Dim priorityMap As New Dictionary(Of PersonaBoxVm, Integer)
            For idx = 0 To allBoxes.Length - 1
                priorityMap(allBoxes(idx)) = idx
            Next
            Return candidates _
                .OrderByDescending(Function(c) c.Item5) _
                .ThenByDescending(Function(c) If(priorityMap.ContainsKey(c.Item1), priorityMap(c.Item1), 0)) _
                .First()
        End Function

        ''' <summary>
        ''' When two signals qualify, the one with the higher ADX must be selected
        ''' regardless of persona evaluation order.
        ''' </summary>
        <Fact>
        Public Sub SelectBest_HigherAdxWins_OverFirstEvaluated()
            Dim lewisBox  As New PersonaBoxVm() With {.PersonaName = "Lewis"}
            Dim damianBox As New PersonaBoxVm() With {.PersonaName = "Damian"}
            Dim joeBox    As New PersonaBoxVm() With {.PersonaName = "Joe"}

            Dim allBoxes As PersonaBoxVm() = {lewisBox, damianBox, joeBox}

            ' Lewis (evaluated first) has weak ADX=21; Joe has strong ADX=48
            Dim candidates As New List(Of Tuple(Of PersonaBoxVm, String, String, Decimal, Single)) From {
                Tuple.Create(lewisBox, "MES", "Buy", 4520D, 21.0F),
                Tuple.Create(joeBox,   "MGC", "Buy", 2310D, 48.0F)
            }

            Dim best = SelectBest(candidates, allBoxes)

            Assert.Equal(joeBox, best.Item1)
            Assert.Equal("MGC", best.Item2)
            Assert.Equal(48.0F, best.Item5)
        End Sub

        ''' <summary>
        ''' When two signals have the same ADX, Joe (highest priority) wins over Lewis.
        ''' </summary>
        <Fact>
        Public Sub SelectBest_TiedAdx_JoeBeatsLewis()
            Dim lewisBox  As New PersonaBoxVm() With {.PersonaName = "Lewis"}
            Dim damianBox As New PersonaBoxVm() With {.PersonaName = "Damian"}
            Dim joeBox    As New PersonaBoxVm() With {.PersonaName = "Joe"}

            Dim allBoxes As PersonaBoxVm() = {lewisBox, damianBox, joeBox}

            Dim candidates As New List(Of Tuple(Of PersonaBoxVm, String, String, Decimal, Single)) From {
                Tuple.Create(lewisBox, "MES", "Buy", 4520D, 35.0F),
                Tuple.Create(joeBox,   "MGC", "Buy", 2310D, 35.0F)
            }

            Dim best = SelectBest(candidates, allBoxes)

            Assert.Equal(joeBox, best.Item1)
        End Sub

        ''' <summary>
        ''' When only one signal qualifies, first-past-the-post behaviour is preserved.
        ''' </summary>
        <Fact>
        Public Sub SelectBest_SingleCandidate_ReturnsThatCandidate()
            Dim lewisBox  As New PersonaBoxVm() With {.PersonaName = "Lewis"}
            Dim damianBox As New PersonaBoxVm() With {.PersonaName = "Damian"}
            Dim joeBox    As New PersonaBoxVm() With {.PersonaName = "Joe"}

            Dim allBoxes As PersonaBoxVm() = {lewisBox, damianBox, joeBox}

            Dim candidates As New List(Of Tuple(Of PersonaBoxVm, String, String, Decimal, Single)) From {
                Tuple.Create(lewisBox, "MES", "Sell", 4480D, 27.0F)
            }

            Dim best = SelectBest(candidates, allBoxes)

            Assert.Equal(lewisBox, best.Item1)
            Assert.Equal("MES", best.Item2)
        End Sub

    End Class

End Namespace
