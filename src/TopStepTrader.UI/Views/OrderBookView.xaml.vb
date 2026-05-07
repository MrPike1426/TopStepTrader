Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class OrderBookView
        Inherits System.Windows.Controls.UserControl

        Public Sub New(viewModel As OrderBookViewModel)
            InitializeComponent()
            DataContext = viewModel
        End Sub

        Private Sub OnViewLoaded(sender As Object, e As System.Windows.RoutedEventArgs)
            Dim vm = DirectCast(DataContext, OrderBookViewModel)
            If vm IsNot Nothing Then
                vm.LoadDataAsync()
            End If
        End Sub

    End Class

End Namespace
