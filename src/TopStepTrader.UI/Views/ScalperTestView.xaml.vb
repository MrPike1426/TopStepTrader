Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class ScalperTestView
        Inherits System.Windows.Controls.UserControl

        Public Sub New(viewModel As ScalperTestViewModel)
            InitializeComponent()
            DataContext = viewModel
            Dim _loadTask As Task = viewModel.LoadDataAsync()
        End Sub

    End Class

End Namespace
