Imports System.IO
Imports System.Windows
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Data.Debug
Imports TopStepTrader.Services.Market
Imports TopStepTrader.Services.Training
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' Settings — shows current API config, allows editing risk thresholds and
    ''' toggling auto-execution. Changes persist to the in-memory options objects
    ''' for the current session; restart required for appsettings.json changes.
    ''' </summary>
    Public Class SettingsViewModel
        Inherits ViewModelBase

        Private ReadOnly _authService As IAuthService
        Private ReadOnly _riskSettings As RiskSettings
        Private ReadOnly _tradingSettings As TradingSettings
        Private ReadOnly _apiSettings As ApiSettings
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _userPrefs As IUserPreferencesService
        Private ReadOnly _trainingOrchestrator As TrainingOrchestrator
        Private ReadOnly _scorePrefs As IOpportunityScorePreferences
        Private ReadOnly _adaptiveWatchlist As AdaptiveWatchlistService

        ' ── API Status ───────────────────────────────────────────────────────

        Private _isConnected As Boolean
        Public Property IsConnected As Boolean
            Get
                Return _isConnected
            End Get
            Set(value As Boolean)
                SetProperty(_isConnected, value)
                OnPropertyChanged(NameOf(ConnectionStatusText))
                OnPropertyChanged(NameOf(ConnectionStatusColor))
            End Set
        End Property

        Private _tokenExpiresAt As String = "—"
        Public Property TokenExpiresAt As String
            Get
                Return _tokenExpiresAt
            End Get
            Set(value As String)
                SetProperty(_tokenExpiresAt, value)
            End Set
        End Property

        Private _statusMessage As String = "Ready"
        Public Property StatusMessage As String
            Get
                Return _statusMessage
            End Get
            Set(value As String)
                SetProperty(_statusMessage, value)
            End Set
        End Property

        Public ReadOnly Property ApiBaseUrl As String
            Get
                Return _apiSettings.BaseUrl
            End Get
        End Property

        Public ReadOnly Property ConnectionStatusText As String
            Get
                Return If(_isConnected, "Connected", "Disconnected")
            End Get
        End Property

        Public ReadOnly Property ConnectionStatusColor As String
            Get
                Return If(_isConnected, "BuyBrush", "SellBrush")
            End Get
        End Property

        ' ── Risk settings ────────────────────────────────────────────────────

        Private _dailyLossLimit As String
        Public Property DailyLossLimit As String
            Get
                Return _dailyLossLimit
            End Get
            Set(value As String)
                SetProperty(_dailyLossLimit, value)
            End Set
        End Property

        Private _maxDrawdown As String
        Public Property MaxDrawdown As String
            Get
                Return _maxDrawdown
            End Get
            Set(value As String)
                SetProperty(_maxDrawdown, value)
            End Set
        End Property

        Private _maxPosition As String
        Public Property MaxPosition As String
            Get
                Return _maxPosition
            End Get
            Set(value As String)
                SetProperty(_maxPosition, value)
            End Set
        End Property

        Private _minConfidence As String
        Public Property MinConfidence As String
            Get
                Return _minConfidence
            End Get
            Set(value As String)
                SetProperty(_minConfidence, value)
            End Set
        End Property

        Private _autoExecutionEnabled As Boolean
        Public Property AutoExecutionEnabled As Boolean
            Get
                Return _autoExecutionEnabled
            End Get
            Set(value As Boolean)
                If SetProperty(_autoExecutionEnabled, value) Then
                    _riskSettings.AutoExecutionEnabled = value
                    _userPrefs.AutoExecutionEnabled = value
                    _userPrefs.Save()
                    _session.SetAutoExecution(value)
                    StatusMessage = If(value,
                        "⚠ Auto-execution ENABLED — AI will place live orders",
                        "Auto-execution disabled — manual orders only")
                End If
            End Set
        End Property

        ' ── Retrain state ────────────────────────────────────────────────────

        Private _isRetraining As Boolean
        Public Property IsRetraining As Boolean
            Get
                Return _isRetraining
            End Get
            Set(value As Boolean)
                If SetProperty(_isRetraining, value) Then
                    OnPropertyChanged(NameOf(CanRetrainModel))
                    RelayCommand.RaiseCanExecuteChanged()
                End If
            End Set
        End Property

        Public ReadOnly Property CanRetrainModel As Boolean
            Get
                Return Not _isRetraining
            End Get
        End Property

        ' ── FEAT-72: Adaptive watchlist ─────────────────────────────────────

        Private _adaptiveEnabled As Boolean
        Public Property AdaptiveWatchlistEnabled As Boolean
            Get
                Return _adaptiveEnabled
            End Get
            Set(value As Boolean)
                SetProperty(_adaptiveEnabled, value)
            End Set
        End Property

        Private _adaptiveSize As Integer
        Public Property AdaptiveWatchlistSize As Integer
            Get
                Return _adaptiveSize
            End Get
            Set(value As Integer)
                SetProperty(_adaptiveSize, Math.Max(3, Math.Min(10, value)))
            End Set
        End Property

        Private _adaptiveRefreshMinutes As Integer
        Public Property AdaptiveRefreshMinutes As Integer
            Get
                Return _adaptiveRefreshMinutes
            End Get
            Set(value As Integer)
                SetProperty(_adaptiveRefreshMinutes, Math.Max(15, Math.Min(240, value)))
            End Set
        End Property

        Private _pinnedSymbolsCsv As String = String.Empty
        Public Property PinnedSymbolsCsv As String
            Get
                Return _pinnedSymbolsCsv
            End Get
            Set(value As String)
                SetProperty(_pinnedSymbolsCsv, If(value, String.Empty))
            End Set
        End Property

        Private _blacklistedSymbolsCsv As String = String.Empty
        Public Property BlacklistedSymbolsCsv As String
            Get
                Return _blacklistedSymbolsCsv
            End Get
            Set(value As String)
                SetProperty(_blacklistedSymbolsCsv, If(value, String.Empty))
            End Set
        End Property

        Public ReadOnly Property AdaptiveUniverseHint As String
            Get
                Return "Available roots: " &
                       String.Join(", ", InstrumentUniverse.GetAll().
                                          Select(Function(e) e.Contract.PxRootSymbol).
                                          Distinct(StringComparer.OrdinalIgnoreCase))
            End Get
        End Property

        ' ── Commands ─────────────────────────────────────────────────────────

        Public ReadOnly Property ConnectCommand As RelayCommand
        Public ReadOnly Property ApplyRiskCommand As RelayCommand
        Public ReadOnly Property RetrainModelCommand As RelayCommand
        Public ReadOnly Property ApplyAdaptiveCommand As RelayCommand
        Public ReadOnly Property RefreshAdaptiveNowCommand As RelayCommand

        ' ── Constructor ──────────────────────────────────────────────────────

        Public Sub New(authService As IAuthService,
                       apiOptions As IOptions(Of ApiSettings),
                       riskOptions As IOptions(Of RiskSettings),
                       tradingOptions As IOptions(Of TradingSettings),
                       session As ITradingSessionContext,
                       userPrefs As IUserPreferencesService,
                       trainingOrchestrator As TrainingOrchestrator,
                       scorePrefs As IOpportunityScorePreferences,
                       Optional adaptiveWatchlist As AdaptiveWatchlistService = Nothing)
            _authService = authService
            _apiSettings = apiOptions.Value
            _riskSettings = riskOptions.Value
            _tradingSettings = tradingOptions.Value
            _session = session
            _userPrefs = userPrefs
            _trainingOrchestrator = trainingOrchestrator
            _scorePrefs = scorePrefs
            _adaptiveWatchlist = adaptiveWatchlist

            ' Populate form from current settings (AutoExecution from persisted prefs via session)
            _dailyLossLimit = _riskSettings.DailyLossLimitDollars.ToString()
            _maxDrawdown = _riskSettings.MaxDrawdownDollars.ToString()
            _maxPosition = _riskSettings.MaxPositionSizeContracts.ToString()
            _minConfidence = _riskSettings.MinSignalConfidence.ToString("F2")
            _autoExecutionEnabled = _session.AutoExecutionEnabled

            ' FEAT-72: load persisted adaptive watchlist preferences.
            Dim s = _scorePrefs.GetSettings()
            _adaptiveEnabled = s.AdaptiveWatchlistEnabled
            _adaptiveSize = s.AdaptiveWatchlistMaxSize
            _adaptiveRefreshMinutes = s.AdaptiveWatchlistRefreshMinutes
            _pinnedSymbolsCsv = String.Join(", ", If(s.PinnedRootSymbols, New List(Of String)()))
            _blacklistedSymbolsCsv = String.Join(", ", If(s.BlacklistedRootSymbols, New List(Of String)()))

            ConnectCommand = New RelayCommand(AddressOf ExecuteConnect)
            ApplyRiskCommand = New RelayCommand(AddressOf ExecuteApplyRisk)
            RetrainModelCommand = New RelayCommand(AddressOf ExecuteRetrainModel,
                                                    Function() CanRetrainModel)
            ApplyAdaptiveCommand = New RelayCommand(AddressOf ExecuteApplyAdaptive)
            RefreshAdaptiveNowCommand = New RelayCommand(AddressOf ExecuteRefreshAdaptive)
        End Sub

        Public Sub LoadDataAsync()
            RefreshConnectionStatus()
        End Sub

        Private Sub RefreshConnectionStatus()
            Dispatch(Sub()
                         IsConnected = _authService.IsAuthenticated
                         If _authService.TokenExpiresAt > DateTimeOffset.MinValue Then
                             TokenExpiresAt = _authService.TokenExpiresAt.LocalDateTime.ToString("MM/dd HH:mm:ss")
                         End If
                     End Sub)
        End Sub

        Private Sub ExecuteConnect(param As Object)
            Task.Run(Async Function()
                         Try
                             Dispatch(Sub() StatusMessage = "Connecting...")
                             Dim token = Await _authService.LoginAsync("", "")
                             Dispatch(Sub()
                                          IsConnected = _authService.IsAuthenticated
                                          StatusMessage = If(IsConnected, "Connected successfully", "Connection failed")
                                          RefreshConnectionStatus()
                                      End Sub)
                         Catch ex As Exception
                             Dispatch(Sub() StatusMessage = $"Connect error: {ex.Message}")
                         End Try
                     End Function)
        End Sub

        Private Sub ExecuteApplyRisk(param As Object)
            Try
                Dim dl, dd As Decimal
                Dim mp As Integer
                Dim mc As Single

                If Not Decimal.TryParse(_dailyLossLimit, dl) Then
                    StatusMessage = "Invalid daily loss limit" : Return
                End If
                If Not Decimal.TryParse(_maxDrawdown, dd) Then
                    StatusMessage = "Invalid max drawdown" : Return
                End If
                If Not Integer.TryParse(_maxPosition, mp) OrElse mp < 1 Then
                    StatusMessage = "Invalid max position size" : Return
                End If
                If Not Single.TryParse(_minConfidence, mc) OrElse mc < 0 OrElse mc > 1 Then
                    StatusMessage = "Min confidence must be 0.0–1.0" : Return
                End If

                ' Apply to live settings object (affects all services using IOptions<RiskSettings>)
                _riskSettings.DailyLossLimitDollars = dl
                _riskSettings.MaxDrawdownDollars = dd
                _riskSettings.MaxPositionSizeContracts = mp
                _riskSettings.MinSignalConfidence = mc

                StatusMessage = "Risk settings applied for this session"

            Catch ex As Exception
                StatusMessage = $"Error: {ex.Message}"
            End Try
        End Sub

        ''' <summary>
        ''' FEAT-60: invokes <see cref="TrainingOrchestrator.RunAsync"/> against the first
        ''' favourite contract over the last 90 days at the default timeframe; writes the
        ''' .zip to Diagnostics\models with a timestamped filename.
        ''' </summary>
        Private Sub ExecuteRetrainModel(param As Object)
            If _isRetraining Then Return
            IsRetraining = True
            StatusMessage = "Retraining signal model…"

            Task.Run(Async Function()
                         Try
                             Dim fav = FavouriteContracts.GetDefaults().FirstOrDefault()
                             If fav Is Nothing Then
                                 Dispatch(Sub() StatusMessage = "Train failed: no favourite contracts configured")
                                 Return
                             End If
                             Dim resolved = FavouriteContracts.TryGetBySymbolResolved(fav.Name)
                             Dim contractId = If(resolved IsNot Nothing, resolved.PxContractId, fav.PxContractId)

                             Const Timeframe As String = "15min"
                             Dim fromUtc = DateTimeOffset.UtcNow.AddDays(-90)

                             Dim diagnosticsRoot = DebugTradeDbContext.ResolveDiagnosticsFolder()
                             Dim modelsDir = Path.Combine(diagnosticsRoot, "models")
                             Directory.CreateDirectory(modelsDir)
                             Dim stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
                             Dim outputPath = Path.Combine(modelsDir,
                                 $"signal-model-{contractId}-{Timeframe}-{stamp}.zip")

                             Dim metrics = Await _trainingOrchestrator.RunAsync(
                                 contractId, Timeframe, fromUtc, outputPath)

                             Dispatch(Sub()
                                          StatusMessage = String.Format(
                                              "Trained: {0} samples, AUC={1:F3}, Accuracy={2:P1} → {3}",
                                              metrics.TrainingSamples, metrics.AUC, metrics.Accuracy, outputPath)
                                      End Sub)
                         Catch ex As Exception
                             Dispatch(Sub() StatusMessage = $"Train failed: {ex.Message}")
                         Finally
                             Dispatch(Sub() IsRetraining = False)
                         End Try
                     End Function)
        End Sub

        ''' <summary>
        ''' FEAT-72: persist the adaptive watchlist toggle / size / refresh + pin / blacklist
        ''' lists. AdaptiveWatchlistService subscribes to <see cref="IOpportunityScorePreferences.Changed"/>
        ''' and refreshes automatically.
        ''' </summary>
        Private Sub ExecuteApplyAdaptive(param As Object)
            Try
                Dim settings = New OpportunityScoreSettings With {
                    .AdaptiveWatchlistEnabled = _adaptiveEnabled,
                    .AdaptiveWatchlistMaxSize = Math.Max(3, Math.Min(10, _adaptiveSize)),
                    .AdaptiveWatchlistRefreshMinutes = Math.Max(15, Math.Min(240, _adaptiveRefreshMinutes)),
                    .AdaptiveWatchlistMinTenureMinutes = _scorePrefs.GetSettings().AdaptiveWatchlistMinTenureMinutes,
                    .ScoreBarsCount = _scorePrefs.GetSettings().ScoreBarsCount,
                    .IndicatorLength = _scorePrefs.GetSettings().IndicatorLength,
                    .PinnedRootSymbols = ParseCsvSymbols(_pinnedSymbolsCsv),
                    .BlacklistedRootSymbols = ParseCsvSymbols(_blacklistedSymbolsCsv)
                }
                _scorePrefs.Save(settings)
                StatusMessage = "Adaptive watchlist settings saved (refresh kicked off)"
            Catch ex As Exception
                StatusMessage = $"Adaptive watchlist save failed: {ex.Message}"
            End Try
        End Sub

        Private Sub ExecuteRefreshAdaptive(param As Object)
            If _adaptiveWatchlist Is Nothing Then
                StatusMessage = "Adaptive watchlist service unavailable"
                Return
            End If
            StatusMessage = "Adaptive watchlist refreshing…"
            Task.Run(Async Function()
                         Try
                             Await _adaptiveWatchlist.RefreshNowAsync()
                             Dispatch(Sub() StatusMessage = "Adaptive watchlist refreshed")
                         Catch ex As Exception
                             Dispatch(Sub() StatusMessage = $"Refresh failed: {ex.Message}")
                         End Try
                     End Function)
        End Sub

        Private Shared Function ParseCsvSymbols(csv As String) As List(Of String)
            If String.IsNullOrWhiteSpace(csv) Then Return New List(Of String)()
            Return csv.Split(New Char() {","c, ";"c, " "c, ControlChars.Tab, ControlChars.Lf, ControlChars.Cr},
                             StringSplitOptions.RemoveEmptyEntries).
                Select(Function(s) s.Trim().ToUpperInvariant()).
                Where(Function(s) s.Length > 0).
                Distinct(StringComparer.OrdinalIgnoreCase).
                ToList()
        End Function

        Private Sub Dispatch(action As Action)
            If Application.Current?.Dispatcher IsNot Nothing Then
                Application.Current.Dispatcher.Invoke(action)
            End If
        End Sub

    End Class

End Namespace
