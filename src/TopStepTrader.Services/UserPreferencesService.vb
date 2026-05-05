Imports System.IO
Imports System.Text.Json
Imports TopStepTrader.Core.Interfaces

Namespace TopStepTrader.Services

    Public Class UserPreferencesService
        Implements IUserPreferencesService

        Private ReadOnly _filePath As String
        Private _autoExecEnabled As Boolean = False

        Public Sub New()
            Dim folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TopStepTrader")
            Directory.CreateDirectory(folder)
            _filePath = Path.Combine(folder, "user-prefs.json")
            Load()
        End Sub

        Public Property AutoExecutionEnabled As Boolean Implements IUserPreferencesService.AutoExecutionEnabled
            Get
                Return _autoExecEnabled
            End Get
            Set(value As Boolean)
                _autoExecEnabled = value
            End Set
        End Property

        Public Sub Save() Implements IUserPreferencesService.Save
            Try
                Dim json = JsonSerializer.Serialize(New PrefsModel With {
                    .AutoExecutionEnabled = _autoExecEnabled
                })
                File.WriteAllText(_filePath, json)
            Catch
            End Try
        End Sub

        Private Sub Load()
            Try
                If File.Exists(_filePath) Then
                    Dim json = File.ReadAllText(_filePath)
                    Dim model = JsonSerializer.Deserialize(Of PrefsModel)(json)
                    If model IsNot Nothing Then
                        _autoExecEnabled = model.AutoExecutionEnabled
                    End If
                End If
            Catch
            End Try
        End Sub

        Private Class PrefsModel
            Public Property AutoExecutionEnabled As Boolean
        End Class

    End Class

End Namespace
