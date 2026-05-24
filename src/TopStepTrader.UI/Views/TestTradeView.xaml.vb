Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class TestTradeView
        Inherits System.Windows.Controls.UserControl

        Private ReadOnly _vm As TestTradeViewModel

        Public Sub New(viewModel As TestTradeViewModel)
            InitializeComponent()
            _vm = viewModel
            DataContext = _vm
        End Sub

    End Class

End Namespace
