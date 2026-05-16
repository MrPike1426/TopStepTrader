Imports System.Windows.Controls
Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class DebugTradeViewerView
        Inherits UserControl

        Private _vm As DebugTradeViewerViewModel

        Public Sub New(vm As DebugTradeViewerViewModel)
            InitializeComponent()
            _vm = vm
            DataContext = vm
            vm.LoadDataAsync()
        End Sub

    End Class

End Namespace

