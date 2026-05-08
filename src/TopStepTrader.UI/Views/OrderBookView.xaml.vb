Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class OrderBookView
        Inherits System.Windows.Controls.UserControl

        Public Sub New(viewModel As OrderBookViewModel)
            InitializeComponent()
            DataContext = viewModel
        End Sub

        Private Sub OnViewLoaded(sender As Object, e As System.Windows.RoutedEventArgs)
            ' BUG-66: VM debounces; just delegate.
            Try
                Dim vm = TryCast(DataContext, OrderBookViewModel)
                If vm IsNot Nothing Then vm.LoadDataAsync()
            Catch
                ' Loaded handlers must never throw into WPF.
            End Try
        End Sub

    End Class

End Namespace
