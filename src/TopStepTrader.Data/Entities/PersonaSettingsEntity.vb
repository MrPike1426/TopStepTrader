Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    ''' <summary>
    ''' Persisted persona settings row. One row per persona name (Lewis / Damian / Joe).
    ''' Only written when the user clicks "Save Persona" on the Persona config page.
    ''' Absent rows → factory defaults from appsettings.json are used instead.
    ''' </summary>
    <Table("PersonaSettings")>
    Public Class PersonaSettingsEntity

        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.Identity)>
        Public Property Id As Integer

        <Required>
        <MaxLength(50)>
        Public Property Name As String = String.Empty

        <Column(TypeName:="decimal(18,4)")>
        Public Property TradeAmount As Decimal

        Public Property Leverage As Integer

        Public Property MaxScaleIns As Integer

        <Column(TypeName:="decimal(18,4)")>
        Public Property SlMultipleOfN As Decimal

        <Column(TypeName:="decimal(18,4)")>
        Public Property LeveragedSlMultipleOfN As Decimal

        <Column(TypeName:="decimal(18,4)")>
        Public Property TpMultipleOfN As Decimal

        Public Property AdxThreshold As Single

        Public Property DefaultConfidencePct As Integer

        ''' <summary>Minimum MACD histogram magnitude as a fraction of ATR(14). Default 0.05.</summary>
        Public Property MacdHistMinAtrFraction As Double = 0.05

        Public Property LastModifiedAt As DateTimeOffset = DateTimeOffset.UtcNow

    End Class

End Namespace
