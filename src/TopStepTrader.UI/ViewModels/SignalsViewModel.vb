Imports System.Collections.ObjectModel
Imports System.Windows
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' Displays the stream of AI trade signals — live via event + historical refresh.
    ''' </summary>
    Public Class SignalsViewModel
        Inherits ViewModelBase

        Private ReadOnly _signalService As ISignalService

        ' ── Bindable properties ──────────────────────────────────────────────

        Public ReadOnly Property Signals As New ObservableCollection(Of SignalRowVm)()

        Private _selectedSignal As SignalRowVm
        Public Property SelectedSignal As SignalRowVm
            Get
                Return _selectedSignal
            End Get
            Set(value As SignalRowVm)
                SetProperty(_selectedSignal, value)
            End Set
        End Property

        Private _filterContractId As String = ""
        Public Property FilterContractId As String
            Get
                Return _filterContractId
            End Get
            Set(value As String)
                SetProperty(_filterContractId, value)
            End Set
        End Property

        Private _statusText As String = "Loading signals..."
        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Set(value As String)
                SetProperty(_statusText, value)
            End Set
        End Property

        ' ── Commands ─────────────────────────────────────────────────────────

        Public ReadOnly Property RefreshCommand As RelayCommand

        ' ── Constructor ──────────────────────────────────────────────────────

        Public Sub New(signalService As ISignalService)
            _signalService = signalService
            RefreshCommand = New RelayCommand(AddressOf LoadHistory)
            AddHandler _signalService.SignalGenerated, AddressOf OnSignalGenerated
        End Sub

        Public Sub LoadDataAsync()
            LoadHistory()
        End Sub

        Private Sub LoadHistory()
            Task.Run(Async Function()
                         Try
                             Dim from = DateTime.Today.AddDays(-7)
                             Dim [to] = DateTime.Now
                             Dim id As Integer
                             Integer.TryParse(_filterContractId, id)
                             If id = 0 Then id = -1  ' use -1 as "all" sentinel

                             Dim fetched = Await _signalService.GetSignalHistoryAsync(
                                 If(id > 0, id, 0), from, [to])
                             Dim rows = fetched.OrderByDescending(Function(x) x.GeneratedAt).ToList()
                             Dispatch(Sub()
                                          Signals.Clear()
                                          For Each s In rows
                                              Signals.Add(New SignalRowVm(s))
                                          Next
                                          StatusText = $"{Signals.Count} signals (last 7 days)"
                                      End Sub)
                         Catch ex As Exception
                             Dispatch(Sub() StatusText = $"Error: {ex.Message}")
                         End Try
                     End Function)
        End Sub

        Private Sub OnSignalGenerated(sender As Object, e As SignalGeneratedEventArgs)
            Dispatch(Sub() Signals.Insert(0, New SignalRowVm(e.Signal)))
        End Sub

        Private Sub Dispatch(action As Action)
            If Application.Current?.Dispatcher IsNot Nothing Then
                Application.Current.Dispatcher.Invoke(action)
            End If
        End Sub

    End Class

    ''' <summary>View-friendly wrapper around TradeSignal.</summary>
    Public Class SignalRowVm
        Public Property Time          As String
        Public Property ContractId    As Integer
        Public Property SignalType    As String
        Public Property Confidence    As String
        Public Property ModelVersion  As String
        Public Property EntryPrice    As String
        Public Property StopLoss      As String
        Public Property TakeProfit    As String

        Public ReadOnly Property SignalColor As String
            Get
                Select Case SignalType
                    Case "Buy"  : Return "BuyBrush"
                    Case "Sell" : Return "SellBrush"
                    Case Else   : Return "TextSecondaryBrush"
                End Select
            End Get
        End Property

        Public Sub New(s As TradeSignal)
            Time         = s.GeneratedAt.LocalDateTime.ToString("MM/dd HH:mm:ss")
            ContractId   = s.ContractId
            SignalType   = s.SignalType.ToString()
            Confidence   = s.Confidence.ToString("P0")
            ModelVersion = s.ModelVersion
            EntryPrice   = If(s.SuggestedEntryPrice.HasValue, s.SuggestedEntryPrice.Value.ToString("F2"), "—")
            StopLoss     = If(s.SuggestedStopLoss.HasValue,   s.SuggestedStopLoss.Value.ToString("F2"),   "—")
            TakeProfit   = If(s.SuggestedTakeProfit.HasValue, s.SuggestedTakeProfit.Value.ToString("F2"), "—")
        End Sub
    End Class

End Namespace
