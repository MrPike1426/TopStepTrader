Imports System.Collections.ObjectModel
Imports System.IO
Imports System.Text.Json
Imports System.Windows
Imports Microsoft.Extensions.DependencyInjection
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.Personas
Imports TopStepTrader.Services.Trading
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    Public Class ProTraderViewModel
        Inherits TradingViewModelBase
        Implements IDisposable

        ' ── Dependencies ──────────────────────────────────────────────────────
        Private ReadOnly _scopeFactory As IServiceScopeFactory
        Private ReadOnly _accountService As IAccountService
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _personaService As IPersonaService
        Private ReadOnly _slotStore As ProTraderSlotStore

        Private ReadOnly _slotsPath As String =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "TopStepTrader", "protrader_slots.json")

        ' ── Slots ─────────────────────────────────────────────────────────────
        Public ReadOnly Property Slots As New ObservableCollection(Of ProTraderSlotVm)()

        ' ── Active position panel ─────────────────────────────────────────────
        Public ReadOnly Property ActivePositionSlots As New ObservableCollection(Of ProTraderSlotVm)()

        Private _hasActiveSlots As Boolean = False
        Public Property HasActiveSlots As Boolean
            Get
                Return _hasActiveSlots
            End Get
            Private Set(value As Boolean)
                SetProperty(_hasActiveSlots, value)
            End Set
        End Property

        ' ── Engine management ─────────────────────────────────────────────────
        Private ReadOnly _slotScopes As New Dictionary(Of ProTraderSlotVm, IServiceScope)()
        Private ReadOnly _slotEngines As New Dictionary(Of ProTraderSlotVm, StrategyExecutionEngine)()
        Private ReadOnly _activeSlots As New List(Of ProTraderSlotVm)()
        Private _disposed As Boolean = False

        ' ── ATR Override (FEAT-18) ────────────────────────────────────────────
        Private _atrOverrideEnabled As Boolean = False
        Public Property AtrOverrideEnabled As Boolean
            Get
                Return _atrOverrideEnabled
            End Get
            Set(value As Boolean)
                SetProperty(_atrOverrideEnabled, value)
            End Set
        End Property

        ' ── ATR Tier ──────────────────────────────────────────────────────────
        Private _selectedAtrTier As String = "Standard"

        Public ReadOnly Property IsAtrTightSelected As Boolean
            Get
                Return _selectedAtrTier = "Tight"
            End Get
        End Property

        Public ReadOnly Property IsAtrStandardSelected As Boolean
            Get
                Return _selectedAtrTier = "Standard"
            End Get
        End Property

        Public ReadOnly Property IsAtrWideSelected As Boolean
            Get
                Return _selectedAtrTier = "Wide"
            End Get
        End Property

        Public ReadOnly Property IsAtrUltraSelected As Boolean
            Get
                Return _selectedAtrTier = "Ultra"
            End Get
        End Property

        Public ReadOnly Property SelectAtrTierCommand As RelayCommand
            Get
                Return New RelayCommand(
                    Sub(param)
                        Dim tier = TryCast(param, String)
                        If String.IsNullOrEmpty(tier) Then Return
                        _selectedAtrTier = tier
                        ApplyAtrTier(tier)
                        NotifyPropertyChanged(NameOf(IsAtrTightSelected))
                        NotifyPropertyChanged(NameOf(IsAtrStandardSelected))
                        NotifyPropertyChanged(NameOf(IsAtrWideSelected))
                        NotifyPropertyChanged(NameOf(IsAtrUltraSelected))
                    End Sub,
                    Function(param) Not _isRunning)
            End Get
        End Property

        ' ── SL / TP multiples (global ATR override values) ───────────────────
        Private _slMultipleOfN As Decimal = 1.5D
        Public Property SlMultipleOfN As Decimal
            Get
                Return _slMultipleOfN
            End Get
            Set(value As Decimal)
                SetProperty(_slMultipleOfN, value)
            End Set
        End Property

        Private _tpMultipleOfN As Decimal = 3.0D
        Public Property TpMultipleOfN As Decimal
            Get
                Return _tpMultipleOfN
            End Get
            Set(value As Decimal)
                SetProperty(_tpMultipleOfN, value)
            End Set
        End Property

        ' ── Confidence % ──────────────────────────────────────────────────────
        Private _minConfidencePct As Integer = 80
        Public Property MinConfidencePct As Integer
            Get
                Return _minConfidencePct
            End Get
            Set(value As Integer)
                SetProperty(_minConfidencePct, Math.Max(0, Math.Min(100, value)))
            End Set
        End Property

        ' ── Trade amount ──────────────────────────────────────────────────────
        Private _tradeAmount As Decimal = 500D
        Public Property TradeAmount As Decimal
            Get
                Return _tradeAmount
            End Get
            Set(value As Decimal)
                SetProperty(_tradeAmount, Math.Max(0D, value))
            End Set
        End Property

        ' ── Running state ─────────────────────────────────────────────────────
        Private _isRunning As Boolean = False

        Public ReadOnly Property IsNotRunning As Boolean
            Get
                Return Not _isRunning
            End Get
        End Property

        Public ReadOnly Property StartCommand As RelayCommand
            Get
                Return New RelayCommand(
                    AddressOf ExecuteStart,
                    Function(p) Not _isRunning AndAlso SelectedAccount IsNot Nothing AndAlso
                                Slots.Any(Function(s) s.IsEnabled))
            End Get
        End Property

        Public ReadOnly Property StopCommand As RelayCommand
            Get
                Return New RelayCommand(
                    AddressOf ExecuteStop,
                    Function(p) _isRunning)
            End Get
        End Property

        ' ── IsFormReady ───────────────────────────────────────────────────────
        Public Overrides ReadOnly Property IsFormReady As Boolean
            Get
                Return SelectedAccount IsNot Nothing
            End Get
        End Property

        ' ── Constructor ───────────────────────────────────────────────────────
        Public Sub New(scopeFactory As IServiceScopeFactory,
                       accountService As IAccountService,
                       session As ITradingSessionContext,
                       personaService As IPersonaService,
                       slotStore As ProTraderSlotStore)
            _scopeFactory = scopeFactory
            _accountService = accountService
            _session = session
            _personaService = personaService
            _slotStore = slotStore
            slotStore.Register(AddressOf AddSlot)

            InitDefaultSlots()
            LoadSlots()

            AddHandler _session.AccountChanged, AddressOf OnSessionAccountChanged
        End Sub

        ' ── Data loading ──────────────────────────────────────────────────────
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
                ' Offline — account required to trade but slot config still works
            End Try
        End Sub

        ' ── Session account sync ──────────────────────────────────────────────
        Private Sub OnSessionAccountChanged(sender As Object, account As Account)
            Dispatch(Sub()
                         If account IsNot Nothing Then
                             Dim match = Accounts.FirstOrDefault(Function(a) a.Id = account.Id)
                             If match IsNot Nothing Then SelectedAccount = match
                         End If
                     End Sub)
        End Sub

        ' ── Slot management ───────────────────────────────────────────────────
        Public Sub AddSlot(slot As ProTraderSlotVm)
            If Slots.Any(Function(s) s.Label = slot.Label) Then Return
            slot.SetSaveCallback(AddressOf SaveSlots)
            Slots.Add(slot)
            SaveSlots()
        End Sub

        ' ── Persistence ───────────────────────────────────────────────────────
        Private Sub InitDefaultSlots()
            Dim save = New Action(AddressOf SaveSlots)
            Dim defaults = New ProTraderSlotVm() {
                New ProTraderSlotVm("GOLD.24-7", "Gold",   StrategyConditionType.MultiConfluence,      BarTimeframe.OneHour,       "Damian", 1.0D, 2.5D, save),
                New ProTraderSlotVm("OIL",       "Oil",    StrategyConditionType.MultiConfluence,      BarTimeframe.FiveMinute,    "Damian", 1.0D, 2.5D, save),
                New ProTraderSlotVm("SPX500",    "MES",    StrategyConditionType.EmaRsiWeightedScore,  BarTimeframe.FifteenMinute, "Joe",    0.75D, 1.5D, save),
                New ProTraderSlotVm("GOLD.24-7", "Gold",   StrategyConditionType.OpeningRangeBreakout, BarTimeframe.FifteenMinute, "Damian", 1.0D, 2.5D, save),
                New ProTraderSlotVm("MNQ",       "MNQ",    StrategyConditionType.OpeningRangeBreakout, BarTimeframe.FifteenMinute, "Joe",    0.75D, 1.5D, save),
                New ProTraderSlotVm("SPX500",    "MES",    StrategyConditionType.VwapMeanReversion,    BarTimeframe.FiveMinute,    "Lewis",  1.5D, 3.0D, save)
            }
            For Each slot In defaults
                Slots.Add(slot)
            Next
        End Sub

        Private Sub LoadSlots()
            Try
                If Not File.Exists(_slotsPath) Then Return
                Dim json = File.ReadAllText(_slotsPath)
                Dim states = JsonSerializer.Deserialize(Of List(Of SlotState))(json)
                If states Is Nothing Then Return
                For Each state In states
                    Dim match = Slots.FirstOrDefault(Function(s) s.Label = state.Label)
                    If match IsNot Nothing Then
                        match.IsEnabled = state.IsEnabled
                    End If
                Next
            Catch
            End Try
        End Sub

        Friend Sub SaveSlots()
            Try
                Dim dir = Path.GetDirectoryName(_slotsPath)
                If Not Directory.Exists(dir) Then Directory.CreateDirectory(dir)
                Dim states = Slots.Select(Function(s) New SlotState With {
                    .Label = s.Label, .IsEnabled = s.IsEnabled
                }).ToList()
                Dim json = JsonSerializer.Serialize(states, New JsonSerializerOptions With {.WriteIndented = True})
                File.WriteAllText(_slotsPath, json)
            Catch
            End Try
        End Sub

        ' ── Engine start/stop ─────────────────────────────────────────────────

        Private Sub ExecuteStart(param As Object)
            If SelectedAccount Is Nothing Then Return

            _isRunning = True
            NotifyPropertyChanged(NameOf(IsNotRunning))
            RelayCommand.RaiseCanExecuteChanged()

            DisposeAllEngines()
            _activeSlots.Clear()
            ActivePositionSlots.Clear()
            HasActiveSlots = False

            For Each slot In Slots
                If Not slot.IsEnabled Then Continue For

                Dim scope = _scopeFactory.CreateScope()
                Dim engine = scope.ServiceProvider.GetRequiredService(Of StrategyExecutionEngine)()
                _slotScopes(slot) = scope
                _slotEngines(slot) = engine

                Dim capturedSlot = slot
                Dim capturedVm As ProTraderViewModel = Me
                engine.IsOrderingAllowed = Function()
                    If Not capturedVm._isRunning Then Return False
                    If capturedVm._activeSlots.Count = 0 Then Return True
                    Return capturedVm._activeSlots.Any(Function(s) _
                        String.Equals(s.ContractId, capturedSlot.ContractId, StringComparison.OrdinalIgnoreCase))
                End Function

                WireSlotEngineEvents(engine, slot)
                capturedSlot.CloseCommand = New RelayCommand(
                    Sub() Task.Run(Sub() CloseSlotPositionAsync(capturedSlot)),
                    Function() capturedSlot.HasOpenPosition)
                capturedSlot.NudgeBracketCommand = New RelayCommand(
                    Sub() Task.Run(Sub() NudgeSlotBracketAsync(capturedSlot)),
                    Function() capturedSlot.HasOpenPosition)

                Dim sd = BuildStrategyDefinition(slot)
                engine.Start(sd)
                slot.StatusText = "Monitoring"
                slot.ConfidencePct = 0
            Next
        End Sub

        Private Sub ExecuteStop(param As Object)
            _isRunning = False

            For Each kvp In _slotEngines
                Try
                    kvp.Value.Stop()
                Catch ex As ObjectDisposedException
                Catch
                End Try
            Next

            DisposeAllEngines()
            _activeSlots.Clear()
            Dispatch(Sub()
                         ActivePositionSlots.Clear()
                         HasActiveSlots = False
                         For Each slot In Slots
                             slot.StatusText = "Stopped"
                             slot.HasOpenPosition = False
                             slot.ClearSlStatus()
                         Next
                         NotifyPropertyChanged(NameOf(IsNotRunning))
                         RelayCommand.RaiseCanExecuteChanged()
                     End Sub)
        End Sub

        ' ── State machine ─────────────────────────────────────────────────────

        Private Sub OnAnyTradeOpened(firedSlot As ProTraderSlotVm)
            firedSlot.HasOpenPosition = True
            firedSlot.StatusText = "Trading"

            Dim isScaleIn = _activeSlots.Any(Function(s) _
                String.Equals(s.ContractId, firedSlot.ContractId, StringComparison.OrdinalIgnoreCase))

            _activeSlots.Add(firedSlot)
            ActivePositionSlots.Add(firedSlot)
            HasActiveSlots = True

            If Not isScaleIn Then
                For Each kvp In _slotEngines.ToList()
                    If Not _activeSlots.Contains(kvp.Key) Then
                        Try
                            kvp.Value.Stop()
                        Catch
                        End Try
                        kvp.Key.StatusText = "Paused"
                    End If
                Next
            End If
        End Sub

        Private Sub OnPositionClosed(closedSlot As ProTraderSlotVm)
            closedSlot.HasOpenPosition = False
            closedSlot.ClearSlStatus()
            closedSlot.StatusText = "Monitoring"
            _activeSlots.Remove(closedSlot)
            ActivePositionSlots.Remove(closedSlot)

            If _activeSlots.Count = 0 Then
                HasActiveSlots = False
                Dim pausedSlots = _slotEngines.Keys _
                    .Where(Function(s) s.IsEnabled AndAlso s.StatusText = "Paused") _
                    .ToList()
                For Each slot In pausedSlots
                    ResumeSlot(slot)
                Next
            End If
        End Sub

        Private Sub ResumeSlot(slot As ProTraderSlotVm)
            Try
                If _slotEngines.ContainsKey(slot) Then _slotEngines(slot).Dispose()
                If _slotScopes.ContainsKey(slot) Then _slotScopes(slot).Dispose()
            Catch ex As ObjectDisposedException
            Catch
            End Try

            Dim newScope = _scopeFactory.CreateScope()
            Dim newEngine = newScope.ServiceProvider.GetRequiredService(Of StrategyExecutionEngine)()
            _slotScopes(slot) = newScope
            _slotEngines(slot) = newEngine

            Dim capturedSlot = slot
            Dim capturedVm As ProTraderViewModel = Me
            newEngine.IsOrderingAllowed = Function()
                If Not capturedVm._isRunning Then Return False
                If capturedVm._activeSlots.Count = 0 Then Return True
                Return capturedVm._activeSlots.Any(Function(s) _
                    String.Equals(s.ContractId, capturedSlot.ContractId, StringComparison.OrdinalIgnoreCase))
            End Function

            WireSlotEngineEvents(newEngine, slot)
            capturedSlot.CloseCommand = New RelayCommand(
                Sub() Task.Run(Sub() CloseSlotPositionAsync(capturedSlot)),
                Function() capturedSlot.HasOpenPosition)
            capturedSlot.NudgeBracketCommand = New RelayCommand(
                Sub() Task.Run(Sub() NudgeSlotBracketAsync(capturedSlot)),
                Function() capturedSlot.HasOpenPosition)

            Dim sd = BuildStrategyDefinition(capturedSlot)
            newEngine.Start(sd)
            slot.StatusText = "Monitoring"
        End Sub

        ' ── Engine event wiring ───────────────────────────────────────────────

        Private Sub WireSlotEngineEvents(engine As StrategyExecutionEngine, slot As ProTraderSlotVm)
            AddHandler engine.ConfidenceUpdated,
                Sub(s As Object, e As ConfidenceUpdatedEventArgs)
                    Dispatch(Sub()
                                 slot.ConfidencePct = Math.Max(e.UpPct, e.DownPct)
                                 If Not slot.HasOpenPosition Then
                                     If e.IsMarketClosed Then
                                         slot.StatusText = "⏸ Closed"
                                     Else
                                         slot.StatusText = "Monitoring"
                                     End If
                                 End If
                             End Sub)
                End Sub

            AddHandler engine.TradeOpened,
                Sub(s As Object, e As TradeOpenedEventArgs)
                    Dispatch(Sub() OnAnyTradeOpened(slot))
                End Sub

            AddHandler engine.TradeClosed,
                Sub(s As Object, e As TradeClosedEventArgs)
                    Dispatch(Sub() OnPositionClosed(slot))
                End Sub

            AddHandler engine.TurtleBracketChanged,
                Sub(s As Object, e As TurtleBracketChangedEventArgs)
                    Dispatch(Sub() slot.ApplySl(e.SlPrice, e.TpPrice, e.IsAdvance, e.IsFreeRide))
                End Sub
        End Sub

        ' ── Strategy definition builder ───────────────────────────────────────

        Private Function BuildStrategyDefinition(slot As ProTraderSlotVm) As StrategyDefinition
            Dim persona = _personaService.GetProfile(slot.PersonaName)
            Dim fav = FavouriteContracts.TryGetBySymbol(slot.ContractId)
            Dim slMult = If(AtrOverrideEnabled, SlMultipleOfN, slot.SlMultiple)
            Dim tpMult = If(AtrOverrideEnabled, TpMultipleOfN, slot.TpMultiple)

            Return New StrategyDefinition With {
                .Name = GetStrategyName(slot.StrategyType),
                .Indicator = GetIndicatorType(slot.StrategyType),
                .Condition = slot.StrategyType,
                .IndicatorPeriod = GetIndicatorPeriod(slot.StrategyType),
                .SecondaryPeriod = GetSecondaryPeriod(slot.StrategyType),
                .IndicatorMultiplier = 0,
                .GoLongWhenBelowBands = True,
                .GoShortWhenAboveBands = True,
                .TimeframeMinutes = CInt(slot.Timeframe),
                .DurationHours = 8760,
                .ContractId = slot.ContractId,
                .AccountId = SelectedAccount.Id,
                .Quantity = If(persona IsNot Nothing, persona.PositionSize, 1),
                .AdxThreshold = If(persona IsNot Nothing, persona.AdxThreshold, 20.0F),
                .MaxScaleIns = If(persona IsNot Nothing, persona.MaxScaleIns, 1),
                .MinConfidencePct = _minConfidencePct,
                .SlMultipleOfN = slMult,
                .TpMultipleOfN = tpMult,
                .TickSize = If(fav IsNot Nothing AndAlso fav.PxTickSize > 0D, fav.PxTickSize, 1D),
                .TickValue = If(fav IsNot Nothing AndAlso fav.PxTickValue > 0D, fav.PxTickValue, 1D),
                .PersonaName = slot.PersonaName
            }
        End Function

        Private Shared Function GetStrategyName(t As StrategyConditionType) As String
            Select Case t
                Case StrategyConditionType.MultiConfluence      : Return "Multi-Confluence Engine"
                Case StrategyConditionType.EmaRsiWeightedScore  : Return "EMA/RSI Combined"
                Case StrategyConditionType.OpeningRangeBreakout : Return "Opening Range Breakout"
                Case StrategyConditionType.VwapMeanReversion    : Return "VWAP Mean Reversion"
                Case StrategyConditionType.VidyaCross           : Return "VIDYA Cross"
                Case StrategyConditionType.NakedTrader          : Return "Naked Trader"
                Case StrategyConditionType.BbSqueezeScalper     : Return "BB Squeeze Scalper"
                Case StrategyConditionType.LultDivergence       : Return "LULT Divergence"
                Case StrategyConditionType.DoubleBubbleButt     : Return "Double Bubble Butt"
                Case Else                                        : Return t.ToString()
            End Select
        End Function

        Private Shared Function GetIndicatorType(t As StrategyConditionType) As StrategyIndicatorType
            Select Case t
                Case StrategyConditionType.MultiConfluence      : Return StrategyIndicatorType.MultiConfluence
                Case StrategyConditionType.EmaRsiWeightedScore  : Return StrategyIndicatorType.EmaRsiCombined
                Case StrategyConditionType.VidyaCross           : Return StrategyIndicatorType.Vidya
                Case StrategyConditionType.NakedTrader          : Return StrategyIndicatorType.NakedTrader
                Case StrategyConditionType.BbSqueezeScalper     : Return StrategyIndicatorType.BbSqueezeScalper
                Case StrategyConditionType.LultDivergence       : Return StrategyIndicatorType.LultDivergence
                Case StrategyConditionType.DoubleBubbleButt     : Return StrategyIndicatorType.DoubleBollingerBands
                Case Else                                        : Return StrategyIndicatorType.EmaRsiCombined
            End Select
        End Function

        Private Shared Function GetIndicatorPeriod(t As StrategyConditionType) As Integer
            Select Case t
                Case StrategyConditionType.MultiConfluence      : Return 80
                Case StrategyConditionType.EmaRsiWeightedScore  : Return 50
                Case StrategyConditionType.VidyaCross           : Return 14
                Case StrategyConditionType.NakedTrader          : Return 14
                Case StrategyConditionType.BbSqueezeScalper     : Return 25
                Case StrategyConditionType.LultDivergence       : Return 100
                Case StrategyConditionType.DoubleBubbleButt     : Return 20
                Case Else                                        : Return 14
            End Select
        End Function

        Private Shared Function GetSecondaryPeriod(t As StrategyConditionType) As Integer
            Select Case t
                Case StrategyConditionType.VidyaCross : Return 9
                Case StrategyConditionType.NakedTrader : Return 9
                Case Else : Return 0
            End Select
        End Function

        ' ── Per-slot bracket management ───────────────────────────────────────

        Private Async Sub CloseSlotPositionAsync(slot As ProTraderSlotVm)
            Try
                If Not _slotScopes.ContainsKey(slot) Then Return
                Dim scope = _slotScopes(slot)
                Dim accountId = If(SelectedAccount IsNot Nothing, SelectedAccount.Id, 0L)
                Dim orderService = scope.ServiceProvider.GetRequiredService(Of IOrderService)()
                Dim ok = Await orderService.FlattenContractAsync(accountId, slot.ContractId)
                Dispatch(Sub()
                             slot.HasOpenPosition = False
                             slot.ClearSlStatus()
                             slot.StatusText = "Monitoring"
                         End Sub)
            Catch ex As Exception
            End Try
        End Sub

        Private Async Sub NudgeSlotBracketAsync(slot As ProTraderSlotVm)
            Try
                If Not _slotScopes.ContainsKey(slot) Then Return
                Dim scope = _slotScopes(slot)
                Dim accountId = If(SelectedAccount IsNot Nothing, SelectedAccount.Id, 0L)
                Dim orderService = scope.ServiceProvider.GetRequiredService(Of IOrderService)()

                Dim snapshot = Await orderService.GetLivePositionSnapshotAsync(accountId, slot.ContractId)
                If snapshot Is Nothing Then Return

                Dim fav = FavouriteContracts.TryGetBySymbol(slot.ContractId)
                Dim tickSize As Decimal = If(fav IsNot Nothing AndAlso fav.PxTickSize > 0, fav.PxTickSize, 0.25D)
                Dim isBuy = snapshot.IsBuy
                Dim entryPrice = snapshot.OpenRate

                Dim resolvedSl = slot.CurrentSlPrice
                If resolvedSl <= 0D Then
                    resolvedSl = If(isBuy, entryPrice - 20 * tickSize, entryPrice + 20 * tickSize)
                End If

                Dim slGap = If(isBuy, entryPrice - resolvedSl, resolvedSl - entryPrice)
                Dim slStep = Math.Max(Math.Round(slGap * 0.1D / tickSize) * tickSize, tickSize)
                Dim newSl = Math.Round(If(isBuy, resolvedSl + slStep, resolvedSl - slStep) / tickSize) * tickSize

                Dim newTp As Decimal? = Nothing
                Dim resolvedTp = slot.CurrentTpPrice
                If resolvedTp > 0D Then
                    Dim tpGap = If(isBuy, resolvedTp - entryPrice, entryPrice - resolvedTp)
                    Dim tpStep = Math.Max(Math.Round(tpGap * 0.1D / tickSize) * tickSize, tickSize)
                    newTp = Math.Round(If(isBuy, resolvedTp - tpStep, resolvedTp + tpStep) / tickSize) * tickSize
                End If

                Dim ok = Await orderService.EditPositionSlTpAsync(snapshot.PositionId, newSl, newTp)
                If ok Then
                    Dispatch(Sub() slot.ApplySl(newSl, If(newTp.HasValue, newTp.Value, resolvedTp), False, slot.IsFreeRide))
                End If
            Catch ex As Exception
            End Try
        End Sub

        ' ── Helpers ───────────────────────────────────────────────────────────

        Private Sub DisposeAllEngines()
            For Each kvp In _slotEngines.ToList()
                Try
                    kvp.Value.Dispose()
                Catch ex As ObjectDisposedException
                Catch
                End Try
            Next
            For Each kvp In _slotScopes.ToList()
                Try
                    kvp.Value.Dispose()
                Catch ex As ObjectDisposedException
                Catch
                End Try
            Next
            _slotEngines.Clear()
            _slotScopes.Clear()
        End Sub

        Private Sub ApplyAtrTier(tier As String)
            Select Case tier
                Case "Tight"
                    SlMultipleOfN = 0.75D
                    TpMultipleOfN = 1.5D
                Case "Standard"
                    SlMultipleOfN = 1.5D
                    TpMultipleOfN = 3.0D
                Case "Wide"
                    SlMultipleOfN = 2.5D
                    TpMultipleOfN = 5.0D
                Case "Ultra"
                    SlMultipleOfN = 5.0D
                    TpMultipleOfN = 10.0D
            End Select
        End Sub

        Private Sub Dispatch(action As Action)
            If Application.Current?.Dispatcher IsNot Nothing Then
                Application.Current.Dispatcher.Invoke(action)
            End If
        End Sub

        ' ── Inner DTO for JSON persistence ───────────────────────────────────
        Private Class SlotState
            Public Property Label As String
            Public Property IsEnabled As Boolean
        End Class

        ' ── IDisposable ───────────────────────────────────────────────────────
        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                RemoveHandler _session.AccountChanged, AddressOf OnSessionAccountChanged
                DisposeAllEngines()
                _disposed = True
            End If
        End Sub

    End Class

End Namespace
