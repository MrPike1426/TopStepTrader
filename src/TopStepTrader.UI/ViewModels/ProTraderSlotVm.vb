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
                Case BarTimeframe.TenMinute     : Return "10min"
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
