Imports Microsoft.EntityFrameworkCore
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data

    ''' <summary>
    ''' FEAT-70: Scoped repository for the singleton SlipStream config row (id=1).
    ''' Mirrors <c>UltimateScalperConfigRepository</c>: load returns a default instance
    ''' when no row exists; save upserts the singleton.
    ''' </summary>
    Public Class SlipStreamConfigRepository

        Private ReadOnly _db As AppDbContext

        Public Sub New(db As AppDbContext)
            _db = db
        End Sub

        ''' <summary>Returns the saved config entity, or a default instance if no row exists.</summary>
        Public Async Function LoadEntityAsync() As Task(Of SlipStreamConfigEntity)
            Dim entity = Await _db.SlipStreamConfig.FirstOrDefaultAsync()
            Return If(entity, New SlipStreamConfigEntity())
        End Function

        ''' <summary>Loads the entity and maps to <see cref="SlipStreamConfig"/>.</summary>
        Public Async Function LoadAsync() As Task(Of SlipStreamConfig)
            Dim entity = Await LoadEntityAsync()
            Return ToConfig(entity)
        End Function

        ''' <summary>Upserts the singleton row (id=1) from an in-memory <see cref="SlipStreamConfig"/>.</summary>
        Public Async Function SaveAsync(config As SlipStreamConfig) As Task
            Dim incoming = ToEntity(config)
            Dim existing = Await _db.SlipStreamConfig.FirstOrDefaultAsync()
            If existing Is Nothing Then
                incoming.Id = 1
                _db.SlipStreamConfig.Add(incoming)
            Else
                existing.HtfTimeframe = incoming.HtfTimeframe
                existing.UseHtfFilter = incoming.UseHtfFilter
                existing.HtfEmaLength = incoming.HtfEmaLength
                existing.EmaFastLength = incoming.EmaFastLength
                existing.EmaSlowLength = incoming.EmaSlowLength
                existing.RsiLength = incoming.RsiLength
                existing.RsiLongMin = incoming.RsiLongMin
                existing.RsiShortMax = incoming.RsiShortMax
                existing.AdxLength = incoming.AdxLength
                existing.AdxMin = incoming.AdxMin
                existing.AtrLength = incoming.AtrLength
                existing.AtrPercentLookback = incoming.AtrPercentLookback
                existing.AtrPercentMin = incoming.AtrPercentMin
                existing.ExtendBars = incoming.ExtendBars
                existing.ExtendAtrMult = incoming.ExtendAtrMult
                existing.RiskPct = incoming.RiskPct
                existing.AtrSLmult = incoming.AtrSLmult
                existing.AtrTP1mult = incoming.AtrTP1mult
                existing.Tp1Pct = incoming.Tp1Pct
                existing.TrailMult = incoming.TrailMult
                existing.TrailOffsetMult = incoming.TrailOffsetMult
                existing.MaxBarsInTrade = incoming.MaxBarsInTrade
                existing.UseSession = incoming.UseSession
                existing.SessionWindow = incoming.SessionWindow
                existing.FlatWindow = incoming.FlatWindow
                existing.CooldownBars = incoming.CooldownBars
                existing.EnableLong = incoming.EnableLong
                existing.EnableShort = incoming.EnableShort
                existing.MinSlEditStepTicks = incoming.MinSlEditStepTicks
                existing.MaxSlEditsPerSecond = incoming.MaxSlEditsPerSecond
                existing.MaxConcurrentPositions = incoming.MaxConcurrentPositions
                existing.MesAtrSLmultOverride = incoming.MesAtrSLmultOverride
                existing.MesAtrTP1multOverride = incoming.MesAtrTP1multOverride
                existing.MesTrailMultOverride = incoming.MesTrailMultOverride
                existing.MesTrailOffsetMultOverride = incoming.MesTrailOffsetMultOverride
                existing.MnqAtrSLmultOverride = incoming.MnqAtrSLmultOverride
                existing.MnqAtrTP1multOverride = incoming.MnqAtrTP1multOverride
                existing.MnqTrailMultOverride = incoming.MnqTrailMultOverride
                existing.MnqTrailOffsetMultOverride = incoming.MnqTrailOffsetMultOverride
                existing.MgcAtrSLmultOverride = incoming.MgcAtrSLmultOverride
                existing.MgcAtrTP1multOverride = incoming.MgcAtrTP1multOverride
                existing.MgcTrailMultOverride = incoming.MgcTrailMultOverride
                existing.MgcTrailOffsetMultOverride = incoming.MgcTrailOffsetMultOverride
            End If
            Await _db.SaveChangesAsync()
        End Function

        Public Shared Function ToConfig(entity As SlipStreamConfigEntity) As SlipStreamConfig
            Dim cfg As New SlipStreamConfig()
            cfg.HtfTimeframe = If(String.IsNullOrWhiteSpace(entity.HtfTimeframe), "60min", entity.HtfTimeframe)
            cfg.UseHtfFilter = entity.UseHtfFilter
            cfg.HtfEmaLength = If(entity.HtfEmaLength < 1, 50, entity.HtfEmaLength)
            cfg.EmaFastLength = If(entity.EmaFastLength < 1, 21, entity.EmaFastLength)
            cfg.EmaSlowLength = If(entity.EmaSlowLength < 1, 200, entity.EmaSlowLength)
            cfg.RsiLength = If(entity.RsiLength < 1, 14, entity.RsiLength)
            cfg.RsiLongMin = entity.RsiLongMin
            cfg.RsiShortMax = entity.RsiShortMax
            cfg.AdxLength = If(entity.AdxLength < 1, 14, entity.AdxLength)
            cfg.AdxMin = entity.AdxMin
            cfg.AtrLength = If(entity.AtrLength < 1, 14, entity.AtrLength)
            cfg.AtrPercentLookback = If(entity.AtrPercentLookback < 10, 100, entity.AtrPercentLookback)
            cfg.AtrPercentMin = entity.AtrPercentMin
            cfg.ExtendBars = If(entity.ExtendBars < 1, 3, entity.ExtendBars)
            cfg.ExtendAtrMult = entity.ExtendAtrMult
            cfg.RiskPct = If(entity.RiskPct <= 0, 0.5, entity.RiskPct)
            cfg.AtrSLmult = entity.AtrSLmult
            cfg.AtrTP1mult = entity.AtrTP1mult
            cfg.Tp1Pct = Math.Max(0, Math.Min(100, entity.Tp1Pct))
            cfg.TrailMult = entity.TrailMult
            cfg.TrailOffsetMult = entity.TrailOffsetMult
            cfg.MaxBarsInTrade = If(entity.MaxBarsInTrade < 0, 40, entity.MaxBarsInTrade)
            cfg.UseSession = entity.UseSession
            cfg.SessionWindow = If(String.IsNullOrWhiteSpace(entity.SessionWindow), "0830-1500", entity.SessionWindow)
            cfg.FlatWindow = If(String.IsNullOrWhiteSpace(entity.FlatWindow), "1450-1500", entity.FlatWindow)
            cfg.CooldownBars = If(entity.CooldownBars < 0, 3, entity.CooldownBars)
            cfg.EnableLong = entity.EnableLong
            cfg.EnableShort = entity.EnableShort
            cfg.MinSlEditStepTicks = If(entity.MinSlEditStepTicks < 1, 1, entity.MinSlEditStepTicks)
            cfg.MaxSlEditsPerSecond = If(entity.MaxSlEditsPerSecond < 1, 5, entity.MaxSlEditsPerSecond)
            cfg.MaxConcurrentPositions = If(entity.MaxConcurrentPositions < 1, 1, entity.MaxConcurrentPositions)
            cfg.InstrumentProfiles = New List(Of SlipStreamInstrumentRiskProfile) From {
                New SlipStreamInstrumentRiskProfile With {
                    .Symbol = "MES",
                    .AtrSLmultOverride = entity.MesAtrSLmultOverride,
                    .AtrTP1multOverride = entity.MesAtrTP1multOverride,
                    .TrailMultOverride = entity.MesTrailMultOverride,
                    .TrailOffsetMultOverride = entity.MesTrailOffsetMultOverride
                },
                New SlipStreamInstrumentRiskProfile With {
                    .Symbol = "MNQ",
                    .AtrSLmultOverride = entity.MnqAtrSLmultOverride,
                    .AtrTP1multOverride = entity.MnqAtrTP1multOverride,
                    .TrailMultOverride = entity.MnqTrailMultOverride,
                    .TrailOffsetMultOverride = entity.MnqTrailOffsetMultOverride
                },
                New SlipStreamInstrumentRiskProfile With {
                    .Symbol = "MGC",
                    .AtrSLmultOverride = entity.MgcAtrSLmultOverride,
                    .AtrTP1multOverride = entity.MgcAtrTP1multOverride,
                    .TrailMultOverride = entity.MgcTrailMultOverride,
                    .TrailOffsetMultOverride = entity.MgcTrailOffsetMultOverride
                }
            }
            Return cfg
        End Function

        Public Shared Function ToEntity(config As SlipStreamConfig) As SlipStreamConfigEntity
            Dim e As New SlipStreamConfigEntity()
            e.HtfTimeframe = config.HtfTimeframe
            e.UseHtfFilter = config.UseHtfFilter
            e.HtfEmaLength = config.HtfEmaLength
            e.EmaFastLength = config.EmaFastLength
            e.EmaSlowLength = config.EmaSlowLength
            e.RsiLength = config.RsiLength
            e.RsiLongMin = config.RsiLongMin
            e.RsiShortMax = config.RsiShortMax
            e.AdxLength = config.AdxLength
            e.AdxMin = config.AdxMin
            e.AtrLength = config.AtrLength
            e.AtrPercentLookback = config.AtrPercentLookback
            e.AtrPercentMin = config.AtrPercentMin
            e.ExtendBars = config.ExtendBars
            e.ExtendAtrMult = config.ExtendAtrMult
            e.RiskPct = config.RiskPct
            e.AtrSLmult = config.AtrSLmult
            e.AtrTP1mult = config.AtrTP1mult
            e.Tp1Pct = config.Tp1Pct
            e.TrailMult = config.TrailMult
            e.TrailOffsetMult = config.TrailOffsetMult
            e.MaxBarsInTrade = config.MaxBarsInTrade
            e.UseSession = config.UseSession
            e.SessionWindow = config.SessionWindow
            e.FlatWindow = config.FlatWindow
            e.CooldownBars = config.CooldownBars
            e.EnableLong = config.EnableLong
            e.EnableShort = config.EnableShort
            e.MinSlEditStepTicks = config.MinSlEditStepTicks
            e.MaxSlEditsPerSecond = config.MaxSlEditsPerSecond
            e.MaxConcurrentPositions = config.MaxConcurrentPositions

            Dim mes = config.GetProfile("MES")
            If mes IsNot Nothing Then
                e.MesAtrSLmultOverride = mes.AtrSLmultOverride
                e.MesAtrTP1multOverride = mes.AtrTP1multOverride
                e.MesTrailMultOverride = mes.TrailMultOverride
                e.MesTrailOffsetMultOverride = mes.TrailOffsetMultOverride
            End If
            Dim mnq = config.GetProfile("MNQ")
            If mnq IsNot Nothing Then
                e.MnqAtrSLmultOverride = mnq.AtrSLmultOverride
                e.MnqAtrTP1multOverride = mnq.AtrTP1multOverride
                e.MnqTrailMultOverride = mnq.TrailMultOverride
                e.MnqTrailOffsetMultOverride = mnq.TrailOffsetMultOverride
            End If
            Dim mgc = config.GetProfile("MGC")
            If mgc IsNot Nothing Then
                e.MgcAtrSLmultOverride = mgc.AtrSLmultOverride
                e.MgcAtrTP1multOverride = mgc.AtrTP1multOverride
                e.MgcTrailMultOverride = mgc.TrailMultOverride
                e.MgcTrailOffsetMultOverride = mgc.TrailOffsetMultOverride
            End If

            Return e
        End Function

    End Class

End Namespace
