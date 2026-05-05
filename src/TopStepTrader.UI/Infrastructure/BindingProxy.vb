Imports System.Windows

Namespace TopStepTrader.UI.Infrastructure

    ''' <summary>
    ''' Allows DataGrid columns (which are not FrameworkElements) to bind to a ViewModel property
    ''' via a Freezable that inherits the DataContext.
    ''' Usage: add as a resource with Data="{Binding}", then reference via Source={StaticResource ...}
    ''' </summary>
    Public Class BindingProxy
        Inherits Freezable

        Protected Overrides Function CreateInstanceCore() As Freezable
            Return New BindingProxy()
        End Function

        Public Shared ReadOnly DataProperty As DependencyProperty =
            DependencyProperty.Register(NameOf(Data), GetType(Object), GetType(BindingProxy),
                                        New UIPropertyMetadata(Nothing))

        Public Property Data As Object
            Get
                Return GetValue(DataProperty)
            End Get
            Set(value As Object)
                SetValue(DataProperty, value)
            End Set
        End Property

    End Class

End Namespace
