Imports System.ComponentModel
Imports System.Runtime.CompilerServices

Namespace TopStepTrader.UI.ViewModels.Base

    ''' <summary>
    ''' Base class for all ViewModels. Implements INotifyPropertyChanged and
    ''' provides a SetProperty helper for compact property declarations.
    ''' </summary>
    Public MustInherit Class ViewModelBase
        Implements INotifyPropertyChanged

        Public Event PropertyChanged As PropertyChangedEventHandler _
            Implements INotifyPropertyChanged.PropertyChanged

        Protected Sub OnPropertyChanged(<CallerMemberName> Optional propertyName As String = Nothing)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(propertyName))
        End Sub

        ''' <summary>
        ''' Sets <paramref name="field"/> to <paramref name="value"/> and raises
        ''' PropertyChanged when the value actually changes.
        ''' </summary>
        ''' <returns>True if the value changed, False otherwise.</returns>
        Protected Function SetProperty(Of T)(ByRef field As T,
                                             value As T,
                                             <CallerMemberName> Optional propertyName As String = Nothing) As Boolean
            If Object.Equals(field, value) Then Return False
            field = value
            OnPropertyChanged(propertyName)
            Return True
        End Function

        ''' <summary>Raise PropertyChanged for the given name explicitly.</summary>
        Protected Sub NotifyPropertyChanged(propertyName As String)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(propertyName))
        End Sub

    End Class

End Namespace
