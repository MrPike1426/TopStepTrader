Imports System.Windows
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' Risk Guard monitor — live drawdown, daily P&amp;L, halt status, manual reset.
    ''' SAFETY: the Reset button requires the user to type a confirmation reason.
    ''' </summary>
    Public Class RiskGuardViewModel
        Inherits ViewModelBase

        Private ReadOnly _riskGuard    As IRiskGuardService
        Private ReadOnly _riskSettings As RiskSettings

        ' ── Bindable properties ──────────────────────────────────────────────

        Private _isHalted As Boolean
        Public Property IsHalted As Boolean
            Get
                Return _isHalted
            End Get
            Set(value As Boolean)
                SetProperty(_isHalted, value)
                OnPropertyChanged(NameOf(HaltStatusText))
                OnPropertyChanged(NameOf(HaltStatusColor))
                OnPropertyChanged(NameOf(CanReset))
            End Set
        End Property

        Private _haltReason As String = "None"
        Public Property HaltReason As String
            Get
                Return _haltReason
            End Get
            Set(value As String)
                SetProperty(_haltReason, value)
            End Set
        End Property

        Private _dailyPnL As Decimal
        Public Property DailyPnL As Decimal
            Get
                Return _dailyPnL
            End Get
            Set(value As Decimal)
                SetProperty(_dailyPnL, value)
                OnPropertyChanged(NameOf(DailyPnLColor))
                OnPropertyChanged(NameOf(DailyPnLPct))
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
                OnPropertyChanged(NameOf(DrawdownPct))
            End Set
        End Property

        Private _resetReason As String = ""
        Public Property ResetReason As String
            Get
                Return _resetReason
            End Get
            Set(value As String)
                SetProperty(_resetReason, value)
                OnPropertyChanged(NameOf(CanReset))
            End Set
        End Property

        Private _statusText As String = "Loading..."
        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Set(value As String)
                SetProperty(_statusText, value)
            End Set
        End Property

        ' ── Derived / display ────────────────────────────────────────────────

        Public ReadOnly Property HaltStatusText As String
            Get
                Return If(_isHalted, "⛔ TRADING HALTED", "✅ ACTIVE — Trading Permitted")
            End Get
        End Property

        Public ReadOnly Property HaltStatusColor As String
            Get
                Return If(_isHalted, "HaltedBrush", "BuyBrush")
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

        Public ReadOnly Property DailyPnLColor As String
            Get
                Return If(_dailyPnL >= 0, "BuyBrush", "SellBrush")
            End Get
        End Property

        Public ReadOnly Property DrawdownColor As String
            Get
                Return If(Math.Abs(_drawdown) >= Math.Abs(_riskSettings.MaxDrawdownDollars) * 0.75D,
                          "HaltedBrush", "WarningBrush")
            End Get
        End Property

        ''' <summary>Daily P&amp;L as % of limit (0-100, capped).</summary>
        Public ReadOnly Property DailyPnLPct As Double
            Get
                If _riskSettings.DailyLossLimitDollars = 0 Then Return 0
                Return Math.Min(100, Math.Abs(CDbl(_dailyPnL) / CDbl(_riskSettings.DailyLossLimitDollars)) * 100)
            End Get
        End Property

        ''' <summary>Drawdown as % of limit (0-100, capped).</summary>
        Public ReadOnly Property DrawdownPct As Double
            Get
                If _riskSettings.MaxDrawdownDollars = 0 Then Return 0
                Return Math.Min(100, Math.Abs(CDbl(_drawdown) / CDbl(_riskSettings.MaxDrawdownDollars)) * 100)
            End Get
        End Property

        Public ReadOnly Property CanReset As Boolean
            Get
                Return _isHalted AndAlso _resetReason.Trim().Length >= 5
            End Get
        End Property

        ' ── Commands ─────────────────────────────────────────────────────────

        Public ReadOnly Property RefreshCommand    As RelayCommand
        Public ReadOnly Property ResetHaltCommand  As RelayCommand

        ' ── Constructor ──────────────────────────────────────────────────────

        Public Sub New(riskGuard As IRiskGuardService,
                       riskOptions As IOptions(Of RiskSettings))
            _riskGuard    = riskGuard
            _riskSettings = riskOptions.Value

            RefreshCommand   = New RelayCommand(AddressOf LoadMetrics)
            ResetHaltCommand = New RelayCommand(AddressOf ExecuteResetHalt,
                                                 Function() CanReset)

            AddHandler _riskGuard.TradingHalted,  AddressOf OnHalted
            AddHandler _riskGuard.TradingResumed, AddressOf OnResumed
        End Sub

        Public Sub LoadDataAsync()
            LoadMetrics()
        End Sub

        Private Sub LoadMetrics()
            Task.Run(Async Function()
                         Try
                             Dim pnl = Await _riskGuard.GetDailyPnLAsync()
                             Dim dd  = Await _riskGuard.GetCurrentDrawdownAsync()
                             Dispatch(Sub()
                                          DailyPnL  = pnl
                                          Drawdown   = dd
                                          IsHalted   = _riskGuard.IsHalted
                                          HaltReason = _riskGuard.HaltReason.ToString()
                                          StatusText = $"Updated {DateTime.Now:HH:mm:ss}"
                                      End Sub)
                         Catch ex As Exception
                             Dispatch(Sub() StatusText = $"Error: {ex.Message}")
                         End Try
                     End Function)
        End Sub

        Private Sub ExecuteResetHalt(param As Object)
            Dim reason = _resetReason.Trim()
            Task.Run(Async Function()
                         Try
                             Await _riskGuard.ResetHaltAsync(reason)
                             Dispatch(Sub()
                                          ResetReason = ""
                                          StatusText = $"Halt reset by user: {reason}"
                                      End Sub)
                         Catch ex As Exception
                             Dispatch(Sub() StatusText = $"Reset error: {ex.Message}")
                         End Try
                     End Function)
        End Sub

        Private Sub OnHalted(sender As Object, e As RiskHaltEventArgs)
            Dispatch(Sub()
                         IsHalted   = True
                         HaltReason = e.Reason.ToString()
                         DailyPnL   = e.DailyPnL
                         Drawdown   = e.Drawdown
                     End Sub)
        End Sub

        Private Sub OnResumed(sender As Object, e As EventArgs)
            Dispatch(Sub()
                         IsHalted   = False
                         HaltReason = "None"
                     End Sub)
        End Sub

        Private Sub Dispatch(action As Action)
            If Application.Current?.Dispatcher IsNot Nothing Then
                Application.Current.Dispatcher.Invoke(action)
            End If
        End Sub

    End Class

End Namespace
