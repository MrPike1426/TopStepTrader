Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class TestTradeView
        Inherits System.Windows.Controls.UserControl

        Public Sub New(viewModel As TestTradeViewModel)
            InitializeComponent()
            DataContext = viewModel
            Dim loadTask As Task = viewModel.LoadDataAsync()
        End Sub

    End Class

End Namespace
