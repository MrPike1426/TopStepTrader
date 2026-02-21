Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class MarketDataView
        Inherits System.Windows.Controls.UserControl

        Public Sub New(viewModel As MarketDataViewModel)
            InitializeComponent()
            DataContext = viewModel
        End Sub

    End Class

End Namespace
