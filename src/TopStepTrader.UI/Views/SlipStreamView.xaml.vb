Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class SlipStreamView
        Inherits System.Windows.Controls.UserControl

        Private ReadOnly _vm As SlipStreamViewModel

        Public Sub New(viewModel As SlipStreamViewModel)
            InitializeComponent()
            _vm = viewModel
            DataContext = _vm
        End Sub

    End Class

End Namespace
