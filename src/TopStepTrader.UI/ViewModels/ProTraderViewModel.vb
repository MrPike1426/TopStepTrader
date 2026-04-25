Imports System.Collections.ObjectModel
Imports TopStepTrader.Core.Models
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' ViewModel for the Pro-Trader view.
    ''' Skeleton — persona selection, ATR tier selection (Tight/Standard/Wide/Ultra),
    ''' account picker, confidence %, trade amount, and Start/Stop commands.
    ''' Content will be expanded in future tickets.
    ''' </summary>
    Public Class ProTraderViewModel
        Inherits TradingViewModelBase

        ' ── Persona ───────────────────────────────────────────────────────────────
        Private _selectedProfile As String = "Damian"

        Public ReadOnly Property IsLewisSelected As Boolean
            Get
                Return _selectedProfile = "Lewis"
            End Get
        End Property

        Public ReadOnly Property IsDamianSelected As Boolean
            Get
                Return _selectedProfile = "Damian"
            End Get
        End Property

        Public ReadOnly Property IsJoeSelected As Boolean
            Get
                Return _selectedProfile = "Joe"
            End Get
        End Property

        Public ReadOnly Property SelectProfileCommand As RelayCommand
            Get
                Return New RelayCommand(
                    Sub(param)
                        Dim name = TryCast(param, String)
                        If String.IsNullOrEmpty(name) Then Return
                        _selectedProfile = name
                        NotifyPropertyChanged(NameOf(IsLewisSelected))
                        NotifyPropertyChanged(NameOf(IsDamianSelected))
                        NotifyPropertyChanged(NameOf(IsJoeSelected))
                        ApplyPersonaDefaults(name)
                    End Sub)
            End Get
        End Property

        ' ── ATR Tier ──────────────────────────────────────────────────────────────
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

        ' ── SL / TP multiples (display only for now) ──────────────────────────────
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

        ' ── Confidence % ──────────────────────────────────────────────────────────
        Private _minConfidencePct As Integer = 80
        Public Property MinConfidencePct As Integer
            Get
                Return _minConfidencePct
            End Get
            Set(value As Integer)
                SetProperty(_minConfidencePct, Math.Max(0, Math.Min(100, value)))
            End Set
        End Property

        ' ── Trade amount ──────────────────────────────────────────────────────────
        Private _tradeAmount As Decimal = 500D
        Public Property TradeAmount As Decimal
            Get
                Return _tradeAmount
            End Get
            Set(value As Decimal)
                SetProperty(_tradeAmount, Math.Max(0D, value))
            End Set
        End Property

        ' ── Running state ─────────────────────────────────────────────────────────
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

        ' ── IsFormReady ───────────────────────────────────────────────────────────
        Public Overrides ReadOnly Property IsFormReady As Boolean
            Get
                Return SelectedAccount IsNot Nothing
            End Get
        End Property

        ' ── Helpers ───────────────────────────────────────────────────────────────
        Private Sub ApplyPersonaDefaults(name As String)
            Select Case name
                Case "Lewis"
                    MinConfidencePct = 90
                Case "Damian"
                    MinConfidencePct = 80
                Case "Joe"
                    MinConfidencePct = 70
            End Select
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

    End Class

End Namespace
