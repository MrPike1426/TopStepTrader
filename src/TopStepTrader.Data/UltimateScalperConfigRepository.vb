Imports Microsoft.EntityFrameworkCore
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data

    ''' <summary>
    ''' FEAT-64: Scoped repository for the singleton Ultimate Scalper config row (id=1).
    ''' Provides both raw entity access and a convenience mapper to/from
    ''' <see cref="UltimateScalperConfig"/> (the in-memory model used by services + UI).
    ''' </summary>
    Public Class UltimateScalperConfigRepository

        Private ReadOnly _db As AppDbContext

        Public Sub New(db As AppDbContext)
            _db = db
        End Sub

        ''' <summary>Returns the saved config entity, or a default instance if no row exists.</summary>
        Public Async Function LoadEntityAsync() As Task(Of UltimateScalperConfigEntity)
            Dim entity = Await _db.UltimateScalperConfig.FirstOrDefaultAsync()
            Return If(entity, New UltimateScalperConfigEntity())
        End Function

        ''' <summary>Loads the entity and maps to <see cref="UltimateScalperConfig"/>.</summary>
        Public Async Function LoadAsync() As Task(Of UltimateScalperConfig)
            Dim entity = Await LoadEntityAsync()
            Return ToConfig(entity)
        End Function

        ''' <summary>Upserts the singleton row (id=1) from an in-memory <see cref="UltimateScalperConfig"/>.</summary>
        Public Async Function SaveAsync(config As UltimateScalperConfig) As Task
            Dim incoming = ToEntity(config)
            Dim existing = Await _db.UltimateScalperConfig.FirstOrDefaultAsync()
            If existing Is Nothing Then
                incoming.Id = 1
                _db.UltimateScalperConfig.Add(incoming)
            Else
                existing.MaLength = incoming.MaLength
                existing.RsiLength = incoming.RsiLength
                existing.RsiOverbought = incoming.RsiOverbought
                existing.RsiOversold = incoming.RsiOversold
                existing.MaxBarsSinceMidlineCross = incoming.MaxBarsSinceMidlineCross
                existing.SafetyCeilingTpDollars = incoming.SafetyCeilingTpDollars
                existing.MinSlEditStepTicks = incoming.MinSlEditStepTicks
                existing.MaxSlEditsPerSecond = incoming.MaxSlEditsPerSecond
                existing.Leverage = incoming.Leverage
                existing.PreStagedEntriesEnabled = incoming.PreStagedEntriesEnabled
                existing.EntryTriggerOffsetTicks = incoming.EntryTriggerOffsetTicks
                existing.RepriceThresholdTicks = incoming.RepriceThresholdTicks
                existing.ArmStaleMinutes = incoming.ArmStaleMinutes
                existing.ReArmDebounceSeconds = incoming.ReArmDebounceSeconds
                existing.MaxBrokerCallsPerMinute = incoming.MaxBrokerCallsPerMinute
                existing.MaxConcurrentPositions = incoming.MaxConcurrentPositions
                existing.MesInitialStopDollars = incoming.MesInitialStopDollars
                existing.MesBreakevenSnapDollars = incoming.MesBreakevenSnapDollars
                existing.MesTrailDistanceDollars = incoming.MesTrailDistanceDollars
                existing.MnqInitialStopDollars = incoming.MnqInitialStopDollars
                existing.MnqBreakevenSnapDollars = incoming.MnqBreakevenSnapDollars
                existing.MnqTrailDistanceDollars = incoming.MnqTrailDistanceDollars
                existing.MgcInitialStopDollars = incoming.MgcInitialStopDollars
                existing.MgcBreakevenSnapDollars = incoming.MgcBreakevenSnapDollars
                existing.MgcTrailDistanceDollars = incoming.MgcTrailDistanceDollars
            End If
            Await _db.SaveChangesAsync()
        End Function

        Public Shared Function ToConfig(entity As UltimateScalperConfigEntity) As UltimateScalperConfig
            Dim cfg As New UltimateScalperConfig()
            cfg.MaLength = entity.MaLength
            cfg.RsiLength = entity.RsiLength
            cfg.RsiOverbought = entity.RsiOverbought
            cfg.RsiOversold = entity.RsiOversold
            cfg.MaxBarsSinceMidlineCross = entity.MaxBarsSinceMidlineCross
            cfg.SafetyCeilingTpDollars = entity.SafetyCeilingTpDollars
            cfg.MinSlEditStepTicks = entity.MinSlEditStepTicks
            cfg.MaxSlEditsPerSecond = entity.MaxSlEditsPerSecond
            cfg.Leverage = If(entity.Leverage < 1, 1, entity.Leverage)
            cfg.PreStagedEntriesEnabled = entity.PreStagedEntriesEnabled
            cfg.EntryTriggerOffsetTicks = If(entity.EntryTriggerOffsetTicks < 1, 1, entity.EntryTriggerOffsetTicks)
            cfg.RepriceThresholdTicks = If(entity.RepriceThresholdTicks < 1, 1, entity.RepriceThresholdTicks)
            cfg.ArmStaleMinutes = If(entity.ArmStaleMinutes < 1, 30, entity.ArmStaleMinutes)
            cfg.ReArmDebounceSeconds = If(entity.ReArmDebounceSeconds < 0, 30, entity.ReArmDebounceSeconds)
            cfg.MaxBrokerCallsPerMinute = If(entity.MaxBrokerCallsPerMinute < 10, 80, entity.MaxBrokerCallsPerMinute)
            cfg.MaxConcurrentPositions = If(entity.MaxConcurrentPositions < 1, 1, entity.MaxConcurrentPositions)
            cfg.InstrumentProfiles = New List(Of UltimateScalperInstrumentRiskProfile) From {
                New UltimateScalperInstrumentRiskProfile With {
                    .Symbol = "MES",
                    .InitialStopDollars = entity.MesInitialStopDollars,
                    .BreakevenSnapDollars = entity.MesBreakevenSnapDollars,
                    .TrailDistanceDollars = entity.MesTrailDistanceDollars
                },
                New UltimateScalperInstrumentRiskProfile With {
                    .Symbol = "MNQ",
                    .InitialStopDollars = entity.MnqInitialStopDollars,
                    .BreakevenSnapDollars = entity.MnqBreakevenSnapDollars,
                    .TrailDistanceDollars = entity.MnqTrailDistanceDollars
                },
                New UltimateScalperInstrumentRiskProfile With {
                    .Symbol = "MGC",
                    .InitialStopDollars = entity.MgcInitialStopDollars,
                    .BreakevenSnapDollars = entity.MgcBreakevenSnapDollars,
                    .TrailDistanceDollars = entity.MgcTrailDistanceDollars
                }
            }
            Return cfg
        End Function

        Public Shared Function ToEntity(config As UltimateScalperConfig) As UltimateScalperConfigEntity
            Dim entity As New UltimateScalperConfigEntity()
            entity.MaLength = config.MaLength
            entity.RsiLength = config.RsiLength
            entity.RsiOverbought = config.RsiOverbought
            entity.RsiOversold = config.RsiOversold
            entity.MaxBarsSinceMidlineCross = config.MaxBarsSinceMidlineCross
            entity.SafetyCeilingTpDollars = config.SafetyCeilingTpDollars
            entity.MinSlEditStepTicks = config.MinSlEditStepTicks
            entity.MaxSlEditsPerSecond = config.MaxSlEditsPerSecond
            entity.Leverage = If(config.Leverage < 1, 1, config.Leverage)
            entity.PreStagedEntriesEnabled = config.PreStagedEntriesEnabled
            entity.EntryTriggerOffsetTicks = If(config.EntryTriggerOffsetTicks < 1, 1, config.EntryTriggerOffsetTicks)
            entity.RepriceThresholdTicks = If(config.RepriceThresholdTicks < 1, 1, config.RepriceThresholdTicks)
            entity.ArmStaleMinutes = If(config.ArmStaleMinutes < 1, 30, config.ArmStaleMinutes)
            entity.ReArmDebounceSeconds = If(config.ReArmDebounceSeconds < 0, 30, config.ReArmDebounceSeconds)
            entity.MaxBrokerCallsPerMinute = If(config.MaxBrokerCallsPerMinute < 10, 80, config.MaxBrokerCallsPerMinute)
            entity.MaxConcurrentPositions = If(config.MaxConcurrentPositions < 1, 1, config.MaxConcurrentPositions)

            Dim mes = config.GetProfile("MES")
            If mes IsNot Nothing Then
                entity.MesInitialStopDollars = mes.InitialStopDollars
                entity.MesBreakevenSnapDollars = mes.BreakevenSnapDollars
                entity.MesTrailDistanceDollars = mes.TrailDistanceDollars
            End If

            Dim mnq = config.GetProfile("MNQ")
            If mnq IsNot Nothing Then
                entity.MnqInitialStopDollars = mnq.InitialStopDollars
                entity.MnqBreakevenSnapDollars = mnq.BreakevenSnapDollars
                entity.MnqTrailDistanceDollars = mnq.TrailDistanceDollars
            End If

            Dim mgc = config.GetProfile("MGC")
            If mgc IsNot Nothing Then
                entity.MgcInitialStopDollars = mgc.InitialStopDollars
                entity.MgcBreakevenSnapDollars = mgc.BreakevenSnapDollars
                entity.MgcTrailDistanceDollars = mgc.TrailDistanceDollars
            End If

            Return entity
        End Function

    End Class

End Namespace
