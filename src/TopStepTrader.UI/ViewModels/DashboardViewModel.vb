Imports System.Collections.ObjectModel
Imports System.Windows
Imports System.Windows.Threading
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' Dashboard — summary of account health, daily P&amp;L, drawdown, and last signal.
    ''' Subscribes to IRiskGuardService events for real-time halt status updates.
    ''' </summary>
    Public Class DashboardViewModel
        Inherits ViewModelBase

        Private ReadOnly _accountService  As IAccountService
        Private ReadOnly _riskGuard       As IRiskGuardService
        Private ReadOnly _signalService   As ISignalService
        Private ReadOnly _riskSettings    As RiskSettings

        ' ── Bindable properties ──────────────────────────────────────────────

        Private _accountName As String = "—"
        Public Property AccountName As String
            Get
                Return _accountName
            End Get
            Set(value As String)
                SetProperty(_accountName, value)
            End Set
        End Property

        Private _balance As Decimal
        Public Property Balance As Decimal
            Get
                Return _balance
            End Get
            Set(value As Decimal)
                SetProperty(_balance, value)
            End Set
        End Property

        Private _dailyPnL As Decimal
        Public Property DailyPnL As Decimal
            Get
                Return _dailyPnL
            End Get
            Set(value As Decimal)
                SetProperty(_dailyPnL, value)
                OnPropertyChanged(NameOf(PnLColor))
            End Set
        End Property

        Private _drawdown As Decimal
        Public Property Drawdown As Decimal
            Get
                Return _drawdown
            End Get
            Set(value As Decimal)
                SetProperty(_drawdown, value)
                OnPropertyChanged(NameOf(DrawdownColor))
            End Set
        End Property

        Private _isHalted As Boolean
        Public Property IsHalted As Boolean
            Get
                Return _isHalted
            End Get
            Set(value As Boolean)
                SetProperty(_isHalted, value)
                OnPropertyChanged(NameOf(RiskStatusText))
                OnPropertyChanged(NameOf(RiskStatusColor))
            End Set
        End Property

        Private _haltReason As RiskHaltReason = RiskHaltReason.None
        Public Property HaltReason As RiskHaltReason
            Get
                Return _haltReason
            End Get
            Set(value As RiskHaltReason)
                SetProperty(_haltReason, value)
            End Set
        End Property

        Private _lastSignalType As SignalType = SignalType.Hold
        Public Property LastSignalType As SignalType
            Get
                Return _lastSignalType
            End Get
            Set(value As SignalType)
                SetProperty(_lastSignalType, value)
                OnPropertyChanged(NameOf(LastSignalText))
                OnPropertyChanged(NameOf(LastSignalColor))
            End Set
        End Property

        Private _lastSignalConfidence As Single
        Public Property LastSignalConfidence As Single
            Get
                Return _lastSignalConfidence
            End Get
            Set(value As Single)
                SetProperty(_lastSignalConfidence, value)
            End Set
        End Property

        Private _lastSignalTime As String = "—"
        Public Property LastSignalTime As String
            Get
                Return _lastSignalTime
            End Get
            Set(value As String)
                SetProperty(_lastSignalTime, value)
            End Set
        End Property

        Private _statusMessage As String = "Loading..."
        Public Property StatusMessage As String
            Get
                Return _statusMessage
            End Get
            Set(value As String)
                SetProperty(_statusMessage, value)
            End Set
        End Property

        ' ── Derived display properties ───────────────────────────────────────

        Public ReadOnly Property PnLColor As String
            Get
                Return If(_dailyPnL >= 0, "BuyBrush", "SellBrush")
            End Get
        End Property

        Public ReadOnly Property DrawdownColor As String
            Get
                Return If(_drawdown <= _riskSettings.MaxDrawdownDollars * 0.5D, "SellBrush", "WarningBrush")
            End Get
        End Property

        Public ReadOnly Property RiskStatusText As String
            Get
                Return If(_isHalted, "⛔ TRADING HALTED", "✅ Active")
            End Get
        End Property

        Public ReadOnly Property RiskStatusColor As String
            Get
                Return If(_isHalted, "HaltedBrush", "BuyBrush")
            End Get
        End Property

        Public ReadOnly Property LastSignalText As String
            Get
                Select Case _lastSignalType
                    Case SignalType.Buy  : Return "▲ BUY"
                    Case SignalType.Sell : Return "▼ SELL"
                    Case Else            : Return "— HOLD"
                End Select
            End Get
        End Property

        Public ReadOnly Property LastSignalColor As String
            Get
                Select Case _lastSignalType
                    Case SignalType.Buy  : Return "BuyBrush"
                    Case SignalType.Sell : Return "SellBrush"
                    Case Else            : Return "TextSecondaryBrush"
                End Select
            End Get
        End Property

        Public ReadOnly Property DailyLossLimit As Decimal
            Get
                Return _riskSettings.DailyLossLimitDollars
            End Get
        End Property

        Public ReadOnly Property MaxDrawdownLimit As Decimal
            Get
                Return _riskSettings.MaxDrawdownDollars
            End Get
        End Property

        ' ── Commands ─────────────────────────────────────────────────────────

        Public ReadOnly Property RefreshCommand As RelayCommand

        ' ── Constructor ──────────────────────────────────────────────────────

        Public Sub New(accountService As IAccountService,
                       riskGuard As IRiskGuardService,
                       signalService As ISignalService,
                       riskOptions As IOptions(Of RiskSettings))
            _accountService = accountService
            _riskGuard = riskGuard
            _signalService = signalService
            _riskSettings = riskOptions.Value

            RefreshCommand = New RelayCommand(AddressOf LoadData)

            AddHandler _riskGuard.TradingHalted,  AddressOf OnTradingHalted
            AddHandler _riskGuard.TradingResumed, AddressOf OnTradingResumed
            AddHandler _signalService.SignalGenerated, AddressOf OnSignalGenerated
        End Sub

        ' ── Data loading ─────────────────────────────────────────────────────

        Public Sub LoadDataAsync()
            LoadData()
        End Sub

        Private Sub LoadData()
            Task.Run(AddressOf LoadDataInternal)
        End Sub

        Private Async Function LoadDataInternal() As Task
            Try
                ' Load accounts
                Dim accounts = Await _accountService.GetActiveAccountsAsync()
                Dim first = accounts.FirstOrDefault()
                If first IsNot Nothing Then
                    Dispatch(Sub()
                                 AccountName = first.Name
                                 Balance = first.Balance
                             End Sub)
                End If

                ' Load risk metrics
                Dim pnl = Await _riskGuard.GetDailyPnLAsync()
                Dim dd = Await _riskGuard.GetCurrentDrawdownAsync()
                Dispatch(Sub()
                             DailyPnL = pnl
                             Drawdown = dd
                             IsHalted = _riskGuard.IsHalted
                             HaltReason = _riskGuard.HaltReason
                         End Sub)

                ' Last signal
                Dim last = _signalService.LastSignal
                If last IsNot Nothing Then
                    Dispatch(Sub()
                                 LastSignalType = last.SignalType
                                 LastSignalConfidence = last.Confidence
                                 LastSignalTime = last.GeneratedAt.LocalDateTime.ToString("HH:mm:ss")
                             End Sub)
                End If

                Dispatch(Sub() StatusMessage = $"Updated {DateTime.Now:HH:mm:ss}")

            Catch ex As Exception
                Dispatch(Sub() StatusMessage = $"Error: {ex.Message}")
            End Try
        End Function

        ' ── Event handlers ───────────────────────────────────────────────────

        Private Sub OnTradingHalted(sender As Object, e As RiskHaltEventArgs)
            Dispatch(Sub()
                         IsHalted = True
                         HaltReason = e.Reason
                         DailyPnL = e.DailyPnL
                         Drawdown = e.Drawdown
                     End Sub)
        End Sub

        Private Sub OnTradingResumed(sender As Object, e As EventArgs)
            Dispatch(Sub()
                         IsHalted = False
                         HaltReason = RiskHaltReason.None
                     End Sub)
        End Sub

        Private Sub OnSignalGenerated(sender As Object, e As SignalGeneratedEventArgs)
            Dispatch(Sub()
                         LastSignalType = e.Signal.SignalType
                         LastSignalConfidence = e.Signal.Confidence
                         LastSignalTime = e.Signal.GeneratedAt.LocalDateTime.ToString("HH:mm:ss")
                     End Sub)
        End Sub

        Private Sub Dispatch(action As Action)
            If Application.Current?.Dispatcher IsNot Nothing Then
                Application.Current.Dispatcher.Invoke(action)
            End If
        End Sub

    End Class

End Namespace
