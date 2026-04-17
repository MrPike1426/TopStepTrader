Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' Mutable runtime persona profile. Loaded from SQLite on startup; falls back to
    ''' the defaults in appsettings.json "Personas" section when no saved row exists.
    ''' Shared globally via IPersonaService — changes propagate to all views on next
    ''' persona application (running engines are not affected mid-session).
    ''' </summary>
    Public Class PersonaProfile

        Public Property Name As String = String.Empty

        ''' <summary>Cash amount per initial trade in USD.</summary>
        Public Property TradeAmount As Decimal

        ''' <summary>Preferred leverage multiplier (capped by instrument MaxLeverage).</summary>
        Public Property Leverage As Integer

        ''' <summary>Maximum additional positions after the initial entry.</summary>
        Public Property MaxScaleIns As Integer

        ''' <summary>Initial SL as a multiple of N (ATR × DollarPerPoint), non-leveraged orders.</summary>
        Public Property SlMultipleOfN As Decimal

        ''' <summary>Wider SL multiple for leveraged CFDs (eToro spread buffer). 0 = disabled.</summary>
        Public Property LeveragedSlMultipleOfN As Decimal

        ''' <summary>Initial TP as a multiple of N.</summary>
        Public Property TpMultipleOfN As Decimal

        ''' <summary>Minimum ADX(14) required for the trend-strength gate to pass.</summary>
        Public Property AdxThreshold As Single

        ''' <summary>Recommended minimum confidence score (0–100) to fire a trade.</summary>
        Public Property DefaultConfidencePct As Integer

    End Class

End Namespace
