Imports System.Windows.Controls
Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    ''' <summary>
    ''' Code-behind for the Hydra multi-asset monitoring tab.
    ''' Loads account data via the ViewModel.
    ''' </summary>
    Partial Public Class HydraView
        Inherits UserControl

        Private ReadOnly _vm As HydraViewModel

        Public Sub New(viewModel As HydraViewModel)
            InitializeComponent()
            _vm = viewModel
            DataContext = _vm
            _vm.LoadDataAsync()
        End Sub

    End Class

End Namespace
