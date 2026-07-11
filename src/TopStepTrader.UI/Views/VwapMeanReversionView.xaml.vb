Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class VwapMeanReversionView
        Inherits System.Windows.Controls.UserControl

        Private ReadOnly _vm As VwapMeanReversionViewModel

        Public Sub New(viewModel As VwapMeanReversionViewModel)
            InitializeComponent()
            _vm = viewModel
            DataContext = _vm
        End Sub

    End Class

End Namespace
