Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' Global persona profile store. Singleton lifetime.
    ''' On startup: loads saved values from SQLite; falls back to appsettings.json defaults.
    ''' On save: persists to SQLite and updates the in-memory cache immediately.
    ''' On reset: deletes the SQLite row and reverts to appsettings.json defaults.
    ''' All ViewModels call GetProfile() / GetAllProfiles() when applying a persona —
    ''' saved overrides are therefore picked up automatically on next persona application.
    ''' </summary>
    Public Interface IPersonaService

        ''' <summary>Returns the current effective profile (SQLite override if saved, else appsettings default).</summary>
        Function GetProfile(name As String) As PersonaProfile

        ''' <summary>Returns all three profiles in ascending risk order: Lewis, Damian, Joe.</summary>
        Function GetAllProfiles() As IReadOnlyList(Of PersonaProfile)

        ''' <summary>Returns the factory default for a persona from appsettings.json, ignoring any saved override.</summary>
        Function GetDefault(name As String) As PersonaProfile

        ''' <summary>Persists the profile to SQLite and updates the in-memory cache.</summary>
        Function SaveProfileAsync(profile As PersonaProfile) As Task

        ''' <summary>Deletes the SQLite override for a persona and reverts the cache to the appsettings default.</summary>
        Function ResetToDefaultAsync(name As String) As Task

    End Interface

End Namespace
