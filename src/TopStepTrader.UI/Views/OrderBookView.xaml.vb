Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class OrderBookView
        Inherits System.Windows.Controls.UserControl

        Public Sub New(viewModel As OrderBookViewModel)
            InitializeComponent()
            DataContext = viewModel
            viewModel.LoadDataAsync()
        End Sub

    End Class

End Namespace
