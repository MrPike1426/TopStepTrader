Imports System.Windows
Imports TopStepTrader.UI.ViewModels

Namespace TopStepTrader.UI.Views

    Partial Public Class TradeDetailWindow
        Inherits Window

        Public Sub New()
            InitializeComponent()
            AddHandler Loaded, AddressOf OnLoaded
        End Sub

        Private Sub OnLoaded(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, TradeDetailViewModel)
            If vm Is Nothing Then Return
            AddHandler vm.RequestClose, Sub(s, args) Me.Close()
            AddHandler vm.RequestIssueText, AddressOf OnRequestIssueText
        End Sub

        Private Sub OnRequestIssueText(sender As Object, req As IssueTextRequest)
            Dim dlg As New PostMortemIssueDialog() With {.Owner = Me}
            Dim ok = dlg.ShowDialog()
            If ok = True Then
                req.IssueText = dlg.IssueText
                req.Cancelled = False
            Else
                req.Cancelled = True
            End If
        End Sub

    End Class

End Namespace
