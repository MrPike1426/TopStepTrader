Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class SignalsView
        Inherits System.Windows.Controls.UserControl

        Public Sub New(viewModel As SignalsViewModel)
            InitializeComponent()
            DataContext = viewModel
            viewModel.LoadDataAsync()
        End Sub

    End Class

End Namespace
