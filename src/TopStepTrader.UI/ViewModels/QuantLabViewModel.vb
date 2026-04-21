Imports System.Collections.ObjectModel
Imports System.IO
Imports System.Text
Imports System.Threading
Imports System.Windows
Imports Microsoft.Win32
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Services.AI
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ' ── Strategy card model ───────────────────────────────────────────────────

    ''' <summary>
    ''' Display model for a single strategy card on the QuantLab page.
    ''' </summary>
    Public Class QuantLabStrategyCard
        Inherits ViewModelBase

        Public Property Name As String = String.Empty
        Public Property Subtitle As String = String.Empty
        Public Property Description As String = String.Empty
        Public Property Emoji As String = "📊"
        Public Property WinRateRange As String = String.Empty
        Public Property SharpeRange As String = String.Empty
        Public Property StrategyType As String = String.Empty   ' enum name as string
        Public Property ConditionType As StrategyConditionType

        Private _isSelected As Boolean
        Public Property IsSelected As Boolean
            Get
                Return _isSelected
            End Get
            Set(value As Boolean)
                SetProperty(_isSelected, value)
            End Set
        End Property
    End Class

    ' ── Main ViewModel ───────────────────────────────────────────────────────

    ''' <summary>
    ''' QuantLab — Research Strategy Testing page.
    '''
    ''' Provides a card-based interface to test four academically-validated
    ''' algorithmic trading strategies against historical bar data:
    '''
    '''   1. Connors RSI-2      — mean reversion using RSI(2) + SMA(200)
    '''   2. SuperTrend         — ATR-based trend-following with direction flips
    '''   3. Donchian Breakout  — 20-bar channel breakout (Turtle system)
    '''   4. BB + RSI           — Bollinger Band + RSI dual-confirmation reversion
    '''
    ''' Workflow:
    '''   1. Click a strategy card to select it.
    '''   2. Enter a contract and date range.
    '''   3. Click "Run Backtest" — bars are downloaded if needed, then the
    '''      BacktestEngine is invoked with the matching StrategyConditionType.
    '''   4. Results (win rate, Sharpe, P&amp;L, drawdown) are displayed below.
    '''   5. Optionally export the trade list to CSV.
    ''' </summary>
    Public Class QuantLabViewModel
        Inherits ViewModelBase

        Private ReadOnly _backtestService As IBacktestService
        Private ReadOnly _barCollectionService As IBarCollectionService
        Private ReadOnly _claudeReviewService As ClaudeReviewService

        Private _cancelSource As CancellationTokenSource

        ' ══════════════════════════════════════════════════════════════════════
        ' STRATEGY CARDS
        ' ══════════════════════════════════════════════════════════════════════

        Public ReadOnly Property StrategyCards As ObservableCollection(Of QuantLabStrategyCard)

        Private _selectedCard As QuantLabStrategyCard
        Public Property SelectedCard As QuantLabStrategyCard
            Get
                Return _selectedCard
            End Get
            Set(value As QuantLabStrategyCard)
                If _selectedCard IsNot Nothing Then _selectedCard.IsSelected = False
                SetProperty(_selectedCard, value)
                If _selectedCard IsNot Nothing Then _selectedCard.IsSelected = True
                OnPropertyChanged(NameOf(SelectedStrategyName))
                OnPropertyChanged(NameOf(HasSelectedStrategy))
                ClearResults()
                RelayCommand.RaiseCanExecuteChanged()
            End Set
        End Property

        Public ReadOnly Property SelectedStrategyName As String
            Get
                Return If(_selectedCard?.Name, String.Empty)
            End Get
        End Property

        Public ReadOnly Property HasSelectedStrategy As Boolean
            Get
                Return _selectedCard IsNot Nothing
            End Get
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' CONFIGURATION PROPERTIES
        ' ══════════════════════════════════════════════════════════════════════

        Private _contractIdText As String = ""
        ''' <summary>Long-form contract ID set by ContractSelectorControl.</summary>
        Public Property ContractIdText As String
            Get
                Return _contractIdText
            End Get
            Set(value As String)
                Dim old = _contractIdText
                SetProperty(_contractIdText, value)
                If old <> value Then ClearResults()
            End Set
        End Property

        Private _startDate As Date = DateTime.Today.AddMonths(-6)
        Public Property StartDate As Date
            Get
                Return _startDate
            End Get
            Set(value As Date)
                SetProperty(_startDate, value)
            End Set
        End Property

        Private _endDate As Date = DateTime.Today
        Public Property EndDate As Date
            Get
                Return _endDate
            End Get
            Set(value As Date)
                SetProperty(_endDate, value)
            End Set
        End Property

        Private _initialCapital As String = "50000"
        Public Property InitialCapital As String
            Get
                Return _initialCapital
            End Get
            Set(value As String)
                SetProperty(_initialCapital, value)
            End Set
        End Property

        Private _quantity As String = "1"
        Public Property Quantity As String
            Get
                Return _quantity
            End Get
            Set(value As String)
                SetProperty(_quantity, value)
            End Set
        End Property

        Private _selectedInterval As String = "1 day"
        Public Property SelectedInterval As String
            Get
                Return _selectedInterval
            End Get
            Set(value As String)
                SetProperty(_selectedInterval, value)
            End Set
        End Property

        Public ReadOnly Property AvailableIntervals As New List(Of String) From {
            "1 min", "5 min", "15 min", "30 min", "1 hour", "4 hour", "1 day"
        }

        ' ══════════════════════════════════════════════════════════════════════
        ' PROGRESS & STATE
        ' ══════════════════════════════════════════════════════════════════════

        Private _isRunning As Boolean
        Public Property IsRunning As Boolean
            Get
                Return _isRunning
            End Get
            Set(value As Boolean)
                SetProperty(_isRunning, value)
                OnPropertyChanged(NameOf(IsIdle))
                RelayCommand.RaiseCanExecuteChanged()
            End Set
        End Property

        Public ReadOnly Property IsIdle As Boolean
            Get
                Return Not _isRunning
            End Get
        End Property

        Private _progressPct As Integer
        Public Property ProgressPct As Integer
            Get
                Return _progressPct
            End Get
            Set(value As Integer)
                SetProperty(_progressPct, value)
            End Set
        End Property

        Private _statusText As String = "Select a strategy to begin."
        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Set(value As String)
                SetProperty(_statusText, value)
            End Set
        End Property

        Private _hasResults As Boolean
        Public Property HasResults As Boolean
            Get
                Return _hasResults
            End Get
            Set(value As Boolean)
                SetProperty(_hasResults, value)
            End Set
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' RESULTS PROPERTIES
        ' ══════════════════════════════════════════════════════════════════════

        Private _totalTrades As String = "--"
        Public Property TotalTrades As String
            Get
                Return _totalTrades
            End Get
            Set(value As String)
                SetProperty(_totalTrades, value)
            End Set
        End Property

        Private _winRate As String = "--"
        Public Property WinRate As String
            Get
                Return _winRate
            End Get
            Set(value As String)
                SetProperty(_winRate, value)
            End Set
        End Property

        Private _totalPnL As String = "--"
        Public Property TotalPnL As String
            Get
                Return _totalPnL
            End Get
            Set(value As String)
                SetProperty(_totalPnL, value)
            End Set
        End Property

        Private _sharpe As String = "--"
        Public Property Sharpe As String
            Get
                Return _sharpe
            End Get
            Set(value As String)
                SetProperty(_sharpe, value)
            End Set
        End Property

        Private _maxDrawdown As String = "--"
        Public Property MaxDrawdown As String
            Get
                Return _maxDrawdown
            End Get
            Set(value As String)
                SetProperty(_maxDrawdown, value)
            End Set
        End Property

        Private _avgPnL As String = "--"
        Public Property AvgPnL As String
            Get
                Return _avgPnL
            End Get
            Set(value As String)
                SetProperty(_avgPnL, value)
            End Set
        End Property

        Private _finalCapital As String = "--"
        Public Property FinalCapital As String
            Get
                Return _finalCapital
            End Get
            Set(value As String)
                SetProperty(_finalCapital, value)
            End Set
        End Property

        Private _winRateColour As String = "#AAAAAA"
        Public Property WinRateColour As String
            Get
                Return _winRateColour
            End Get
            Set(value As String)
                SetProperty(_winRateColour, value)
            End Set
        End Property

        Private _pnlColour As String = "#AAAAAA"
        Public Property PnlColour As String
            Get
                Return _pnlColour
            End Get
            Set(value As String)
                SetProperty(_pnlColour, value)
            End Set
        End Property

        Public ReadOnly Property Trades As ObservableCollection(Of BacktestTrade)

        ' ══════════════════════════════════════════════════════════════════════
        ' COMMANDS
        ' ══════════════════════════════════════════════════════════════════════

        Public ReadOnly Property RunCommand As RelayCommand
        Public ReadOnly Property CancelCommand As RelayCommand
        Public ReadOnly Property SelectCardCommand As RelayCommand(Of QuantLabStrategyCard)
        Public ReadOnly Property ExportCsvCommand As RelayCommand
        Public ReadOnly Property AskClaudeCommand As RelayCommand

        ' ══════════════════════════════════════════════════════════════════════
        ' CONSTRUCTOR
        ' ══════════════════════════════════════════════════════════════════════

        ' ══════════════════════════════════════════════════════════════════════
        ' CLAUDE ANALYSIS
        ' ══════════════════════════════════════════════════════════════════════

        Private _claudeAnalysis As String = String.Empty
        Public Property ClaudeAnalysis As String
            Get
                Return _claudeAnalysis
            End Get
            Set(value As String)
                SetProperty(_claudeAnalysis, value)
            End Set
        End Property

        Private _isClaudeAnalysing As Boolean = False
        Public Property IsClaudeAnalysing As Boolean
            Get
                Return _isClaudeAnalysing
            End Get
            Set(value As Boolean)
                If SetProperty(_isClaudeAnalysing, value) Then
                    RelayCommand.RaiseCanExecuteChanged()
                End If
            End Set
        End Property

        Private _hasClaudeAnalysis As Boolean = False
        Public Property HasClaudeAnalysis As Boolean
            Get
                Return _hasClaudeAnalysis
            End Get
            Set(value As Boolean)
                SetProperty(_hasClaudeAnalysis, value)
            End Set
        End Property

        Public Sub New(backtestService As IBacktestService,
                       barCollectionService As IBarCollectionService,
                       claudeReviewService As ClaudeReviewService)
            _backtestService = backtestService
            _barCollectionService = barCollectionService
            _claudeReviewService = claudeReviewService

            Trades = New ObservableCollection(Of BacktestTrade)()

            ' Build strategy cards
            StrategyCards = New ObservableCollection(Of QuantLabStrategyCard) From {
                New QuantLabStrategyCard With {
                    .Name = "Connors RSI-2",
                    .Subtitle = "Mean Reversion",
                    .Description = "RSI(2) oversold/overbought dips filtered by a 200-bar SMA long-term trend. " &
                                   "Buys short-term pullbacks in bull markets; shorts short-term rallies in bear markets.",
                    .Emoji = "📉",
                    .WinRateRange = "67–72%",
                    .SharpeRange = "1.0–1.5",
                    .StrategyType = "ConnorsRsi2",
                    .ConditionType = StrategyConditionType.ConnorsRsi2
                },
                New QuantLabStrategyCard With {
                    .Name = "SuperTrend",
                    .Subtitle = "Trend-Following",
                    .Description = "ATR(10) × 3.0 dynamic support/resistance. Enters long on direction flip " &
                                   "from bearish to bullish, short on reverse. SL at SuperTrend line; TP = 2× ATR.",
                    .Emoji = "📈",
                    .WinRateRange = "40–52%",
                    .SharpeRange = "0.70–1.05",
                    .StrategyType = "SuperTrend",
                    .ConditionType = StrategyConditionType.SuperTrend
                },
                New QuantLabStrategyCard With {
                    .Name = "Donchian Breakout",
                    .Subtitle = "Turtle / Breakout",
                    .Description = "Buys 20-bar highest-high breakouts; shorts 20-bar lowest-low breakouts. " &
                                   "Classic Turtle Trading system. Exits at the 10-bar mid-channel level.",
                    .Emoji = "🐢",
                    .WinRateRange = "30–40%",
                    .SharpeRange = "0.4–0.8",
                    .StrategyType = "DonchianBreakout",
                    .ConditionType = StrategyConditionType.DonchianBreakout
                },
                New QuantLabStrategyCard With {
                    .Name = "BB + RSI Reversion",
                    .Subtitle = "Mean Reversion",
                    .Description = "Requires both Bollinger Band and RSI(14) confirmation of an extreme. " &
                                   "Long when close < lower BB(20,2) AND RSI < 30. Exits at middle BB or RSI 50.",
                    .Emoji = "🔄",
                    .WinRateRange = "55–65%",
                    .SharpeRange = "0.6–1.2",
                    .StrategyType = "BbRsiMeanReversion",
                    .ConditionType = StrategyConditionType.BbRsiMeanReversion
                },
                New QuantLabStrategyCard With {
                    .Name = "Double Bubble Butt",
                    .Subtitle = "Momentum / Zone",
                    .Description = "Two BB sets over SMA(20): inner ±1.0 SD and outer ±2.0 SD define three zones. " &
                                   "Long when close enters Buy Zone (above upper 1-SD). Short in Sell Zone. " &
                                   "Exit when price returns to Neutral Zone. Kathy Lien DBB system.",
                    .Emoji = "🫧",
                    .WinRateRange = "45–60%",
                    .SharpeRange = "0.6–1.1",
                    .StrategyType = "DoubleBubbleButt",
                    .ConditionType = StrategyConditionType.DoubleBubbleButt
                }
            }

            ' Commands
            RunCommand = New RelayCommand(
                Sub() RunBacktestAsync(),
                Function() Not _isRunning AndAlso HasSelectedStrategy AndAlso
                           Not String.IsNullOrWhiteSpace(_contractIdText))

            CancelCommand = New RelayCommand(
                Sub()
                    _cancelSource?.Cancel()
                    StatusText = "Cancelling…"
                End Sub,
                Function() _isRunning)

            SelectCardCommand = New RelayCommand(Of QuantLabStrategyCard)(
                Sub(card) SelectedCard = card)

            ExportCsvCommand = New RelayCommand(
                Sub() ExportCsvAsync(),
                Function() _hasResults AndAlso Trades.Count > 0)

            AskClaudeCommand = New RelayCommand(
                AddressOf ExecuteAskClaude,
                Function() _hasResults AndAlso Not _isClaudeAnalysing)

            AddHandler _backtestService.ProgressUpdated,
                Sub(sender, args)
                    Application.Current?.Dispatcher.Invoke(
                        Sub()
                            ProgressPct = args.PercentComplete
                            StatusText = $"Running… {args.PercentComplete}% complete ({args.TradesExecuted} trades)"
                        End Sub)
                End Sub
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' COMMAND IMPLEMENTATIONS
        ' ══════════════════════════════════════════════════════════════════════

        Private Async Sub RunBacktestAsync()
            If _selectedCard Is Nothing OrElse String.IsNullOrWhiteSpace(_contractIdText) Then Return

            _cancelSource?.Dispose()
            _cancelSource = New CancellationTokenSource()

            IsRunning = True
            ProgressPct = 0
            ClearResults()
            StatusText = $"Downloading bars for {_contractIdText}…"

            Try
                ' ── Step 1: ensure bars are available ─────────────────────────
                Dim timeframe = ParseIntervalToTimeframe(_selectedInterval)
                Await _barCollectionService.EnsureBarsAsync(
                    _contractIdText, _startDate, _endDate, CType(timeframe, BarTimeframe),
                    cancel:=_cancelSource.Token)

                StatusText = $"Running {_selectedCard.Name} backtest…"

                ' ── Step 2: build configuration ───────────────────────────────
                Dim capital = If(Decimal.TryParse(_initialCapital, Nothing), CDec(_initialCapital), 50000D)
                Dim qty = If(Integer.TryParse(_quantity, Nothing), CInt(_quantity), 1)

                Dim cfg As New BacktestConfiguration With {
                    .RunName = $"QuantLab — {_selectedCard.Name} — {DateTime.Now:yyyy-MM-dd HH:mm}",
                    .ContractId = _contractIdText,
                    .Timeframe = timeframe,
                    .StartDate = _startDate,
                    .EndDate = _endDate,
                    .InitialCapital = capital,
                    .Quantity = qty,
                    .MinSignalConfidence = 1.0F,   ' QuantLab strategies don't use confidence scoring
                    .StrategyCondition = _selectedCard.ConditionType,
                    .MinAdxThreshold = 0.0F
                }

                ' ── Step 3: run backtest ──────────────────────────────────────
                Dim result = Await _backtestService.RunBacktestAsync(cfg, _cancelSource.Token)

                ' ── Step 4: display results ───────────────────────────────────
                Application.Current?.Dispatcher.Invoke(
                    Sub() PopulateResults(result))

            Catch ex As OperationCanceledException
                Application.Current?.Dispatcher.Invoke(
                    Sub() StatusText = "Backtest cancelled.")

            Catch ex As Exception
                Application.Current?.Dispatcher.Invoke(
                    Sub() StatusText = $"Error: {ex.Message}")

            Finally
                Application.Current?.Dispatcher.Invoke(
                    Sub()
                        IsRunning = False
                        ProgressPct = 0
                    End Sub)
            End Try
        End Sub

        Private Async Sub ExecuteAskClaude()
            If Not _hasResults OrElse _selectedCard Is Nothing Then Return

            IsClaudeAnalysing = True
            HasClaudeAnalysis = False
            ClaudeAnalysis = "🤖 Asking Claude Haiku…"

            Dim sb As New StringBuilder()
            sb.AppendLine($"QuantLab Backtest Results — {_selectedCard.Name} ({_selectedCard.Subtitle})")
            sb.AppendLine($"Contract: {_contractIdText}  |  Interval: {_selectedInterval}")
            sb.AppendLine($"Date range: {_startDate:yyyy-MM-dd} to {_endDate:yyyy-MM-dd}")
            sb.AppendLine($"Trades: {_totalTrades}  |  Win Rate: {_winRate}  |  Total P&L: {_totalPnL}")
            sb.AppendLine($"Sharpe: {_sharpe}  |  Max Drawdown: {_maxDrawdown}  |  Avg P&L/Trade: {_avgPnL}")
            sb.AppendLine()
            sb.AppendLine($"Strategy description: {_selectedCard.Description}")
            sb.AppendLine("Provide a concise assessment: what these numbers say about this strategy on this instrument,")
            sb.AppendLine("any overfitting warnings, and one specific improvement recommendation.")

            Dim analysis = Await _claudeReviewService.AnalyseBacktestResultsAsync(sb.ToString())
            Application.Current?.Dispatcher.Invoke(
                Sub()
                    ClaudeAnalysis = analysis
                    HasClaudeAnalysis = True
                    IsClaudeAnalysing = False
                End Sub)
        End Sub

        Private Sub PopulateResults(result As BacktestResult)
            TotalTrades = result.TotalTrades.ToString()
            WinRate = result.WinRate.ToString("P1")
            TotalPnL = result.TotalPnL.ToString("C0")
            FinalCapital = result.FinalCapital.ToString("C0")
            MaxDrawdown = result.MaxDrawdown.ToString("C0")
            AvgPnL = result.AveragePnLPerTrade.ToString("C2")
            Sharpe = If(result.SharpeRatio.HasValue, result.SharpeRatio.Value.ToString("F2"), "N/A")

            WinRateColour = If(result.WinRate >= 0.55F, "#00C851",
                               If(result.WinRate >= 0.4F, "#FFB300", "#FF4444"))
            PnlColour = If(result.TotalPnL > 0, "#00C851", "#FF4444")

            Trades.Clear()
            For Each t In result.Trades.Take(500)   ' cap UI list at 500 rows
                Trades.Add(t)
            Next

            HasResults = True
            HasClaudeAnalysis = False
            ClaudeAnalysis = String.Empty
            RelayCommand.RaiseCanExecuteChanged()
            Dim pnlSign = If(result.TotalPnL >= 0, "+", "")
            StatusText = $"Complete — {result.TotalTrades} trades | " &
                         $"Win {result.WinRate:P1} | " &
                         $"P&L {pnlSign}{result.TotalPnL:C0} | " &
                         $"Sharpe {Sharpe}"
        End Sub

        Private Sub ClearResults()
            TotalTrades = "--"
            WinRate = "--"
            TotalPnL = "--"
            FinalCapital = "--"
            MaxDrawdown = "--"
            AvgPnL = "--"
            Sharpe = "--"
            WinRateColour = "#AAAAAA"
            PnlColour = "#AAAAAA"
            Trades.Clear()
            HasResults = False
            HasClaudeAnalysis = False
            ClaudeAnalysis = String.Empty
            RelayCommand.RaiseCanExecuteChanged()
        End Sub

        Private Async Sub ExportCsvAsync()
            Dim dlg As New SaveFileDialog With {
                .Title = "Export Trade List",
                .Filter = "CSV Files (*.csv)|*.csv",
                .FileName = $"QuantLab_{_selectedCard?.StrategyType}_{DateTime.Now:yyyyMMdd_HHmm}.csv",
                .DefaultExt = ".csv"
            }
            If dlg.ShowDialog() <> True Then Return

            Dim filePath = dlg.FileName
            Dim tradeCount = Trades.Count
            Dim content = BuildCsvContent()
            Try
                Await Task.Run(Sub() File.WriteAllText(filePath, content, Encoding.UTF8))
                StatusText = $"Exported {tradeCount} trades → {Path.GetFileName(filePath)}"
            Catch ex As Exception
                MessageBox.Show($"Export failed: {ex.Message}", "Export Error",
                                MessageBoxButton.OK, MessageBoxImage.Warning)
            End Try
        End Sub

        Private Function BuildCsvContent() As String
            Dim sb As New StringBuilder()
            sb.AppendLine("PositionGroupId,Side,EntryTime,ExitTime,EntryPrice,ExitPrice,Quantity,PnL,ExitReason,Confidence")
            For Each t In Trades
                Dim exitTimeStr = If(t.ExitTime.HasValue, t.ExitTime.Value.ToString("yyyy-MM-dd HH:mm:ss"), String.Empty)
                Dim exitPriceStr = If(t.ExitPrice.HasValue, t.ExitPrice.Value.ToString("F4"), String.Empty)
                Dim pnlStr = If(t.PnL.HasValue, t.PnL.Value.ToString("F2"), String.Empty)
                sb.AppendLine($"{t.PositionGroupId},{t.Side}," &
                              $"{t.EntryTime:yyyy-MM-dd HH:mm:ss}," &
                              $"{exitTimeStr}," &
                              $"{t.EntryPrice:F4},{exitPriceStr}," &
                              $"{t.Quantity},{pnlStr},{t.ExitReason}," &
                              $"{t.SignalConfidence:F2}")
            Next
            Return sb.ToString()
        End Function

        ' ══════════════════════════════════════════════════════════════════════
        ' HELPERS
        ' ══════════════════════════════════════════════════════════════════════

        Private Shared Function ParseIntervalToTimeframe(interval As String) As Integer
            Select Case interval
                Case "1 min"  : Return 1
                Case "5 min"  : Return 5
                Case "15 min" : Return 15
                Case "30 min" : Return 30
                Case "1 hour" : Return 60
                Case "4 hour" : Return 240
                Case "1 day"  : Return 1440
                Case Else     : Return 5
            End Select
        End Function

    End Class

End Namespace
