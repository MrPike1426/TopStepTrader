Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class DashboardView
        Inherits System.Windows.Controls.UserControl

        Private ReadOnly _vm As DashboardViewModel

        Public Sub New(viewModel As DashboardViewModel)
            InitializeComponent()
            _vm = viewModel
            DataContext = _vm
            _vm.LoadDataAsync()
        End Sub

    End Class

End Namespace
