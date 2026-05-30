Imports System.IO
Imports System.Text.Json
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Settings

Namespace TopStepTrader.Services.Market

    ''' <summary>
    ''' FEAT-72: JSON-backed implementation of <see cref="IOpportunityScorePreferences"/>.
    ''' Stored at <c>%LocalAppData%\TopStepTrader\opportunity-score-prefs.json</c>; defaults
    ''' supplied when the file is absent or unparseable.
    ''' </summary>
    Public Class OpportunityScorePreferencesService
        Implements IOpportunityScorePreferences

        Private ReadOnly _filePath As String
        Private _settings As OpportunityScoreSettings

        Public Event Changed As EventHandler Implements IOpportunityScorePreferences.Changed

        Public Sub New()
            Dim folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TopStepTrader")
            Directory.CreateDirectory(folder)
            _filePath = Path.Combine(folder, "opportunity-score-prefs.json")
            _settings = LoadOrDefault()
        End Sub

        Public Function GetSettings() As OpportunityScoreSettings _
            Implements IOpportunityScorePreferences.GetSettings
            Return _settings
        End Function

        Public Sub Save(settings As OpportunityScoreSettings) _
            Implements IOpportunityScorePreferences.Save
            If settings Is Nothing Then Return
            _settings = settings
            Try
                Dim json = JsonSerializer.Serialize(settings, New JsonSerializerOptions With {.WriteIndented = True})
                File.WriteAllText(_filePath, json)
            Catch
            End Try
            RaiseEvent Changed(Me, EventArgs.Empty)
        End Sub

        Private Function LoadOrDefault() As OpportunityScoreSettings
            Try
                If File.Exists(_filePath) Then
                    Dim json = File.ReadAllText(_filePath)
                    Dim loaded = JsonSerializer.Deserialize(Of OpportunityScoreSettings)(json)
                    If loaded IsNot Nothing Then Return loaded
                End If
            Catch
            End Try
            Return New OpportunityScoreSettings()
        End Function

    End Class

End Namespace
