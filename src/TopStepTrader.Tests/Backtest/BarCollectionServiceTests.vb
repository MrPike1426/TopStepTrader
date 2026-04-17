Imports Microsoft.Extensions.Logging.Abstractions
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Services.Market
Imports Xunit

Namespace TopStepTrader.Tests.Backtest

    ''' <summary>
    ''' Unit tests for BarCollectionService input-validation paths.
    ''' These tests exercise the fast-fail guards that run before any I/O occurs,
    ''' allowing them to pass Nothing for the HistoryClient and BarRepository
    ''' dependencies without causing NullReferenceExceptions.
    ''' TICKET-006 Phase 5.
    ''' </summary>
    Public Class BarCollectionServiceTests

        Private Shared Function MakeSut() As BarCollectionService
            ' Dependencies are Nothing — safe because the tested paths return before
            ' either _yahooClient or _barRepository is accessed.
            Return New BarCollectionService(
                Nothing,
                Nothing,
                NullLogger(Of BarCollectionService).Instance)
        End Function

        ' ══════════════════════════════════════════════════════════════════
        ' Empty / blank contract ID — fast-fail guard
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Async Function EnsureBarsAsync_EmptyContractId_ReturnsFail() As Task
            Dim sut = MakeSut()

            Dim result = Await sut.EnsureBarsAsync("", Date.Today.AddDays(-7), Date.Today, BarTimeframe.FiveMinute)

            Assert.False(result.Success)
            Assert.Equal(0, result.BarCount)
        End Function

        <Fact>
        Public Async Function EnsureBarsAsync_EmptyContractId_MessageIndicatesRequired() As Task
            Dim sut = MakeSut()

            Dim result = Await sut.EnsureBarsAsync("", Date.Today.AddDays(-7), Date.Today, BarTimeframe.FiveMinute)

            Assert.Contains("required", result.Message, StringComparison.OrdinalIgnoreCase)
        End Function

        <Fact>
        Public Async Function EnsureBarsAsync_WhitespaceContractId_ReturnsFail() As Task
            Dim sut = MakeSut()

            Dim result = Await sut.EnsureBarsAsync("   ", Date.Today.AddDays(-7), Date.Today, BarTimeframe.FiveMinute)

            Assert.False(result.Success)
            Assert.Equal(0, result.BarCount)
        End Function

        <Fact>
        Public Async Function EnsureBarsAsync_EmptyContractId_ResultContractIdPreserved() As Task
            ' The Fail helper echoes back the contractId supplied by the caller.
            Dim sut = MakeSut()

            Dim result = Await sut.EnsureBarsAsync("", Date.Today.AddDays(-7), Date.Today, BarTimeframe.FiveMinute)

            Assert.Equal("", result.ContractId)
        End Function

        ' ══════════════════════════════════════════════════════════════════
        ' Progress reporting on fast-fail path
        ' ══════════════════════════════════════════════════════════════════

        <Fact>
        Public Async Function EnsureBarsAsync_EmptyContractId_ReportsFailureSymbol() As Task
            Dim sut = MakeSut()
            Dim messages As New List(Of String)()
            ' SyncProgress invokes the callback synchronously, ensuring messages are
            ' populated before the assertion — no race with ThreadPool.
            Dim progress As IProgress(Of String) = New SyncProgress(Of String)(
                Sub(msg) messages.Add(msg))

            Await sut.EnsureBarsAsync("", Date.Today.AddDays(-7), Date.Today, BarTimeframe.FiveMinute, progress)

            Assert.NotEmpty(messages)
            Assert.Contains(messages, Function(m) m.StartsWith("✗"))
        End Function

    End Class

    ''' <summary>
    ''' Synchronous IProgress(Of T) implementation for use in unit tests.
    ''' The built-in Progress(Of T) posts callbacks to the ThreadPool when no
    ''' SynchronizationContext is present, making assertions race-prone.
    ''' SyncProgress calls the delegate inline on the reporting thread.
    ''' </summary>
    Friend NotInheritable Class SyncProgress(Of T)
        Implements IProgress(Of T)

        Private ReadOnly _callback As Action(Of T)

        Public Sub New(callback As Action(Of T))
            _callback = callback
        End Sub

        Public Sub Report(value As T) Implements IProgress(Of T).Report
            _callback(value)
        End Sub

    End Class

End Namespace
