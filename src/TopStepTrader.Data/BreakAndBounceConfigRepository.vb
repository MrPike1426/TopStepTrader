Imports Microsoft.EntityFrameworkCore
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Data.Entities

Namespace TopStepTrader.Data

    ''' <summary>
    ''' FEAT-62: Scoped repository for the singleton Break and Bounce config row (id=1).
    ''' Mirrors <c>SlipStreamConfigRepository</c>: load returns a default instance when
    ''' no row exists; save upserts the singleton.
    ''' </summary>
    Public Class BreakAndBounceConfigRepository

        Private ReadOnly _db As AppDbContext

        Public Sub New(db As AppDbContext)
            _db = db
        End Sub

        Public Async Function LoadEntityAsync() As Task(Of BreakAndBounceConfigEntity)
            Dim entity = Await _db.BreakAndBounceConfig.FirstOrDefaultAsync()
            Return If(entity, New BreakAndBounceConfigEntity())
        End Function

        Public Async Function LoadAsync() As Task(Of BreakAndBounceConfig)
            Dim entity = Await LoadEntityAsync()
            Return ToConfig(entity)
        End Function

        Public Async Function SaveAsync(config As BreakAndBounceConfig) As Task
            Dim incoming = ToEntity(config)
            Dim existing = Await _db.BreakAndBounceConfig.FirstOrDefaultAsync()
            If existing Is Nothing Then
                incoming.Id = 1
                _db.BreakAndBounceConfig.Add(incoming)
            Else
                existing.EntryWindow = incoming.EntryWindow
                existing.FlatWindow = incoming.FlatWindow
                existing.BreakoutTimeframe = incoming.BreakoutTimeframe
                existing.RetestTimeframe = incoming.RetestTimeframe
                existing.MinimumStopDistanceTicks = incoming.MinimumStopDistanceTicks
                existing.MinimumStopAtrFraction = incoming.MinimumStopAtrFraction
                existing.AtrLength = incoming.AtrLength
                existing.InvalidateDirOnCounterBreakout = incoming.InvalidateDirOnCounterBreakout
                existing.InvalidateDirOnWindowExpiry = incoming.InvalidateDirOnWindowExpiry
                existing.ContractsPerEntry = incoming.ContractsPerEntry
                existing.AiVetoEnabled = incoming.AiVetoEnabled
                existing.EnableLong = incoming.EnableLong
                existing.EnableShort = incoming.EnableShort
                existing.MinSlEditStepTicks = incoming.MinSlEditStepTicks
                existing.MaxConcurrentPositions = incoming.MaxConcurrentPositions
            End If
            Await _db.SaveChangesAsync()
        End Function

        Public Shared Function ToConfig(entity As BreakAndBounceConfigEntity) As BreakAndBounceConfig
            Dim cfg As New BreakAndBounceConfig()
            cfg.EntryWindow = If(String.IsNullOrWhiteSpace(entity.EntryWindow), "0830-1100", entity.EntryWindow)
            cfg.FlatWindow = If(String.IsNullOrWhiteSpace(entity.FlatWindow), "1450-1500", entity.FlatWindow)
            cfg.BreakoutTimeframe = If(String.IsNullOrWhiteSpace(entity.BreakoutTimeframe), "15min", entity.BreakoutTimeframe)
            cfg.RetestTimeframe = If(String.IsNullOrWhiteSpace(entity.RetestTimeframe), "5min", entity.RetestTimeframe)
            cfg.MinimumStopDistanceTicks = If(entity.MinimumStopDistanceTicks < 1, 8, entity.MinimumStopDistanceTicks)
            cfg.MinimumStopAtrFraction = If(entity.MinimumStopAtrFraction <= 0, 0.5, entity.MinimumStopAtrFraction)
            cfg.AtrLength = If(entity.AtrLength < 1, 14, entity.AtrLength)
            cfg.InvalidateDirOnCounterBreakout = entity.InvalidateDirOnCounterBreakout
            cfg.InvalidateDirOnWindowExpiry = entity.InvalidateDirOnWindowExpiry
            cfg.ContractsPerEntry = If(entity.ContractsPerEntry < 1, 1, entity.ContractsPerEntry)
            cfg.AiVetoEnabled = entity.AiVetoEnabled
            cfg.EnableLong = entity.EnableLong
            cfg.EnableShort = entity.EnableShort
            cfg.MinSlEditStepTicks = If(entity.MinSlEditStepTicks < 1, 1, entity.MinSlEditStepTicks)
            cfg.MaxConcurrentPositions = If(entity.MaxConcurrentPositions < 1, 1, entity.MaxConcurrentPositions)
            Return cfg
        End Function

        Public Shared Function ToEntity(config As BreakAndBounceConfig) As BreakAndBounceConfigEntity
            Dim e As New BreakAndBounceConfigEntity()
            e.EntryWindow = config.EntryWindow
            e.FlatWindow = config.FlatWindow
            e.BreakoutTimeframe = config.BreakoutTimeframe
            e.RetestTimeframe = config.RetestTimeframe
            e.MinimumStopDistanceTicks = config.MinimumStopDistanceTicks
            e.MinimumStopAtrFraction = config.MinimumStopAtrFraction
            e.AtrLength = config.AtrLength
            e.InvalidateDirOnCounterBreakout = config.InvalidateDirOnCounterBreakout
            e.InvalidateDirOnWindowExpiry = config.InvalidateDirOnWindowExpiry
            e.ContractsPerEntry = config.ContractsPerEntry
            e.AiVetoEnabled = config.AiVetoEnabled
            e.EnableLong = config.EnableLong
            e.EnableShort = config.EnableShort
            e.MinSlEditStepTicks = config.MinSlEditStepTicks
            e.MaxConcurrentPositions = config.MaxConcurrentPositions
            Return e
        End Function

    End Class

End Namespace
