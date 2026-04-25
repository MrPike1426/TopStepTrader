Imports Microsoft.EntityFrameworkCore
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data.Repositories

    Public Interface IAdaptiveParametersRepository
        Function GetAdjustmentAsync(strategyName As String, personaName As String, parameterName As String) As Task(Of AdaptiveParametersEntity)
        Function UpsertAsync(entity As AdaptiveParametersEntity) As Task
        Function GetAllActiveAsync(strategyName As String, personaName As String) As Task(Of List(Of AdaptiveParametersEntity))
    End Interface

    ''' <summary>
    ''' Persists and retrieves adaptive parameter adjustments written by PerformanceTracker and WeeklyDigest.
    ''' </summary>
    Public Class AdaptiveParametersRepository
        Implements IAdaptiveParametersRepository

        Private ReadOnly _db As AppDbContext

        Public Sub New(db As AppDbContext)
            _db = db
        End Sub

        Public Async Function GetAdjustmentAsync(strategyName As String, personaName As String, parameterName As String) As Task(Of AdaptiveParametersEntity) Implements IAdaptiveParametersRepository.GetAdjustmentAsync
            Return Await _db.AdaptiveParameters _
                            .AsNoTracking() _
                            .FirstOrDefaultAsync(Function(p) p.StrategyName = strategyName AndAlso
                                                              p.PersonaName = personaName AndAlso
                                                              p.ParameterName = parameterName)
        End Function

        Public Async Function UpsertAsync(entity As AdaptiveParametersEntity) As Task Implements IAdaptiveParametersRepository.UpsertAsync
            Dim existing = Await _db.AdaptiveParameters _
                .FirstOrDefaultAsync(Function(p) p.StrategyName = entity.StrategyName AndAlso
                                                  p.PersonaName = entity.PersonaName AndAlso
                                                  p.ParameterName = entity.ParameterName)
            If existing Is Nothing Then
                entity.CreatedAt = DateTimeOffset.UtcNow
                entity.UpdatedAt = DateTimeOffset.UtcNow
                _db.AdaptiveParameters.Add(entity)
            Else
                existing.BaseValue = entity.BaseValue
                existing.AdjustmentValue = entity.AdjustmentValue
                existing.EffectiveValue = entity.EffectiveValue
                existing.Rationale = entity.Rationale
                existing.IsActive = entity.IsActive
                existing.SourceTradeCount = entity.SourceTradeCount
                existing.UpdatedAt = DateTimeOffset.UtcNow
                _db.AdaptiveParameters.Update(existing)
            End If
            Await _db.SaveChangesAsync()
        End Function

        Public Async Function GetAllActiveAsync(strategyName As String, personaName As String) As Task(Of List(Of AdaptiveParametersEntity)) Implements IAdaptiveParametersRepository.GetAllActiveAsync
            Return Await _db.AdaptiveParameters _
                            .AsNoTracking() _
                            .Where(Function(p) p.StrategyName = strategyName AndAlso
                                               p.PersonaName = personaName AndAlso
                                               p.IsActive) _
                            .ToListAsync()
        End Function

    End Class

End Namespace
