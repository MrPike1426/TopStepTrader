Namespace TopStepTrader.UI

    ''' <summary>
    ''' Application entry point.
    ''' WPF on .NET Core does not auto-generate Sub Main for VB.NET,
    ''' so we provide it explicitly here.
    ''' </summary>
    Module Program

        <System.STAThread>
        Sub Main()
            Dim app As New App()
            app.InitializeComponent()
            app.Run()
        End Sub

    End Module

End Namespace
