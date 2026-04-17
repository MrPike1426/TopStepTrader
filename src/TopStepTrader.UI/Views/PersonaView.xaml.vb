Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class PersonaView
        Inherits System.Windows.Controls.UserControl

        Public Sub New(viewModel As PersonaViewModel)
            InitializeComponent()
            DataContext = viewModel
        End Sub

    End Class

End Namespace
