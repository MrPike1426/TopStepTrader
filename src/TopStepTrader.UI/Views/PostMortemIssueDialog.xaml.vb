Imports System.Windows

Namespace TopStepTrader.UI.Views

    Partial Public Class PostMortemIssueDialog
        Inherits Window

        Public Property IssueText As String = String.Empty

        Public Sub New()
            InitializeComponent()
        End Sub

        Private Sub OnOk(sender As Object, e As RoutedEventArgs)
            IssueText = If(IssueTextBox.Text, String.Empty).Trim()
            DialogResult = True
            Close()
        End Sub

        Private Sub OnCancel(sender As Object, e As RoutedEventArgs)
            DialogResult = False
            Close()
        End Sub

    End Class

End Namespace
