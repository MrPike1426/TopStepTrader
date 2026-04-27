Imports System.Windows.Media
Imports TopStepTrader.Core.Enums
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    Public Class ProTraderSlotVm
        Inherits ViewModelBase

        ' ── Identity (set at construction, never change) ─────────────────────
        Public ReadOnly Property Label As String
        Public ReadOnly Property Symbol As String
        Public ReadOnly Property ContractId As String
        Public ReadOnly Property StrategyType As StrategyConditionType
        Public ReadOnly Property Timeframe As BarTimeframe
        Public ReadOnly Property PersonaName As String
        Public ReadOnly Property SlMultiple As Decimal
        Public ReadOnly Property TpMultiple As Decimal
        Public ReadOnly Property PersonaAccentBrush As SolidColorBrush
        Public ReadOnly Property StrategyLabel As String
        Public ReadOnly Property TimeframeLabel As String
        Public ReadOnly Property StrategyTimeframeLabel As String

        ' ── Runtime (mutable) ────────────────────────────────────────────────
        Private _isEnabled As Boolean
        Private _saveCallback As Action

        Public Property IsEnabled As Boolean
            Get
                Return _isEnabled
            End Get
            Set(value As Boolean)
                If SetProperty(_isEnabled, value) Then _saveCallback?.Invoke()
            End Set
        End Property

        Private _statusText As String = "Stopped"
        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Set(value As String)
                SetProperty(_statusText, value)
            End Set
        End Property

        Private _confidencePct As Integer
        Public Property ConfidencePct As Integer
            Get
                Return _confidencePct
            End Get
            Set(value As Integer)
                SetProperty(_confidencePct, value)
            End Set
        End Property

        Private _hasOpenPosition As Boolean
        Public Property HasOpenPosition As Boolean
            Get
                Return _hasOpenPosition
            End Get
            Set(value As Boolean)
                SetProperty(_hasOpenPosition, value)
            End Set
        End Property

        ' ── Bracket price display (set by ProTraderViewModel on TurtleBracketChanged) ─
        Private _bracketPriceDisplay As String = String.Empty
        Public Property BracketPriceDisplay As String
            Get
                Return _bracketPriceDisplay
            End Get
            Private Set(value As String)
                If SetProperty(_bracketPriceDisplay, value) Then
                    NotifyPropertyChanged(NameOf(HasBracketPrices))
                End If
            End Set
        End Property

        Public ReadOnly Property HasBracketPrices As Boolean
            Get
                Return Not String.IsNullOrEmpty(_bracketPriceDisplay)
            End Get
        End Property

        Private _isFreeRide As Boolean
        Public Property IsFreeRide As Boolean
            Get
                Return _isFreeRide
            End Get
            Private Set(value As Boolean)
                SetProperty(_isFreeRide, value)
            End Set
        End Property

        Private _currentSlPrice As Decimal = 0D
        Private _currentTpPrice As Decimal = 0D

        Public ReadOnly Property CurrentSlPrice As Decimal
            Get
                Return _currentSlPrice
            End Get
        End Property

        Public ReadOnly Property CurrentTpPrice As Decimal
            Get
                Return _currentTpPrice
            End Get
        End Property

        ' ── Per-slot bracket management commands (wired by ProTraderViewModel on Start) ─
        Public Property CloseCommand As System.Windows.Input.ICommand
        Public Property NudgeBracketCommand As System.Windows.Input.ICommand

        Public Sub ApplySl(slPrice As Decimal, tpPrice As Decimal, isAdvance As Boolean, isFreeRide As Boolean)
            IsFreeRide = isFreeRide
            If slPrice > 0D Then _currentSlPrice = slPrice
            If tpPrice > 0D Then _currentTpPrice = tpPrice
            If slPrice > 0D Then
                BracketPriceDisplay = If(tpPrice > 0D,
                    $"SL: {slPrice:F2}  TP: {tpPrice:F2}",
                    $"SL: {slPrice:F2}")
            End If
        End Sub

        Public Sub ClearSlStatus()
            IsFreeRide = False
            _currentSlPrice = 0D
            _currentTpPrice = 0D
            BracketPriceDisplay = String.Empty
        End Sub

        ' ── Constructor ─────────────────────────────────────────────────────
        Public Sub New(contractId As String, symbol As String,
                       strategyType As StrategyConditionType,
                       timeframe As BarTimeframe,
                       personaName As String,
                       slMultiple As Decimal, tpMultiple As Decimal,
                       Optional saveCallback As Action = Nothing)
            Me.ContractId = contractId
            Me.Symbol = symbol
            Me.StrategyType = strategyType
            Me.Timeframe = timeframe
            Me.PersonaName = personaName
            Me.SlMultiple = slMultiple
            Me.TpMultiple = tpMultiple
            _saveCallback = saveCallback
            PersonaAccentBrush = MakePersonaBrush(personaName)
            StrategyLabel = GetStrategyLabel(strategyType)
            TimeframeLabel = GetTimeframeLabel(timeframe)
            StrategyTimeframeLabel = $"{StrategyLabel} · {TimeframeLabel}"
            Label = $"{symbol} · {StrategyLabel} · {TimeframeLabel} · {personaName}"
        End Sub

        Public Sub SetSaveCallback(callback As Action)
            _saveCallback = callback
        End Sub

        ' ── Static helpers ───────────────────────────────────────────────────
        Friend Shared Function GetStrategyLabel(t As StrategyConditionType) As String
            Select Case t
                Case StrategyConditionType.MultiConfluence      : Return "Multi-Conf"
                Case StrategyConditionType.EmaRsiWeightedScore  : Return "EMA/RSI"
                Case StrategyConditionType.OpeningRangeBreakout : Return "ORB"
                Case StrategyConditionType.VwapMeanReversion    : Return "VWAP"
                Case StrategyConditionType.VidyaCross           : Return "VIDYA"
                Case StrategyConditionType.NakedTrader          : Return "Naked"
                Case StrategyConditionType.BbSqueezeScalper     : Return "BB Squeeze"
                Case StrategyConditionType.LultDivergence       : Return "LULT"
                Case StrategyConditionType.DoubleBubbleButt     : Return "DBB"
                Case StrategyConditionType.TripleEmaCascade     : Return "3-EMA"
                Case Else                                        : Return t.ToString()
            End Select
        End Function

        Friend Shared Function GetTimeframeLabel(tf As BarTimeframe) As String
            Select Case tf
                Case BarTimeframe.OneMinute     : Return "1min"
                Case BarTimeframe.FiveMinute    : Return "5min"
                Case BarTimeframe.FifteenMinute : Return "15min"
                Case BarTimeframe.ThirtyMinute  : Return "30min"
                Case BarTimeframe.OneHour       : Return "1hr"
                Case BarTimeframe.TwoHour       : Return "2hr"
                Case BarTimeframe.FourHour      : Return "4hr"
                Case Else                        : Return tf.ToString()
            End Select
        End Function

        Private Shared Function MakePersonaBrush(name As String) As SolidColorBrush
            Select Case name
                Case "Lewis" : Return New SolidColorBrush(Color.FromRgb(&H4C, &HAF, &H50))
                Case "Joe"   : Return New SolidColorBrush(Color.FromRgb(&HEF, &H53, &H50))
                Case Else    : Return New SolidColorBrush(Color.FromRgb(&H90, &HA4, &HAE))
            End Select
        End Function

    End Class

End Namespace
