Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Shapes
Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class DebugTradeViewerView
        Inherits UserControl

        Private _vm As DebugTradeViewerViewModel

        Public Sub New(vm As DebugTradeViewerViewModel)
            InitializeComponent()
            _vm = vm
            DataContext = vm
            AddHandler vm.PropertyChanged, AddressOf OnVmPropertyChanged
            vm.LoadDataAsync()
        End Sub

        Private Sub OnVmPropertyChanged(sender As Object, e As System.ComponentModel.PropertyChangedEventArgs)
            If e.PropertyName = NameOf(DebugTradeViewerViewModel.ChartPoints) OrElse
               e.PropertyName = NameOf(DebugTradeViewerViewModel.SelectedTrade) Then
                Dispatcher.InvokeAsync(AddressOf DrawChart)
            End If
        End Sub

        Private Sub ChartCanvas_SizeChanged(sender As Object, e As System.Windows.SizeChangedEventArgs)
            DrawChart()
        End Sub

        Private Sub DrawChart()
            PriceChartCanvas.Children.Clear()

            If _vm Is Nothing OrElse _vm.ChartPoints Is Nothing OrElse _vm.ChartPoints.Count < 2 Then Return

            Dim pts = _vm.ChartPoints
            Dim w = PriceChartCanvas.ActualWidth
            Dim h = PriceChartCanvas.ActualHeight
            If w <= 0 OrElse h <= 0 Then Return

            Dim pad = 10.0
            Dim chartW = w - 2 * pad
            Dim chartH = h - 2 * pad

            ' Compute min/max across all three series
            Dim allValues As New List(Of Double)()
            For Each p In pts
                If p.LastPrice > 0 Then allValues.Add(p.LastPrice)
                If p.CurrentSL > 0 Then allValues.Add(p.CurrentSL)
                If p.SuperTrendValue > 0 Then allValues.Add(p.SuperTrendValue)
            Next
            If allValues.Count = 0 Then Return

            Dim minY = allValues.Min()
            Dim maxY = allValues.Max()
            Dim rangeY = maxY - minY
            If rangeY = 0 Then rangeY = 1

            Dim minX = pts.Min(Function(p) p.Timestamp.Ticks)
            Dim maxX = pts.Max(Function(p) p.Timestamp.Ticks)
            Dim rangeX = maxX - minX
            If rangeX = 0 Then rangeX = 1

            Dim toCanvasX = Function(t As Long) pad + (t - minX) / rangeX * chartW
            Dim toCanvasY = Function(v As Double) pad + (1 - (v - minY) / rangeY) * chartH

            ' Draw each series as a Polyline
            DrawPolyline(pts,
                         Function(p) p.LastPrice,
                         toCanvasX, toCanvasY,
                         New SolidColorBrush(Color.FromRgb(46, 204, 113)), 1.5)

            DrawPolyline(pts,
                         Function(p) p.CurrentSL,
                         toCanvasX, toCanvasY,
                         New SolidColorBrush(Color.FromRgb(231, 76, 60)), 1.2)

            DrawPolyline(pts,
                         Function(p) p.SuperTrendValue,
                         toCanvasX, toCanvasY,
                         New SolidColorBrush(Color.FromRgb(243, 156, 18)), 1.2)

            ' Draw event markers
            For Each p In pts.Where(Function(pt) pt.IsMarker)
                Dim cx = toCanvasX(p.Timestamp.Ticks)
                Dim cy = toCanvasY(p.LastPrice)
                Dim dot As New Ellipse With {
                    .Width = 7,
                    .Height = 7,
                    .Fill = New SolidColorBrush(Colors.White),
                    .Stroke = New SolidColorBrush(Colors.Gray),
                    .StrokeThickness = 1
                }
                Canvas.SetLeft(dot, cx - 3.5)
                Canvas.SetTop(dot, cy - 3.5)
                PriceChartCanvas.Children.Add(dot)

                Dim lbl As New TextBlock With {
                    .Text = p.MarkerLabel,
                    .FontSize = 9,
                    .Foreground = New SolidColorBrush(Colors.LightGray)
                }
                Canvas.SetLeft(lbl, cx + 4)
                Canvas.SetTop(lbl, cy - 10)
                PriceChartCanvas.Children.Add(lbl)
            Next
        End Sub

        Private Sub DrawPolyline(
                pts As IEnumerable(Of ChartPoint),
                getValue As Func(Of ChartPoint, Double),
                toX As Func(Of Long, Double),
                toY As Func(Of Double, Double),
                stroke As Brush,
                thickness As Double)

            Dim line As New Polyline With {
                .Stroke = stroke,
                .StrokeThickness = thickness,
                .StrokeLineJoin = PenLineJoin.Round
            }
            For Each p In pts
                Dim v = getValue(p)
                If v = 0 Then Continue For
                line.Points.Add(New System.Windows.Point(toX(p.Timestamp.Ticks), toY(v)))
            Next
            If line.Points.Count > 0 Then
                PriceChartCanvas.Children.Add(line)
            End If
        End Sub

    End Class

End Namespace
