Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Data
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Services.Personas

    ''' <summary>
    ''' Singleton persona profile store.
    ''' Constructor: loads factory defaults from IOptions(Of PersonasSettings), then overlays
    ''' any saved rows from SQLite.  The in-memory dict is the authoritative source for all
    ''' GetProfile() callers throughout the session.
    ''' Save / Reset operations update both SQLite and the in-memory dict atomically.
    ''' IServiceScopeFactory is used to create short-lived scopes for DB access because
    ''' AppDbContext is Scoped but PersonaService is Singleton.
    ''' </summary>
    Public Class PersonaService
        Implements IPersonaService

        Private ReadOnly _scopeFactory As IServiceScopeFactory
        Private ReadOnly _defaults As PersonasSettings
        Private ReadOnly _profiles As New Dictionary(Of String, PersonaProfile)(StringComparer.OrdinalIgnoreCase)

        Public Sub New(scopeFactory As IServiceScopeFactory,
                       options As IOptions(Of PersonasSettings))
            _scopeFactory = scopeFactory
            _defaults = options.Value
            LoadDefaults()
            LoadFromDatabase()
        End Sub

        ' ── IPersonaService ────────────────────────────────────────────────────

        Public Function GetProfile(name As String) As PersonaProfile Implements IPersonaService.GetProfile
            Dim p As PersonaProfile = Nothing
            If _profiles.TryGetValue(name, p) Then Return p
            Return GetDefault(name)
        End Function

        Public Function GetAllProfiles() As IReadOnlyList(Of PersonaProfile) Implements IPersonaService.GetAllProfiles
            Return New List(Of PersonaProfile) From {
                GetProfile("Lewis"),
                GetProfile("Damian"),
                GetProfile("Joe")
            }
        End Function

        Public Function GetDefault(name As String) As PersonaProfile Implements IPersonaService.GetDefault
            Select Case name.ToUpperInvariant()
                Case "LEWIS"  : Return ToProfile("Lewis", _defaults.Lewis)
                Case "JOE"    : Return ToProfile("Joe", _defaults.Joe)
                Case Else     : Return ToProfile("Damian", _defaults.Damian)
            End Select
        End Function

        Public Async Function SaveProfileAsync(profile As PersonaProfile) As Task Implements IPersonaService.SaveProfileAsync
            Using scope = _scopeFactory.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of AppDbContext)()
                Dim entity = Await db.PersonaSettings _
                    .FirstOrDefaultAsync(Function(p) p.Name = profile.Name)

                If entity Is Nothing Then
                    entity = New PersonaSettingsEntity With {.Name = profile.Name}
                    db.PersonaSettings.Add(entity)
                End If

                MapToEntity(profile, entity)
                Await db.SaveChangesAsync()
            End Using

            ' Update in-memory cache with a snapshot of the saved values
            _profiles(profile.Name) = CloneProfile(profile)
        End Function

        Public Async Function ResetToDefaultAsync(name As String) As Task Implements IPersonaService.ResetToDefaultAsync
            Using scope = _scopeFactory.CreateScope()
                Dim db = scope.ServiceProvider.GetRequiredService(Of AppDbContext)()
                Dim entity = Await db.PersonaSettings _
                    .FirstOrDefaultAsync(Function(p) p.Name = name)
                If entity IsNot Nothing Then
                    db.PersonaSettings.Remove(entity)
                    Await db.SaveChangesAsync()
                End If
            End Using

            _profiles(name) = GetDefault(name)
        End Function

        ' ── Private helpers ───────────────────────────────────────────────────

        Private Sub LoadDefaults()
            _profiles("Lewis")  = ToProfile("Lewis",  _defaults.Lewis)
            _profiles("Damian") = ToProfile("Damian", _defaults.Damian)
            _profiles("Joe")    = ToProfile("Joe",    _defaults.Joe)
        End Sub

        Private Sub LoadFromDatabase()
            Try
                Using scope = _scopeFactory.CreateScope()
                    Dim db = scope.ServiceProvider.GetRequiredService(Of AppDbContext)()
                    For Each entity In db.PersonaSettings.ToList()
                        _profiles(entity.Name) = EntityToProfile(entity)
                    Next
                End Using
            Catch
                ' DB may not be ready yet on first run — defaults remain in effect.
            End Try
        End Sub

        Private Shared Function ToProfile(name As String, s As PersonaProfileSettings) As PersonaProfile
            Return New PersonaProfile With {
                .Name                  = name,
                .TradeAmount           = s.TradeAmount,
                .Leverage              = s.Leverage,
                .MaxScaleIns           = s.MaxScaleIns,
                .SlMultipleOfN         = s.SlMultipleOfN,
                .LeveragedSlMultipleOfN = s.LeveragedSlMultipleOfN,
                .TpMultipleOfN         = s.TpMultipleOfN,
                .AdxThreshold          = s.AdxThreshold,
                .DefaultConfidencePct  = s.DefaultConfidencePct,
                .MacdHistMinAtrFraction = s.MacdHistMinAtrFraction,
                .MultiConfluence       = s.MultiConfluence
            }
        End Function

        Private Shared Function EntityToProfile(e As PersonaSettingsEntity) As PersonaProfile
            Return New PersonaProfile With {
                .Name                  = e.Name,
                .TradeAmount           = e.TradeAmount,
                .Leverage              = e.Leverage,
                .MaxScaleIns           = e.MaxScaleIns,
                .SlMultipleOfN         = e.SlMultipleOfN,
                .LeveragedSlMultipleOfN = e.LeveragedSlMultipleOfN,
                .TpMultipleOfN         = e.TpMultipleOfN,
                .AdxThreshold          = e.AdxThreshold,
                .DefaultConfidencePct  = e.DefaultConfidencePct,
                .MacdHistMinAtrFraction = e.MacdHistMinAtrFraction
            }
        End Function

        Private Shared Sub MapToEntity(profile As PersonaProfile, entity As PersonaSettingsEntity)
            entity.TradeAmount           = profile.TradeAmount
            entity.Leverage              = profile.Leverage
            entity.MaxScaleIns           = profile.MaxScaleIns
            entity.SlMultipleOfN         = profile.SlMultipleOfN
            entity.LeveragedSlMultipleOfN = profile.LeveragedSlMultipleOfN
            entity.TpMultipleOfN         = profile.TpMultipleOfN
            entity.AdxThreshold          = profile.AdxThreshold
            entity.DefaultConfidencePct  = profile.DefaultConfidencePct
            entity.MacdHistMinAtrFraction = profile.MacdHistMinAtrFraction
            entity.LastModifiedAt        = DateTimeOffset.UtcNow
        End Sub

        Private Shared Function CloneProfile(p As PersonaProfile) As PersonaProfile
            Return New PersonaProfile With {
                .Name                  = p.Name,
                .TradeAmount           = p.TradeAmount,
                .Leverage              = p.Leverage,
                .MaxScaleIns           = p.MaxScaleIns,
                .SlMultipleOfN         = p.SlMultipleOfN,
                .LeveragedSlMultipleOfN = p.LeveragedSlMultipleOfN,
                .TpMultipleOfN         = p.TpMultipleOfN,
                .AdxThreshold          = p.AdxThreshold,
                .DefaultConfidencePct  = p.DefaultConfidencePct,
                .MacdHistMinAtrFraction = p.MacdHistMinAtrFraction,
                .MultiConfluence       = p.MultiConfluence
            }
        End Function

    End Class

End Namespace
