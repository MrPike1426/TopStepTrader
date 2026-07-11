Imports System.Collections.Concurrent
Imports System.Collections.ObjectModel
Imports System.Threading
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Hosting
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Core.Trading

Namespace TopStepTrader.Services.Market

    ''' <summary>
    ''' FEAT-72: event payload emitted when the live watchlist actually changes.
    ''' <see cref="Added"/> + <see cref="Removed"/> are the symbol-level diffs;
    ''' <see cref="Current"/> is the new full list (pinned/open-slot first, then by score desc).
    ''' </summary>
    Public Class WatchlistChangedEventArgs
        Inherits EventArgs
        Public Property Current As IReadOnlyList(Of FavouriteContract)
        Public Property Added As IReadOnlyList(Of String)
        Public Property Removed As IReadOnlyList(Of String)
    End Class

    ''' <summary>
    ''' FEAT-72: singleton background service that maintains the live "hot" watchlist
    ''' for the strategy layer. Scores every non-blacklisted instrument in
    ''' <see cref="InstrumentUniverse"/> via <see cref="InstrumentOpportunityScorer"/>
    ''' and keeps the top N (per <see cref="OpportunityScoreSettings.AdaptiveWatchlistMaxSize"/>),
    ''' refreshed on a cadence + on demand.
    '''
    ''' Hysteresis: a contract that just joined the list cannot be displaced until its
    ''' tenure reaches <see cref="OpportunityScoreSettings.AdaptiveWatchlistMinTenureMinutes"/>.
    ''' Open-slot pin: instruments returned by any registered
    ''' <see cref="IOpenSlotInstrumentSource"/> always stay regardless of score / tenure.
    '''
    ''' Toggle-off semantics: <see cref="GetCurrentWatchlist"/> still returns a coherent
    ''' list (the Core favourites) so strategy consumers can always treat this service as
    ''' the single source of truth; the scoring loop simply skips when disabled.
    ''' </summary>
    Public Class AdaptiveWatchlistService
        Implements IHostedService, IDisposable

        Private Shared ReadOnly StartupDelay As TimeSpan = TimeSpan.FromSeconds(15)

        Private ReadOnly _scopeFactory As IServiceScopeFactory
        Private ReadOnly _scorer As InstrumentOpportunityScorer
        Private ReadOnly _preferences As IOpportunityScorePreferences
        Private ReadOnly _logger As ILogger(Of AdaptiveWatchlistService)
        Private ReadOnly _combineSettings As CombineSettings
        Private ReadOnly _slotSources As New ConcurrentDictionary(Of IOpenSlotInstrumentSource, Byte)()
        Private ReadOnly _stateLock As New Object()
        Private ReadOnly _tenureStart As New Dictionary(Of String, DateTimeOffset)(StringComparer.OrdinalIgnoreCase)
        Private _current As IReadOnlyList(Of FavouriteContract)
        Private _timer As Timer
        Private _refreshing As Integer
        Private _currentCadence As TimeSpan = TimeSpan.FromMinutes(60)
        Private _disposed As Boolean

        Public Event WatchlistChanged As EventHandler(Of WatchlistChangedEventArgs)

        Public Sub New(scopeFactory As IServiceScopeFactory,
                       scorer As InstrumentOpportunityScorer,
                       preferences As IOpportunityScorePreferences,
                       logger As ILogger(Of AdaptiveWatchlistService),
                       Optional combineOptions As IOptions(Of CombineSettings) = Nothing)
            _scopeFactory = scopeFactory
            _scorer = scorer
            _preferences = preferences
            _logger = logger
            _combineSettings = If(combineOptions?.Value, New CombineSettings())
            _current = FilterMicrosOnly(BuildCoreFallback(), _combineSettings)
            AddHandler _preferences.Changed, AddressOf OnPreferencesChanged
        End Sub

        ' ─── Hosted lifetime ────────────────────────────────────────────────────

        Public Function StartAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StartAsync
            Dim settings = _preferences.GetSettings()
            _currentCadence = TimeSpan.FromMinutes(Math.Max(15, settings.AdaptiveWatchlistRefreshMinutes))
            _timer = New Timer(AddressOf TimerCallback, Nothing, StartupDelay, _currentCadence)
            _logger?.LogInformation(
                "AdaptiveWatchlistService starting (enabled={Enabled}, size={Size}, refresh={Refresh}min)",
                settings.AdaptiveWatchlistEnabled,
                settings.AdaptiveWatchlistMaxSize,
                settings.AdaptiveWatchlistRefreshMinutes)
            Return Task.CompletedTask
        End Function

        Public Function StopAsync(cancellationToken As CancellationToken) As Task _
            Implements IHostedService.StopAsync
            _timer?.Change(Timeout.Infinite, 0)
            Return Task.CompletedTask
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If _disposed Then Return
            _disposed = True
            RemoveHandler _preferences.Changed, AddressOf OnPreferencesChanged
            _timer?.Dispose()
        End Sub

        ' ─── Public API ─────────────────────────────────────────────────────────

        ''' <summary>Returns an immutable snapshot of the live watchlist. Safe to call from any thread.</summary>
        Public Function GetCurrentWatchlist() As IList(Of FavouriteContract)
            SyncLock _stateLock
                Return New List(Of FavouriteContract)(_current)
            End SyncLock
        End Function

        ''' <summary>Whether the adaptive toggle is currently on. Strategy consumers gate on this:
        ''' when False they should fall back to their pre-FEAT-72 watchlist source.</summary>
        Public ReadOnly Property IsEnabled As Boolean
            Get
                Return _preferences.GetSettings().AdaptiveWatchlistEnabled
            End Get
        End Property

        ''' <summary>Force a refresh immediately (e.g. user clicked "Refresh now" in Settings).</summary>
        Public Async Function RefreshNowAsync() As Task
            Await RefreshAsync(CancellationToken.None)
        End Function

        Public Sub RegisterOpenSlotSource(source As IOpenSlotInstrumentSource)
            If source Is Nothing Then Return
            _slotSources.TryAdd(source, 0)
        End Sub

        Public Sub UnregisterOpenSlotSource(source As IOpenSlotInstrumentSource)
            If source Is Nothing Then Return
            Dim ignored As Byte
            _slotSources.TryRemove(source, ignored)
        End Sub

        ' ─── Internals ──────────────────────────────────────────────────────────

        Private Async Sub TimerCallback(state As Object)
            If Interlocked.Exchange(_refreshing, 1) = 1 Then Return
            Try
                Await RefreshAsync(CancellationToken.None)
            Catch ex As Exception
                _logger?.LogWarning(ex, "AdaptiveWatchlistService timer refresh failed")
            Finally
                Interlocked.Exchange(_refreshing, 0)
            End Try
        End Sub

        Private Sub OnPreferencesChanged(sender As Object, e As EventArgs)
            ' Pick up new cadence + force a refresh.
            Try
                Dim settings = _preferences.GetSettings()
                Dim desired = TimeSpan.FromMinutes(Math.Max(15, settings.AdaptiveWatchlistRefreshMinutes))
                If desired <> _currentCadence Then
                    _currentCadence = desired
                    _timer?.Change(TimeSpan.FromSeconds(1), desired)
                End If
                Dim ignored = RefreshNowAsync()
            Catch ex As Exception
                _logger?.LogDebug(ex, "AdaptiveWatchlistService preferences-change handler threw")
            End Try
        End Sub

        Friend Async Function RefreshAsync(ct As CancellationToken) As Task
            Try
                Dim settings = _preferences.GetSettings()
                ' STRAT-45: combine mode + MicrosOnly restricts the universe to micro
                ' contracts BEFORE scoring, so the scorer still picks the best of the
                ' micro set day by day. Minis re-enter only when MicrosOnly is off.
                Dim universe = FilterUniverseMicrosOnly(InstrumentUniverse.GetAll(), _combineSettings)
                Dim selection = If(settings.AdaptiveWatchlistEnabled,
                                    Await PickAdaptiveAsync(universe, settings, ct),
                                    FilterMicrosOnly(BuildCoreFallback(), _combineSettings))
                Dim added = New List(Of String)
                Dim removed = New List(Of String)
                Dim previous As IReadOnlyList(Of FavouriteContract)
                Dim changed As Boolean = False

                SyncLock _stateLock
                    previous = _current
                    Dim prevSet = New HashSet(Of String)(
                        previous.Select(Function(c) c.PxRootSymbol),
                        StringComparer.OrdinalIgnoreCase)
                    Dim newSet = New HashSet(Of String)(
                        selection.Select(Function(c) c.PxRootSymbol),
                        StringComparer.OrdinalIgnoreCase)
                    For Each s In selection
                        If Not prevSet.Contains(s.PxRootSymbol) Then
                            added.Add(s.PxRootSymbol)
                        End If
                        ' Ensure every current member has a tenure timestamp so the hysteresis
                        ' rule treats long-resident members as "in tenure" the same as newcomers.
                        If Not _tenureStart.ContainsKey(s.PxRootSymbol) Then
                            _tenureStart(s.PxRootSymbol) = DateTimeOffset.UtcNow
                        End If
                    Next
                    For Each p In previous
                        If Not newSet.Contains(p.PxRootSymbol) Then
                            removed.Add(p.PxRootSymbol)
                        End If
                    Next
                    ' Drop tenure entries for symbols no longer in the live list.
                    Dim staleKeys = _tenureStart.Keys.Where(Function(k) Not newSet.Contains(k)).ToList()
                    For Each k In staleKeys
                        _tenureStart.Remove(k)
                    Next
                    changed = (added.Count > 0 OrElse removed.Count > 0)
                    _current = selection
                End SyncLock

                If changed Then
                    _logger?.LogInformation(
                        "AdaptiveWatchlistService refreshed: +[{Added}] -[{Removed}] now {Now}",
                        String.Join(",", added), String.Join(",", removed),
                        String.Join(",", selection.Select(Function(c) c.PxRootSymbol)))
                    SafeRaiseChanged(selection, added, removed)
                End If
            Catch ex As Exception
                _logger?.LogWarning(ex, "AdaptiveWatchlistService.RefreshAsync failed")
            End Try
        End Function

        Private Async Function PickAdaptiveAsync(universe As IList(Of UniverseEntry),
                                                  settings As OpportunityScoreSettings,
                                                  ct As CancellationToken) As Task(Of IReadOnlyList(Of FavouriteContract))
            Dim pinned = New HashSet(Of String)(
                If(settings.PinnedRootSymbols, New List(Of String)()),
                StringComparer.OrdinalIgnoreCase)
            Dim blacklisted = New HashSet(Of String)(
                If(settings.BlacklistedRootSymbols, New List(Of String)()),
                StringComparer.OrdinalIgnoreCase)
            Dim openSymbols = CollectOpenSlotSymbols()

            Dim maxSize = Math.Max(1, Math.Min(10, settings.AdaptiveWatchlistMaxSize))
            Dim candidates As New List(Of (Entry As UniverseEntry, Score As Single, Pinned As Boolean))

            Using scope = _scopeFactory.CreateScope()
                Dim barService = scope.ServiceProvider.GetRequiredService(Of IBarIngestionService)()
                For Each entry In universe
                    If ct.IsCancellationRequested Then Exit For
                    If entry?.Contract Is Nothing Then Continue For
                    Dim root = entry.Contract.PxRootSymbol
                    If String.IsNullOrEmpty(root) Then Continue For
                    If blacklisted.Contains(root) AndAlso Not openSymbols.Contains(root) Then Continue For

                    Dim score As Single = Await ScoreContractAsync(barService, entry.Contract, settings, ct)
                    candidates.Add((entry, score, pinned.Contains(root)))
                Next
            End Using

            ' Order: open-slot first (forced), then user-pinned, then by score desc.
            Dim ordered = candidates.
                OrderByDescending(Function(c) openSymbols.Contains(c.Entry.Contract.PxRootSymbol)).
                ThenByDescending(Function(c) c.Pinned).
                ThenByDescending(Function(c)
                                     If Single.IsNaN(c.Score) Then Return -1.0F
                                     Return c.Score
                                 End Function).
                ToList()

            Dim selected As New List(Of FavouriteContract)
            Dim chosen = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

            ' 1. Always include open-slot symbols (cannot drop under live exposure).
            For Each c In ordered
                If openSymbols.Contains(c.Entry.Contract.PxRootSymbol) AndAlso chosen.Add(c.Entry.Contract.PxRootSymbol) Then
                    selected.Add(c.Entry.Contract)
                End If
            Next

            ' 2. Always include user-pinned symbols (unless blacklisted by user — already filtered).
            For Each c In ordered
                If selected.Count >= maxSize Then Exit For
                If c.Pinned AndAlso chosen.Add(c.Entry.Contract.PxRootSymbol) Then
                    selected.Add(c.Entry.Contract)
                End If
            Next

            ' 3. Hysteresis carry-over: previously-selected entries inside their tenure keep their slot.
            Dim previousSnapshot As List(Of FavouriteContract)
            Dim tenureSnapshot As Dictionary(Of String, DateTimeOffset)
            SyncLock _stateLock
                previousSnapshot = New List(Of FavouriteContract)(_current)
                tenureSnapshot = New Dictionary(Of String, DateTimeOffset)(_tenureStart, StringComparer.OrdinalIgnoreCase)
            End SyncLock
            Dim tenureLimit = TimeSpan.FromMinutes(Math.Max(0, settings.AdaptiveWatchlistMinTenureMinutes))
            Dim nowUtc = DateTimeOffset.UtcNow
            For Each prev In previousSnapshot
                If selected.Count >= maxSize Then Exit For
                Dim root = prev.PxRootSymbol
                If chosen.Contains(root) Then Continue For
                If blacklisted.Contains(root) Then Continue For
                Dim tenureStart As DateTimeOffset
                If tenureSnapshot.TryGetValue(root, tenureStart) AndAlso (nowUtc - tenureStart) < tenureLimit Then
                    ' Still inside tenure — find the candidate entry to preserve metadata.
                    Dim match = ordered.FirstOrDefault(Function(c) String.Equals(c.Entry.Contract.PxRootSymbol, root, StringComparison.OrdinalIgnoreCase))
                    If match.Entry IsNot Nothing AndAlso chosen.Add(root) Then
                        selected.Add(match.Entry.Contract)
                    End If
                End If
            Next

            ' 4. Fill remaining slots with the top scorers we haven't already chosen.
            For Each c In ordered
                If selected.Count >= maxSize Then Exit For
                If chosen.Add(c.Entry.Contract.PxRootSymbol) Then
                    selected.Add(c.Entry.Contract)
                End If
            Next

            Return selected
        End Function

        Private Async Function ScoreContractAsync(barService As IBarIngestionService,
                                                   contract As FavouriteContract,
                                                   settings As OpportunityScoreSettings,
                                                   ct As CancellationToken) As Task(Of Single)
            Try
                Dim resolved = FavouriteContracts.TryGetBySymbolResolved(contract.PxRootSymbol)
                Dim contractId As String = If(resolved IsNot Nothing, resolved.PxContractId, contract.PxContractId)
                If String.IsNullOrEmpty(contractId) Then Return Single.NaN
                Dim bars = Await barService.GetBarsForMLAsync(
                    contractId, BarTimeframe.FifteenMinute,
                    Math.Max(40, settings.ScoreBarsCount), ct)
                If bars Is Nothing OrElse bars.Count = 0 Then Return Single.NaN
                Return _scorer.Score(contract, bars, 20.0F, settings.IndicatorLength)
            Catch ex As Exception
                _logger?.LogDebug(ex, "Score failed for {Root}", contract.PxRootSymbol)
                Return Single.NaN
            End Try
        End Function

        Private Function CollectOpenSlotSymbols() As HashSet(Of String)
            Dim result = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each src In _slotSources.Keys
                Try
                    Dim syms = src.GetOpenInstrumentRootSymbols()
                    If syms Is Nothing Then Continue For
                    For Each s In syms
                        If Not String.IsNullOrEmpty(s) Then result.Add(s)
                    Next
                Catch ex As Exception
                    _logger?.LogDebug(ex, "Open-slot source threw — skipping")
                End Try
            Next
            Return result
        End Function

        Private Shared Function BuildCoreFallback() As IReadOnlyList(Of FavouriteContract)
            Return FavouriteContracts.GetDefaults().
                Where(Function(f) Not String.IsNullOrEmpty(f.PxRootSymbol)).
                ToList()
        End Function

        ''' <summary>STRAT-45: drops non-micro universe entries when combine mode +
        ''' MicrosOnly are on; pass-through otherwise. Friend for direct test coverage.</summary>
        Friend Shared Function FilterUniverseMicrosOnly(universe As IList(Of UniverseEntry),
                                                         combine As CombineSettings) As IList(Of UniverseEntry)
            If combine Is Nothing OrElse Not combine.Enabled OrElse Not combine.MicrosOnly Then Return universe
            Return universe.
                Where(Function(e) e?.Contract IsNot Nothing AndAlso
                                  InstrumentUniverse.IsMicro(e.Contract.PxRootSymbol)).
                ToList()
        End Function

        ''' <summary>STRAT-45: same micro restriction for flat contract lists (the
        ''' non-adaptive fallback path and the constructor's initial snapshot).</summary>
        Friend Shared Function FilterMicrosOnly(contracts As IReadOnlyList(Of FavouriteContract),
                                                 combine As CombineSettings) As IReadOnlyList(Of FavouriteContract)
            If combine Is Nothing OrElse Not combine.Enabled OrElse Not combine.MicrosOnly Then Return contracts
            Return contracts.
                Where(Function(c) c IsNot Nothing AndAlso InstrumentUniverse.IsMicro(c.PxRootSymbol)).
                ToList()
        End Function

        Private Sub SafeRaiseChanged(current As IReadOnlyList(Of FavouriteContract),
                                      added As IReadOnlyList(Of String),
                                      removed As IReadOnlyList(Of String))
            Try
                RaiseEvent WatchlistChanged(Me, New WatchlistChangedEventArgs With {
                    .Current = current,
                    .Added = added,
                    .Removed = removed
                })
            Catch ex As Exception
                _logger?.LogDebug(ex, "WatchlistChanged handler threw")
            End Try
        End Sub

    End Class

End Namespace
