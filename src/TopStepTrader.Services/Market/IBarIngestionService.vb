Imports System.Threading
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Market

    ''' <summary>
    ''' Abstraction over bar ingestion sources.
    ''' Yahoo Finance implementation (<see cref="BarIngestionService"/>) is used for backtest
    ''' and Quant Lab.  TopStepX implementation (<see cref="TopStepXBarIngestionService"/>)
    ''' is used for all live trading views (Hydra, Asset Bassett, Sniper, CryptoJoe,
    ''' Test Trade, PumpNDump).
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

    End Interface

End Namespace
