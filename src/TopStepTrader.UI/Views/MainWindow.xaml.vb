Imports System.ComponentModel
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.UI.ViewModels
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI

    Partial Public Class MainWindow
        Inherits Window
        Implements INotifyPropertyChanged

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

        Private Sub NotifyChanged(propertyName As String)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(propertyName))
        End Sub

        Private ReadOnly _viewModelLocator As ViewModelLocator
        Private ReadOnly _session As ITradingSessionContext

        Public Sub New(viewModelLocator As ViewModelLocator, session As ITradingSessionContext)
            InitializeComponent()
            _viewModelLocator = viewModelLocator
            _session = session
            DataContext = Me
            AddHandler _session.AccountChanged, AddressOf OnSessionAccountChanged
            AddHandler Me.Closing, AddressOf OnWindowClosing
            NavigateTo("Dashboard")
        End Sub

        ''' <summary>
        ''' Ensures the SuperTrend+ VM is cleanly stopped before the process exits so that no
        ''' in-flight tick can place an order after the window is closed (e.g. the M2K ghost-order bug).
        ''' </summary>
        Private Sub OnWindowClosing(sender As Object, e As CancelEventArgs)
            Try
                Dim stView = _viewModelLocator.SuperTrendPlusView
                If stView IsNot Nothing Then
                    Dim vm = TryCast(stView.DataContext, SuperTrendPlusViewModel)
                    If vm IsNot Nothing AndAlso vm.IsMonitoring Then
                        vm.StopMonitoring()
                    End If
                End If
            Catch
                ' Never block the close — just swallow any error
            End Try
        End Sub

        Private Sub OnSessionAccountChanged(sender As Object, account As Account)
            ' TopStepX is the only broker — no broker-gate properties to refresh.
        End Sub

        Public ReadOnly Property ConnectionStatus As String
            Get
                Return "● Connected"
            End Get
        End Property

        Public ReadOnly Property ConnectionStatusBrush As SolidColorBrush
            Get
                Return New SolidColorBrush(Color.FromRgb(39, 174, 96))
            End Get
        End Property

        Private Sub NavButton_Click(sender As Object, e As RoutedEventArgs)
            Dim btn = CType(sender, Button)
            NavigateTo(btn.Tag.ToString())
        End Sub

        Private Sub NavigateTo(section As String)
            Select Case section
                Case "Dashboard"
                    MainContent.Content = _viewModelLocator.DashboardView
                Case "Orders"
                    MainContent.Content = _viewModelLocator.OrderBookView
                Case "TestTrade"
                    MainContent.Content = _viewModelLocator.TestTradeView
                Case "ScalperTest"
                    MainContent.Content = _viewModelLocator.ScalperTestView
                Case "PriceTracker"
                    MainContent.Content = _viewModelLocator.PriceTrackerView

                Case "Backtest"
                    MainContent.Content = _viewModelLocator.BacktestView
                Case "Sniper"
                    MainContent.Content = _viewModelLocator.SniperView
                Case "PumpNDump"
                    MainContent.Content = _viewModelLocator.PumpNDumpView
                Case "SuperTrendPlus"
                    MainContent.Content = _viewModelLocator.SuperTrendPlusView
                Case "Hydra"
                    NavigateToTradingView(_viewModelLocator.HydraView)
                Case "AssetBassett"
                    NavigateToTradingView(_viewModelLocator.AssetBassettView)
                Case "CryptoJoe"
                    NavigateToTradingView(_viewModelLocator.CryptoJoeView)
                Case "Settings"
                    MainContent.Content = _viewModelLocator.SettingsView
                Case "ProTrader"
                    MainContent.Content = _viewModelLocator.ProTraderView
                Case "DebugTradeViewer"
                    MainContent.Content = _viewModelLocator.DebugTradeViewerView
                Case "Persona"
                    MainContent.Content = _viewModelLocator.PersonaView
                Case "ApiKeys"
                    MainContent.Content = _viewModelLocator.ApiKeysView
            End Select
        End Sub

        Private Sub NavigateToTradingView(view As Object)
            ' ITradingSessionContext propagates the account selection automatically;
            ' no manual push needed.
            MainContent.Content = view
        End Sub

    End Class

End Namespace
