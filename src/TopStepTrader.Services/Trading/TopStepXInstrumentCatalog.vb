Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.API.Http.ProjectX
Imports TopStepTrader.API.Models.Responses
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Trading

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' Provides per-instrument tick metadata for TopStepX / ProjectX.
    ''' At startup (or after TTL expiry) it calls the ProjectX /api/Contract/available
    ''' endpoint and merges the live tick data with the FavouriteContracts defaults.
    ''' Falls back to FavouriteContracts when the API is unavailable or the contract
    ''' is not returned by the API.
    ''' </summary>
    Public Class TopStepXInstrumentCatalog

        Private ReadOnly _contractClient As PXContractClient
        Private ReadOnly _logger As ILogger(Of TopStepXInstrumentCatalog)

        ' In-memory cache: PX contractId → InstrumentInfo
        Private ReadOnly _cache As New Dictionary(Of String, InstrumentInfo)(StringComparer.OrdinalIgnoreCase)
        ' Root-symbol → resolved active front-month contract ID (e.g. "MES" → "MESM26")
        Private ReadOnly _rootToActiveId As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Private _cacheBuiltAt As DateTimeOffset = DateTimeOffset.MinValue
        Private ReadOnly _cacheTtl As TimeSpan = TimeSpan.FromMinutes(15)
        Private ReadOnly _lock As New SemaphoreSlim(1, 1)

        Public Sub New(contractClient As PXContractClient,
                       logger As ILogger(Of TopStepXInstrumentCatalog))
            _contractClient = contractClient
            _logger = logger
        End Sub

        ''' <summary>
        ''' Returns tick metadata for the given ProjectX contractId (e.g. "CON.F.US.MGC.J26").
        ''' May trigger a cache refresh if TTL has elapsed.
        ''' Overridable to allow test fakes.
        ''' </summary>
        Public Overridable Async Function GetInfoAsync(pxContractId As String,
                                                       Optional cancel As CancellationToken = Nothing) As Task(Of InstrumentInfo)
            Await EnsureCacheAsync(cancel)

            Dim info As InstrumentInfo = Nothing
            If _cache.TryGetValue(pxContractId, info) Then
                Return info
            End If

            ' Fall back to FavouriteContracts local defaults
            Return BuildFromDefaults(pxContractId)
        End Function

        ''' <summary>
        ''' Validates and clamps <paramref name="requestedTicks"/> to the instrument's minimum
        ''' stop distance. Logs a warning when bumped.
        ''' Overridable to allow test fakes.
        ''' </summary>
        Public Overridable Async Function ClampStopTicksAsync(pxContractId As String, requestedTicks As Integer,
                                                   Optional cancel As CancellationToken = Nothing) As Task(Of Integer)
            Dim info = Await GetInfoAsync(pxContractId, cancel)
            Dim clamped = TickMath.ClampToMinStop(requestedTicks, info.MinStopTicks)
            If clamped <> requestedTicks Then
                _logger.LogWarning(
                    "TopStepXInstrumentCatalog: stop bumped from {Req} → {Min} ticks for {Contract} (API min={Min})",
                    requestedTicks, clamped, pxContractId, info.MinStopTicks)
            End If
            Return clamped
        End Function

        ''' <summary>
        ''' Returns the live front-month contract ID for <paramref name="fav"/>, resolved via the
        ''' ProjectX /api/Contract/search endpoint.
        ''' Falls back to <paramref name="fav"/>.PxContractId when the root symbol is absent or
        ''' the API search has not yet produced a result.
        ''' Overridable to allow test fakes.
        ''' </summary>
        Public Overridable Async Function GetResolvedContractIdAsync(
            fav As FavouriteContract,
            Optional cancel As CancellationToken = Nothing) As Task(Of String)

            If String.IsNullOrEmpty(fav.PxRootSymbol) Then Return fav.PxContractId

            ' ── Fast path: already resolved for this root (dictionary hit, no API) ────────
            Dim cached As String = Nothing
            If _rootToActiveId.TryGetValue(fav.PxRootSymbol, cached) Then Return cached

            ' ── Slow path: targeted search for this ONE root symbol only ─────────────────
            ' Do NOT call EnsureCacheAsync here — that rebuilds all 8 contracts regardless
            ' of which one was requested.  A single SearchForFrontMonthAsync call is enough.
            If _contractClient Is Nothing Then Return fav.PxContractId

            Await _lock.WaitAsync(cancel)
            Try
                ' Double-check inside lock in case another thread resolved it while we waited
                If _rootToActiveId.TryGetValue(fav.PxRootSymbol, cached) Then Return cached

                Dim match = Await SearchForFrontMonthAsync(fav, cancel)
                If match IsNot Nothing Then
                    _rootToActiveId(fav.PxRootSymbol) = match.ContractId
                    ' Populate the info cache for the resolved ID so GetInfoAsync hits it without
                    ' needing a full refresh (e.g. when ClampStopTicksAsync is called at order time)
                    If Not _cache.ContainsKey(match.ContractId) Then
                        Dim local = BuildFromDefaults(fav.PxContractId)
                        _cache(match.ContractId) = New InstrumentInfo With {
                            .PxContractId = match.ContractId,
                            .DisplayName = If(match.Name, local.DisplayName),
                            .TickSize = If(match.TickSize > 0, match.TickSize, local.TickSize),
                            .TickValue = If(match.TickValue > 0, match.TickValue, local.TickValue),
                            .MinStopTicks = If(match.MinInitialMarginTicks > 0,
                                               CType(match.MinInitialMarginTicks, Integer?),
                                               local.MinStopTicks)
                        }
                    End If
                    Return match.ContractId
                End If
            Catch ex As Exception
                _logger.LogWarning(ex,
                    "TopStepXInstrumentCatalog: targeted search failed for root '{Root}'", fav.PxRootSymbol)
            Finally
                _lock.Release()
            End Try

            _logger.LogWarning(
                "TopStepXInstrumentCatalog: could not resolve root '{Root}' — falling back to hardcoded '{Id}'.",
                fav.PxRootSymbol, fav.PxContractId)
            Return fav.PxContractId
        End Function

        ' ── Cache management ─────────────────────────────────────────────────────────

        Private Async Function EnsureCacheAsync(cancel As CancellationToken) As Task
            If DateTimeOffset.UtcNow - _cacheBuiltAt < _cacheTtl Then Return

            Await _lock.WaitAsync(cancel)
            Try
                ' Double-check inside lock
                If DateTimeOffset.UtcNow - _cacheBuiltAt < _cacheTtl Then Return
                Await RefreshCacheAsync(cancel)
            Finally
                _lock.Release()
            End Try
        End Function

        Private Async Function RefreshCacheAsync(cancel As CancellationToken) As Task
            If _contractClient Is Nothing Then
                SeedFromDefaults()
                _cacheBuiltAt = DateTimeOffset.UtcNow
                Return
            End If

            ' Always start with local defaults so callers have a baseline even if the API is slow
            SeedFromDefaults()
            _rootToActiveId.Clear()

            ' Step 1: Try the full available list to get live tick data
            Try
                Dim resp = Await _contractClient.GetAvailableContractsAsync(live:=False, cancel:=cancel)
                If resp?.Contracts IsNot Nothing AndAlso resp.Contracts.Count > 0 Then
                    ' Diagnostic: log first 5 raw contract IDs to confirm the "id" field maps correctly
                    Dim sample = resp.Contracts.Take(5).Select(Function(c) $"'{c.ContractId}'").ToList()
                    _logger.LogInformation(
                        "TopStepXInstrumentCatalog: available list returned {Total} contracts. " &
                        "Sample IDs (first 5): {Sample}",
                        resp.Contracts.Count, String.Join(", ", sample))

                    Dim skipped = 0
                    For Each c In resp.Contracts
                        If String.IsNullOrEmpty(c.ContractId) Then
                            skipped += 1
                            Continue For
                        End If
                        Dim local = BuildFromDefaults(c.ContractId)
                        _cache(c.ContractId) = New InstrumentInfo With {
                            .PxContractId = c.ContractId,
                            .DisplayName = If(c.Name, local.DisplayName),
                            .TickSize = If(c.TickSize > 0, c.TickSize, local.TickSize),
                            .TickValue = If(c.TickValue > 0, c.TickValue, local.TickValue),
                            .MinStopTicks = If(c.MinInitialMarginTicks > 0,
                                               CType(c.MinInitialMarginTicks, Integer?),
                                               local.MinStopTicks)
                        }
                    Next
                    If skipped > 0 Then
                        _logger.LogWarning(
                            "TopStepXInstrumentCatalog: {Skipped}/{Total} contracts had empty ContractId — " &
                            "check that ContractDto maps the correct JSON field name.",
                            skipped, resp.Contracts.Count)
                    End If
                    _logger.LogInformation("TopStepXInstrumentCatalog: cached {N} contracts from available list", _cache.Count)
                Else
                    _logger.LogWarning("TopStepXInstrumentCatalog: available list empty — using local defaults for tick data")
                End If
            Catch ex As Exception
                _logger.LogWarning(ex, "TopStepXInstrumentCatalog: GetAvailableContracts failed — using local defaults")
            End Try

            ' Step 2: Resolve front-month contract ID for each favourite via root-symbol search
            For Each fav In FavouriteContracts.GetDefaults(BrokerType.TopStepX)
                If String.IsNullOrEmpty(fav.PxRootSymbol) Then Continue For
                Try
                    Dim match = Await SearchForFrontMonthAsync(fav, cancel)
                    If match IsNot Nothing Then
                        _rootToActiveId(fav.PxRootSymbol) = match.ContractId
                        ' Ensure the resolved ID is also in the info cache
                        If Not _cache.ContainsKey(match.ContractId) Then
                            Dim local As InstrumentInfo = Nothing
                            If Not _cache.TryGetValue(fav.PxContractId, local) Then
                                local = BuildFromDefaults(fav.PxContractId)
                            End If
                            _cache(match.ContractId) = New InstrumentInfo With {
                                .PxContractId = match.ContractId,
                                .DisplayName = If(match.Name, local.DisplayName),
                                .TickSize = If(match.TickSize > 0, match.TickSize, local.TickSize),
                                .TickValue = If(match.TickValue > 0, match.TickValue, local.TickValue),
                                .MinStopTicks = If(match.MinInitialMarginTicks > 0,
                                                   CType(match.MinInitialMarginTicks, Integer?),
                                                   local.MinStopTicks)
                            }
                        End If
                        _logger.LogInformation(
                            "TopStepXInstrumentCatalog: {Root} → front-month {Id}",
                            fav.PxRootSymbol, match.ContractId)
                    Else
                        _logger.LogWarning(
                            "TopStepXInstrumentCatalog: no active contract found for root '{Root}' — " &
                            "falling back to static '{Id}'",
                            fav.PxRootSymbol, fav.PxContractId)
                    End If
                Catch ex As Exception
                    _logger.LogWarning(ex,
                        "TopStepXInstrumentCatalog: root-symbol search failed for '{Root}'", fav.PxRootSymbol)
                End Try
            Next

            _cacheBuiltAt = DateTimeOffset.UtcNow
        End Function

        ''' <summary>
        ''' Searches for the active front-month contract matching <paramref name="fav"/>.PxRootSymbol.
        ''' Tries <c>live:=False</c> first (simulated universe); if no match is found, retries with
        ''' <c>live:=True</c> (full exchange universe) because the search endpoint is metadata-only
        ''' and works regardless of account type.
        ''' Returns the best <see cref="ContractDto"/> or Nothing when no match is found.
        ''' </summary>
        Private Async Function SearchForFrontMonthAsync(
            fav As FavouriteContract,
            cancel As CancellationToken) As Task(Of ContractDto)

            Dim prefix = $"CON.F.US.{fav.PxRootSymbol}."
            ' Exclude contracts expiring within RollLeadDays days — they have rolled or are rolling.
            ' Monthly contracts (MCL/MGC) use 28; quarterly (MES/M6E etc.) default to 7.
            Dim cutoff = DateTime.UtcNow.Date.AddDays(fav.RollLeadDays)

            ' Try simulated universe first, then full exchange universe
            For Each liveFlag In {False, True}
                Dim searchResp = Await _contractClient.SearchContractsAsync(fav.PxRootSymbol, live:=liveFlag, cancel:=cancel)

                If searchResp?.Contracts IsNot Nothing Then
                    Dim rawIds = searchResp.Contracts.Select(Function(c) $"'{c.ContractId}'").ToList()
                    _logger.LogInformation(
                        "TopStepXInstrumentCatalog: search '{Root}' (live={Live}) returned {N} contracts: {Ids}",
                        fav.PxRootSymbol, liveFlag, rawIds.Count, String.Join(", ", rawIds))
                End If

                Dim best = searchResp?.Contracts?.
                    Where(Function(c) Not String.IsNullOrEmpty(c.ContractId) AndAlso
                                      c.ContractId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).
                    Select(Function(c) New With {.Contract = c, .Expiry = ParseFuturesExpiry(c.ContractId)}).
                    Where(Function(x) x.Expiry >= cutoff).
                    OrderBy(Function(x) x.Expiry).
                    FirstOrDefault()

                If best IsNot Nothing Then
                    _logger.LogInformation(
                        "TopStepXInstrumentCatalog: {Root} matched {Id} (live={Live}, expiry {Exp:yyyy-MM})",
                        fav.PxRootSymbol, best.Contract.ContractId, liveFlag, best.Expiry)
                    Return best.Contract
                End If

                If Not liveFlag Then
                    _logger.LogInformation(
                        "TopStepXInstrumentCatalog: no match for '{Root}' with live=False — retrying with live=True",
                        fav.PxRootSymbol)
                End If
            Next

            ' Last resort: verify the static PxContractId directly via searchById
            If Not String.IsNullOrEmpty(fav.PxContractId) Then
                Try
                    Dim byIdResp = Await _contractClient.SearchByIdAsync(fav.PxContractId, cancel)
                    Dim exact = byIdResp?.Contracts?.FirstOrDefault(
                        Function(c) String.Equals(c.ContractId, fav.PxContractId, StringComparison.OrdinalIgnoreCase))
                    If exact IsNot Nothing Then
                        _logger.LogInformation(
                            "TopStepXInstrumentCatalog: searchById fallback found '{Id}' for root '{Root}'",
                            fav.PxContractId, fav.PxRootSymbol)
                        Return exact
                    End If
                Catch ex As Exception
                    _logger.LogWarning(ex,
                        "TopStepXInstrumentCatalog: searchById fallback failed for '{Id}'", fav.PxContractId)
                End Try
            End If

            Return Nothing
        End Function

        Private Sub SeedFromDefaults()
            _cache.Clear()
            For Each fav In FavouriteContracts.GetDefaults(BrokerType.TopStepX)
                _cache(fav.PxContractId) = New InstrumentInfo With {
                    .PxContractId = fav.PxContractId,
                    .DisplayName = fav.Name,
                    .TickSize = fav.PxTickSize,
                    .TickValue = fav.PxTickValue,
                    .MinStopTicks = Nothing
                }
            Next
        End Sub

        ' CME futures month codes: F=Jan G=Feb H=Mar J=Apr K=May M=Jun
        '                          N=Jul Q=Aug U=Sep V=Oct X=Nov Z=Dec
        Private Shared ReadOnly s_monthCodes As New Dictionary(Of Char, Integer) From {
            {"F"c, 1}, {"G"c, 2}, {"H"c, 3}, {"J"c, 4}, {"K"c, 5}, {"M"c, 6},
            {"N"c, 7}, {"Q"c, 8}, {"U"c, 9}, {"V"c, 10}, {"X"c, 11}, {"Z"c, 12}
        }

        ''' <summary>
        ''' Parses the expiry month from a ProjectX contract ID like "CON.F.US.MCL.K26".
        ''' Returns DateTime.MaxValue when the ID cannot be decoded.
        ''' </summary>
        Private Shared Function ParseFuturesExpiry(contractId As String) As DateTime
            If String.IsNullOrEmpty(contractId) Then Return DateTime.MaxValue
            Dim parts = contractId.Split("."c)
            If parts.Length < 2 Then Return DateTime.MaxValue
            Dim code = parts(parts.Length - 1)   ' e.g. "K26"
            If code.Length < 2 Then Return DateTime.MaxValue
            Dim month As Integer
            If Not s_monthCodes.TryGetValue(code(0), month) Then Return DateTime.MaxValue
            Dim year As Integer
            If Not Integer.TryParse(code.Substring(1), year) Then Return DateTime.MaxValue
            Return New DateTime(2000 + year, month, 1)
        End Function

        Private Shared Function BuildFromDefaults(pxContractId As String) As InstrumentInfo
            Dim fav = FavouriteContracts.GetDefaults(BrokerType.TopStepX).
                FirstOrDefault(Function(f) String.Equals(f.PxContractId, pxContractId,
                                                         StringComparison.OrdinalIgnoreCase))
            If fav IsNot Nothing Then
                Return New InstrumentInfo With {
                    .PxContractId = fav.PxContractId,
                    .DisplayName = fav.Name,
                    .TickSize = fav.PxTickSize,
                    .TickValue = fav.PxTickValue,
                    .MinStopTicks = Nothing
                }
            End If
            ' Last-resort fallback
            Return New InstrumentInfo With {
                .PxContractId = pxContractId,
                .DisplayName = pxContractId,
                .TickSize = 0.25D,
                .TickValue = 1.25D,
                .MinStopTicks = Nothing
            }
        End Function

    End Class

    ''' <summary>Per-instrument metadata returned by TopStepXInstrumentCatalog.</summary>
    Public Class InstrumentInfo
        Public Property PxContractId As String = String.Empty
        Public Property DisplayName As String = String.Empty
        ''' <summary>Minimum price increment (e.g. MES = 0.25).</summary>
        Public Property TickSize As Decimal
        ''' <summary>Dollar value per tick (e.g. MES = $1.25).</summary>
        Public Property TickValue As Decimal
        ''' <summary>
        ''' Minimum stop distance in ticks as returned by the ProjectX API.
        ''' Nothing when the API does not provide this value.
        ''' </summary>
        Public Property MinStopTicks As Integer?
    End Class

End Namespace
