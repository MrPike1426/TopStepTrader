Imports System.Collections.ObjectModel
Imports System.Windows
Imports System.Windows.Media
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' Page ViewModel for the Persona config screen.
    ''' Holds one PersonaEditViewModel per persona (Lewis / Damian / Joe).
    ''' </summary>
    Public Class PersonaViewModel
        Inherits ViewModelBase

        Public Property Personas As New ObservableCollection(Of PersonaEditViewModel)

        Public Sub New(personaService As IPersonaService)
            Personas.Add(New PersonaEditViewModel("Lewis",  "Risk Averse", "#3498DB", personaService))
            Personas.Add(New PersonaEditViewModel("Damian", "Moderate",    "#27AE60", personaService))
            Personas.Add(New PersonaEditViewModel("Joe",    "Aggressive",  "#E74C3C", personaService))
        End Sub

    End Class

    ' ──────────────────────────────────────────────────────────────────────────
    ' Per-persona editable card
    ' ──────────────────────────────────────────────────────────────────────────

    ''' <summary>
    ''' Editable card for a single persona. Binds directly to fields in the UI.
    ''' "Save Persona" → persists to SQLite and updates the global in-memory cache.
    ''' "Reset to Defaults" → removes the SQLite row and reverts to appsettings.json defaults.
    ''' </summary>
    Public Class PersonaEditViewModel
        Inherits ViewModelBase

        Private ReadOnly _service As IPersonaService

        ' ── Identity ──────────────────────────────────────────────────────────
        Private ReadOnly _name As String
        Public ReadOnly Property Name As String
            Get
                Return _name
            End Get
        End Property

        Private ReadOnly _riskLabel As String
        Public ReadOnly Property RiskLabel As String
            Get
                Return _riskLabel
            End Get
        End Property

        Private ReadOnly _accentColor As String
        Public ReadOnly Property AccentColor As String
            Get
                Return _accentColor
            End Get
        End Property

        Public ReadOnly Property AccentBrush As SolidColorBrush
            Get
                Dim c = ColorConverter.ConvertFromString(_accentColor)
                Return New SolidColorBrush(CType(c, Color))
            End Get
        End Property

        ' ── Editable fields ───────────────────────────────────────────────────
        Private _tradeAmount As Decimal
        Public Property TradeAmount As Decimal
            Get
                Return _tradeAmount
            End Get
            Set(value As Decimal)
                SetProperty(_tradeAmount, value)
            End Set
        End Property

        Private _leverage As Integer
        Public Property Leverage As Integer
            Get
                Return _leverage
            End Get
            Set(value As Integer)
                SetProperty(_leverage, value)
            End Set
        End Property

        Private _maxScaleIns As Integer
        Public Property MaxScaleIns As Integer
            Get
                Return _maxScaleIns
            End Get
            Set(value As Integer)
                SetProperty(_maxScaleIns, value)
            End Set
        End Property

        Private _slMultipleOfN As Decimal
        Public Property SlMultipleOfN As Decimal
            Get
                Return _slMultipleOfN
            End Get
            Set(value As Decimal)
                SetProperty(_slMultipleOfN, value)
            End Set
        End Property

        Private _leveragedSlMultipleOfN As Decimal
        Public Property LeveragedSlMultipleOfN As Decimal
            Get
                Return _leveragedSlMultipleOfN
            End Get
            Set(value As Decimal)
                SetProperty(_leveragedSlMultipleOfN, value)
            End Set
        End Property

        Private _tpMultipleOfN As Decimal
        Public Property TpMultipleOfN As Decimal
            Get
                Return _tpMultipleOfN
            End Get
            Set(value As Decimal)
                SetProperty(_tpMultipleOfN, value)
            End Set
        End Property

        Private _adxThreshold As Single
        Public Property AdxThreshold As Single
            Get
                Return _adxThreshold
            End Get
            Set(value As Single)
                SetProperty(_adxThreshold, value)
            End Set
        End Property

        Private _defaultConfidencePct As Integer
        Public Property DefaultConfidencePct As Integer
            Get
                Return _defaultConfidencePct
            End Get
            Set(value As Integer)
                SetProperty(_defaultConfidencePct, value)
            End Set
        End Property

        ' ── Feedback ──────────────────────────────────────────────────────────
        Private _isBusy As Boolean = False
        Public Property IsBusy As Boolean
            Get
                Return _isBusy
            End Get
            Set(value As Boolean)
                If SetProperty(_isBusy, value) Then
                    RelayCommand.RaiseCanExecuteChanged()
                End If
            End Set
        End Property

        Private _statusMessage As String = String.Empty
        Public Property StatusMessage As String
            Get
                Return _statusMessage
            End Get
            Set(value As String)
                SetProperty(_statusMessage, value)
            End Set
        End Property

        Private _isStatusSuccess As Boolean = True
        Public Property IsStatusSuccess As Boolean
            Get
                Return _isStatusSuccess
            End Get
            Set(value As Boolean)
                SetProperty(_isStatusSuccess, value)
            End Set
        End Property

        ' ── Commands ──────────────────────────────────────────────────────────
        Public ReadOnly Property SaveCommand As RelayCommand
        Public ReadOnly Property ResetCommand As RelayCommand

        ' ── Constructor ───────────────────────────────────────────────────────
        Public Sub New(name As String, riskLabel As String, accentColor As String,
                       service As IPersonaService)
            _name = name
            _riskLabel = riskLabel
            _accentColor = accentColor
            _service = service
            LoadFromService()
            SaveCommand  = New RelayCommand(AddressOf ExecuteSave,  Function() Not _isBusy)
            ResetCommand = New RelayCommand(AddressOf ExecuteReset, Function() Not _isBusy)
        End Sub

        ' ── Private ───────────────────────────────────────────────────────────

        Private Sub LoadFromService()
            Dim p = _service.GetProfile(_name)
            _tradeAmount            = p.TradeAmount
            _leverage               = p.Leverage
            _maxScaleIns            = p.MaxScaleIns
            _slMultipleOfN          = p.SlMultipleOfN
            _leveragedSlMultipleOfN = p.LeveragedSlMultipleOfN
            _tpMultipleOfN          = p.TpMultipleOfN
            _adxThreshold           = p.AdxThreshold
            _defaultConfidencePct   = p.DefaultConfidencePct
        End Sub

        Private Sub LoadFromProfile(p As PersonaProfile)
            TradeAmount            = p.TradeAmount
            Leverage               = p.Leverage
            MaxScaleIns            = p.MaxScaleIns
            SlMultipleOfN          = p.SlMultipleOfN
            LeveragedSlMultipleOfN = p.LeveragedSlMultipleOfN
            TpMultipleOfN          = p.TpMultipleOfN
            AdxThreshold           = p.AdxThreshold
            DefaultConfidencePct   = p.DefaultConfidencePct
        End Sub

        Private Sub ExecuteSave()
            IsBusy = True
            StatusMessage = String.Empty
            Dim profile As New PersonaProfile With {
                .Name                  = _name,
                .TradeAmount           = _tradeAmount,
                .Leverage              = _leverage,
                .MaxScaleIns           = _maxScaleIns,
                .SlMultipleOfN         = _slMultipleOfN,
                .LeveragedSlMultipleOfN = _leveragedSlMultipleOfN,
                .TpMultipleOfN         = _tpMultipleOfN,
                .AdxThreshold          = _adxThreshold,
                .DefaultConfidencePct  = _defaultConfidencePct
            }
            Task.Run(Async Function()
                         Try
                             Await _service.SaveProfileAsync(profile)
                             Application.Current.Dispatcher.Invoke(Sub()
                                 IsStatusSuccess = True
                                 StatusMessage = $"✔  {_name} saved."
                                 IsBusy = False
                             End Sub)
                         Catch ex As Exception
                             Application.Current.Dispatcher.Invoke(Sub()
                                 IsStatusSuccess = False
                                 StatusMessage = $"✘  Save failed: {ex.Message}"
                                 IsBusy = False
                             End Sub)
                         End Try
                     End Function)
        End Sub

        Private Sub ExecuteReset()
            IsBusy = True
            StatusMessage = String.Empty
            Task.Run(Async Function()
                         Try
                             Await _service.ResetToDefaultAsync(_name)
                             Dim defaults = _service.GetProfile(_name)
                             Application.Current.Dispatcher.Invoke(Sub()
                                 LoadFromProfile(defaults)
                                 IsStatusSuccess = True
                                 StatusMessage = $"↺  {_name} reset to defaults."
                                 IsBusy = False
                             End Sub)
                         Catch ex As Exception
                             Application.Current.Dispatcher.Invoke(Sub()
                                 IsStatusSuccess = False
                                 StatusMessage = $"✘  Reset failed: {ex.Message}"
                                 IsBusy = False
                             End Sub)
                         End Try
                     End Function)
        End Sub

    End Class

End Namespace
