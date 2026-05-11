Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class PriceTrackerView
        Inherits System.Windows.Controls.UserControl

        Public Sub New(viewModel As PriceTrackerViewModel)
            InitializeComponent()
            DataContext = viewModel
            viewModel.LoadAsync()
        End Sub

    End Class

End Namespace
