Imports System.Windows.Controls
Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    ''' <summary>
    ''' Code-behind for the Asset Bassett single-asset, multi-strategy tab.
    ''' Loads account data via the ViewModel on construction.
    ''' </summary>
    Partial Public Class AssetBassettView
        Inherits UserControl

        Private ReadOnly _vm As AssetBassettViewModel

        Public Sub New(viewModel As AssetBassettViewModel)
            InitializeComponent()
            _vm = viewModel
            DataContext = _vm
            _vm.LoadDataAsync()
        End Sub

    End Class

End Namespace
