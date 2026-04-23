Imports System.Windows
Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class BarDownloadProgressWindow
        Inherits Window

        Public Sub New(viewModel As BarDownloadProgressViewModel)
            InitializeComponent()
            DataContext = viewModel
        End Sub

    End Class

End Namespace
