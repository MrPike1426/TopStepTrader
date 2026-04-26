Imports System.Collections.ObjectModel
Imports System.Linq
Imports System.Text
Imports System.Threading
Imports System.Windows
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.AI
Imports TopStepTrader.Services.Personas
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' ViewModel for Tab 2 — Maximum Effort!
    ''' Runs 1,080 backtest combinations (3 personas × 5 instruments × 9 strategies × 8 timeframes)
    ''' and requests Claude Haiku analysis of the top 20 results.
    ''' Extracted from BacktestViewModel as part of ARCH-02b.
    ''' </summary>
    Public Class MaxEffortViewModel
        Inherits ViewModelBase

        Private ReadOnly _backtestService As IBacktestService
        Private ReadOnly _barCollectionService As IBarCollectionService
        Private ReadOnly _claudeReviewService As IClaudeReviewService
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _personaService As IPersonaService
        Private ReadOnly _pinnedResults As ObservableCollection(Of MaxEffortRowVm)
        Private ReadOnly _runVm As BacktestRunViewModel
        Private ReadOnly _slotStore As ProTraderSlotStore

        Private _cancelSource As CancellationTokenSource

        ' ══════════════════════════════════════════════════════════════════════
        ' COMMANDS
        ' ══════════════════════════════════════════════════════════════════════

        Public ReadOnly Property MaximumEffortCommand As RelayCommand
        Public ReadOnly Property MaximumEffortCancelCommand As RelayCommand
        Public ReadOnly Property PinResultCommand As RelayCommand
        Public ReadOnly Property CopyAiAnalysisCommand As RelayCommand
        Public ReadOnly Property ToggleValidateSplitCommand As RelayCommand

        ' ══════════════════════════════════════════════════════════════════════
        ' COLLECTIONS
        ' ══════════════════════════════════════════════════════════════════════

        Public ReadOnly Property MaxEffortResults As New ObservableCollection(Of MaxEffortRowVm)()

        ' ══════════════════════════════════════════════════════════════════════
        ' STATE PROPERTIES
        ' ══════════════════════════════════════════════════════════════════════

        Private _isRunning As Boolean
        Public Property MaxEffortIsRunning As Boolean
            Get
                Return _isRunning
            End Get
            Set(value As Boolean)
                SetProperty(_isRunning, value)
                OnPropertyChanged(NameOf(MaxEffortCanRun))
                OnPropertyChanged(NameOf(MaxEffortCanCancel))
            End Set
        End Property

        Public ReadOnly Property MaxEffortCanRun As Boolean
            Get
                Return Not _isRunning
            End Get
        End Property

        Public ReadOnly Property MaxEffortCanCancel As Boolean
            Get
                Return _isRunning
            End Get
        End Property

        Private _progressText As String = "Click Maximum Effort! to run all 1,080 combinations (3 personas × 5 instruments × 9 strategies × 8 timeframes)."
        Public Property MaxEffortProgressText As String
            Get
                Return _progressText
            End Get
            Set(value As String)
                SetProperty(_progressText, value)
            End Set
        End Property

        Private _progress As Integer
        Public Property MaxEffortProgress As Integer
            Get
                Return _progress
            End Get
            Set(value As Integer)
                SetProperty(_progress, value)
            End Set
        End Property

        Private _aiAnalysis As String = ""
        Public Property MaxEffortAiAnalysis As String
            Get
                Return _aiAnalysis
            End Get
            Set(value As String)
                SetProperty(_aiAnalysis, value)
            End Set
        End Property

        Private _aiIsLoading As Boolean
        Public Property MaxEffortAiIsLoading As Boolean
            Get
                Return _aiIsLoading
            End Get
            Set(value As Boolean)
                SetProperty(_aiIsLoading, value)
            End Set
        End Property

        Private _validateSplitEnabled As Boolean = False
        Public Property ValidateSplitEnabled As Boolean
            Get
                Return _validateSplitEnabled
            End Get
            Set(value As Boolean)
                SetProperty(_validateSplitEnabled, value)
            End Set
        End Property

        Private _bestPickLine1 As String = ""
        Public Property BestPickLine1 As String
            Get
                Return _bestPickLine1
            End Get
            Set(value As String)
                SetProperty(_bestPickLine1, value)
                OnPropertyChanged(NameOf(HasBestPick))
            End Set
        End Property

        Private _bestPickLine2 As String = ""
        Public Property BestPickLine2 As String
            Get
                Return _bestPickLine2
            End Get
            Set(value As String)
                SetProperty(_bestPickLine2, value)
            End Set
        End Property

        Public ReadOnly Property HasBestPick As Boolean
            Get
                Return Not String.IsNullOrEmpty(_bestPickLine1)
            End Get
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' CONSTRUCTOR
        ' ══════════════════════════════════════════════════════════════════════

        ''' <param name="pinnedResults">Shared reference to the shell's PinnedResults collection — the Pin button appends here.</param>
        ''' <param name="runVm">Tab 1 VM — ForceClose settings are read from here at run time.</param>
        ''' <param name="slotStore">Optional — when set, pinning a row also adds a slot to Pro-Trader.</param>
        Public Sub New(backtestService As IBacktestService,
                       barCollectionService As IBarCollectionService,
                       claudeReviewService As IClaudeReviewService,
                       session As ITradingSessionContext,
                       personaService As IPersonaService,
                       pinnedResults As ObservableCollection(Of MaxEffortRowVm),
                       runVm As BacktestRunViewModel,
                       Optional slotStore As ProTraderSlotStore = Nothing)

            _backtestService = backtestService
            _barCollectionService = barCollectionService
            _claudeReviewService = claudeReviewService
            _session = session
            _personaService = personaService
            _pinnedResults = pinnedResults
            _runVm = runVm
            _slotStore = slotStore

            MaximumEffortCommand = New RelayCommand(AddressOf ExecuteMaximumEffort, Function() Not _isRunning)
            MaximumEffortCancelCommand = New RelayCommand(Sub() _cancelSource?.Cancel(), Function() _isRunning)
            ToggleValidateSplitCommand = New RelayCommand(Sub() ValidateSplitEnabled = Not ValidateSplitEnabled)
            PinResultCommand = New RelayCommand(
                Sub(param)
                    Dim row = TryCast(param, MaxEffortRowVm)
                    If row IsNot Nothing AndAlso Not _pinnedResults.Contains(row) Then
                        _pinnedResults.Add(row)
                        If _slotStore IsNot Nothing AndAlso row.ContractIdRaw <> "" Then
                            Dim slot = New ProTraderSlotVm(
                                row.ContractIdRaw, row.Contract,
                                row.StrategyTypeRaw, row.TimeframeRaw,
                                row.Persona, row.SlAtrMultiple, row.TpAtrMultiple)
                            _slotStore.AddSlot(slot)
                        End If
                    End If
                End Sub,
                Function(param) param IsNot Nothing)
            CopyAiAnalysisCommand = New RelayCommand(
                Sub()
                    If Not String.IsNullOrEmpty(_aiAnalysis) Then
                        Clipboard.SetText(_aiAnalysis)
                    End If
                End Sub,
                Function() Not String.IsNullOrEmpty(_aiAnalysis))
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' MAXIMUM EFFORT EXECUTION
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub ExecuteMaximumEffort()
            MaxEffortResults.Clear()
            MaxEffortAiAnalysis = ""
            BestPickLine1 = ""
            BestPickLine2 = ""
            MaxEffortIsRunning = True
            MaxEffortProgress = 0
            MaxEffortProgressText = "Starting Maximum Effort run..."

            Dim contracts = FavouriteContracts.GetDefaults()

            Dim strategies = New (String, StrategyConditionType)() {
                ("EMA/RSI Combined",   StrategyConditionType.EmaRsiWeightedScore),
                ("Multi-Confluence",   StrategyConditionType.MultiConfluence),
                ("BB Squeeze",         StrategyConditionType.BbSqueezeScalper),
                ("VIDYA Cross",        StrategyConditionType.VidyaCross),
                ("Naked Trader",       StrategyConditionType.NakedTrader),
                ("LULT Divergence",    StrategyConditionType.LultDivergence),
                ("Double Bubble Butt", StrategyConditionType.DoubleBubbleButt),
                ("ORB",                StrategyConditionType.OpeningRangeBreakout),
                ("VWAP Mean Reversion", StrategyConditionType.VwapMeanReversion)
            }

            Dim timeframes = New (BarTimeframe, String)() {
                (BarTimeframe.OneMinute,     "1 min"),
                (BarTimeframe.FiveMinute,    "5 min"),
                (BarTimeframe.TenMinute,     "10 min"),
                (BarTimeframe.FifteenMinute, "15 min"),
                (BarTimeframe.ThirtyMinute,  "30 min"),
                (BarTimeframe.OneHour,       "1 hr"),
                (BarTimeframe.TwoHour,       "2 hr"),
                (BarTimeframe.FourHour,      "4 hr")
            }

            Dim personas = _personaService.GetAllProfiles()

            Dim totalRuns = contracts.Count * strategies.Length * timeframes.Length * personas.Count
            Dim capStart = DateTime.Today.AddDays(-180)
            Dim capEnd = DateTime.Today

            ' ForceClose settings are inherited from the Run Backtest tab (Tab 1)
            Dim capForceClose = _runVm.ForceCloseEnabled
            Dim capForceAmt As Decimal = 50D
            Decimal.TryParse(_runVm.ForceCloseAmount, capForceAmt)
            If capForceAmt <= 0 Then capForceAmt = 50D
            Dim capValidateSplit = ValidateSplitEnabled

            MaxEffortProgressText = $"Starting Maximum Effort run... ForceClose: {If(capForceClose, $"ON (${capForceAmt:F0}/day)", "OFF")} | ValidateSplit: {If(capValidateSplit, "ON", "OFF")}"

            _cancelSource = New CancellationTokenSource()
            Dim cts = _cancelSource

            Task.Run(Async Function()
                Dim runIndex = 0
                Dim rawResults As New List(Of MaxEffortRowVm)()
                Try

                For Each persona In personas
                    Dim personaShort = persona.Name.Split(" "c)(0)
                    Dim pSlMul = persona.SlMultipleOfN
                    Dim pTpMul = persona.TpMultipleOfN
                    For Each contract In contracts
                        For Each strat In strategies
                            For Each tf In timeframes
                                If cts.IsCancellationRequested Then Exit For

                                runIndex += 1
                                Dim contractId = contract.ContractId
                                Dim contractName = contract.Name
                                Dim stratName = strat.Item1
                                Dim stratCondition = strat.Item2
                                Dim tfEnum = tf.Item1
                                Dim tfLabel = tf.Item2
                                Dim ri = runIndex
                                Dim pShort = personaShort

                                Dispatch(Sub()
                                    MaxEffortProgressText =
                                        $"[{ri}/{totalRuns}]  {pShort}  ·  {contractName}  ·  {stratName}  ·  {tfLabel}"
                                    MaxEffortProgress = CInt((ri * 100.0) / totalRuns)
                                End Sub)

                                Try
                                    Dim maxDays = BacktestRunViewModel.GetMaxLookbackDays(tfEnum)
                                    Dim earliestAllowed = DateTime.Today.AddDays(-maxDays)
                                    Dim effectiveStart = If(capStart < earliestAllowed, earliestAllowed, capStart)

                                    Dim barResult = Await _barCollectionService.EnsureBarsAsync(
                                        contractId, effectiveStart, capEnd, tfEnum, cancel:=cts.Token)

                                    If Not barResult.Success Then Continue For

                                    Dim tickSize = contract.GetTickSize(_session.ActiveBroker)
                                    Dim pointValue = contract.GetPointValue(_session.ActiveBroker)
                                    If tickSize <= 0D Then tickSize = 0.01D
                                    If pointValue <= 0D Then pointValue = 1.0D

                                    Dim config As New BacktestConfiguration With {
                                        .RunName = $"ME · {pShort} · {contractName} · {stratName} · {tfLabel}",
                                        .ContractId = contractId,
                                        .Timeframe = CInt(tfEnum),
                                        .StartDate = effectiveStart,
                                        .EndDate = capEnd,
                                        .InitialCapital = 0D,
                                        .Quantity = persona.PositionSize,
                                        .TickSize = tickSize,
                                        .PointValue = pointValue,
                                        .MinSignalConfidence = CSng(persona.DefaultConfidencePct) / 100.0F,
                                        .MinAdxThreshold = persona.AdxThreshold,
                                        .MaxScaleIns = persona.MaxScaleIns,
                                        .StrategyCondition = stratCondition,
                                        .UseAtrMode = True,
                                        .SlAtrMultiple = persona.SlMultipleOfN,
                                        .TpAtrMultiple = persona.TpMultipleOfN,
                                        .ForceCloseEnabled = capForceClose,
                                        .ForceCloseAmount = capForceAmt,
                                        .SlippageTicks = 1,
                                        .CommissionPerSideUsd = contract.RoundTripFee / 2D,
                                        .TrainTestSplit = If(capValidateSplit, 0.6, 0.0)
                                    }

                                    Dim result = Await _backtestService.RunBacktestAsync(config, cts.Token)
                                    Dim row = New MaxEffortRowVm(pShort, contractName, stratName, tfLabel, result,
                                                                 contractId, stratCondition, tfEnum, pSlMul, pTpMul)

                                    rawResults.Add(row)
                                    Dispatch(Sub()
                                        Dim insertAt = MaxEffortResults.Count
                                        For j = 0 To MaxEffortResults.Count - 1
                                            Dim cmpKey = If(capValidateSplit, MaxEffortResults(j).TestPnLRaw, MaxEffortResults(j).CalmarRaw)
                                            Dim rowKey  = If(capValidateSplit, row.TestPnLRaw, row.CalmarRaw)
                                            If cmpKey < rowKey Then
                                                insertAt = j
                                                Exit For
                                            End If
                                        Next
                                        MaxEffortResults.Insert(insertAt, row)
                                    End Sub)

                                Catch ex As OperationCanceledException
                                    Exit For
                                Catch
                                End Try
                            Next
                            If cts.IsCancellationRequested Then Exit For
                        Next
                        If cts.IsCancellationRequested Then Exit For
                    Next
                    If cts.IsCancellationRequested Then Exit For
                Next

                Dim top20 = If(capValidateSplit,
                               rawResults.OrderByDescending(Function(r) r.TestPnLRaw).Take(20).ToList(),
                               rawResults.OrderByDescending(Function(r) r.CalmarRaw).Take(20).ToList())
                If top20.Count > 0 AndAlso Not cts.IsCancellationRequested Then
                    Dispatch(Sub()
                        MaxEffortProgress = 100
                        MaxEffortProgressText = $"Complete — {rawResults.Count} combinations ran. Asking Claude Haiku for analysis..."
                        MaxEffortAiIsLoading = True
                    End Sub)

                    Dim sb As New StringBuilder()
                    sb.AppendLine($"Backtest results — {rawResults.Count} combinations across {contracts.Count} instruments, 9 strategies, 8 timeframes, 3 personas (Lewis/Damian/Joe).")
                    Dim dayCount = (capEnd - capStart).Days
                    Dim feeList = String.Join(", ", contracts.Select(Function(c) $"{c.Name}=${c.RoundTripFee:F2}"))
                    sb.AppendLine($"Date range: {dayCount} days ({capStart:yyyy-MM-dd} to {capEnd:yyyy-MM-dd}). Commission = contract round-trip fee ({feeList}) + 1-tick slippage per entry.")
                    sb.AppendLine($"ATR-based stops: Lewis SL=1.5×/TP=3.0×N  Damian SL=1.0×/TP=2.0×N  Joe SL=0.75×/TP=2.0×N  (N = ATR14 × point value).")
                    sb.AppendLine($"ForceClose: {If(capForceClose, $"ON (daily loss cap = ${capForceAmt:F0})", "OFF")}.")
                    If capValidateSplit Then
                        sb.AppendLine("OUT-OF-SAMPLE VALIDATION: 60/40 train/test split applied. Results sorted by out-of-sample Test P&L.")
                        sb.AppendLine("Degradation = (TrainPnL − TestPnL) / |TrainPnL| × 100. >50% = possible overfit; negative TestPnL = strategy reverses out-of-sample.")
                    Else
                        sb.AppendLine("Ranking: Calmar ratio (TotalP&L / MaxDrawdown) — normalises for instrument point-value and volatility so MES and Gold are directly comparable.")
                    End If
                    sb.AppendLine()
                    If capValidateSplit Then
                        sb.AppendLine("TOP 20 BY TEST P&L (out-of-sample):")
                        sb.AppendLine("Rank | Persona | Contract | Strategy | Timeframe | Trades | Win% | Train P&L | Test P&L | Degradation%")
                        sb.AppendLine("-----|---------|----------|----------|-----------|--------|------|-----------|----------|-------------")
                        For rank = 1 To top20.Count
                            Dim r = top20(rank - 1)
                            sb.AppendLine($"{rank,4} | {r.Persona,-7} | {r.Contract,-12} | {r.Strategy,-17} | {r.Timeframe,8} | {r.Trades,6} | {r.WinRate,5} | {r.TotalPnL,9} | {r.TestPnL,8} | {r.DegradationPct,12}")
                        Next
                    Else
                        sb.AppendLine("TOP 20 BY CALMAR RATIO (P&L / MaxDrawdown — cross-instrument fair ranking):")
                        sb.AppendLine("Rank | Persona | Contract | Strategy | Timeframe | Trades | Win% | P&L | MaxDD | Calmar | Sharpe | Avg P&L")
                        sb.AppendLine("-----|---------|----------|----------|-----------|--------|------|-----|-------|--------|--------|--------")
                        For rank = 1 To top20.Count
                            Dim r = top20(rank - 1)
                            sb.AppendLine($"{rank,4} | {r.Persona,-7} | {r.Contract,-12} | {r.Strategy,-17} | {r.Timeframe,8} | {r.Trades,6} | {r.WinRate,5} | {r.TotalPnL,8} | {r.MaxDD,7} | {r.Calmar,6} | {r.Sharpe,6} | {r.AvgPnL}")
                        Next
                    End If

                    Dim analysis = Await _claudeReviewService.AnalyseBacktestResultsAsync(sb.ToString(), cts.Token)
                    Dispatch(Sub()
                        MaxEffortAiAnalysis = analysis
                        MaxEffortAiIsLoading = False
                        ParseBestPick(analysis)
                    End Sub)
                Else
                    Dispatch(Sub()
                        MaxEffortProgress = 100
                        MaxEffortProgressText = If(cts.IsCancellationRequested,
                            $"Cancelled — {rawResults.Count} combinations ran.",
                            $"Complete — {rawResults.Count} combinations ran.")
                    End Sub)
                End If

                Finally
                    Dispatch(Sub()
                        MaxEffortIsRunning = False
                        cts?.Dispose()
                    End Sub)
                End Try
            End Function)
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' BEST PICK PARSER
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub ParseBestPick(analysis As String)
            Dim candidate = BestPickParser.ParseRecommendation(analysis)
            If candidate Is Nothing Then Return

            Dim matchedRow = MaxEffortResults.FirstOrDefault(
                Function(r)
                    Return r.Persona.Equals(candidate.Persona, StringComparison.OrdinalIgnoreCase) AndAlso
                           candidate.RecommendationLine.IndexOf(r.Contract, StringComparison.OrdinalIgnoreCase) >= 0
                End Function)

            If matchedRow IsNot Nothing Then
                BestPickLine1 = $"{matchedRow.Persona} · {matchedRow.Contract} · {matchedRow.Strategy} · {matchedRow.Timeframe}"
                BestPickLine2 = $"Sharpe {matchedRow.Sharpe} · Win {matchedRow.WinRate} · {matchedRow.AvgPnL}/trade"
                matchedRow.IsRecommended = True
            End If
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' INFRASTRUCTURE
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub Dispatch(action As Action)
            If Application.Current?.Dispatcher IsNot Nothing Then
                Application.Current.Dispatcher.Invoke(action)
            End If
        End Sub

    End Class

    ' ══════════════════════════════════════════════════════════════════════════
    ' MAXIMUM EFFORT ROW — shared by MaxEffortViewModel and PinnedResultsViewModel
    ' ══════════════════════════════════════════════════════════════════════════

    Public Class MaxEffortRowVm
        Inherits ViewModelBase

        Public ReadOnly Property Persona As String
        Public ReadOnly Property Contract As String
        Public ReadOnly Property Strategy As String
        Public ReadOnly Property Timeframe As String
        Public ReadOnly Property Trades As String
        Public ReadOnly Property WinRate As String
        Public ReadOnly Property TotalPnL As String
        Public ReadOnly Property TotalPnLRaw As Decimal
        Public ReadOnly Property TotalPnLColor As String
        Public ReadOnly Property Sharpe As String
        Public ReadOnly Property AvgPnL As String
        Public ReadOnly Property MaxDD As String
        Public ReadOnly Property CalmarRaw As Decimal
        Public ReadOnly Property Calmar As String
        Public ReadOnly Property EndOfDayCount As String
        Public ReadOnly Property CommissionPaid As String
        ' Out-of-sample split columns (FEAT-13) — "—" when no split active
        Public ReadOnly Property TestPnL As String
        Public ReadOnly Property TestPnLRaw As Decimal
        Public ReadOnly Property TestPnLColor As String
        Public ReadOnly Property DegradationPct As String
        ' Raw typed fields for Pro-Trader slot construction (FEAT-16)
        Public ReadOnly Property ContractIdRaw As String
        Public ReadOnly Property StrategyTypeRaw As StrategyConditionType
        Public ReadOnly Property TimeframeRaw As BarTimeframe
        Public ReadOnly Property SlAtrMultiple As Decimal
        Public ReadOnly Property TpAtrMultiple As Decimal

        Private _oosBackground As String = "Transparent"
        Private _isRecommended As Boolean

        Public Property IsRecommended As Boolean
            Get
                Return _isRecommended
            End Get
            Set(value As Boolean)
                _isRecommended = value
                OnPropertyChanged(NameOf(IsRecommended))
                OnPropertyChanged(NameOf(RowBackground))
            End Set
        End Property

        Public ReadOnly Property RowBackground As String
            Get
                If _isRecommended Then Return "#2D2000"
                Return _oosBackground
            End Get
        End Property

        Public Sub New(personaName As String, contractName As String, strategyName As String,
                       timeframeLabel As String, result As BacktestResult,
                       Optional contractIdRaw As String = "",
                       Optional strategyTypeRaw As StrategyConditionType = StrategyConditionType.EmaRsiWeightedScore,
                       Optional timeframeRaw As BarTimeframe = BarTimeframe.OneHour,
                       Optional slAtrMultiple As Decimal = 0D,
                       Optional tpAtrMultiple As Decimal = 0D)
            Persona = personaName
            Contract = contractName
            Strategy = strategyName
            Timeframe = timeframeLabel
            Trades = result.TotalTrades.ToString()
            WinRate = result.WinRate.ToString("P0")
            TotalPnLRaw = result.TotalPnL
            TotalPnL = result.TotalPnL.ToString("C0")
            TotalPnLColor = If(result.TotalPnL >= 0, "BuyBrush", "SellBrush")
            Sharpe = If(result.SharpeRatio.HasValue, result.SharpeRatio.Value.ToString("F2"), "—")
            AvgPnL = result.AveragePnLPerTrade.ToString("C0")
            MaxDD = result.MaxDrawdown.ToString("C0")
            Dim rawCalmar = If(result.MaxDrawdown > 0D, result.TotalPnL / result.MaxDrawdown, result.TotalPnL)
            CalmarRaw = rawCalmar
            Calmar = rawCalmar.ToString("F2")
            EndOfDayCount = result.EndOfDayCloseCount.ToString()
            CommissionPaid = result.CommissionPaid.ToString("C0")
            ContractIdRaw = contractIdRaw
            StrategyTypeRaw = strategyTypeRaw
            TimeframeRaw = timeframeRaw
            Me.SlAtrMultiple = slAtrMultiple
            Me.TpAtrMultiple = tpAtrMultiple
            If result.OutOfSampleResult IsNot Nothing Then
                Dim oos = result.OutOfSampleResult
                TestPnLRaw = oos.TotalPnL
                TestPnL = oos.TotalPnL.ToString("C0")
                TestPnLColor = If(oos.TotalPnL >= 0, "BuyBrush", "SellBrush")
                If result.TotalPnL <> 0D Then
                    Dim deg = (result.TotalPnL - oos.TotalPnL) / Math.Abs(result.TotalPnL) * 100D
                    DegradationPct = deg.ToString("F0") & "%"
                    If oos.TotalPnL < 0D Then
                        _oosBackground = "#220000"
                    ElseIf deg > 50D Then
                        _oosBackground = "#1A1200"
                    Else
                        _oosBackground = "Transparent"
                    End If
                Else
                    DegradationPct = "—"
                    _oosBackground = "Transparent"
                End If
            Else
                TestPnLRaw = Decimal.MinValue  ' sentinel: sorts below all real OOS results when ValidateSplit is ON
                TestPnL = "—"
                TestPnLColor = "TextSecondaryBrush"
                DegradationPct = "—"
                _oosBackground = "Transparent"
            End If
        End Sub

        Public Sub New(personaName As String, contractName As String, strategyName As String,
                       timeframeLabel As String, trades As Integer, winRatePct As Double,
                       totalPnLRaw As Decimal, sharpe As Double, avgPnL As Decimal, maxDD As Decimal)
            Persona = personaName
            Contract = contractName
            Strategy = strategyName
            Timeframe = timeframeLabel
            Me.Trades = trades.ToString()
            WinRate = (winRatePct / 100.0).ToString("P0")
            TotalPnLRaw = totalPnLRaw
            TotalPnL = totalPnLRaw.ToString("C0")
            TotalPnLColor = If(totalPnLRaw >= 0, "BuyBrush", "SellBrush")
            Me.Sharpe = sharpe.ToString("F2")
            AvgPnL = avgPnL.ToString("C0")
            MaxDD = maxDD.ToString("C0")
            Dim rawCalmar2 = If(maxDD > 0D, totalPnLRaw / maxDD, totalPnLRaw)
            CalmarRaw = rawCalmar2
            Calmar = rawCalmar2.ToString("F2")
            CommissionPaid = "—"
            TestPnL = "—"
            TestPnLColor = "TextSecondaryBrush"
            DegradationPct = "—"
            _oosBackground = "Transparent"
        End Sub
    End Class

End Namespace
