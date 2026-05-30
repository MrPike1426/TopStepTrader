Imports System.Collections.ObjectModel
Imports System.Windows
Imports System.Windows.Input
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.Market
Imports TopStepTrader.Services.Scalper
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' BUG-94 F6: diagnostic tab that places a Stop Market entry with an attached
    ''' <c>stopLossBracket</c> on a sim or live account, then auto-verifies that the
    ''' broker honoured the bracket. Mirrors <c>ScalperStopEntryManager.BuildStopEntryOrder</c>'s
    ''' post-BUG-95 shape so the diagnostic tests the same payload the strategy will send.
    '''
    ''' <para>This VM is intentionally minimal — Stop Market parent + bracket SL only. It is
    ''' NOT a manual trading surface; see BUG-94 "Out of Scope".</para>
    ''' </summary>
    Public Class TestTradeViewModel
        Inherits ViewModelBase

        Private Const MaxContracts As Integer = 2

        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _bars As IBarIngestionService
        Private ReadOnly _tradeRecordService As ITradeRecordService
        Private ReadOnly _logger As ILogger(Of TestTradeViewModel)

        Private _selectedSymbol As String = "MES"
        Private _triggerOffsetTicks As Integer = 8
        Private _initialStopDollars As Decimal = 25D
        Private _contracts As Integer = 1
        Private _resultLog As String = String.Empty
        Private _lastTestOrderId As Long?
        Private _lastTestContractResolved As String = String.Empty

        Public Sub New(orderService As IOrderService,
                       session As ITradingSessionContext,
                       bars As IBarIngestionService,
                       tradeRecordService As ITradeRecordService,
                       snapshotHealth As SnapshotHealthViewModel,
                       logger As ILogger(Of TestTradeViewModel))
            _orderService = orderService
            _session = session
            _bars = bars
            _tradeRecordService = tradeRecordService
            SnapshotHealth = snapshotHealth
            _logger = logger

            For Each s In UltimateScalperOrchestrator.WatchlistSymbols
                Symbols.Add(s)
            Next

            BuyStopCommand = New RelayCommand(Sub() FireAsync(OrderSide.Buy))
            SellStopCommand = New RelayCommand(Sub() FireAsync(OrderSide.Sell))
            CancelLastCommand = New RelayCommand(Sub() CancelLastAsync(),
                                                  Function() _lastTestOrderId.HasValue)
            AuditEntryPriceCommand = New RelayCommand(Sub() AuditEntryPriceAsync())
        End Sub

        Public ReadOnly Property Symbols As New ObservableCollection(Of String)()

        Public Property SelectedSymbol As String
            Get
                Return _selectedSymbol
            End Get
            Set(value As String)
                SetProperty(_selectedSymbol, value)
            End Set
        End Property

        Public Property TriggerOffsetTicks As Integer
            Get
                Return _triggerOffsetTicks
            End Get
            Set(value As Integer)
                SetProperty(_triggerOffsetTicks, Math.Max(0, value))
            End Set
        End Property

        Public Property InitialStopDollars As Decimal
            Get
                Return _initialStopDollars
            End Get
            Set(value As Decimal)
                SetProperty(_initialStopDollars, Math.Max(1D, value))
            End Set
        End Property

        Public Property Contracts As Integer
            Get
                Return _contracts
            End Get
            Set(value As Integer)
                Dim clamped = Math.Min(MaxContracts, Math.Max(1, value))
                SetProperty(_contracts, clamped)
            End Set
        End Property

        Public Property ResultLog As String
            Get
                Return _resultLog
            End Get
            Set(value As String)
                SetProperty(_resultLog, value)
            End Set
        End Property

        Public ReadOnly Property BuyStopCommand As ICommand
        Public ReadOnly Property SellStopCommand As ICommand
        Public ReadOnly Property CancelLastCommand As ICommand
        ''' <summary>BUG-102 F3: audits LiveTradeRecords with EntryPrice=0 and attempts broker-driven repair.</summary>
        Public ReadOnly Property AuditEntryPriceCommand As ICommand

        ''' <summary>OBS-07 F2: Snapshot Health tile bound under the Diagnostics view.</summary>
        Public ReadOnly Property SnapshotHealth As SnapshotHealthViewModel

        Private Async Sub FireAsync(side As OrderSide)
            Try
                Dim account = _session?.SelectedAccount
                If account Is Nothing OrElse account.Id = 0L Then
                    Append("No account selected.")
                    Return
                End If

                If account.IsLive Then
                    Dim result = MessageBox.Show(
                        $"This will place a real {side} Stop Market on a LIVE account ({account.Id}). Continue?",
                        "Test Trade — Live confirmation",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning)
                    If result <> MessageBoxResult.Yes Then
                        Append("Cancelled by user (live confirmation).")
                        Return
                    End If
                End If

                Dim contract = FavouriteContracts.TryGetBySymbolResolved(SelectedSymbol)
                If contract Is Nothing Then
                    Append($"Unknown symbol '{SelectedSymbol}'.")
                    Return
                End If

                Dim lastPrice = Await _bars.GetLatestPriceAsync(contract.PxContractId)
                If lastPrice <= 0D Then
                    Append($"GetLatestPriceAsync returned {lastPrice}; aborting.")
                    Return
                End If

                Dim offset As Decimal = TriggerOffsetTicks * contract.PxTickSize
                Dim raw As Decimal = If(side = OrderSide.Buy, lastPrice + offset, lastPrice - offset)
                Dim trigger As Decimal = If(side = OrderSide.Buy,
                    Math.Ceiling(raw / contract.PxTickSize) * contract.PxTickSize,
                    Math.Floor(raw / contract.PxTickSize) * contract.PxTickSize)

                Dim stopTicks As Integer = CInt(Math.Ceiling(CDbl(InitialStopDollars / contract.PxTickValue)))

                Dim order As New Order With {
                    .AccountId = account.Id,
                    .ContractId = contract.PxContractId,
                    .Side = side,
                    .Quantity = Math.Min(MaxContracts, Math.Max(1, Contracts)),
                    .OrderType = OrderType.StopOrder,
                    .StopPrice = trigger,
                    .InitialStopTicks = stopTicks
                }

                Append($"Composed: {side} {order.Quantity}× {SelectedSymbol} trigger={trigger} stopTicks={stopTicks} (last={lastPrice})")

                Dim placed As Order = Nothing
                Try
                    placed = Await _orderService.PlaceOrderAsync(order)
                Catch ex As Exception
                    _logger.LogWarning(ex, "TestTrade PlaceOrderAsync threw")
                    Append($"✗ PlaceOrderAsync threw: {ex.Message}")
                    Return
                End Try

                If placed Is Nothing OrElse Not placed.ExternalOrderId.HasValue Then
                    Append("✗ ORDER REJECTED — broker returned no ExternalOrderId.")
                    Return
                End If

                _lastTestOrderId = placed.ExternalOrderId
                _lastTestContractResolved = contract.PxContractId
                Append($"✅ ORDER ACCEPTED orderId={placed.ExternalOrderId.Value}")
                RelayCommand.RaiseCanExecuteChanged()

                ' Bracket-verification probe: 2 s after acceptance, query the broker for working
                ' Stop (type=4) orders on this contract. Two means parent + sibling SL bracket;
                ' one means TopStepX dropped the bracket on a Stop Market parent (FEAT-69 hard-stop).
                Await Task.Delay(TimeSpan.FromSeconds(2))
                Try
                    Dim working = Await _orderService.GetLiveWorkingOrdersAsync(account.Id, contract.PxContractId)
                    Dim stops = working _
                        .Where(Function(o) o.OrderType = OrderType.StopOrder) _
                        .OrderBy(Function(o) If(o.StopPrice, 0D)) _
                        .ToList()
                    If stops.Count >= 2 Then
                        Dim parentPx = stops.FirstOrDefault(Function(o) o.ExternalOrderId = placed.ExternalOrderId)
                        Dim siblings = stops.Where(Function(o) o.ExternalOrderId <> placed.ExternalOrderId).ToList()
                        Dim parentStr = If(parentPx?.StopPrice.HasValue, parentPx.StopPrice.Value.ToString(), "?")
                        Dim slStr = If(siblings.Count > 0 AndAlso siblings(0).StopPrice.HasValue,
                                       siblings(0).StopPrice.Value.ToString(), "?")
                        Append($"✅ BRACKET PRESENT — parent @ {parentStr}, SL @ {slStr}")
                    ElseIf stops.Count = 1 Then
                        Append("🚨 BRACKET DROPPED — TopStepX rejected stopLossBracket on Stop Market parent")
                    Else
                        Append($"✗ No working stop orders found for {contract.PxContractId} (count={stops.Count}).")
                    End If
                Catch ex As Exception
                    _logger.LogWarning(ex, "TestTrade GetLiveWorkingOrdersAsync threw")
                    Append($"✗ Bracket probe threw: {ex.Message}")
                End Try

            Catch ex As Exception
                _logger.LogWarning(ex, "TestTrade FireAsync threw")
                Append($"✗ Unhandled: {ex.Message}")
            End Try
        End Sub

        Private Async Sub CancelLastAsync()
            If Not _lastTestOrderId.HasValue Then Return
            Dim id = _lastTestOrderId.Value
            Try
                Dim ok = Await _orderService.CancelOrderAsync(id)
                Append(If(ok, $"✅ Cancel sent for orderId={id}.", $"✗ Cancel returned false for orderId={id}."))
            Catch ex As Exception
                _logger.LogWarning(ex, "TestTrade CancelOrderAsync threw")
                Append($"✗ Cancel threw: {ex.Message}")
            End Try
            _lastTestOrderId = Nothing
            _lastTestContractResolved = String.Empty
            RelayCommand.RaiseCanExecuteChanged()
        End Sub

        ''' <summary>
        ''' BUG-102 F3: dev-only audit of LiveTradeRecords whose <c>EntryPrice = 0</c>.
        ''' Surfaces the count and attempts broker-driven repair on rows whose context is
        ''' still resolvable; logs unresolvable rows for manual handling.
        ''' </summary>
        Private Async Sub AuditEntryPriceAsync()
            Try
                Dim accountId As Long = If(_session?.SelectedAccount?.Id, 0L)
                If accountId = 0L Then
                    Append("Audit EntryPrice=0: no account selected.")
                    Return
                End If
                Append("Audit EntryPrice=0: starting…")
                Dim result = Await _tradeRecordService.AuditZeroEntryPriceRowsAsync(accountId)
                Append($"Audit EntryPrice=0 complete: audited {result.Audited}; repaired {result.Repaired}; unresolvable {result.Unresolvable}.")
            Catch ex As Exception
                _logger.LogWarning(ex, "TestTrade AuditEntryPrice threw")
                Append($"✗ Audit EntryPrice=0 threw: {ex.Message}")
            End Try
        End Sub

        Private Sub Append(line As String)
            Dim ts = DateTime.Now.ToString("HH:mm:ss.fff")
            Dim formatted = $"[{ts}] {line}"
            Dim disp = Application.Current?.Dispatcher
            If disp IsNot Nothing AndAlso Not disp.CheckAccess() Then
                disp.BeginInvoke(New Action(Sub() ResultLog = If(String.IsNullOrEmpty(ResultLog), formatted, ResultLog & Environment.NewLine & formatted)))
            Else
                ResultLog = If(String.IsNullOrEmpty(ResultLog), formatted, ResultLog & Environment.NewLine & formatted)
            End If
        End Sub

    End Class

End Namespace
