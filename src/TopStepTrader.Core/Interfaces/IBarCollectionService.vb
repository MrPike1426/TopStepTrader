Imports System.Threading
Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' Ensures price bars for a contract and timeframe are cached in the local SQLite database.
    '''
    ''' Used by the Backtest page (TICKET-006) to download historic bars before running a
    ''' backtest.  Can be called regardless of market open/closed status — historic bars
    ''' are always available from the ProjectX API.
    ''' Supported timeframes: OneMinute, FiveMinute, FifteenMinute, ThirtyMinute, OneHour.
    ''' </summary>
    Public Interface IBarCollectionService

        ''' <summary>
        ''' Checks whether sufficient bars (≥ 50) already exist in SQLite for the contract,
        ''' timeframe and date range.  If not, downloads missing bars from the API in paginated
        ''' 500-bar batches and stores them in the local database.
        ''' </summary>
        ''' <param name="contractId">Long-form contract ID (e.g. "CON.F.US.MES.H26")</param>
        ''' <param name="startDate">Inclusive start of the backtest date range</param>
        ''' <param name="endDate">Inclusive end of the backtest date range</param>
        ''' <param name="timeframe">Bar timeframe to download and check (e.g. FiveMinute)</param>
        ''' <param name="progress">
        '''   Optional progress reporter — called with a human-readable status string after
        '''   each API batch, e.g. "⏳ Downloaded 500 bars for MES.H26 (stored 487 new)..."
        ''' </param>
        ''' <param name="cancel">Optional cancellation token</param>
        ''' <returns><see cref="BarEnsureResult"/> describing success, bar count, and message.</returns>
        Function EnsureBarsAsync(contractId As String,
                                 startDate As Date,
                                 endDate As Date,
                                 timeframe As BarTimeframe,
                                 Optional progress As IProgress(Of String) = Nothing,
                                 Optional cancel As CancellationToken = Nothing) As Task(Of BarEnsureResult)

    End Interface

    ''' <summary>Result returned by <see cref="IBarCollectionService.EnsureBarsAsync"/>.</summary>
    Public Class BarEnsureResult
        ''' <summary>True when at least 50 bars are available for the date range.</summary>
        Public Property Success As Boolean
        ''' <summary>Total bars available in SQLite for the requested contract + date range.</summary>
        Public Property BarCount As Integer
        ''' <summary>Contract ID this result applies to.</summary>
        Public Property ContractId As String
        ''' <summary>Human-readable status message (shown in the Backtest page UI).</summary>
        Public Property Message As String
    End Class

End Namespace
