Imports System.Collections.ObjectModel
Imports System.Windows
Imports System.Windows.Media
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.Trading
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' ViewModel for the Pump-n-Dump view.
    ''' Wires PumpNDumpExecutionEngine: 3-bar price-action entry, P&amp;L free-ride, dynamic TP tightening.
    ''' </summary>
    Public Class PumpNDumpViewModel
        Inherits ViewModelBase
        Implements IDisposable

        ' ── Dependencies ───────────────────────────────────────────────────────
        Private ReadOnly _accountService As IAccountService
        Private ReadOnly _engine As IPumpNDumpExecutionEngine
        Private ReadOnly _session As ITradingSessionContext

        ' ── Internal state ─────────────────────────────────────────────────────
        Private _disposed As Boolean = False
        Private _winCount As Integer = 0
        Private _lossCount As Integer = 0
        Private _totalPnl As Decimal = 0D

        ' ══════════════════════════════════════════════════════════════════════
        ' ACCOUNTS
        ' ══════════════════════════════════════════════════════════════════════

        Public Property Accounts As New ObservableCollection(Of Account)

        Private _selectedAccount As Account
        Public Property SelectedAccount As Account
            Get
                Return _selectedAccount
            End Get
            Set(value As Account)
                SetProperty(_selectedAccount, value)
                NotifyPropertyChanged(NameOf(CanStart))
                RelayCommand.RaiseCanExecuteChanged()
            End Set
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' PARAMETERS
        ' ══════════════════════════════════════════════════════════════════════

        Private _contractId As String = ""
        Public Property ContractId As String
            Get
                Return _contractId
            End Get
            Set(value As String)
                SetProperty(_contractId, value)
                NotifyPropertyChanged(NameOf(CanStart))
                RelayCommand.RaiseCanExecuteChanged()
            End Set
        End Property

        Private _takeProfitTicks As String = "40"
        Public Property TakeProfitTicks As String
            Get
                Return _takeProfitTicks
            End Get
            Set(value As String)
                SetProperty(_takeProfitTicks, value)
            End Set
        End Property

        Private _stopLossTicks As String = "15"
        Public Property StopLossTicks As String
            Get
                Return _stopLossTicks
            End Get
            Set(value As String)
                SetProperty(_stopLossTicks, value)
            End Set
        End Property

        Private _freeRidePnlThreshold As String = "50"
        Public Property FreeRidePnlThreshold As String
            Get
                Return _freeRidePnlThreshold
            End Get
            Set(value As String)
                SetProperty(_freeRidePnlThreshold, value)
            End Set
        End Property

        Private _scaleInTicks As String = "8"
        Public Property ScaleInTicks As String
            Get
                Return _scaleInTicks
            End Get
            Set(value As String)
                SetProperty(_scaleInTicks, value)
            End Set
        End Property

        Private _maxRiskHeatTicks As String = "60"
        Public Property MaxRiskHeatTicks As String
            Get
                Return _maxRiskHeatTicks
            End Get
            Set(value As String)
                SetProperty(_maxRiskHeatTicks, value)
            End Set
        End Property

        Private _targetTotalSize As String = "5"
        Public Property TargetTotalSize As String
            Get
                Return _targetTotalSize
            End Get
            Set(value As String)
                SetProperty(_targetTotalSize, value)
            End Set
        End Property

        Private _momentumFadeAtrFraction As String = "0.5"
        Public Property MomentumFadeAtrFraction As String
            Get
                Return _momentumFadeAtrFraction
            End Get
            Set(value As String)
                SetProperty(_momentumFadeAtrFraction, value)
            End Set
        End Property

        Private _tightenTicksPerBar As String = "2"
        Public Property TightenTicksPerBar As String
            Get
                Return _tightenTicksPerBar
            End Get
            Set(value As String)
                SetProperty(_tightenTicksPerBar, value)
            End Set
        End Property

        Private _durationHours As String = "2"
        Public Property DurationHours As String
            Get
                Return _durationHours
            End Get
            Set(value As String)
                SetProperty(_durationHours, value)
            End Set
        End Property

        Private _tradingStartHourUtc As String = "6"
        Public Property TradingStartHourUtc As String
            Get
                Return _tradingStartHourUtc
            End Get
            Set(value As String)
                SetProperty(_tradingStartHourUtc, value)
            End Set
        End Property

        Private _tradingEndHourUtc As String = "21"
        Public Property TradingEndHourUtc As String
            Get
                Return _tradingEndHourUtc
            End Get
            Set(value As String)
                SetProperty(_tradingEndHourUtc, value)
            End Set
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' ENGINE STATE
        ' ══════════════════════════════════════════════════════════════════════

        Private _isRunning As Boolean = False
        Public Property IsRunning As Boolean
            Get
                Return _isRunning
            End Get
            Set(value As Boolean)
                SetProperty(_isRunning, value)
                NotifyPropertyChanged(NameOf(IsNotRunning))
                NotifyPropertyChanged(NameOf(CanStart))
                RelayCommand.RaiseCanExecuteChanged()
            End Set
        End Property

        Public ReadOnly Property IsNotRunning As Boolean
            Get
                Return Not _isRunning
            End Get
        End Property

        Public ReadOnly Property CanStart As Boolean
            Get
                Return Not _isRunning AndAlso
                       Not String.IsNullOrEmpty(_contractId) AndAlso
                       _selectedAccount IsNot Nothing AndAlso
                       _selectedAccount.Broker = BrokerType.TopStepX
            End Get
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' POSITION DISPLAY
        ' ══════════════════════════════════════════════════════════════════════

        Private _positionDisplay As String = "Contracts: 0 / 5"
        Public Property PositionDisplay As String
            Get
                Return _positionDisplay
            End Get
            Set(value As String)
                SetProperty(_positionDisplay, value)
            End Set
        End Property

        Private _averageEntryDisplay As String = "—"
        Public Property AverageEntryDisplay As String
            Get
                Return _averageEntryDisplay
            End Get
            Set(value As String)
                SetProperty(_averageEntryDisplay, value)
            End Set
        End Property

        Private _unrealisedPnlDisplay As String = "—"
        Public Property UnrealisedPnlDisplay As String
            Get
                Return _unrealisedPnlDisplay
            End Get
            Set(value As String)
                SetProperty(_unrealisedPnlDisplay, value)
                NotifyPropertyChanged(NameOf(UnrealisedPnlBrush))
            End Set
        End Property

        Private _unrealisedPnlValue As Decimal = 0D

        Public ReadOnly Property UnrealisedPnlBrush As Brush
            Get
                Return If(_unrealisedPnlValue >= 0,
                          DirectCast(New SolidColorBrush(Color.FromRgb(&H4A, &HBA, &H61)), Brush),
                          New SolidColorBrush(Color.FromRgb(&HE5, &H53, &H3A)))
            End Get
        End Property

        Private _freeRideDisplay As String = ""
        Public Property FreeRideDisplay As String
            Get
                Return _freeRideDisplay
            End Get
            Set(value As String)
                SetProperty(_freeRideDisplay, value)
            End Set
        End Property

        Private _heatDisplay As String = ""
        Public Property HeatDisplay As String
            Get
                Return _heatDisplay
            End Get
            Set(value As String)
                SetProperty(_heatDisplay, value)
            End Set
        End Property

        Private _barCountDisplay As String = "Watching…"
        Public Property BarCountDisplay As String
            Get
                Return _barCountDisplay
            End Get
            Set(value As String)
                SetProperty(_barCountDisplay, value)
                NotifyPropertyChanged(NameOf(BarCountBrush))
            End Set
        End Property

        Private _barCountIsGreen As Boolean = True
        Public ReadOnly Property BarCountBrush As Brush
            Get
                If _barCountDisplay = "Watching…" Then
                    Return DirectCast(New SolidColorBrush(Color.FromRgb(&H95, &H99, &H9C)), Brush)
                End If
                Return If(_barCountIsGreen,
                          DirectCast(New SolidColorBrush(Color.FromRgb(&H4A, &HBA, &H61)), Brush),
                          New SolidColorBrush(Color.FromRgb(&HE5, &H53, &H3A)))
            End Get
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' PERFORMANCE
        ' ══════════════════════════════════════════════════════════════════════

        Public ReadOnly Property WinLossDisplay As String
            Get
                Return $"Wins: {_winCount}   Losses: {_lossCount}"
            End Get
        End Property

        Public ReadOnly Property TotalPnlDisplay As String
            Get
                Dim sign = If(_totalPnl >= 0, "+", "")
                Return $"{sign}${_totalPnl:F2}"
            End Get
        End Property

        Public ReadOnly Property TotalPnlBrush As Brush
            Get
                Return If(_totalPnl >= 0,
                          DirectCast(New SolidColorBrush(Color.FromRgb(&H4A, &HBA, &H61)), Brush),
                          New SolidColorBrush(Color.FromRgb(&HE5, &H53, &H3A)))
            End Get
        End Property

        ' ══════════════════════════════════════════════════════════════════════
        ' LOG
        ' ══════════════════════════════════════════════════════════════════════

        Public Property LogEntries As New ObservableCollection(Of String)

        ' ══════════════════════════════════════════════════════════════════════
        ' COMMANDS
        ' ══════════════════════════════════════════════════════════════════════

        Public ReadOnly Property StartCommand As RelayCommand
        Public ReadOnly Property StopCommand As RelayCommand

        ' ══════════════════════════════════════════════════════════════════════
        ' CONSTRUCTOR
        ' ══════════════════════════════════════════════════════════════════════

        Public Sub New(accountService As IAccountService,
                       engine As IPumpNDumpExecutionEngine,
                       session As ITradingSessionContext)
            _accountService = accountService
            _engine = engine
            _session = session

            StartCommand = New RelayCommand(AddressOf ExecuteStart, Function() CanStart)
            StopCommand = New RelayCommand(AddressOf ExecuteStop, Function() _isRunning)

            AddHandler _engine.LogMessage, AddressOf OnEngineLog
            AddHandler _engine.ExecutionStopped, AddressOf OnEngineStopped
            AddHandler _engine.TradeOpened, AddressOf OnTradeOpened
            AddHandler _engine.TradeClosed, AddressOf OnTradeClosed
            AddHandler _engine.PositionChanged, AddressOf OnPositionChanged
            AddHandler _engine.BarCountChanged, AddressOf OnBarCountChanged

            ' Seed lines inserted in reverse order so they read top-to-bottom after construction
            LogEntries.Insert(0, "Setup: Select Account & Contract, then click Start.")
            LogEntries.Insert(0, "—")
            LogEntries.Insert(0, "Tighten: When bar ranges shrink vs ATR (momentum fading), TP is moved closer each poll.")
            LogEntries.Insert(0, "Free-Ride: When unrealised P&L ≥ threshold, all SLs move to avg entry (zero risk).")
            LogEntries.Insert(0, "Scale-In: Adds 1 contract each time price moves ScaleIn ticks further.")
            LogEntries.Insert(0, "       3× Green → Long (Pump).  3× Red → Short (Dump).")
            LogEntries.Insert(0, "Entry: 3 consecutive 3-min bars all closing in the same direction.")
            LogEntries.Insert(0, "📌 STRATEGY: PUMP-N-DUMP (3-Bar Price Action)")
            AddHandler _session.AutoExecutionChanged, AddressOf OnAutoExecutionChanged
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' DATA LOADING
        ' ══════════════════════════════════════════════════════════════════════

        Public Async Sub LoadDataAsync()
            Try
                Dim accountList = Await _accountService.GetActiveAccountsAsync()
                Dispatch(Sub()
                             Accounts.Clear()
                             For Each a In accountList
                                 Accounts.Add(a)
                             Next
                             If Accounts.Count > 0 Then
                                 Dim sessionAcc = _session.SelectedAccount
                                 Dim preferred = If(
                                     If(sessionAcc IsNot Nothing,
                                        Accounts.FirstOrDefault(Function(a) a.Id = sessionAcc.Id),
                                        Nothing),
                                     Accounts.FirstOrDefault(
                                         Function(a) a.Name IsNot Nothing AndAlso
                                                     a.Name.StartsWith("PRAC", StringComparison.OrdinalIgnoreCase)))
                                 SelectedAccount = If(preferred, Accounts(0))
                             End If
                         End Sub)
            Catch ex As Exception
                Dispatch(Sub() AddLog($"⚠ Account load error: {ex.Message}"))
            End Try
        End Sub

        Private Sub OnAutoExecutionChanged(sender As Object, e As EventArgs)
            Task.Run(AddressOf RefreshAccountsAsync)
        End Sub

        Private Async Function RefreshAccountsAsync() As Task
            Try
                Dim accountList = Await _accountService.GetActiveAccountsAsync()
                Dispatch(Sub()
                             Accounts.Clear()
                             For Each a In accountList
                                 Accounts.Add(a)
                             Next
                             If Accounts.Count > 0 Then
                                 Dim preferred = Accounts.FirstOrDefault(
                                     Function(a) a.Name IsNot Nothing AndAlso
                                                 a.Name.StartsWith("PRAC", StringComparison.OrdinalIgnoreCase))
                                 SelectedAccount = If(preferred, Accounts(0))
                             End If
                         End Sub)
            Catch
            End Try
        End Function

        ' ══════════════════════════════════════════════════════════════════════
        ' START / STOP
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub ExecuteStart()
            If String.IsNullOrEmpty(_contractId) OrElse _selectedAccount Is Nothing Then Return

            Dim tp, sl, scaleIn, heat, maxSize, tighten As Integer
            Dim freeRideThreshold As Decimal = 50D
            Dim fadeFraction As Double = 0.5
            Dim tradingStart As Integer = 6
            Dim tradingEnd As Integer = 21
            Dim dur As Double = 2.0

            Dim parsed As Boolean
            parsed = Integer.TryParse(_takeProfitTicks, tp)
            parsed = Integer.TryParse(_stopLossTicks, sl)
            parsed = Decimal.TryParse(_freeRidePnlThreshold, freeRideThreshold)
            parsed = Integer.TryParse(_scaleInTicks, scaleIn)
            parsed = Integer.TryParse(_maxRiskHeatTicks, heat)
            parsed = Integer.TryParse(_targetTotalSize, maxSize)
            parsed = Double.TryParse(_momentumFadeAtrFraction, fadeFraction)
            parsed = Integer.TryParse(_tightenTicksPerBar, tighten)
            parsed = Double.TryParse(_durationHours, dur)
            Integer.TryParse(_tradingStartHourUtc, tradingStart)
            Integer.TryParse(_tradingEndHourUtc, tradingEnd)

            If tp <= 0 Then tp = 40
            If sl <= 0 Then sl = 15
            If freeRideThreshold <= 0 Then freeRideThreshold = 50D
            If scaleIn <= 0 Then scaleIn = 8
            If heat <= 0 Then heat = 60
            If maxSize <= 0 Then maxSize = 5
            If fadeFraction <= 0 Then fadeFraction = 0.5
            If tighten <= 0 Then tighten = 2
            If dur <= 0 Then dur = 2.0

            ' Reset counters
            _winCount = 0
            _lossCount = 0
            _totalPnl = 0D
            _unrealisedPnlValue = 0D
            NotifyPropertyChanged(NameOf(WinLossDisplay))
            NotifyPropertyChanged(NameOf(TotalPnlDisplay))
            NotifyPropertyChanged(NameOf(TotalPnlBrush))

            PositionDisplay = $"Contracts: 0 / {maxSize}"
            AverageEntryDisplay = "—"
            UnrealisedPnlDisplay = "—"
            HeatDisplay = ""
            FreeRideDisplay = ""
            BarCountDisplay = "Watching…"
            LogEntries.Clear()

            LogEntries.Insert(0, $"   ScaleIn={scaleIn}t | MaxHeat={heat}t | Fade<{fadeFraction:F2}×ATR tighten={tighten}t/poll")
            LogEntries.Insert(0, $"⚡ Starting Pump-n-Dump: MaxSize={maxSize} | TP={tp}t SL={sl}t | FreeRide>=${freeRideThreshold:F0}")

            IsRunning = True

            _engine.Start(_contractId, _selectedAccount.Id,
                          tp, sl, freeRideThreshold, scaleIn, heat, maxSize,
                          fadeFraction, tighten, dur,
                          GetTickSize(_contractId),
                          GetTickValue(_contractId),
                          BrokerType.TopStepX,
                          tradingStart, tradingEnd)
        End Sub

        Private Async Sub ExecuteStop()
            Await _engine.StopAsync("Stopped by user")
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' ENGINE EVENT HANDLERS
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub OnEngineLog(sender As Object, e As String)
            Dispatch(Sub() AddLog(e))
        End Sub

        Private Sub OnEngineStopped(sender As Object, e As String)
            Dispatch(Sub()
                         IsRunning = False
                         AddLog($"⏹ Pump-n-Dump stopped: {e}")
                     End Sub)
        End Sub

        Private Sub OnTradeOpened(sender As Object, e As TradeOpenedEventArgs)
            Dispatch(Sub()
                         AddLog($"🔵 Trade opened — {e.Side} on {e.ContractId}")
                     End Sub)
        End Sub

        Private Sub OnTradeClosed(sender As Object, e As TradeClosedEventArgs)
            Dispatch(Sub()
                         If e.PnL > 0 Then
                             _winCount += 1
                         ElseIf e.PnL < 0 Then
                             _lossCount += 1
                         End If
                         _totalPnl += e.PnL
                         _unrealisedPnlValue = 0D

                         Dim sign = If(e.PnL >= 0, "+", "")
                         AddLog($"🔴 Trade closed — {e.ExitReason}  P&L: {sign}${e.PnL:F2}")

                         NotifyPropertyChanged(NameOf(WinLossDisplay))
                         NotifyPropertyChanged(NameOf(TotalPnlDisplay))
                         NotifyPropertyChanged(NameOf(TotalPnlBrush))
                     End Sub)
        End Sub

        Private Sub OnPositionChanged(sender As Object, e As SniperPositionEventArgs)
            Dispatch(Sub()
                         PositionDisplay = $"Contracts: {e.CurrentQty} / {TargetTotalSize}"
                         AverageEntryDisplay = If(e.CurrentQty > 0, e.AverageEntry.ToString("F2"), "—")
                         HeatDisplay = If(e.CurrentQty > 0, $"Heat: {e.CurrentHeat:F0}/{MaxRiskHeatTicks}", "")
                         FreeRideDisplay = If(e.FreeRideActive, "🔒 Free ride active", "")

                         ' Update unrealised P&L from engine
                         _unrealisedPnlValue = _engine.UnrealisedPnl
                         If e.CurrentQty > 0 Then
                             Dim sign = If(_unrealisedPnlValue >= 0, "+", "")
                             UnrealisedPnlDisplay = $"{sign}${_unrealisedPnlValue:F2}"
                         Else
                             UnrealisedPnlDisplay = "—"
                         End If

                         ' Reset bar count when position opens
                         If e.CurrentQty > 0 Then
                             BarCountDisplay = "Watching…"
                         End If
                     End Sub)
        End Sub

        Private Sub OnBarCountChanged(sender As Object, e As BarCountEventArgs)
            Dispatch(Sub()
                         _barCountIsGreen = e.IsGreen
                         Dim direction = If(e.IsGreen, "GREEN", "RED")
                         
                         Select Case e.Count
                             Case 0
                                 BarCountDisplay = "Watching…"
                             Case 1
                                 BarCountDisplay = $"ONE {direction}"
                             Case 2
                                 BarCountDisplay = $"TWO {direction}"
                             Case 3
                                 Dim signal = If(e.IsGreen, "LONG", "SHORT")
                                 BarCountDisplay = $"⚡ THREE {direction} → {signal}"
                             Case Else
                                 BarCountDisplay = "Watching…"
                         End Select
                     End Sub)
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        ' HELPERS
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub AddLog(message As String)
            LogEntries.Insert(0, message)
        End Sub

        Private Shared Sub Dispatch(action As Action)
            If Application.Current?.Dispatcher IsNot Nothing Then
                Application.Current.Dispatcher.Invoke(action)
            End If
        End Sub

        Private Function GetTickSize(contractId As String) As Decimal
            Dim fav = FavouriteContracts.TryGetBySymbolResolved(contractId)
            If fav Is Nothing Then Return 0.01D
            Return If(fav.PxTickSize > 0, fav.PxTickSize, 0.01D)
        End Function

        Private Function GetTickValue(contractId As String) As Decimal
            Dim fav = FavouriteContracts.TryGetBySymbolResolved(contractId)
            If fav Is Nothing Then Return 0.01D
            Return If(fav.PxTickValue > 0, fav.PxTickValue, 0.01D)
        End Function

        ' ══════════════════════════════════════════════════════════════════════
        ' DISPOSE
        ' ══════════════════════════════════════════════════════════════════════

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                RemoveHandler _engine.LogMessage, AddressOf OnEngineLog
                RemoveHandler _engine.ExecutionStopped, AddressOf OnEngineStopped
                RemoveHandler _engine.TradeOpened, AddressOf OnTradeOpened
                RemoveHandler _engine.TradeClosed, AddressOf OnTradeClosed
                RemoveHandler _engine.PositionChanged, AddressOf OnPositionChanged
                _engine?.Dispose()
                _disposed = True
                GC.SuppressFinalize(Me)
            End If
        End Sub

    End Class

End Namespace
