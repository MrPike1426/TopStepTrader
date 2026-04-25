Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    ''' <summary>
    ''' Stores a single adaptive parameter adjustment written by the PerformanceTracker or WeeklyDigest.
    ''' One row per (StrategyName, PersonaName, ParameterName) tuple — upserted on each digest run.
    ''' </summary>
    <Table("AdaptiveParameters")>
    Public Class AdaptiveParametersEntity

        <Key>
        <DatabaseGenerated(DatabaseGeneratedOption.Identity)>
        Public Property Id As Integer

        <MaxLength(100)>
        Public Property StrategyName As String = String.Empty

        <MaxLength(50)>
        Public Property PersonaName As String = String.Empty

        <MaxLength(100)>
        Public Property ParameterName As String = String.Empty

        Public Property BaseValue As Single = 0F

        Public Property AdjustmentValue As Single = 0F

        Public Property EffectiveValue As Single = 0F

        <MaxLength(500)>
        Public Property Rationale As String = String.Empty

        Public Property IsActive As Boolean = True

        Public Property SourceTradeCount As Integer = 0

        Public Property CreatedAt As DateTimeOffset = DateTimeOffset.UtcNow

        Public Property UpdatedAt As DateTimeOffset = DateTimeOffset.UtcNow

    End Class

End Namespace
