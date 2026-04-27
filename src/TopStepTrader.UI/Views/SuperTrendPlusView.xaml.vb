Imports System.Windows.Controls
Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class SuperTrendPlusView
        Inherits UserControl

        Public Sub New(vm As SuperTrendPlusViewModel)
            InitializeComponent()
            DataContext = vm
        End Sub

    End Class

End Namespace
