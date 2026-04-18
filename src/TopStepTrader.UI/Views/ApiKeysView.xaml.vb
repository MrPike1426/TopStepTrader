Imports System.ComponentModel
Imports System.Windows
Imports System.Windows.Controls
Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class ApiKeysView
        Inherits UserControl

        Private ReadOnly _vm As ApiKeysViewModel

        Public Sub New(viewModel As ApiKeysViewModel)
            InitializeComponent()
            _vm = viewModel
            DataContext = viewModel
            AddHandler _vm.PropertyChanged, AddressOf Vm_PropertyChanged
            AddHandler Loaded, AddressOf OnLoaded
        End Sub

        Private Sub OnLoaded(sender As Object, e As RoutedEventArgs)
            SyncPasswordBoxes()
        End Sub

        ''' <summary>
        ''' When the user hides keys after editing the visible TextBoxes, push the
        ''' current VM values back into the PasswordBoxes so they stay in sync.
        ''' </summary>
        Private Sub Vm_PropertyChanged(sender As Object, e As PropertyChangedEventArgs)
            If e.PropertyName = NameOf(ApiKeysViewModel.ShowKeys) AndAlso Not _vm.ShowKeys Then
                SyncPasswordBoxes()
            End If
        End Sub

        ''' <summary>Push all VM key values into their corresponding PasswordBoxes.</summary>
        Private Sub SyncPasswordBoxes()
            pb_TopStepX.Password = _vm.TopStepXApiKey
            pb_Claude.Password = _vm.ClaudeApiKey
            pb_Binance.Password = _vm.BinanceApiKey
            pb_BinanceSecret.Password = _vm.BinanceSecretKey
            pb_Future1.Password = _vm.Future1Key
            pb_Future2.Password = _vm.Future2Key
            pb_Future3.Password = _vm.Future3Key
            pb_Future4.Password = _vm.Future4Key
        End Sub

        ' ── PasswordChanged handlers — push typed value into ViewModel ────────────

        Private Sub pb_TopStepX_PasswordChanged
            _vm.TopStepXApiKey = pb_TopStepX.Password
        End Sub

        Private Sub pb_Claude_PasswordChanged(s As Object, e As RoutedEventArgs)
            _vm.ClaudeApiKey = pb_Claude.Password
        End Sub

        Private Sub pb_Binance_PasswordChanged(s As Object, e As RoutedEventArgs)
            _vm.BinanceApiKey = pb_Binance.Password
        End Sub

        Private Sub pb_BinanceSecret_PasswordChanged(s As Object, e As RoutedEventArgs)
            _vm.BinanceSecretKey = pb_BinanceSecret.Password
        End Sub

        Private Sub pb_Future1_PasswordChanged(s As Object, e As RoutedEventArgs)
            _vm.Future1Key = pb_Future1.Password
        End Sub

        Private Sub pb_Future2_PasswordChanged(s As Object, e As RoutedEventArgs)
            _vm.Future2Key = pb_Future2.Password
        End Sub

        Private Sub pb_Future3_PasswordChanged(s As Object, e As RoutedEventArgs)
            _vm.Future3Key = pb_Future3.Password
        End Sub

        Private Sub pb_Future4_PasswordChanged(s As Object, e As RoutedEventArgs)
            _vm.Future4Key = pb_Future4.Password
        End Sub

    End Class

End Namespace
