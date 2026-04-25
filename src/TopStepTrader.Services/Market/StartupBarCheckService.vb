Imports System.Text
Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Data.Repositories

Namespace TopStepTrader.Services.Market

    ''' <summary>
    ''' Result describing a single contract/timeframe combination that is missing
    ''' or has stale data (latest stored bar older than 60 days).
    ''' </summary>
    Public Class StartupBarCheckResult
        Public Property ContractId As String
        Public Property FriendlyName As String
        Public Property Timeframe As BarTimeframe
        Public Property LatestBar As DateTimeOffset?
    End Class

    ''' <summary>
    ''' Checks for missing bars across all favourite contracts for the past 60 days
    ''' and optionally backfills them via <see cref="IBarCollectionService"/>.
    ''' </summary>
    Public Interface IStartupBarCheckService

        ''' <summary>
        ''' Scans the database for favourite contracts/timeframes where bar data is absent
        ''' or stale (latest bar older than 60 days).  Returns one entry per missing slot.
        ''' </summary>
        Function CheckMissingBarsAsync(Optional cancel As CancellationToken = Nothing) As Task(Of List(Of StartupBarCheckResult))

        ''' <summary>
        ''' Downloads and stores bars for each result returned by <see cref="CheckMissingBarsAsync"/>
        ''' covering the past 60 days.
        ''' </summary>
        Function BackfillAsync(missing As List(Of StartupBarCheckResult),
                               Optional progress As IProgress(Of String) = Nothing,
                               Optional cancel As CancellationToken = Nothing) As Task

    End Interface

    ''' <inheritdoc cref="IStartupBarCheckService"/>
    Public Class StartupBarCheckService
        Implements IStartupBarCheckService

        ''' <summary>Timeframes checked at startup — covers all strategy and backtest use-cases.</summary>
        Private Shared ReadOnly CheckTimeframes As BarTimeframe() = {
            BarTimeframe.FifteenSecond,
            BarTimeframe.FiveMinute,
            BarTimeframe.FifteenMinute,
            BarTimeframe.OneHour,
            BarTimeframe.Daily
        }

        Private Const LookbackDays As Integer = 60

        Private ReadOnly _barRepository As BarRepository
        Private ReadOnly _barCollectionService As IBarCollectionService
        Private ReadOnly _logger As ILogger(Of StartupBarCheckService)

        Public Sub New(barRepository As BarRepository,
                       barCollectionService As IBarCollectionService,
                       logger As ILogger(Of StartupBarCheckService))
            _barRepository = barRepository
            _barCollectionService = barCollectionService
            _logger = logger
        End Sub

        ''' <inheritdoc/>
        Public Async Function CheckMissingBarsAsync(Optional cancel As CancellationToken = Nothing) As Task(Of List(Of StartupBarCheckResult)) _
            Implements IStartupBarCheckService.CheckMissingBarsAsync

            Dim cutoff = DateTimeOffset.UtcNow.AddDays(-LookbackDays)
            Dim contracts = FavouriteContracts.GetDefaults(BrokerType.TopStepX)
            Dim results As New List(Of StartupBarCheckResult)()

            For Each contract In contracts
                For Each tf In CheckTimeframes
                    Try
                        Dim latest = Await _barRepository.GetLatestTimestampAsync(contract.PxContractId, tf, cancel)
                        Dim isMissing = (Not latest.HasValue) OrElse (latest.Value < cutoff)
                        If isMissing Then
                            _logger.LogDebug(
                                "StartupBarCheck: {Contract} {Tf} is missing/stale (latest={Latest})",
                                contract.PxContractId, tf,
                                If(latest.HasValue, latest.Value.ToString("g"), "none"))
                            results.Add(New StartupBarCheckResult With {
                                .ContractId = contract.PxContractId,
                                .FriendlyName = contract.Name,
                                .Timeframe = tf,
                                .LatestBar = latest
                            })
                        End If
                    Catch ex As OperationCanceledException
                        Throw
                    Catch ex As Exception
                        _logger.LogWarning(ex, "StartupBarCheck: error checking {Contract} {Tf}",
                                           contract.PxContractId, tf)
                    End Try
                Next
            Next

            _logger.LogInformation(
                "StartupBarCheck: {Count} missing bar slot(s) found across {Contracts} favourite contracts",
                results.Count, contracts.Count)
            Return results
        End Function

        ''' <inheritdoc/>
        Public Async Function BackfillAsync(missing As List(Of StartupBarCheckResult),
                                            Optional progress As IProgress(Of String) = Nothing,
                                            Optional cancel As CancellationToken = Nothing) As Task _
            Implements IStartupBarCheckService.BackfillAsync

            If missing Is Nothing OrElse missing.Count = 0 Then Return

            Dim startDate = DateTime.UtcNow.AddDays(-LookbackDays).Date
            Dim endDate = DateTime.UtcNow.Date

            For Each item In missing
                Try
                    progress?.Report($"Downloading {item.FriendlyName} ({TimeframeLabel(item.Timeframe)})…")
                    _logger.LogInformation(
                        "StartupBarCheck backfill: {Contract} {Tf} {From:d} → {To:d}",
                        item.ContractId, item.Timeframe, startDate, endDate)
                    Await _barCollectionService.EnsureBarsAsync(
                        item.ContractId, startDate, endDate, item.Timeframe, progress, cancel)
                Catch ex As OperationCanceledException
                    Throw
                Catch ex As Exception
                    _logger.LogWarning(ex, "StartupBarCheck backfill failed for {Contract} {Tf}",
                                       item.ContractId, item.Timeframe)
                End Try
            Next
        End Function

        ''' <summary>Returns a short human-readable label for a timeframe.</summary>
        Public Shared Function TimeframeLabel(tf As BarTimeframe) As String
            Select Case tf
                Case BarTimeframe.OneMinute : Return "1m"
                Case BarTimeframe.ThreeMinute : Return "3m"
                Case BarTimeframe.FiveMinute : Return "5m"
                Case BarTimeframe.TenMinute : Return "10m"
                Case BarTimeframe.FifteenMinute : Return "15m"
                Case BarTimeframe.ThirtyMinute : Return "30m"
                Case BarTimeframe.OneHour : Return "1h"
                Case BarTimeframe.TwoHour : Return "2h"
                Case BarTimeframe.FourHour : Return "4h"
                Case BarTimeframe.Daily : Return "Daily"
                Case Else : Return tf.ToString()
            End Select
        End Function

        ''' <summary>
        ''' Builds a summary string listing missing contracts grouped by name, suitable for
        ''' display in a MessageBox prompt.
        ''' </summary>
        Public Shared Function BuildSummary(missing As List(Of StartupBarCheckResult)) As String
            Dim sb As New StringBuilder()
            sb.AppendLine($"Missing bar data found for {missing.Count} contract/timeframe slot(s):")
            sb.AppendLine()
            For Each grp In missing.GroupBy(Function(r) r.FriendlyName)
                Dim tfs = String.Join(", ", grp.Select(Function(r) TimeframeLabel(r.Timeframe)))
                Dim latest = grp.First().LatestBar
                Dim latestStr = If(latest.HasValue, $"last bar {latest.Value:dd MMM yyyy}", "no data")
                sb.AppendLine($"  • {grp.Key} — {tfs}  ({latestStr})")
            Next
            sb.AppendLine()
            sb.Append("Download missing bars for all favourite contracts?")
            Return sb.ToString()
        End Function

    End Class

End Namespace
