Imports System.Collections.Concurrent
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.API.Http.ProjectX
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Data

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' Resolves live ProjectX contract IDs once per day and caches them in SQLite.
    ''' All strategies read from the in-memory cache via GetContractId — zero per-call latency.
    ''' </summary>
    Public Class ContractResolutionService
        Implements IContractResolutionService

        Private ReadOnly _db As AppDbContext
        Private ReadOnly _contractClient As PXContractClient
        Private ReadOnly _logger As ILogger(Of ContractResolutionService)

        Private ReadOnly _cache As New ConcurrentDictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _failed As New List(Of String)

        Public Sub New(db As AppDbContext,
                       contractClient As PXContractClient,
                       logger As ILogger(Of ContractResolutionService))
            _db = db
            _contractClient = contractClient
            _logger = logger
        End Sub

        Public ReadOnly Property FailedSymbols As IReadOnlyList(Of String) Implements IContractResolutionService.FailedSymbols
            Get
                Return _failed.AsReadOnly()
            End Get
        End Property

        Public Function IsResolved(rootSymbol As String) As Boolean Implements IContractResolutionService.IsResolved
            Return _cache.ContainsKey(rootSymbol)
        End Function

        Public Function GetContractId(rootSymbol As String) As String Implements IContractResolutionService.GetContractId
            Dim contractId As String = Nothing
            If _cache.TryGetValue(rootSymbol, contractId) Then Return contractId
            Throw New InvalidOperationException(
                $"Contract ID for root symbol '{rootSymbol}' was not resolved. " &
                "Check startup logs — the ProjectX API call may have failed for this instrument.")
        End Function

        Public Async Function InitialiseAsync(Optional cancel As Threading.CancellationToken = Nothing) As Task Implements IContractResolutionService.InitialiseAsync
            _cache.Clear()
            _failed.Clear()

            Dim today = DateTime.UtcNow.ToString("yyyy-MM-dd")
            Dim favourites = FavouriteContracts.GetDefaults()

            ' Load all existing cache rows into memory
            Dim existing = Await _db.ContractCache.ToDictionaryAsync(
                Function(r) r.RootSymbol, StringComparer.OrdinalIgnoreCase, cancel)

            ' Determine which symbols are fresh vs stale/missing
            Dim staleSymbols As New List(Of String)
            For Each fc In favourites
                Dim root = fc.PxRootSymbol
                If String.IsNullOrWhiteSpace(root) Then Continue For

                Dim row As Entities.ContractCacheEntity = Nothing
                If existing.TryGetValue(root, row) AndAlso row.LastUpdated = today Then
                    _cache(root) = row.ContractId
                    _logger.LogInformation("ContractCache HIT  [{Root}] → {Id}", root, row.ContractId)
                Else
                    staleSymbols.Add(root)
                End If
            Next

            If staleSymbols.Count = 0 Then
                _logger.LogInformation("ContractCache: all {Count} symbols fresh — no API call needed.", _cache.Count)
                Return
            End If

            _logger.LogInformation("ContractCache: resolving {Count} stale/missing symbols via ProjectX API: {Symbols}",
                                   staleSymbols.Count, String.Join(", ", staleSymbols))

            ' Query the ProjectX API for each stale root symbol
            Dim anyUpserted As Boolean = False
            For Each root In staleSymbols
                Try
                    ' live:=False — TopStepX practice accounts serve simulated contracts;
                    ' passing live:=True returns empty results for all micro futures.
                    Dim response = Await _contractClient.SearchContractsAsync(root, live:=False, cancel:=cancel)

                    ' Log the raw API response before any filtering to aid diagnosis.
                    Dim previewIds = If(response.Contracts.Count > 0,
                        String.Join(", ", response.Contracts.Take(5).Select(Function(c) c.ContractId)),
                        "(none)")
                    _logger.LogInformation(
                        "ContractCache API [{Root}] success={Ok} errorCode={Err} count={N} ids=[{Ids}]",
                        root, response.Success, response.ErrorCode, response.Contracts.Count, previewIds)

                    If Not response.Success OrElse response.Contracts.Count = 0 Then
                        _logger.LogWarning("ContractCache MISS [{Root}] — API returned no contracts (errorCode={Err}).",
                                           root, response.ErrorCode)
                        ' Stale-but-usable fallback: use any existing SQLite row rather than
                        ' fully blocking trading after a transient API hiccup.
                        Dim staleRow As Entities.ContractCacheEntity = Nothing
                        If existing.TryGetValue(root, staleRow) Then
                            _cache(root) = staleRow.ContractId
                            _logger.LogWarning(
                                "ContractCache FALLBACK [{Root}] → {Id} (stale row from {Date})",
                                root, staleRow.ContractId, staleRow.LastUpdated)
                        Else
                            _failed.Add(root)
                        End If
                        Continue For
                    End If

                    ' Pick the front-month: sort by contract ID ascending (e.g. H26 < M26 < U26 < Z26)
                    ' then take the first one that contains the root symbol in its ID
                    Dim match = response.Contracts _
                        .Where(Function(c) c.ContractId.IndexOf(root, StringComparison.OrdinalIgnoreCase) >= 0) _
                        .OrderBy(Function(c) c.ContractId) _
                        .FirstOrDefault()

                    If match Is Nothing Then
                        _logger.LogWarning("ContractCache MISS [{Root}] — no matching contract in {N} search results.",
                                           root, response.Contracts.Count)
                        ' Stale-but-usable fallback
                        Dim staleRow As Entities.ContractCacheEntity = Nothing
                        If existing.TryGetValue(root, staleRow) Then
                            _cache(root) = staleRow.ContractId
                            _logger.LogWarning(
                                "ContractCache FALLBACK [{Root}] → {Id} (stale row from {Date})",
                                root, staleRow.ContractId, staleRow.LastUpdated)
                        Else
                            _failed.Add(root)
                        End If
                        Continue For
                    End If

                    ' Upsert to SQLite
                    Dim row As Entities.ContractCacheEntity = Nothing
                    If existing.TryGetValue(root, row) Then
                        row.ContractId = match.ContractId
                        row.LastUpdated = today
                        _db.ContractCache.Update(row)
                    Else
                        row = New Entities.ContractCacheEntity With {
                            .RootSymbol = root,
                            .ContractId = match.ContractId,
                            .LastUpdated = today
                        }
                        _db.ContractCache.Add(row)
                    End If

                    _cache(root) = match.ContractId
                    anyUpserted = True
                    _logger.LogInformation("ContractCache RESOLVED [{Root}] → {Id}", root, match.ContractId)

                Catch ex As Exception
                    _logger.LogError(ex, "ContractCache ERROR resolving [{Root}]", root)
                    ' Stale-but-usable fallback on exception too
                    Dim staleRow As Entities.ContractCacheEntity = Nothing
                    If existing.TryGetValue(root, staleRow) Then
                        _cache(root) = staleRow.ContractId
                        _logger.LogWarning(
                            "ContractCache FALLBACK [{Root}] → {Id} (stale row from {Date})",
                            root, staleRow.ContractId, staleRow.LastUpdated)
                    Else
                        _failed.Add(root)
                    End If
                End Try
            Next

            ' Persist all upserts in one round-trip
            If anyUpserted Then
                Await _db.SaveChangesAsync(cancel)
            End If

            If _failed.Count > 0 Then
                _logger.LogWarning("ContractCache: {Count} symbol(s) failed to resolve: {Symbols}",
                                   _failed.Count, String.Join(", ", _failed))
            End If
        End Function

    End Class

End Namespace
