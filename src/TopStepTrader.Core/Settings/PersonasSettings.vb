Namespace TopStepTrader.Core.Settings

    ''' <summary>
    ''' Per-persona default values bound from appsettings.json "Personas:&lt;Name&gt;" sections.
    ''' These are the factory defaults returned by "Reset to Defaults" on the Persona page.
    ''' </summary>
    Public Class PersonaProfileSettings
        ''' <summary>Number of contracts per initial trade entry. TopStepX sizes:
        ''' Lewis = 1 (conservative), Damian = 3 (moderate), Joe = 5 (aggressive).</summary>
        Public Property PositionSize As Integer = 1
        Public Property Leverage As Integer = 10
        Public Property MaxScaleIns As Integer = 2
        Public Property SlMultipleOfN As Decimal = 1.0D
        Public Property LeveragedSlMultipleOfN As Decimal = 2.0D
        Public Property TpMultipleOfN As Decimal = 2.0D
        Public Property AdxThreshold As Single = 20.0F
        Public Property DefaultConfidencePct As Integer = 80
        ''' <summary>Minimum MACD histogram magnitude as a fraction of ATR(14). Default 0.05 (Damian baseline).
        ''' Lewis (aggressive entry filter) = 0.07; Joe (conservative, more signals) = 0.03.</summary>
        Public Property MacdHistMinAtrFraction As Double = 0.05

        ''' <summary>
        ''' Optional per-persona overrides for MultiConfluenceConfig thresholds (STRAT-24).
        ''' Null = use the global "Strategies:MultiConfluence" defaults.
        ''' </summary>
        Public Property MultiConfluence As MultiConfluenceConfig = Nothing
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
