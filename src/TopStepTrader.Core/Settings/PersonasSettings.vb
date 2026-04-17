Namespace TopStepTrader.Core.Settings

    ''' <summary>
    ''' Per-persona default values bound from appsettings.json "Personas:&lt;Name&gt;" sections.
    ''' These are the factory defaults returned by "Reset to Defaults" on the Persona page.
    ''' </summary>
    Public Class PersonaProfileSettings
        Public Property TradeAmount As Decimal = 500D
        Public Property Leverage As Integer = 10
        Public Property MaxScaleIns As Integer = 2
        Public Property SlMultipleOfN As Decimal = 1.0D
        Public Property LeveragedSlMultipleOfN As Decimal = 2.0D
        Public Property TpMultipleOfN As Decimal = 2.0D
        Public Property AdxThreshold As Single = 20.0F
        Public Property DefaultConfidencePct As Integer = 80
    End Class

    ''' <summary>
    ''' Container bound to the "Personas" section in appsettings.json.
    ''' Injected via IOptions(Of PersonasSettings).
    ''' </summary>
    Public Class PersonasSettings
        Public Property Lewis As PersonaProfileSettings = New PersonaProfileSettings()
        Public Property Damian As PersonaProfileSettings = New PersonaProfileSettings()
        Public Property Joe As PersonaProfileSettings = New PersonaProfileSettings()
    End Class

End Namespace
