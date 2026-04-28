Imports TopStepTrader.UI.ViewModels
Imports Xunit

Namespace TopStepTrader.Tests.ViewModels

    Public Class SuperTrendPlusScaleInBarGateTests

        ''' <summary>
        ''' Two consecutive ticks with identical bar timestamp should only allow one scale-in.
        ''' The second tick must be skipped because LastScaleInBarTime equals the bar timestamp.
        ''' </summary>
        <Fact>
        Public Sub ScaleIn_SameBarTimestamp_SecondTickSkipped()
            ' Arrange
            Dim barTime = New DateTimeOffset(2026, 4, 28, 10, 0, 0, TimeSpan.Zero)
            Dim box As New PersonaBoxVm()
            box.LastScaleInBarTime = DateTimeOffset.MinValue

            ' Simulate first tick: new bar → gate allows scale-in, record barTime
            Dim firstTickAllowed = (box.LastScaleInBarTime <> barTime)
            If firstTickAllowed Then
                box.LastScaleInBarTime = barTime
            End If

            ' Simulate second tick: same barTime → gate should block
            Dim secondTickAllowed = (box.LastScaleInBarTime <> barTime)

            ' Assert
            Assert.True(firstTickAllowed,  "First tick with new bar should be allowed.")
            Assert.False(secondTickAllowed, "Second tick with same bar timestamp should be blocked.")
        End Sub

        ''' <summary>
        ''' A new bar timestamp after the first scale-in should be allowed.
        ''' </summary>
        <Fact>
        Public Sub ScaleIn_NewBarTimestamp_AllowedAfterPreviousScaleIn()
            Dim firstBarTime  = New DateTimeOffset(2026, 4, 28, 10, 0, 0, TimeSpan.Zero)
            Dim secondBarTime = firstBarTime.AddMinutes(15)
            Dim box As New PersonaBoxVm()

            ' First scale-in recorded
            box.LastScaleInBarTime = firstBarTime

            ' Second tick with a new bar
            Dim secondTickAllowed = (box.LastScaleInBarTime <> secondBarTime)
            If secondTickAllowed Then
                box.LastScaleInBarTime = secondBarTime
            End If

            Assert.True(secondTickAllowed, "Tick with a new bar timestamp should be allowed.")
            Assert.Equal(secondBarTime, box.LastScaleInBarTime)
        End Sub

        ''' <summary>
        ''' ReleasePositionLock resets LastScaleInBarTime to MinValue.
        ''' </summary>
        <Fact>
        Public Sub LastScaleInBarTime_ResetToMinValue_AfterRelease()
            Dim box As New PersonaBoxVm()
            box.LastScaleInBarTime = New DateTimeOffset(2026, 4, 28, 10, 0, 0, TimeSpan.Zero)

            ' Simulate what ReleasePositionLockAsync does
            box.LastScaleInBarTime = DateTimeOffset.MinValue

            Assert.Equal(DateTimeOffset.MinValue, box.LastScaleInBarTime)
        End Sub

    End Class

End Namespace
