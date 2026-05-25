Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class BreakAndBounceView
        Inherits System.Windows.Controls.UserControl

        Private ReadOnly _vm As BreakAndBounceViewModel

        Public Sub New(viewModel As BreakAndBounceViewModel)
            InitializeComponent()
            _vm = viewModel
            DataContext = _vm
        End Sub

    End Class

End Namespace
