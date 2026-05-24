Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class UltimateScalperView
        Inherits System.Windows.Controls.UserControl

        Private ReadOnly _vm As UltimateScalperViewModel

        Public Sub New(viewModel As UltimateScalperViewModel)
            InitializeComponent()
            _vm = viewModel
            DataContext = _vm
        End Sub

    End Class

End Namespace
