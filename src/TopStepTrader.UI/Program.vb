Namespace TopStepTrader.UI

    ''' <summary>
    ''' Application entry point.
    ''' WPF on .NET Core does not auto-generate Sub Main for VB.NET,
    ''' so we provide it explicitly here.
    ''' </summary>
    Module Program

        <System.STAThread>
        Sub Main(args As String())
            Dim app As New App() With {.StartupArgs = args}
            app.InitializeComponent()
            app.Run()
        End Sub

    End Module

End Namespace
