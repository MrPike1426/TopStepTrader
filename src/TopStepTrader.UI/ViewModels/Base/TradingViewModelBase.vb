Imports System.Collections.ObjectModel
Imports TopStepTrader.Core.Models
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels.Base

    ''' <summary>
    ''' Base class for trading ViewModels that have accounts and need to sync with dashboard.
    ''' </summary>
    Public MustInherit Class TradingViewModelBase
        Inherits ViewModelBase

        ' ── Accounts ──────────────────────────────────────────────────────────────
        Public Property Accounts As New ObservableCollection(Of Account)

        Protected _selectedAccount As Account
        Public Property SelectedAccount As Account
            Get
                Return _selectedAccount
            End Get
            Set(value As Account)
                SetProperty(_selectedAccount, value)
                NotifyPropertyChanged(NameOf(IsFormReady))
                RelayCommand.RaiseCanExecuteChanged()
            End Set
        End Property

        ''' <summary>
        ''' Override in derived classes to indicate if the form is ready (e.g., account selected).
        ''' </summary>
        Public MustOverride ReadOnly Property IsFormReady As Boolean

    End Class

End Namespace