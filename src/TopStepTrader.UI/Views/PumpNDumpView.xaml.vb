Imports System.Collections.Specialized
Imports System.Windows.Controls
Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class PumpNDumpView
        Inherits UserControl

        Private ReadOnly _vm As PumpNDumpViewModel

        Public Sub New(vm As PumpNDumpViewModel)
            InitializeComponent()
            _vm = vm
            DataContext = _vm

            AddHandler _vm.LogEntries.CollectionChanged, AddressOf OnLogChanged

            _vm.LoadDataAsync()
        End Sub

        Private Sub OnLogChanged(sender As Object, e As NotifyCollectionChangedEventArgs)
            ' UX-05: No auto-scroll needed — newest entries at top are always visible
        End Sub

    End Class

End Namespace
