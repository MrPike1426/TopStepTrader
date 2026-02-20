Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI

    Partial Public Class MainWindow
        Inherits Window

        Private ReadOnly _viewModelLocator As ViewModelLocator

        Public Sub New(viewModelLocator As ViewModelLocator)
            InitializeComponent()
            _viewModelLocator = viewModelLocator
            DataContext = Me
            NavigateTo("Dashboard")
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
                Case "Market"
                    MainContent.Content = _viewModelLocator.MarketDataView
                Case "Signals"
                    MainContent.Content = _viewModelLocator.SignalsView
                Case "Orders"
                    MainContent.Content = _viewModelLocator.OrderBookView
                Case "Risk"
                    MainContent.Content = _viewModelLocator.RiskGuardView
                Case "Backtest"
                    MainContent.Content = _viewModelLocator.BacktestView
                Case "Settings"
                    MainContent.Content = _viewModelLocator.SettingsView
            End Select
        End Sub

    End Class

End Namespace
