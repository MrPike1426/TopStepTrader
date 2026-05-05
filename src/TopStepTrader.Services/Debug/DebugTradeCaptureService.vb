Imports System.Collections.Generic
Imports System.Threading
Imports System.Threading.Channels
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models.Debug
Imports TopStepTrader.Data.Debug

Namespace TopStepTrader.Services.Debug

    Public Class DebugTradeCaptureService
        Implements IDebugTradeCaptureService
        Implements IDisposable

        Private ReadOnly _db As DebugTradeDbContext
        Private ReadOnly _logger As ILogger(Of DebugTradeCaptureService)
        Private ReadOnly _channel As Channel(Of DebugMessage)
        Private ReadOnly _cts As New CancellationTokenSource()
        Private ReadOnly _consumerTask As Task
        Private _isEnabled As Boolean = False
        Private _disposed As Boolean = False

        ' ── Internal message hierarchy ──────────────────────────────────────────
        Private MustInherit Class DebugMessage
        End Class

        Private NotInheritable Class BeginMsg
            Inherits DebugMessage
            Public Property Header As DebugTradeRecord
        End Class

        Private NotInheritable Class SnapMsg
            Inherits DebugMessage
            Public Property Snap As DebugSnapshotRecord
        End Class

        Private NotInheritable Class EndMsg
            Inherits DebugMessage
            Public Property TradeId As String
            Public Property ClosedUtc As DateTime
            Public Property RealisedPnl As Nullable(Of Decimal)
        End Class

        Private NotInheritable Class FillMsg
            Inherits DebugMessage
            Public Property TradeId As String
            Public Property FillPrice As Decimal
            Public Property FillConfirmedTime As DateTime
        End Class

        Public Property IsEnabled As Boolean Implements IDebugTradeCaptureService.IsEnabled
            Get
                Return _isEnabled
            End Get
            Set(value As Boolean)
                _isEnabled = value
            End Set
        End Property

        Public Sub New(db As DebugTradeDbContext, logger As ILogger(Of DebugTradeCaptureService))
            _db = db
            _logger = logger
            _channel = Channel.CreateBounded(Of DebugMessage)(
                New BoundedChannelOptions(5000) With {
                    .FullMode = BoundedChannelFullMode.DropOldest,
                    .SingleReader = True,
                    .SingleWriter = False
                })
            _consumerTask = Task.Run(AddressOf ConsumeLoopAsync)
        End Sub

        Public Sub BeginTrade(header As DebugTradeRecord) Implements IDebugTradeCaptureService.BeginTrade
            If Not _isEnabled Then Return
            _channel.Writer.TryWrite(New BeginMsg With {.Header = header})
        End Sub

        Public Sub RecordSnapshot(snap As DebugSnapshotRecord) Implements IDebugTradeCaptureService.RecordSnapshot
            If Not _isEnabled Then Return
            _channel.Writer.TryWrite(New SnapMsg With {.Snap = snap})
        End Sub

        Public Sub UpdateFill(tradeId As String, fillPrice As Decimal, fillConfirmedTime As DateTime) Implements IDebugTradeCaptureService.UpdateFill
            If Not _isEnabled Then Return
            _channel.Writer.TryWrite(New FillMsg With {.TradeId = tradeId, .FillPrice = fillPrice, .FillConfirmedTime = fillConfirmedTime})
        End Sub

        Public Sub EndTrade(tradeId As String, closedUtc As DateTime, Optional realisedPnl As Nullable(Of Decimal) = Nothing) Implements IDebugTradeCaptureService.EndTrade
            If Not _isEnabled Then Return
            _channel.Writer.TryWrite(New EndMsg With {.TradeId = tradeId, .ClosedUtc = closedUtc, .RealisedPnl = realisedPnl})
        End Sub

        Private Async Function ConsumeLoopAsync() As Task
            Try
                Await _db.EnsureSchemaAsync()
                Await _db.PurgeOldTradesAsync()
            Catch ex As Exception
                _logger.LogWarning(ex, "DebugCapture: schema/purge init failed — writes will continue best-effort")
            End Try

            While Not _cts.IsCancellationRequested
                Try
                    Await Task.Delay(1000, _cts.Token)
                Catch ex As OperationCanceledException
                    Exit While
                Catch
                    Exit While
                End Try
                Await FlushBatchAsync()
            End While

            Await FlushBatchAsync()
        End Function

        Private Async Function FlushBatchAsync() As Task
            Dim headers As New List(Of DebugTradeRecord)()
            Dim snapshots As New List(Of DebugSnapshotRecord)()
            Dim endTrades As New List(Of (TradeId As String, ClosedUtc As DateTime, RealisedPnl As Nullable(Of Decimal)))()
            Dim fillUpdates As New List(Of (TradeId As String, FillPrice As Decimal, FillConfirmedTime As DateTime))()

            Dim msg As DebugMessage = Nothing
            While _channel.Reader.TryRead(msg)
                If TypeOf msg Is BeginMsg Then
                    headers.Add(DirectCast(msg, BeginMsg).Header)
                ElseIf TypeOf msg Is SnapMsg Then
                    snapshots.Add(DirectCast(msg, SnapMsg).Snap)
                ElseIf TypeOf msg Is FillMsg Then
                    Dim fm = DirectCast(msg, FillMsg)
                    fillUpdates.Add((fm.TradeId, fm.FillPrice, fm.FillConfirmedTime))
                ElseIf TypeOf msg Is EndMsg Then
                    Dim em = DirectCast(msg, EndMsg)
                    endTrades.Add((em.TradeId, em.ClosedUtc, em.RealisedPnl))
                End If
            End While

            If headers.Count = 0 AndAlso snapshots.Count = 0 AndAlso endTrades.Count = 0 AndAlso fillUpdates.Count = 0 Then Return

            Try
                Await _db.WriteBatchAsync(headers, snapshots, endTrades, fillUpdates)
            Catch ex As Exception
                _logger.LogWarning(ex, "DebugCapture: batch write failed — {Count} items dropped",
                                   headers.Count + snapshots.Count + endTrades.Count + fillUpdates.Count)
            End Try
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                _disposed = True
                _cts.Cancel()
                _channel.Writer.TryComplete()
                Try
                    _consumerTask.Wait(TimeSpan.FromSeconds(3))
                Catch
                End Try
                _cts.Dispose()
            End If
        End Sub

    End Class

End Namespace
