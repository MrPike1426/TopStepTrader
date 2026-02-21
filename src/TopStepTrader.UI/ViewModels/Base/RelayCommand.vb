Imports System.Windows.Input

Namespace TopStepTrader.UI.ViewModels.Base

    ''' <summary>
    ''' Basic ICommand implementation backed by delegate functions.
    ''' Thread-safe CanExecuteChanged via CommandManager.
    ''' </summary>
    Public Class RelayCommand
        Implements ICommand

        Private ReadOnly _execute    As Action(Of Object)
        Private ReadOnly _canExecute As Func(Of Object, Boolean)

        Public Sub New(execute As Action(Of Object),
                       Optional canExecute As Func(Of Object, Boolean) = Nothing)
            _execute = execute
            _canExecute = canExecute
        End Sub

        ''' <summary>Shortcut constructor for no-parameter commands.</summary>
        Public Sub New(execute As Action, Optional canExecute As Func(Of Boolean) = Nothing)
            _execute = Sub(p) execute()
            If canExecute IsNot Nothing Then
                _canExecute = Function(p) canExecute()
            End If
        End Sub

        Public Function CanExecute(parameter As Object) As Boolean _
            Implements ICommand.CanExecute
            Return _canExecute Is Nothing OrElse _canExecute(parameter)
        End Function

        Public Sub Execute(parameter As Object) _
            Implements ICommand.Execute
            _execute(parameter)
        End Sub

        Public Custom Event CanExecuteChanged As EventHandler _
            Implements ICommand.CanExecuteChanged
            AddHandler(value As EventHandler)
                AddHandler System.Windows.Input.CommandManager.RequerySuggested, value
            End AddHandler
            RemoveHandler(value As EventHandler)
                RemoveHandler System.Windows.Input.CommandManager.RequerySuggested, value
            End RemoveHandler
            RaiseEvent(sender As Object, e As EventArgs)
                System.Windows.Input.CommandManager.InvalidateRequerySuggested()
            End RaiseEvent
        End Event

        ''' <summary>Force WPF to re-query CanExecute on all commands.</summary>
        Public Shared Sub RaiseCanExecuteChanged()
            System.Windows.Input.CommandManager.InvalidateRequerySuggested()
        End Sub

    End Class

    ''' <summary>Strongly-typed version of RelayCommand(Of T).</summary>
    Public Class RelayCommand(Of T)
        Implements ICommand

        Private ReadOnly _execute    As Action(Of T)
        Private ReadOnly _canExecute As Func(Of T, Boolean)

        Public Sub New(execute As Action(Of T),
                       Optional canExecute As Func(Of T, Boolean) = Nothing)
            _execute = execute
            _canExecute = canExecute
        End Sub

        Public Function CanExecute(parameter As Object) As Boolean _
            Implements ICommand.CanExecute
            Dim typed = If(TypeOf parameter Is T, CType(parameter, T), Nothing)
            Return _canExecute Is Nothing OrElse _canExecute(typed)
        End Function

        Public Sub Execute(parameter As Object) _
            Implements ICommand.Execute
            Dim typed = If(TypeOf parameter Is T, CType(parameter, T), Nothing)
            _execute(typed)
        End Sub

        Public Custom Event CanExecuteChanged As EventHandler _
            Implements ICommand.CanExecuteChanged
            AddHandler(value As EventHandler)
                AddHandler System.Windows.Input.CommandManager.RequerySuggested, value
            End AddHandler
            RemoveHandler(value As EventHandler)
                RemoveHandler System.Windows.Input.CommandManager.RequerySuggested, value
            End RemoveHandler
            RaiseEvent(sender As Object, e As EventArgs)
                System.Windows.Input.CommandManager.InvalidateRequerySuggested()
            End RaiseEvent
        End Event

    End Class

End Namespace
