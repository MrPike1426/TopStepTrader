Imports System.Collections.ObjectModel
Imports System.Windows
Imports Microsoft.Extensions.DependencyInjection
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.Trading
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' ViewModel for the CryptoJoe multi-asset monitoring view.
    ''' Runs 5 independent EMA/RSI Combined sessions (one per crypto asset) concurrently and
    ''' surfaces per-asset confidence snapshots in real time.  No session expiry — monitors 24/7.
    ''' Each engine runs in its own DI scope so BarIngestionService and IOrderService are
    ''' fully isolated between assets.
    ''' </summary>
    Public Class CryptoJoeViewModel
        Inherits TradingViewModelBase
        Implements IDisposable

        ' ── Dependencies ──────────────────────────────────────────────────────────
        Private ReadOnly _scopeFactory As IServiceScopeFactory
        Private ReadOnly _accountService As IAccountService
        Private ReadOnly _session As ITradingSessionContext

        ' ── Per-asset card ViewModels ─────────────────────────────────────────────
        Public Property Assets As New ObservableCollection(Of HydraAssetViewModel)

        ' ── Per-asset scope + engine ──────────────────────────────────────────────
        Private ReadOnly _assetScopes(4) As IServiceScope
        Private ReadOnly _engines(4) As CryptoStrategyExecutionEngine

        ' ── Internal state ────────────────────────────────────────────────────────
        Private _currentStrategy As StrategyDefinition
        Private _disposed As Boolean = False

        ' ── Risk / quantity ───────────────────────────────────────────────────────
        Private _minConfidencePct As Integer = 85
        Public Property MinConfidencePct As Integer
            Get
                Return _minConfidencePct
            End Get
            Set(value As Integer)
                SetProperty(_minConfidencePct, Math.Max(0, Math.Min(100, value)))
            End Set
        End Property

        ' ── Running state ─────────────────────────────────────────────────────────
        Private _isRunning As Boolean = False
        Public Property IsRunning As Boolean
            Get
                Return _isRunning
            End Get
            Set(value As Boolean)
                SetProperty(_isRunning, value)
                OnPropertyChanged(NameOf(IsNotRunning))
                RelayCommand.RaiseCanExecuteChanged()
            End Set
        End Property

        Public ReadOnly Property IsNotRunning As Boolean
            Get
                Return Not _isRunning
            End Get
        End Property

        ' ── Strategy selection ────────────────────────────────────────────────────
        Private _hasParsedStrategy As Boolean = False
        Public Property HasParsedStrategy As Boolean
            Get
                Return _hasParsedStrategy
            End Get
            Set(value As Boolean)
                SetProperty(_hasParsedStrategy, value)
                RelayCommand.RaiseCanExecuteChanged()
            End Set
        End Property

        Private _activeStrategyText As String = "None selected — click a card above"
        Public Property ActiveStrategyText As String
            Get
                Return _activeStrategyText
            End Get
            Set(value As String)
                SetProperty(_activeStrategyText, value)
            End Set
        End Property

        ' ── Status / Log ──────────────────────────────────────────────────────────
        Private _statusText As String = "● Idle"
        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Set(value As String)
                SetProperty(_statusText, value)
            End Set
        End Property

        Public Property LogEntries As New ObservableCollection(Of String)

        ' ── Commands ──────────────────────────────────────────────────────────────
        Public ReadOnly Property SelectEmaRsiCombinedCommand As RelayCommand
        Public ReadOnly Property SelectMultiConfluenceEngineCommand As RelayCommand
        Public ReadOnly Property SelectLultDivergenceCommand As RelayCommand
        Public ReadOnly Property SelectBbSqueezeScalperCommand As RelayCommand
        Public ReadOnly Property SelectVidyaCommand As RelayCommand
        Public ReadOnly Property SelectNakedTraderCommand As RelayCommand
        Public ReadOnly Property StartCommand As RelayCommand
        Public ReadOnly Property StopCommand As RelayCommand

        ''' <summary>True when the form is ready to trade (account selected).</summary>
        Public Overrides ReadOnly Property IsFormReady As Boolean
            Get
                Return SelectedAccount IsNot Nothing
            End Get
        End Property

        ' ── Constructor ───────────────────────────────────────────────────────────

        Public Sub New(scopeFactory As IServiceScopeFactory,
                       accountService As IAccountService,
                       session As ITradingSessionContext)
            _scopeFactory = scopeFactory
            _accountService = accountService
            _session = session

            ' Build broker-aware crypto asset roster from the session-selected broker
            Dim activeBroker = _session.ActiveBroker
            Dim iconMap = New Dictionary(Of String, String) From {
                {"BTC", "₿"}, {"ETH", "Ξ"}, {"XRP", "✕"}, {"SOL", "◎"}, {"BNB", "◈"}}
            Dim roster = FavouriteContracts.GetDefaults().
                Where(Function(f) f.IsCrypto).
                Take(5).ToList()
            For Each fav In roster
                Dim icon = If(iconMap.ContainsKey(fav.Name), iconMap(fav.Name), "🪙")
                Assets.Add(New HydraAssetViewModel(fav.Name, icon, fav.PxContractId))
            Next

            ' Create one DI scope + engine per asset (roster size varies by broker).
            For i = 0 To Assets.Count - 1
                _assetScopes(i) = _scopeFactory.CreateScope()
                _engines(i) = _assetScopes(i).ServiceProvider _
                                  .GetRequiredService(Of CryptoStrategyExecutionEngine)()
                ' All crypto assets trade 24/7 — ordering is always allowed.
                _engines(i).IsOrderingAllowed = Function() True
                WireEngineEvents(_engines(i), Assets(i))
            Next

            ' Subscribe to session account changes so SelectedAccount tracks dashboard choice.
            AddHandler _session.AccountChanged, AddressOf OnSessionAccountChanged

            SelectEmaRsiCombinedCommand = New RelayCommand(
                Sub(p) ApplyEmaRsiCombined(),
                Function(p) IsFormReady AndAlso IsNotRunning)

            SelectMultiConfluenceEngineCommand = New RelayCommand(
                Sub(p) ApplyMultiConfluenceEngine(),
                Function(p) IsFormReady AndAlso IsNotRunning)

            SelectLultDivergenceCommand = New RelayCommand(
                Sub(p) ApplyLultDivergence(),
                Function(p) IsFormReady AndAlso IsNotRunning)

            SelectBbSqueezeScalperCommand = New RelayCommand(
                Sub(p) ApplyBbSqueezeScalper(),
                Function(p) IsFormReady AndAlso IsNotRunning)

            SelectVidyaCommand = New RelayCommand(
                Sub(p) ApplyVidya(),
                Function(p) IsFormReady AndAlso IsNotRunning)

            SelectNakedTraderCommand = New RelayCommand(
                Sub(p) ApplyNakedTrader(),
                Function(p) IsFormReady AndAlso IsNotRunning)

            StartCommand = New RelayCommand(
                AddressOf ExecuteStart,
                Function(p) HasParsedStrategy AndAlso IsNotRunning AndAlso SelectedAccount IsNot Nothing)

            StopCommand = New RelayCommand(
                AddressOf ExecuteStop,
                Function(p) IsRunning)
        End Sub

        ' ── Session account sync ──────────────────────────────────────────────────

        Private Sub OnSessionAccountChanged(sender As Object, account As Account)
            Dispatch(Sub()
                         If account IsNot Nothing Then
                             Dim match = Accounts.FirstOrDefault(Function(a) a.Id = account.Id)
                             If match IsNot Nothing Then SelectedAccount = match
                         End If
                     End Sub)
        End Sub

        ' ── Engine event wiring ───────────────────────────────────────────────────

        Private Sub WireEngineEvents(engine As CryptoStrategyExecutionEngine,
                                     assetVm As HydraAssetViewModel)
            AddHandler engine.ConfidenceUpdated,
                Sub(s As Object, e As ConfidenceUpdatedEventArgs)
                    Dispatch(Sub() assetVm.ApplyConfidence(e))
                End Sub

            AddHandler engine.LogMessage,
                Sub(s As Object, msg As String)
                    Dispatch(Sub() LogLine($"[{assetVm.Symbol}] {msg}"))
                End Sub

            AddHandler engine.ExecutionStopped,
                Sub(s As Object, reason As String)
                    Dispatch(Sub() LogLine($"[{assetVm.Symbol}] ■ Stopped: {reason}"))
                End Sub

            AddHandler engine.TradeOpened,
                Sub(s As Object, e As TradeOpenedEventArgs)
                    Dispatch(Sub()
                                 assetVm.OpenTrade(e.Side, e.EntryPrice, e.Amount)
                                 LogLine($"[{assetVm.Symbol}] 🟢 Trade opened — {e.Side} @ {e.EntryPrice:F4} | {CInt(e.Amount)}ct")
                             End Sub)
                End Sub

            AddHandler engine.TradeClosed,
                Sub(s As Object, e As TradeClosedEventArgs)
                    Dispatch(Sub()
                                 assetVm.CloseTrade()
                                 LogLine($"[{assetVm.Symbol}] 🔴 Trade closed — {e.ExitReason} | P&L={If(e.PnL >= 0D, "+", "")}${e.PnL:F2}")
                             End Sub)
                End Sub

            AddHandler engine.PositionSynced,
                Sub(s As Object, e As PositionSyncedEventArgs)
                    Dispatch(Sub() assetVm.UpdateTradePnl(e.UnrealizedPnlUsd))
                End Sub
        End Sub

        ' ── Data loading ──────────────────────────────────────────────────────────

        Public Async Sub LoadDataAsync()
            Try
                Dim accountList = Await _accountService.GetActiveAccountsAsync()
                Dispatch(Sub()
                             Accounts.Clear()
                             For Each a In accountList
                                 Accounts.Add(a)
                             Next
                             If Accounts.Count > 0 Then
                                 ' Prefer the account already chosen on the Dashboard (session context).
                                 Dim sessionAcc = _session.SelectedAccount
                                 Dim preferred = If(sessionAcc IsNot Nothing,
                                     Accounts.FirstOrDefault(Function(a) a.Id = sessionAcc.Id),
                                     Nothing)
                                 If preferred Is Nothing Then
                                     preferred = Accounts.FirstOrDefault(
                                         Function(a) a.Name IsNot Nothing AndAlso
                                                     a.Name.StartsWith("PRAC", StringComparison.OrdinalIgnoreCase))
                                 End If
                                 SelectedAccount = If(preferred, Accounts(0))
                             End If
                         End Sub)
            Catch ex As Exception
                Dispatch(Sub() StatusText = $"⚠ Load error: {ex.Message}")
            End Try
        End Sub

        ' ── Strategy activation ───────────────────────────────────────────────────

        ''' <summary>
        ''' Activates the EMA/RSI Combined strategy for all 5 crypto assets.
        ''' DurationHours = 8 760 (one calendar year) so sessions never auto-expire
        ''' — satisfying the "runs 24/7" requirement.
        ''' </summary>
        Private Sub ApplyEmaRsiCombined()
            _currentStrategy = New StrategyDefinition With {
                .Name = "EMA/RSI Combined",
                .Indicator = StrategyIndicatorType.EmaRsiCombined,
                .Condition = StrategyConditionType.EmaRsiWeightedScore,
                .IndicatorPeriod = 50,
                .SecondaryPeriod = 0,
                .IndicatorMultiplier = 0,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .TimeframeMinutes = 5,
                .DurationHours = 8760,
                .Quantity = 1,
                .MinConfidencePct = _minConfidencePct
            }

            HasParsedStrategy = True
            ActiveStrategyText = "✔  EMA/RSI Combined  (5-min · 24/7 · EMA21/EMA50/RSI14)"

            LogEntries.Clear()
            LogLine("─────────────────────────────────────────────────────────────────────")
            LogLine("Configure account + risk settings above, then click  ▶ Start Monitoring.")
            LogLine($"• 5-min bars · ATR stops · 1ct · Conf={_minConfidencePct}%")
            LogLine("• 5 independent sessions — BTC · ETH · XRP · SOL · BNB")
            LogLine("━━━  EMA/RSI Combined — CryptoJoe 5-Asset Monitor  ━━━")
        End Sub

        ''' <summary>
        ''' Activates the Multi-Confluence Engine strategy for all 5 crypto assets.
        ''' Uses ATR-based brackets; bracket advances on each TP hit using 0.5×N ATR steps.
        ''' DurationHours = 8 760 so sessions never auto-expire.
        ''' </summary>
        Private Sub ApplyMultiConfluenceEngine()
            _currentStrategy = New StrategyDefinition With {
                .Name = "Multi-Confluence Engine",
                .Indicator = StrategyIndicatorType.MultiConfluence,
                .Condition = StrategyConditionType.MultiConfluence,
                .IndicatorPeriod = 80,
                .SecondaryPeriod = 0,
                .IndicatorMultiplier = 0,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .TimeframeMinutes = 15,
                .DurationHours = 8760,
                .Quantity = 1,
                .MinConfidencePct = _minConfidencePct
            }

            HasParsedStrategy = True
            ActiveStrategyText = "✔  Multi-Confluence Engine  (15-min · 24/7 · Ichimoku · EMA21/50 · MACD · StochRSI · ADX)"

            LogEntries.Clear()
            LogLine("─────────────────────────────────────────────────────────────────────")
            LogLine("Configure account + risk settings above, then click  ▶ Start Monitoring.")
            LogLine("\u2022 SL = min(1.5×ATR, cloud edge) · TP = 2:1 R:R (dynamic per-trade ATR)")
            LogLine("• Entry fires only when ALL 7 conditions align (Ichimoku + EMA21 + Tenkan/Kijun + Chikou + ADX + MACD + StochRSI)")
            LogLine("• 5 independent sessions — BTC · ETH · XRP · SOL · BNB")
            LogLine("━━━  Multi-Confluence Engine — CryptoJoe 5-Asset Monitor  ━━━")
        End Sub

        ''' <summary>
        ''' Activates the LULT Divergence strategy for all 5 crypto assets.
        ''' Uses WaveTrend (Market Cipher B) Anchor/Trigger divergence on 5-minute bars.
        ''' ATR-derived SL/TP anchored to WaveTrend divergence levels.
        ''' Bracket advances on each TP hit; SL never retreats.
        ''' Time filter: 11:00–17:00 UTC (London + NY pre-market, 07:00–13:00 EST/EDT).
        ''' </summary>
        Private Sub ApplyLultDivergence()
            _currentStrategy = New StrategyDefinition With {
                .Name = "LULT Divergence",
                .Indicator = StrategyIndicatorType.LultDivergence,
                .Condition = StrategyConditionType.LultDivergence,
                .IndicatorPeriod = 100,
                .SecondaryPeriod = 0,
                .IndicatorMultiplier = 0,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .TimeframeMinutes = 5,
                .DurationHours = 8760,
                .Quantity = 1,
                .MinConfidencePct = _minConfidencePct
            }

            HasParsedStrategy = True
            ActiveStrategyText = "✔  LULT Divergence  (5-min · 24/7 · WaveTrend Anchor/Trigger · Engulfing · 2R)"

            LogEntries.Clear()
            LogLine("─────────────────────────────────────────────────────────────────────")
            LogLine("Configure account + risk settings above, then click  ▶ Start Monitoring.")
            LogLine("• SL = trigger wave extreme ± 3 ticks · TP = 2R · Partial TP at nearest swing (50 %)")
            LogLine("• 6-step gate: Anchor→Trigger (shallower)→Divergence→Dot→Engulfing candle")
            LogLine("• Time filter: 11:00–17:00 UTC (07:00–13:00 EST/EDT) — London + NY pre-market")
            LogLine("• 5 independent sessions — BTC · ETH · XRP · SOL · BNB")
            LogLine("━━━  LULT Divergence — CryptoJoe 5-Asset Monitor  ━━━")
        End Sub

        ''' <summary>
        ''' Activates the BB Squeeze Scalper strategy for all 5 crypto assets.
        ''' Dual-mode Bollinger Band scalper on 1-minute bars with 15-second polling.
        ''' Crypto trades 24/7 — IsOrderingAllowed is always True in CryptoStrategyExecutionEngine.
        ''' DurationHours = 8 760 so sessions never auto-expire.
        ''' </summary>
        Private Sub ApplyBbSqueezeScalper()
            _currentStrategy = New StrategyDefinition With {
                .Name = "BB Squeeze Scalper",
                .Indicator = StrategyIndicatorType.BbSqueezeScalper,
                .Condition = StrategyConditionType.BbSqueezeScalper,
                .IndicatorPeriod = 25,
                .SecondaryPeriod = 0,
                .IndicatorMultiplier = 2.0,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .TimeframeMinutes = 1,
                .DurationHours = 8760,
                .Quantity = 1,
                .MinConfidencePct = _minConfidencePct
            }

            HasParsedStrategy = True
            ActiveStrategyText = "✔  BB Squeeze Scalper  (1-min · 24/7 · BB12 · %B · RSI7 · EMA5)"

            LogEntries.Clear()
            LogLine("─────────────────────────────────────────────────────────────────────")
            LogLine("Configure account + risk settings above, then click  ▶ Start Monitoring.")
            LogLine($"• 1-min bars · ATR stops · 1ct · 15s polling")
            LogLine("• Mode B (Band Bounce): %B < 0 or > 1 + RSI7 extreme + rejection wick ≥ 60%")
            LogLine("• Mode A (Squeeze Breakout): BBW < SMA(BBW,20) ≥3 bars + band break + EMA5 + RSI7")
            LogLine("• 5 independent sessions — BTC · ETH · XRP · SOL · BNB")
            LogLine("━━━  BB Squeeze Scalper — CryptoJoe 5-Asset Monitor  ━━━")
        End Sub

        ''' <summary>
        ''' Activates the VIDYA Cross strategy for all 5 crypto assets.
        ''' CMO-adaptive EMA that is fast during trending conditions and near-flat in chop.
        ''' Ideal for crypto where momentum shifts can be sharp and decisive.
        ''' </summary>
        Private Sub ApplyVidya()
            _currentStrategy = New StrategyDefinition With {
                .Name = "VIDYA Cross",
                .Indicator = StrategyIndicatorType.Vidya,
                .Condition = StrategyConditionType.VidyaCross,
                .IndicatorPeriod = 14,
                .SecondaryPeriod = 9,
                .IndicatorMultiplier = 0,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .TimeframeMinutes = 5,
                .DurationHours = 8760,
                .Quantity = 1,
                .MinConfidencePct = _minConfidencePct
            }

            HasParsedStrategy = True
            ActiveStrategyText = "✔  VIDYA Cross  (5-min · 24/7 · VIDYA(14) · CMO(9) · Adaptive EMA)"

            LogEntries.Clear()
            LogLine("─────────────────────────────────────────────────────────────────────")
            LogLine("Configure account + risk settings above, then click  ▶ Start Monitoring.")
            LogLine($"• 5-min bars · VIDYA(14) · CMO(9) · 1ct · Conf={_minConfidencePct}%")
            LogLine("• Long when close crosses above VIDYA · Short when close crosses below VIDYA")
            LogLine("• Dynamic alpha speeds up during strong momentum, slows in ranging markets")
            LogLine("• 5 independent sessions — BTC · ETH · XRP · SOL · BNB")
            LogLine("━━━  VIDYA Cross — CryptoJoe 5-Asset Monitor  ━━━")
        End Sub

        ''' <summary>
        ''' Activates the Naked Trader strategy for all 5 crypto assets.
        ''' 4-vote consensus: EMA(9/21) + MACD(8,17,9) + DMI/ADX(14) + VWAP on 5-min bars.
        ''' Fires on Medium (3/4 votes, ADX≥20) or High (all votes, ADX≥25+vol) confidence.
        ''' DurationHours = 8 760 so sessions never auto-expire.
        ''' </summary>
        Private Sub ApplyNakedTrader()
            _currentStrategy = New StrategyDefinition With {
                .Name = "Naked Trader",
                .Indicator = StrategyIndicatorType.NakedTrader,
                .Condition = StrategyConditionType.NakedTrader,
                .IndicatorPeriod = 21,
                .SecondaryPeriod = 9,
                .IndicatorMultiplier = 0,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .TimeframeMinutes = 5,
                .DurationHours = 8760,
                .Quantity = 1,
                .MinConfidencePct = _minConfidencePct
            }

            HasParsedStrategy = True
            ActiveStrategyText = "✔  Naked Trader  (5-min · 24/7 · EMA9/21 · MACD · DMI/ADX · VWAP · 4-vote)"

            LogEntries.Clear()
            LogLine("─────────────────────────────────────────────────────────────────────")
            LogLine("Configure account + risk settings above, then click  ▶ Start Monitoring.")
            LogLine($"• 5-min bars · ATR stops · 1ct · Conf={_minConfidencePct}%")
            LogLine("• Medium confidence: 3/4 votes + ADX≥20 → 60% · High confidence: 4/4 votes + ADX≥25 + vol → 90%")
            LogLine("• 5 independent sessions — BTC · ETH · XRP · SOL · BNB")
            LogLine("━━━  Naked Trader — CryptoJoe 5-Asset Monitor  ━━━")
        End Sub

        ' ── Command handlers ──────────────────────────────────────────────────────

        Private Sub ExecuteStart(param As Object)
            If _currentStrategy Is Nothing OrElse SelectedAccount Is Nothing Then Return

            IsRunning = True
            StatusText = $"● Running — 🪙 CryptoJoe | {String.Join(" · ", Assets.Select(Function(a) a.Symbol))}"
            LogEntries.Clear()

            For i = 0 To Assets.Count - 1
                Dim assetVm = Assets(i)
                ' Deep-copy the shared template so each engine has its own independent state.
                Dim sd As New StrategyDefinition With {
                    .Name = _currentStrategy.Name,
                    .Indicator = _currentStrategy.Indicator,
                    .Condition = _currentStrategy.Condition,
                    .IndicatorPeriod = _currentStrategy.IndicatorPeriod,
                    .SecondaryPeriod = _currentStrategy.SecondaryPeriod,
                    .IndicatorMultiplier = _currentStrategy.IndicatorMultiplier,
                    .GoLongWhenBelowBands = _currentStrategy.GoLongWhenBelowBands,
                    .GoShortWhenAboveBands = _currentStrategy.GoShortWhenAboveBands,
                    .TimeframeMinutes = _currentStrategy.TimeframeMinutes,
                    .DurationHours = _currentStrategy.DurationHours,
                    .ContractId = assetVm.ContractId,
                    .AccountId = SelectedAccount.Id,
                    .Quantity = 1,
                    .MinConfidencePct = _minConfidencePct
                }
                _engines(i).Start(sd)
                LogLine($"[{assetVm.Symbol}] Session started")
            Next
        End Sub

        Private Sub ExecuteStop(param As Object)
            For i = 0 To Assets.Count - 1
                _engines(i).[Stop]()
            Next
            IsRunning = False
            StatusText = "● Idle"
        End Sub

        ' ── Helpers ───────────────────────────────────────────────────────────────

        Private Sub LogLine(message As String)
            LogEntries.Insert(0, message)
            Do While LogEntries.Count > 500
                LogEntries.RemoveAt(LogEntries.Count - 1)
            Loop
        End Sub

        Private Sub Dispatch(action As Action)
            If Application.Current?.Dispatcher IsNot Nothing Then
                Application.Current.Dispatcher.Invoke(action)
            End If
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                RemoveHandler _session.AccountChanged, AddressOf OnSessionAccountChanged
                For i = 0 To Assets.Count - 1
                    Try
                        _engines(i)?.Dispose()
                    Catch
                    End Try
                    Try
                        _assetScopes(i)?.Dispose()
                    Catch
                    End Try
                Next
                _disposed = True
            End If
        End Sub

    End Class

End Namespace
