Imports System.Windows.Controls
Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    ''' <summary>
    ''' Code-behind for the Pro-Trader view (skeleton).
    ''' </summary>
    Partial Public Class ProTraderView
        Inherits UserControl

        Private ReadOnly _vm As ProTraderViewModel

        Public Sub New(viewModel As ProTraderViewModel)
            InitializeComponent()
            _vm = viewModel
            DataContext = _vm
        End Sub

    End Class

End Namespace
