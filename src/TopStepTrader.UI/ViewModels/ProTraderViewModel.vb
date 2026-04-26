Imports System.Collections.ObjectModel
Imports System.IO
Imports System.Text.Json
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Models
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    Public Class ProTraderViewModel
        Inherits TradingViewModelBase

        Private ReadOnly _slotStore As ProTraderSlotStore
        Private ReadOnly _slotsPath As String =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "TopStepTrader", "protrader_slots.json")

        ' ── Slots ─────────────────────────────────────────────────────────────
        Public ReadOnly Property Slots As New ObservableCollection(Of ProTraderSlotVm)()

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
                    End Sub)
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
                    Sub(param)
                        _isRunning = True
                        NotifyPropertyChanged(NameOf(IsNotRunning))
                    End Sub,
                    Function(param) IsFormReady AndAlso Not _isRunning)
            End Get
        End Property

        Public ReadOnly Property StopCommand As RelayCommand
            Get
                Return New RelayCommand(
                    Sub(param)
                        _isRunning = False
                        NotifyPropertyChanged(NameOf(IsNotRunning))
                    End Sub,
                    Function(param) _isRunning)
            End Get
        End Property

        ' ── IsFormReady ───────────────────────────────────────────────────────
        Public Overrides ReadOnly Property IsFormReady As Boolean
            Get
                Return SelectedAccount IsNot Nothing
            End Get
        End Property

        ' ── Constructor ───────────────────────────────────────────────────────
        Public Sub New(slotStore As ProTraderSlotStore)
            _slotStore = slotStore
            slotStore.Register(AddressOf AddSlot)

            InitDefaultSlots()
            LoadSlots()
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
                        ' Suppress save during load by setting the backing field directly via reflection is messy;
                        ' instead just set the property — SaveSlots will be called but file is already current
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

        ' ── Helpers ───────────────────────────────────────────────────────────
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

        ' ── Inner DTO for JSON persistence ───────────────────────────────────
        Private Class SlotState
            Public Property Label As String
            Public Property IsEnabled As Boolean
        End Class

    End Class

End Namespace
