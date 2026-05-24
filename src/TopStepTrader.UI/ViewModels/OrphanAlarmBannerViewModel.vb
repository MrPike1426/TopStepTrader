Imports System.Windows
Imports System.Windows.Input
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Events
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Services.Background
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' BUG-94 F4: banner shown at the top of <see cref="MainWindow"/> whenever
    ''' <see cref="TradeReconciliationWorker.OrphanPositionDetected"/> fires.
    '''
    ''' Singleton — the worker is a singleton hosted service and this VM owns the
    ''' subscription for the app's lifetime so banners survive view navigation.
    ''' </summary>
    Public Class OrphanAlarmBannerViewModel
        Inherits ViewModelBase

        Private ReadOnly _worker As TradeReconciliationWorker
        Private ReadOnly _orderService As IOrderService
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _logger As ILogger(Of OrphanAlarmBannerViewModel)

        Private _isActive As Boolean
        Private _bannerTitle As String = String.Empty
        Private _bannerDetail As String = String.Empty
        Private _autoSlLine As String = String.Empty
        Private _currentContractId As String = String.Empty

        Public Sub New(worker As TradeReconciliationWorker,
                       orderService As IOrderService,
                       session As ITradingSessionContext,
                       logger As ILogger(Of OrphanAlarmBannerViewModel))
            _worker = worker
            _orderService = orderService
            _session = session
            _logger = logger
            FlattenCommand = New RelayCommand(AddressOf OnFlatten, Function() _isActive AndAlso Not String.IsNullOrEmpty(_currentContractId))
            DismissCommand = New RelayCommand(AddressOf OnDismiss, Function() _isActive)
            AddHandler _worker.OrphanPositionDetected, AddressOf OnOrphanDetected
        End Sub

        Public ReadOnly Property FlattenCommand As ICommand
        Public ReadOnly Property DismissCommand As ICommand

        Public Property IsActive As Boolean
            Get
                Return _isActive
            End Get
            Set(value As Boolean)
                SetProperty(_isActive, value)
            End Set
        End Property

        Public Property BannerTitle As String
            Get
                Return _bannerTitle
            End Get
            Set(value As String)
                SetProperty(_bannerTitle, value)
            End Set
        End Property

        Public Property BannerDetail As String
            Get
                Return _bannerDetail
            End Get
            Set(value As String)
                SetProperty(_bannerDetail, value)
            End Set
        End Property

        Public Property AutoSlLine As String
            Get
                Return _autoSlLine
            End Get
            Set(value As String)
                SetProperty(_autoSlLine, value)
            End Set
        End Property

        Private Sub OnOrphanDetected(sender As Object, args As OrphanPositionDetectedEventArgs)
            If args Is Nothing Then Return
            Dim disp = Application.Current?.Dispatcher
            If disp IsNot Nothing AndAlso Not disp.CheckAccess() Then
                disp.BeginInvoke(New Action(Sub() ApplyAlarm(args)))
            Else
                ApplyAlarm(args)
            End If
        End Sub

        Private Sub ApplyAlarm(args As OrphanPositionDetectedEventArgs)
            Dim ageMin = Math.Round(args.Age.TotalMinutes, 1)
            BannerTitle = $"⚠ Orphan position: {args.Symbol} {args.Side} {args.Size}"
            BannerDetail = $"Open P&L {args.OpenPnLUsd:C}  ·  age {ageMin} min  ·  positionId={args.PositionId}"
            If args.AutoSlApplied Then
                AutoSlLine = $"Auto-SL placed @ {args.AutoSlPriceApplied}"
            ElseIf Not String.IsNullOrEmpty(args.AutoSlSkippedReason) Then
                AutoSlLine = $"No auto-SL applied ({args.AutoSlSkippedReason})"
            Else
                AutoSlLine = String.Empty
            End If
            _currentContractId = args.ContractId
            IsActive = True
            RelayCommand.RaiseCanExecuteChanged()
        End Sub

        Private Sub OnDismiss()
            IsActive = False
            _currentContractId = String.Empty
            RelayCommand.RaiseCanExecuteChanged()
        End Sub

        Private Async Sub OnFlatten()
            Dim contractId = _currentContractId
            Dim accountId As Long = If(_session?.SelectedAccount?.Id, 0L)
            If accountId = 0 OrElse String.IsNullOrEmpty(contractId) Then
                IsActive = False
                Return
            End If
            Try
                Await _orderService.FlattenContractAsync(accountId, contractId)
            Catch ex As Exception
                _logger.LogWarning(ex, "OrphanAlarmBanner: FlattenContractAsync failed for {Contract}", contractId)
            End Try
            IsActive = False
            _currentContractId = String.Empty
            RelayCommand.RaiseCanExecuteChanged()
        End Sub

    End Class

End Namespace
