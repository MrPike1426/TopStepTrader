Imports Microsoft.EntityFrameworkCore
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data

    ''' <summary>
    ''' Scoped repository for the singleton SuperTrend+ config row (id=1).
    ''' </summary>
    Public Class SuperTrendPlusConfigRepository

        Private ReadOnly _db As AppDbContext

        Public Sub New(db As AppDbContext)
            _db = db
        End Sub

        ''' <summary>Returns the saved config, or a default instance if no row exists.</summary>
        Public Async Function LoadAsync() As Task(Of SuperTrendPlusConfigEntity)
            Dim entity = Await _db.SuperTrendPlusConfig.FirstOrDefaultAsync()
            Return If(entity, New SuperTrendPlusConfigEntity())
        End Function

        ''' <summary>Upserts the singleton row (id=1).</summary>
        Public Async Function SaveAsync(entity As SuperTrendPlusConfigEntity) As Task
            Dim existing = Await _db.SuperTrendPlusConfig.FirstOrDefaultAsync()
            If existing Is Nothing Then
                entity.Id = 1
                _db.SuperTrendPlusConfig.Add(entity)
            Else
                existing.ActivePersona         = entity.ActivePersona
                existing.SelectedTimeframe     = entity.SelectedTimeframe
                existing.MaxSlots              = entity.MaxSlots
                existing.AdxWeakThreshold      = entity.AdxWeakThreshold
                existing.AdxModerateThreshold  = entity.AdxModerateThreshold
                existing.AdxStrongThreshold    = entity.AdxStrongThreshold
                existing.BreakevenTriggerR     = entity.BreakevenTriggerR
                existing.ProfitLockTriggerR    = entity.ProfitLockTriggerR
                existing.ProfitLockOffsetR     = entity.ProfitLockOffsetR
                existing.TrailAtrMultiple      = entity.TrailAtrMultiple
                existing.ProfitTrailTriggerR   = entity.ProfitTrailTriggerR
                existing.HarvestTriggerR       = entity.HarvestTriggerR
                existing.HarvestLockR          = entity.HarvestLockR
                existing.FreeRideTriggerR      = entity.FreeRideTriggerR
                existing.FreeRideLockR         = entity.FreeRideLockR
                existing.WarningScoreThreshold = entity.WarningScoreThreshold
                existing.ExitingScoreThreshold = entity.ExitingScoreThreshold
            End If
            Await _db.SaveChangesAsync()
        End Function

    End Class

End Namespace
