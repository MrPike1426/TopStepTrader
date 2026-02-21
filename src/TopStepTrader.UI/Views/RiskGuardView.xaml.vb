Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class RiskGuardView
        Inherits System.Windows.Controls.UserControl

        Public Sub New(viewModel As RiskGuardViewModel)
            InitializeComponent()
            DataContext = viewModel
            viewModel.LoadDataAsync()
        End Sub

    End Class

End Namespace
