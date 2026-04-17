Imports System.Windows.Controls
Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    ''' <summary>
    ''' Code-behind for QuantLabView.
    ''' All logic lives in QuantLabViewModel; this file only wires the DataContext.
    ''' </summary>
    Partial Public Class QuantLabView
        Inherits UserControl

        Public Sub New(viewModel As QuantLabViewModel)
            InitializeComponent()
            DataContext = viewModel
        End Sub

    End Class

End Namespace
