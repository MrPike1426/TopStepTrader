Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class BacktestView
        Inherits System.Windows.Controls.UserControl

        Public Sub New(viewModel As BacktestViewModel)
            InitializeComponent()
            DataContext = viewModel
            viewModel.LoadDataAsync()
        End Sub

    End Class

End Namespace
