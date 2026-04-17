Imports System.Collections.Specialized
Imports System.Windows.Controls
Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    ''' <summary>
    ''' Code-behind for the CryptoJoe multi-asset monitoring tab.
    ''' Loads account data via the ViewModel and auto-scrolls the engine log.
    ''' </summary>
    Partial Public Class CryptoJoeView
        Inherits UserControl

        Private ReadOnly _vm As CryptoJoeViewModel

        Public Sub New(viewModel As CryptoJoeViewModel)
            InitializeComponent()
            _vm = viewModel
            DataContext = _vm

            ' Wire log auto-scroll (newest entry at top, console-style)
            AddHandler _vm.LogEntries.CollectionChanged, AddressOf OnLogChanged

            ' Load accounts in the background
            _vm.LoadDataAsync()
        End Sub

        ''' <summary>
        ''' Scroll the log to the top whenever a new entry is inserted at position 0,
        ''' keeping the most-recent line always visible.
        ''' </summary>
        Private Sub OnLogChanged(sender As Object, e As NotifyCollectionChangedEventArgs)
            If e.Action = NotifyCollectionChangedAction.Add AndAlso LogList.Items.Count > 0 Then
                LogList.ScrollIntoView(LogList.Items(0))
            End If
        End Sub

    End Class

End Namespace
