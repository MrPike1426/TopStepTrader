Imports System.Threading
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Market

    ''' <summary>
    ''' Abstraction over bar ingestion sources.
    ''' <see cref="TopStepXBarIngestionService"/> is the sole implementation, used for all
    ''' views: live trading (Hydra, Asset Bassett, Sniper, CryptoJoe, Test Trade, PumpNDump)
    ''' and historical backtest/QuantLab bar fetches.
    ''' </summary>
    Public Interface IBarIngestionService

        ''' <summary>
        ''' Fetch and store up to <paramref name="barsToFetch"/> bars for a contract.
        ''' Skips bars already in the database.
        ''' </summary>
        Function IngestAsync(contractId As String,
                             timeframe As BarTimeframe,
                             Optional barsToFetch As Integer = 500,
                             Optional cancel As CancellationToken = Nothing) As Task(Of Integer)

        ''' <summary>Returns the N most recent bars from the DB for the ML/strategy engine.</summary>
        Function GetBarsForMLAsync(contractId As String,
                                   timeframe As BarTimeframe,
                                   Optional maxBars As Integer = 200,
                                   Optional cancel As CancellationToken = Nothing) As Task(Of IList(Of MarketBar))

        ''' <summary>
        ''' Fetch the latest price via a lightweight 15-second bar call.
        ''' Returns 0 on failure or when the source does not support sub-minute data.
        ''' No persistence — used exclusively for P&amp;L calculation during live positions.
        ''' </summary>
        Function GetLatestPriceAsync(contractId As String,
                                     Optional cancel As CancellationToken = Nothing) As Task(Of Decimal)

        ''' <summary>
        ''' FEAT-11: Fetch the N most recent 15-second bars directly from the broker API
        ''' without persisting them to the database.  Used by MultiConfluence flat sessions
        ''' so indicators advance on 15-second closes without accumulating high-frequency rows.
        ''' Returns an empty list on failure; callers must handle gracefully.
        ''' </summary>
        Function GetLiveBarsAsync(contractId As String,
                                  timeframe As BarTimeframe,
                                  barCount As Integer,
                                  Optional cancel As CancellationToken = Nothing) As Task(Of IList(Of MarketBar))

    End Interface

End Namespace
