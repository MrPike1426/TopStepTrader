Imports System.IO
Imports System.Text.Json
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Settings

Namespace TopStepTrader.Services

    ''' <summary>
    ''' Persists API keys to %LOCALAPPDATA%\TopStepTrader\apikeys.json.
    ''' The file is scoped to the current OS user account and is never committed
    ''' to source control.
    ''' </summary>
    Public Class ApiKeyStore
        Implements IApiKeyStore

        Private Shared ReadOnly FolderPath As String =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "TopStepTrader")

        Private Shared ReadOnly FilePath As String =
            Path.Combine(FolderPath, "apikeys.json")

        Private Shared ReadOnly JsonOpts As New JsonSerializerOptions With {.WriteIndented = True}

        Public Function Load() As ApiKeySettings Implements IApiKeyStore.Load
            Try
                If Not File.Exists(FilePath) Then Return New ApiKeySettings()
                Dim json = File.ReadAllText(FilePath)
                Return If(JsonSerializer.Deserialize(Of ApiKeySettings)(json, JsonOpts),
                          New ApiKeySettings())
            Catch
                Return New ApiKeySettings()
            End Try
        End Function

        Public Sub Save(settings As ApiKeySettings) Implements IApiKeyStore.Save
            Directory.CreateDirectory(FolderPath)
            Dim json = JsonSerializer.Serialize(settings, JsonOpts)
            File.WriteAllText(FilePath, json)
        End Sub

    End Class

End Namespace
