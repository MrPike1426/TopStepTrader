Imports System.Collections.Specialized
Imports System.Windows.Controls
Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    ''' <summary>
    ''' Code-behind for the Sniper Trading view.
    ''' Wires DataContext and auto-scrolls the signal log.
    ''' </summary>
    Partial Public Class SniperView
        Inherits UserControl

        Private ReadOnly _vm As SniperViewModel

        Public Sub New(viewModel As SniperViewModel)
            InitializeComponent()
            _vm = viewModel
            DataContext = _vm

            ' Wire log auto-scroll
            AddHandler _vm.LogEntries.CollectionChanged, AddressOf OnLogChanged

            ' Wire tab selection → update IsLiveTabSelected so the bottom bar shows/hides
            AddHandler MainTabControl.SelectionChanged, AddressOf OnTabSelectionChanged

            ' Load accounts
            _vm.LoadDataAsync()
        End Sub

        ''' <summary>
        ''' Scroll the log ListBox to the bottom whenever a new entry is added.
        ''' </summary>
        Private Sub OnLogChanged(sender As Object, e As NotifyCollectionChangedEventArgs)
            If e.Action = NotifyCollectionChangedAction.Add AndAlso LogList.Items.Count > 0 Then
                LogList.ScrollIntoView(LogList.Items(LogList.Items.Count - 1))
            End If
        End Sub

        ''' <summary>
        ''' Keep the bottom control bar visibility in sync with the selected tab.
        ''' IsLiveTabSelected = True shows the bar; False hides it (backtest tab).
        ''' </summary>
        Private Sub OnTabSelectionChanged(sender As Object, e As SelectionChangedEventArgs)
            _vm.IsLiveTabSelected = (MainTabControl.SelectedIndex = 0)
        End Sub

    End Class

End Namespace
