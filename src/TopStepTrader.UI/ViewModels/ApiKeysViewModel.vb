Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Settings
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    Public Class ApiKeysViewModel
        Inherits ViewModelBase

        Private ReadOnly _store As IApiKeyStore

        ' ── Active broker selection ───────────────────────────────────────────────

        Private _activeBroker As String = "etoro"
        Public Property ActiveBroker As String
            Get
                Return _activeBroker
            End Get
            Set(value As String)
                If SetProperty(_activeBroker, value) Then
                    OnPropertyChanged(NameOf(IsEToroActive))
                    OnPropertyChanged(NameOf(IsTopStepXActive))
                End If
            End Set
        End Property

        Public Property IsEToroActive As Boolean
            Get
                Return String.Equals(_activeBroker, "etoro", StringComparison.OrdinalIgnoreCase)
            End Get
            Set(value As Boolean)
                If value Then ActiveBroker = "etoro"
            End Set
        End Property

        Public Property IsTopStepXActive As Boolean
            Get
                Return String.Equals(_activeBroker, "topstepx", StringComparison.OrdinalIgnoreCase)
            End Get
            Set(value As Boolean)
                If value Then ActiveBroker = "topstepx"
            End Set
        End Property

        ' ── Named provider keys — credential (non-secret) + API key (secret) ────────

        Private _etoroKeyName As String = String.Empty
        Public Property EtoroKeyName As String
            Get
                Return _etoroKeyName
            End Get
            Set(value As String)
                SetProperty(_etoroKeyName, value)
            End Set
        End Property

        ''' <summary>"demo" or "live" — which eToro account to connect to.</summary>
        Private _etoroAccountMode As String = "demo"
        Public Property EtoroAccountMode As String
            Get
                Return _etoroAccountMode
            End Get
            Set(value As String)
                If SetProperty(_etoroAccountMode, value) Then
                    OnPropertyChanged(NameOf(IsEtoroDemoSelected))
                    OnPropertyChanged(NameOf(IsEtoroLiveSelected))
                End If
            End Set
        End Property

        ''' <summary>True when Demo mode is active — drives radio-button IsChecked binding.</summary>
        Public Property IsEtoroDemoSelected As Boolean
            Get
                Return String.Equals(_etoroAccountMode, "demo", StringComparison.OrdinalIgnoreCase)
            End Get
            Set(value As Boolean)
                If value Then EtoroAccountMode = "demo"
            End Set
        End Property

        ''' <summary>True when Live mode is active — drives radio-button IsChecked binding.</summary>
        Public Property IsEtoroLiveSelected As Boolean
            Get
                Return String.Equals(_etoroAccountMode, "live", StringComparison.OrdinalIgnoreCase)
            End Get
            Set(value As Boolean)
                If value Then EtoroAccountMode = "live"
            End Set
        End Property

        Private _etoroApiKey As String = String.Empty
        Public Property EtoroApiKey As String
            Get
                Return _etoroApiKey
            End Get
            Set(value As String)
                SetProperty(_etoroApiKey, value)
            End Set
        End Property

        Private _etoroUserKey As String = String.Empty
        Public Property EtoroUserKey As String
            Get
                Return _etoroUserKey
            End Get
            Set(value As String)
                SetProperty(_etoroUserKey, value)
            End Set
        End Property

        Private _etoroLiveUserKey As String = String.Empty
        Public Property EtoroLiveUserKey As String
            Get
                Return _etoroLiveUserKey
            End Get
            Set(value As String)
                SetProperty(_etoroLiveUserKey, value)
            End Set
        End Property

        Private _topStepXUsername As String = String.Empty
        Public Property TopStepXUsername As String
            Get
                Return _topStepXUsername
            End Get
            Set(value As String)
                SetProperty(_topStepXUsername, value)
            End Set
        End Property

        Private _topStepXApiKey As String = String.Empty
        Public Property TopStepXApiKey As String
            Get
                Return _topStepXApiKey
            End Get
            Set(value As String)
                SetProperty(_topStepXApiKey, value)
            End Set
        End Property

        Private _claudeOrgId As String = String.Empty
        Public Property ClaudeOrgId As String
            Get
                Return _claudeOrgId
            End Get
            Set(value As String)
                SetProperty(_claudeOrgId, value)
            End Set
        End Property

        Private _claudeApiKey As String = String.Empty
        Public Property ClaudeApiKey As String
            Get
                Return _claudeApiKey
            End Get
            Set(value As String)
                SetProperty(_claudeApiKey, value)
            End Set
        End Property

        Private _binanceApiKey As String = String.Empty
        Public Property BinanceApiKey As String
            Get
                Return _binanceApiKey
            End Get
            Set(value As String)
                SetProperty(_binanceApiKey, value)
            End Set
        End Property

        Private _binanceSecretKey As String = String.Empty
        Public Property BinanceSecretKey As String
            Get
                Return _binanceSecretKey
            End Get
            Set(value As String)
                SetProperty(_binanceSecretKey, value)
            End Set
        End Property

        ' ── Future slots ─────────────────────────────────────────────────────────

        Private _future1Label As String = String.Empty
        Public Property Future1Label As String
            Get
                Return _future1Label
            End Get
            Set(value As String)
                SetProperty(_future1Label, value)
            End Set
        End Property

        Private _future1Username As String = String.Empty
        Public Property Future1Username As String
            Get
                Return _future1Username
            End Get
            Set(value As String)
                SetProperty(_future1Username, value)
            End Set
        End Property

        Private _future1Key As String = String.Empty
        Public Property Future1Key As String
            Get
                Return _future1Key
            End Get
            Set(value As String)
                SetProperty(_future1Key, value)
            End Set
        End Property

        Private _future2Label As String = String.Empty
        Public Property Future2Label As String
            Get
                Return _future2Label
            End Get
            Set(value As String)
                SetProperty(_future2Label, value)
            End Set
        End Property

        Private _future2Username As String = String.Empty
        Public Property Future2Username As String
            Get
                Return _future2Username
            End Get
            Set(value As String)
                SetProperty(_future2Username, value)
            End Set
        End Property

        Private _future2Key As String = String.Empty
        Public Property Future2Key As String
            Get
                Return _future2Key
            End Get
            Set(value As String)
                SetProperty(_future2Key, value)
            End Set
        End Property

        Private _future3Label As String = String.Empty
        Public Property Future3Label As String
            Get
                Return _future3Label
            End Get
            Set(value As String)
                SetProperty(_future3Label, value)
            End Set
        End Property

        Private _future3Username As String = String.Empty
        Public Property Future3Username As String
            Get
                Return _future3Username
            End Get
            Set(value As String)
                SetProperty(_future3Username, value)
            End Set
        End Property

        Private _future3Key As String = String.Empty
        Public Property Future3Key As String
            Get
                Return _future3Key
            End Get
            Set(value As String)
                SetProperty(_future3Key, value)
            End Set
        End Property

        Private _future4Label As String = String.Empty
        Public Property Future4Label As String
            Get
                Return _future4Label
            End Get
            Set(value As String)
                SetProperty(_future4Label, value)
            End Set
        End Property

        Private _future4Username As String = String.Empty
        Public Property Future4Username As String
            Get
                Return _future4Username
            End Get
            Set(value As String)
                SetProperty(_future4Username, value)
            End Set
        End Property

        Private _future4Key As String = String.Empty
        Public Property Future4Key As String
            Get
                Return _future4Key
            End Get
            Set(value As String)
                SetProperty(_future4Key, value)
            End Set
        End Property

        ' ── Show / hide toggle ────────────────────────────────────────────────────

        Private _showKeys As Boolean = False
        Public Property ShowKeys As Boolean
            Get
                Return _showKeys
            End Get
            Set(value As Boolean)
                SetProperty(_showKeys, value)
                OnPropertyChanged(NameOf(HideKeys))
                OnPropertyChanged(NameOf(ShowKeysLabel))
            End Set
        End Property

        ''' <summary>Inverse of ShowKeys — drives PasswordBox visibility.</summary>
        Public ReadOnly Property HideKeys As Boolean
            Get
                Return Not _showKeys
            End Get
        End Property

        Public ReadOnly Property ShowKeysLabel As String
            Get
                Return If(_showKeys, "🙈  Hide Keys", "👁  Show Keys")
            End Get
        End Property

        ' ── Status ───────────────────────────────────────────────────────────────

        Private _statusMessage As String = String.Empty
        Public Property StatusMessage As String
            Get
                Return _statusMessage
            End Get
            Set(value As String)
                SetProperty(_statusMessage, value)
            End Set
        End Property

        ' ── Commands ─────────────────────────────────────────────────────────────

        Public ReadOnly Property SaveCommand As RelayCommand
        Public ReadOnly Property ToggleShowKeysCommand As RelayCommand

        ' ── Constructor ──────────────────────────────────────────────────────────

        Public Sub New(store As IApiKeyStore)
            _store = store
            SaveCommand = New RelayCommand(AddressOf ExecuteSave)
            ToggleShowKeysCommand = New RelayCommand(Sub() ShowKeys = Not ShowKeys)
            LoadFromStore()
        End Sub

        ''' <summary>Reload all key values from the backing file.</summary>
        Public Sub LoadFromStore()
            Dim s = _store.Load()
            _activeBroker = If(String.IsNullOrEmpty(s.ActiveBroker), "etoro", s.ActiveBroker)
            _etoroKeyName = s.EtoroKeyName
            _etoroAccountMode = If(String.IsNullOrEmpty(s.EtoroAccountMode), "demo", s.EtoroAccountMode)
            _etoroApiKey = s.EtoroApiKey
            _etoroUserKey = s.EtoroUserKey
            _etoroLiveUserKey = s.EtoroLiveUserKey
            _topStepXUsername = s.TopStepXUsername
            _topStepXApiKey = s.TopStepXApiKey
            _claudeOrgId = s.ClaudeOrgId
            _claudeApiKey = s.ClaudeApiKey
            _binanceApiKey = s.BinanceApiKey
            _binanceSecretKey = s.BinanceSecretKey
            _future1Label = s.Future1Label
            _future1Username = s.Future1Username
            _future1Key = s.Future1Key
            _future2Label = s.Future2Label
            _future2Username = s.Future2Username
            _future2Key = s.Future2Key
            _future3Label = s.Future3Label
            _future3Username = s.Future3Username
            _future3Key = s.Future3Key
            _future4Label = s.Future4Label
            _future4Username = s.Future4Username
            _future4Key = s.Future4Key
            OnPropertyChanged(String.Empty)   ' notify all bound properties
        End Sub

        Private Sub ExecuteSave(param As Object)
            Try
                _store.Save(New ApiKeySettings With {
                    .ActiveBroker = _activeBroker,
                    .EtoroKeyName = _etoroKeyName,
                    .EtoroAccountMode = _etoroAccountMode,
                    .EtoroApiKey = _etoroApiKey,
                    .EtoroUserKey = _etoroUserKey,
                    .EtoroLiveUserKey = _etoroLiveUserKey,
                    .TopStepXUsername = _topStepXUsername,
                    .TopStepXApiKey = _topStepXApiKey,
                    .ClaudeOrgId = _claudeOrgId,
                    .ClaudeApiKey = _claudeApiKey,
                    .BinanceApiKey = _binanceApiKey,
                    .BinanceSecretKey = _binanceSecretKey,
                    .Future1Label = _future1Label,
                    .Future1Username = _future1Username,
                    .Future1Key = _future1Key,
                    .Future2Label = _future2Label,
                    .Future2Username = _future2Username,
                    .Future2Key = _future2Key,
                    .Future3Label = _future3Label,
                    .Future3Username = _future3Username,
                    .Future3Key = _future3Key,
                    .Future4Label = _future4Label,
                    .Future4Username = _future4Username,
                    .Future4Key = _future4Key
                })
                StatusMessage = $"✅  Keys saved  {DateTime.Now:HH:mm:ss}"
            Catch ex As Exception
                StatusMessage = $"⚠  Save failed: {ex.Message}"
            End Try
        End Sub

    End Class

End Namespace
