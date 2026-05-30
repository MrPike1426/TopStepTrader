Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class TestTradeView
        Inherits System.Windows.Controls.UserControl

        Private ReadOnly _vm As TestTradeViewModel

        Public Sub New(viewModel As TestTradeViewModel)
            InitializeComponent()
            _vm = viewModel
            DataContext = _vm
            ' OBS-07 F2: only poll while the tab is visible — the cached ViewModelLocator
            ' keeps the VM alive across navigation, so we toggle on Loaded/Unloaded.
            AddHandler Loaded, AddressOf OnLoaded
            AddHandler Unloaded, AddressOf OnUnloaded
        End Sub

        Private Sub OnLoaded(sender As Object, e As System.Windows.RoutedEventArgs)
            _vm.SnapshotHealth?.Start()
        End Sub

        Private Sub OnUnloaded(sender As Object, e As System.Windows.RoutedEventArgs)
            _vm.SnapshotHealth?.Stop()
        End Sub

    End Class

End Namespace
