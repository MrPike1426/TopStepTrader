Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.Services.Feedback

Namespace TopStepTrader.Services.Trading

    ''' <summary>
    ''' Listens to ISignalService.SignalGenerated events and, when AutoExecutionEnabled=True,
    ''' passes orders through RiskGuardService before submitting via IOrderService.
    ''' After placement, notifies OutcomeTracker so the ML feedback loop can measure P&amp;L.
    ''' Auto-execution is OFF by default and must be explicitly enabled in Settings.
    ''' </summary>
    Public Class AutoExecutionService
        Implements IDisposable

        Private ReadOnly _signalService  As ISignalService
        Private ReadOnly _orderService   As IOrderService
        Private ReadOnly _riskGuard      As IRiskGuardService
        Private ReadOnly _accountService As IAccountService
        Private ReadOnly _outcomeTracker As OutcomeTracker
        Private ReadOnly _settings       As RiskSettings
        Private ReadOnly _logger         As ILogger(Of AutoExecutionService)
        Private _disposed As Boolean = False

        Public Sub New(signalService  As ISignalService,
                       orderService   As IOrderService,
                       riskGuard      As IRiskGuardService,
                       accountService As IAccountService,
                       outcomeTracker As OutcomeTracker,
                       options        As IOptions(Of RiskSettings),
                       logger         As ILogger(Of AutoExecutionService))
            _signalService  = signalService
            _orderService   = orderService
            _riskGuard      = riskGuard
            _accountService = accountService
            _outcomeTracker = outcomeTracker
            _settings       = options.Value
            _logger         = logger
            AddHandler _signalService.SignalGenerated, AddressOf OnSignalGenerated
        End Sub

        Private Async Sub OnSignalGenerated(sender As Object, e As SignalGeneratedEventArgs)
            Try
                Await HandleSignalAsync(e.Signal)
            Catch ex As Exception
                _logger.LogError(ex, "Unhandled error in auto-execution for signal {Id}", e.Signal.Id)
            End Try
        End Sub

        Private Async Function HandleSignalAsync(signal As TradeSignal) As Task
            ' Guard 1: Feature switch
            If Not _settings.AutoExecutionEnabled Then
                _logger.LogDebug("Auto-execution disabled — signal {Id} not executed", signal.Id)
                Return
            End If

            ' Guard 2: Only act on BUY or SELL — ignore HOLD
            If signal.SignalType = SignalType.Hold Then
                _logger.LogDebug("Signal is HOLD — no order placed")
                Return
            End If

            ' Guard 3: Confidence threshold
            If signal.Confidence < _settings.MinSignalConfidence Then
                _logger.LogInformation(
                    "Signal confidence {Conf:F3} below minimum {Min:F3} — skipped",
                    signal.Confidence, _settings.MinSignalConfidence)
                Return
            End If

            ' Guard 4: Risk guard check (re-evaluates live P&L + drawdown)
            Dim accounts = Await _accountService.GetActiveAccountsAsync()
            Dim account = accounts.FirstOrDefault()
            If account Is Nothing Then
                _logger.LogWarning("No active account found — cannot execute signal {Id}", signal.Id)
                Return
            End If

            Dim safe = Await _riskGuard.EvaluateRiskAsync(account)
            If Not safe Then
                _logger.LogWarning("Risk guard blocked execution of signal {Id}", signal.Id)
                Return
            End If

            ' Build and submit order
            Dim side = If(signal.SignalType = SignalType.Buy, OrderSide.Buy, OrderSide.Sell)
            Dim order = New Order With {
                .AccountId     = account.Id,
                .ContractId    = signal.ContractId,
                .Side          = side,
                .OrderType     = OrderType.Market,
                .Quantity      = 1,
                .SourceSignalId = signal.Id,
                .Notes         = signal.ContractId
            }

            _logger.LogInformation(
                "AUTO-EXECUTE: {Side} x1 for contract {Contract} (signal conf={Conf:F3})",
                side, signal.ContractId, signal.Confidence)

            Dim placedOrder = Await _orderService.PlaceOrderAsync(order)

            ' Record an open outcome for the ML feedback loop
            Try
                Await _outcomeTracker.RecordOpenOutcomeAsync(placedOrder)
            Catch ex As Exception
                _logger.LogWarning(ex, "Could not record outcome for order {Id}", placedOrder.Id)
            End Try
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                RemoveHandler _signalService.SignalGenerated, AddressOf OnSignalGenerated
                _disposed = True
            End If
        End Sub

    End Class

End Namespace
