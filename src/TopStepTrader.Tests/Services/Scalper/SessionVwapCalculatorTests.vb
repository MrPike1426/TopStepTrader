Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.Scalper
Imports Xunit

Namespace TopStepTrader.Tests.Services.Scalper

    Public Class SessionVwapCalculatorTests

        Private Shared Function MakeBar(o As Decimal, h As Decimal, l As Decimal, c As Decimal, v As Long) As MarketBar
            Return New MarketBar With {.Open = o, .High = h, .Low = l, .Close = c, .Volume = v}
        End Function

        <Fact>
        Public Sub Empty_Vwap_IsZero()
            Dim vwap = New SessionVwapCalculator()
            Assert.Equal(0D, vwap.Vwap)
            Assert.Equal(0, vwap.BarCount)
        End Sub

        <Fact>
        Public Sub SingleBar_VwapMatchesHlc3()
            ' typical = (102 + 98 + 100) / 3 = 100. vol = 1000. vwap = 100,000 / 1000 = 100.
            Dim vwap = New SessionVwapCalculator()
            vwap.AddBar(MakeBar(99D, 102D, 98D, 100D, 1000))
            Assert.Equal(100D, vwap.Vwap)
            Assert.Equal(1, vwap.BarCount)
        End Sub

        <Fact>
        Public Sub TwoBars_VolumeWeighted()
            ' Bar 1: typical 100, vol 1000  -> tpv 100,000
            ' Bar 2: typical 110, vol 3000  -> tpv 330,000
            ' vwap = 430,000 / 4000 = 107.5
            Dim vwap = New SessionVwapCalculator()
            vwap.AddBar(MakeBar(99D, 102D, 98D, 100D, 1000))
            vwap.AddBar(MakeBar(108D, 112D, 108D, 110D, 3000))
            Assert.Equal(107.5D, vwap.Vwap)
            Assert.Equal(2, vwap.BarCount)
        End Sub

        <Fact>
        Public Sub Reset_ClearsAccumulator()
            Dim vwap = New SessionVwapCalculator()
            vwap.AddBar(MakeBar(99D, 102D, 98D, 100D, 1000))
            vwap.AddBar(MakeBar(108D, 112D, 108D, 110D, 3000))
            vwap.Reset()
            Assert.Equal(0D, vwap.Vwap)
            Assert.Equal(0, vwap.BarCount)
            vwap.AddBar(MakeBar(199D, 202D, 198D, 200D, 500))
            Assert.Equal(200D, vwap.Vwap)
        End Sub

        <Fact>
        Public Sub ZeroVolumeBar_DoesNotShiftVwap()
            ' Pine's ta.vwap treats zero-volume bars as no contribution.
            Dim vwap = New SessionVwapCalculator()
            vwap.AddBar(MakeBar(99D, 102D, 98D, 100D, 1000))
            vwap.AddBar(MakeBar(108D, 112D, 108D, 110D, 0))
            Assert.Equal(100D, vwap.Vwap)
        End Sub

    End Class

End Namespace
