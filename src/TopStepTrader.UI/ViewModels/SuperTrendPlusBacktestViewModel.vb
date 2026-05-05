Imports System.Collections.ObjectModel
Imports System.Threading
Imports System.Windows
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.Personas
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' Tab 2 — SuperTrend+ Backtest.
    ''' 63 combinations: 3 personas × 7 contracts × 3 timeframes (5 min, 15 min, 1 hr).
    ''' Uses live PersonaService settings. Date range: last 90 days.
    ''' Pivot grid: contract+timeframe rows × Lewis/Damian/Joe columns.
    ''' Pin button adds all three persona results for a row to the shared Pinned collection.
    ''' </summary>
    Public Class SuperTrendPlusBacktestViewModel
        Inherits ViewModelBase

        Private ReadOnly _backtestService As IBacktestService
        Private ReadOnly _barCollectionService As IBarCollectionService
        Private ReadOnly _personaService As IPersonaService
        Private ReadOnly _pinnedResults As ObservableCollection(Of MaxEffortRowVm)

        Private _cancelSource As CancellationTokenSource

        ' ══════════════════════════════════════════════════════════════════════
        ' COMMANDS
        ' ══════════════════════════════════════════════════════════════════════

        Public ReadOnly Property RunCommand As RelayCommand
        Public ReadOnly Property CancelCommand As RelayCommand
        Public ReadOnly Property PinRowCommand As RelayCommand

        ' ══════════════════════════════════════════════════════════════════════
        ' COLLECTIONS
        ' ══════════════════════════════════════════════════════════════════════

        Public ReadOnly Property Results As New ObservableCollection(Of StPlusRowVm)()

        ' ══════════════════════════════════════════════════════════════════════
        ' STATE PROPERTIES
        ' ══════════════════════════════════════════════════════════════════════

        Private _isRunning As Boolean
        Public Property IsRunning As Boolean
            Get
                Return _isRunning
            End Get
            Set(value As Boolean)
                SetProperty(_isRunning, value)
                OnPropertyChanged(NameOf(CanRun))
                OnPropertyChanged(NameOf(CanCancel))
            End Set
        End Property

        Public ReadOnly Property CanRun As Boolean
            Get
                Return Not _isRunning
            End Get
        End Property

        Public ReadOnly Property CanCancel As Boolean
            Get
                Return _isRunning
            End Get
        End Property

        Private _progress As Integer
        Public Property Progress As Integer
            Get
                Return _progress
            End Get
            Set(value As Integer)
                SetProperty(_progress, value)
            End Set
        End Property

        Private _progressText As String = "Click Run to test all 3 personas × 7 contracts × 3 timeframes (63 combinations)."
        Public Property ProgressText As String
            Get
                Return _progressText
            End Get
            Set(value As String)
                SetProperty(_progressText, value)
            End Set
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' CONSTRUCTOR
        ' ══════════════════════════════════════════════════════════════════════

        Public Sub New(backtestService As IBacktestService,
                       barCollectionService As IBarCollectionService,
                       personaService As IPersonaService,
                       pinnedResults As ObservableCollection(Of MaxEffortRowVm))

            _backtestService = backtestService
            _barCollectionService = barCollectionService
            _personaService = personaService
            _pinnedResults = pinnedResults

            RunCommand = New RelayCommand(AddressOf ExecuteRun, Function() Not _isRunning)
            CancelCommand = New RelayCommand(Sub() _cancelSource?.Cancel(), Function() _isRunning)
            PinRowCommand = New RelayCommand(
                Sub(param)
                    Dim row = TryCast(param, StPlusRowVm)
                    If row Is Nothing Then Return
                    For Each cell In {row.Lewis, row.Damian, row.Joe}
                        If cell.HasResult Then
                            Dim meRow = cell.AsMaxEffortRow
                            If Not _pinnedResults.Contains(meRow) Then
                                _pinnedResults.Add(meRow)
                            End If
                        End If
                    Next
                End Sub,
                Function(param) param IsNot Nothing)
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' RUN EXECUTION
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub ExecuteRun()
            Results.Clear()
            IsRunning = True
            Progress = 0
            ProgressText = "Preparing SuperTrend+ backtest run..."

            Dim contracts = FavouriteContracts.GetDefaults()
            Dim timeframes = New (BarTimeframe, String)() {
                (BarTimeframe.FiveMinute,    "5 min"),
                (BarTimeframe.FifteenMinute, "15 min"),
                (BarTimeframe.OneHour,       "1 hr")
            }
            Dim personas = _personaService.GetAllProfiles()

            ' Pre-build all 21 rows so the grid is visible immediately
            Dim allRows As New List(Of StPlusRowVm)()
            For Each fc In contracts
                For Each tf In timeframes
                    Dim row As New StPlusRowVm(fc, tf.Item2, tf.Item1)
                    allRows.Add(row)
                    Results.Add(row)
                Next
            Next

            Dim startDate = DateTime.Today.AddDays(-90)
            Dim endDate = DateTime.Today
            Dim totalRuns = allRows.Count * personas.Count   ' 21 × 3 = 63
            Dim runIndex = 0

            _cancelSource = New CancellationTokenSource()
            Dim cts = _cancelSource

            Task.Run(Async Function()
                Try
                    For Each row In allRows
                        If cts.IsCancellationRequested Then Exit For

                        Dim fc = row.FavouriteContract
                        Dim tfEnum = row.TimeframeEnum

                        ' Cap lookback to what BarCollectionService can provide for this timeframe
                        Dim maxDays = BacktestRunViewModel.GetMaxLookbackDays(tfEnum)
                        Dim effectiveStart = If(startDate < DateTime.Today.AddDays(-maxDays),
                                               DateTime.Today.AddDays(-maxDays),
                                               startDate)

                        ' Fetch bars once per row — shared across all three personas
                        Dim barResult = Await _barCollectionService.EnsureBarsAsync(
                            fc.PxContractId, effectiveStart, endDate, tfEnum, cancel:=cts.Token)

                        If Not barResult.Success Then
                            runIndex += personas.Count
                            Continue For
                        End If

                        Dim tickSize = If(fc.PxTickSize > 0D, fc.PxTickSize, 0.01D)
                        Dim pointValue = If(fc.PxPointValue > 0D, fc.PxPointValue, 1.0D)

                        For Each persona In personas
                            If cts.IsCancellationRequested Then Exit For
                            runIndex += 1

                            Dim pShort = persona.Name.Split(" "c)(0)
                            Dim ri = runIndex

                            Dispatch(Sub()
                                ProgressText = $"[{ri}/{totalRuns}]  {pShort}  ·  {fc.Name}  ·  {row.TimeframeLabel}"
                                Progress = CInt((ri * 100.0) / totalRuns)
                            End Sub)

                            Try
                                Dim config As New BacktestConfiguration With {
                                    .RunName = $"ST+ · {pShort} · {fc.Name} · {row.TimeframeLabel}",
                                    .ContractId = fc.PxContractId,
                                    .Timeframe = CInt(tfEnum),
                                    .StartDate = effectiveStart,
                                    .EndDate = endDate,
                                    .InitialCapital = 0D,
                                    .Quantity = persona.PositionSize,
                                    .TickSize = tickSize,
                                    .PointValue = pointValue,
                                    .MinSignalConfidence = CSng(persona.DefaultConfidencePct) / 100.0F,
                                    .MinAdxThreshold = persona.AdxThreshold,
                                    .MaxScaleIns = persona.MaxScaleIns,
                                    .StrategyCondition = StrategyConditionType.SuperTrendPlus,
                                    .UseAtrMode = True,
                                    .SlAtrMultiple = persona.SlMultipleOfN,
                                    .TpAtrMultiple = persona.TpMultipleOfN,
                                    .TpMultiple = persona.TpMultipleOfN,
                                    .SlippageTicks = 1,
                                    .CommissionPerSideUsd = fc.RoundTripFee / 2D
                                }

                                Dim result = Await _backtestService.RunBacktestAsync(config, cts.Token)
                                Dim meRow As New MaxEffortRowVm(
                                    pShort, fc.Name, "SuperTrend+", row.TimeframeLabel, result,
                                    fc.PxContractId, StrategyConditionType.SuperTrendPlus, tfEnum,
                                    persona.SlMultipleOfN, persona.TpMultipleOfN)

                                Dispatch(Sub() row.GetCell(pShort).SetResult(meRow))

                            Catch ex As OperationCanceledException
                                Exit For
                            Catch
                            End Try
                        Next
                    Next

                Catch ex As OperationCanceledException
                Finally
                    Dim captured = runIndex
                    Dim total = totalRuns
                    Dim cancelled = cts.IsCancellationRequested
                    Dispatch(Sub()
                        IsRunning = False
                        Progress = If(captured >= total, 100, Progress)
                        ProgressText = If(captured >= total,
                            $"Complete — {total} combinations ran.",
                            $"{If(cancelled, "Cancelled", "Stopped")} — {captured} of {total} combinations ran.")
                        cts?.Dispose()
                    End Sub)
                End Try
            End Function)
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
    ' STPLUS ROW — one contract × timeframe pair with three persona cells
    ' ══════════════════════════════════════════════════════════════════════════

    Public Class StPlusRowVm
        Inherits ViewModelBase

        Public ReadOnly Property FavouriteContract As FavouriteContract
        Public ReadOnly Property ContractName As String
        Public ReadOnly Property TimeframeLabel As String
        Public ReadOnly Property TimeframeEnum As BarTimeframe

        Public ReadOnly Property Lewis As StPlusPersonaCellVm
        Public ReadOnly Property Damian As StPlusPersonaCellVm
        Public ReadOnly Property Joe As StPlusPersonaCellVm

        Public Sub New(fc As FavouriteContract, tfLabel As String, tfEnum As BarTimeframe)
            FavouriteContract = fc
            ContractName = fc.Name
            TimeframeLabel = tfLabel
            TimeframeEnum = tfEnum
            Lewis = New StPlusPersonaCellVm("Lewis")
            Damian = New StPlusPersonaCellVm("Damian")
            Joe = New StPlusPersonaCellVm("Joe")
        End Sub

        Public Function GetCell(personaShortName As String) As StPlusPersonaCellVm
            Select Case personaShortName
                Case "Lewis"  : Return Lewis
                Case "Damian" : Return Damian
                Case "Joe"    : Return Joe
                Case Else     : Return Lewis
            End Select
        End Function

    End Class

    ' ══════════════════════════════════════════════════════════════════════════
    ' STPLUS PERSONA CELL — mutable result cell for one persona in a pivot row
    ' ══════════════════════════════════════════════════════════════════════════

    Public Class StPlusPersonaCellVm
        Inherits ViewModelBase

        Public ReadOnly Property PersonaName As String

        Private _meRow As MaxEffortRowVm

        Public Sub New(name As String)
            PersonaName = name
        End Sub

        Public ReadOnly Property HasResult As Boolean
            Get
                Return _meRow IsNot Nothing
            End Get
        End Property

        Public ReadOnly Property AsMaxEffortRow As MaxEffortRowVm
            Get
                Return _meRow
            End Get
        End Property

        Public Sub SetResult(row As MaxEffortRowVm)
            _meRow = row
            OnPropertyChanged(NameOf(HasResult))
            OnPropertyChanged(NameOf(TotalPnL))
            OnPropertyChanged(NameOf(TotalPnLColor))
            OnPropertyChanged(NameOf(WinTrades))
            OnPropertyChanged(NameOf(CalmarSharpe))
        End Sub

        Public ReadOnly Property TotalPnL As String
            Get
                Return If(_meRow IsNot Nothing, _meRow.TotalPnL, "—")
            End Get
        End Property

        Public ReadOnly Property TotalPnLColor As String
            Get
                Return If(_meRow IsNot Nothing, _meRow.TotalPnLColor, "TextSecondaryBrush")
            End Get
        End Property

        Public ReadOnly Property WinTrades As String
            Get
                If _meRow Is Nothing Then Return ""
                Return $"{_meRow.WinRate} · {_meRow.Trades} trades"
            End Get
        End Property

        Public ReadOnly Property CalmarSharpe As String
            Get
                If _meRow Is Nothing Then Return ""
                Return $"Calmar {_meRow.Calmar} · Sharpe {_meRow.Sharpe}"
            End Get
        End Property

    End Class

End Namespace
