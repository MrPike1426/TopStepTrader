Imports System.Globalization
Imports System.Windows
Imports System.Windows.Data
Imports System.Windows.Media

Namespace TopStepTrader.UI.Infrastructure

    ''' <summary>
    ''' Converts a string resource-key like "BuyBrush" to the actual SolidColorBrush
    ''' defined in Colors.xaml. Used so ViewModels can stay type-safe strings
    ''' while XAML renderers resolve the actual brush.
    ''' </summary>
    Public Class BrushKeyConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type,
                                parameter As Object, culture As CultureInfo) As Object _
            Implements IValueConverter.Convert
            If value Is Nothing Then Return Brushes.Transparent
            Dim key = value.ToString()
            Dim brush = Application.Current?.TryFindResource(key)
            Return If(TypeOf brush Is Brush, CType(brush, Brush), Brushes.Transparent)
        End Function

        Public Function ConvertBack(value As Object, targetType As Type,
                                    parameter As Object, culture As CultureInfo) As Object _
            Implements IValueConverter.ConvertBack
            Throw New NotSupportedException()
        End Function
    End Class

    ''' <summary>
    ''' Converts a Boolean to Visibility (True → Visible, False → Collapsed).
    ''' </summary>
    Public Class BoolToVisibilityConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type,
                                parameter As Object, culture As CultureInfo) As Object _
            Implements IValueConverter.Convert
            If TypeOf value Is Boolean Then
                Return If(CBool(value), Visibility.Visible, Visibility.Collapsed)
            End If
            Return Visibility.Collapsed
        End Function

        Public Function ConvertBack(value As Object, targetType As Type,
                                    parameter As Object, culture As CultureInfo) As Object _
            Implements IValueConverter.ConvertBack
            Return Equals(value, Visibility.Visible)
        End Function
    End Class

    ''' <summary>
    ''' Converts a Boolean to a BorderThickness — used to highlight the
    ''' auto-execution panel when enabled (True → Thickness 2, False → 0).
    ''' </summary>
    Public Class BoolToBorderThicknessConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type,
                                parameter As Object, culture As CultureInfo) As Object _
            Implements IValueConverter.Convert
            If TypeOf value Is Boolean AndAlso CBool(value) Then
                Return New Thickness(2)
            End If
            Return New Thickness(0)
        End Function

        Public Function ConvertBack(value As Object, targetType As Type,
                                    parameter As Object, culture As CultureInfo) As Object _
            Implements IValueConverter.ConvertBack
            Throw New NotSupportedException()
        End Function
    End Class

End Namespace
