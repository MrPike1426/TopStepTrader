Imports System.Globalization
Imports System.Windows
Imports System.Windows.Data

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
            Dim key = TryCast(value, String)
            If String.IsNullOrEmpty(key) Then Return Nothing
            Dim app = Application.Current
            If app Is Nothing Then Return Nothing
            If app.Resources.Contains(key) Then
                Return app.Resources(key)
            End If
            Return Nothing
        End Function

        Public Function ConvertBack(value As Object, targetType As Type,
                                    parameter As Object, culture As CultureInfo) As Object _
            Implements IValueConverter.ConvertBack
            Throw New NotSupportedException()
        End Function
    End Class

    ''' <summary>
    ''' Inverts a boolean value. Useful for binding the "No" radio button to
    ''' the same boolean source as the "Yes" radio button.
    ''' </summary>
    Public Class BoolInverseConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type,
                                parameter As Object, culture As CultureInfo) As Object _
            Implements IValueConverter.Convert
            Dim b = False
            If TypeOf value Is Boolean Then b = CType(value, Boolean)
            Return Not b
        End Function

        Public Function ConvertBack(value As Object, targetType As Type,
                                    parameter As Object, culture As CultureInfo) As Object _
            Implements IValueConverter.ConvertBack
            Dim b = False
            If TypeOf value Is Boolean Then b = CType(value, Boolean)
            Return Not b
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
            Dim b = False
            If TypeOf value Is Boolean Then b = CType(value, Boolean)
            Return If(b, Visibility.Visible, Visibility.Collapsed)
        End Function

        Public Function ConvertBack(value As Object, targetType As Type,
                                    parameter As Object, culture As CultureInfo) As Object _
            Implements IValueConverter.ConvertBack
            Throw New NotSupportedException()
        End Function
    End Class

    ''' <summary>
    ''' Converts a Boolean to Visibility (True → Collapsed, False → Visible).
    ''' Inverse of BoolToVisibilityConverter.
    ''' </summary>
    Public Class InverseBoolToVisibilityConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type,
                                parameter As Object, culture As CultureInfo) As Object _
            Implements IValueConverter.Convert
            Dim b = False
            If TypeOf value Is Boolean Then b = CType(value, Boolean)
            Return If(b, Visibility.Collapsed, Visibility.Visible)
        End Function

        Public Function ConvertBack(value As Object, targetType As Type,
                                    parameter As Object, culture As CultureInfo) As Object _
            Implements IValueConverter.ConvertBack
            Throw New NotSupportedException()
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
            Dim b = False
            If TypeOf value Is Boolean Then b = CType(value, Boolean)
            Return If(b, New Thickness(2), New Thickness(1))
        End Function

        Public Function ConvertBack(value As Object, targetType As Type,
                                    parameter As Object, culture As CultureInfo) As Object _
            Implements IValueConverter.ConvertBack
            Throw New NotSupportedException()
        End Function
    End Class

    ''' <summary>
    ''' Converts a String to Visibility.
    ''' ConverterParameter="NotEmpty" → Visible when string is non-null/non-whitespace, otherwise Collapsed.
    ''' No parameter → Visible when string is null/whitespace (inverse).
    ''' ConvertBack is not supported (one-way binding only).
    ''' </summary>
    Public Class StringToVisibilityConverter
        Implements IValueConverter

        Public Function Convert(value As Object, targetType As Type,
                                parameter As Object, culture As CultureInfo) As Object _
            Implements IValueConverter.Convert
            Dim s = TryCast(value, String)
            Dim notEmpty = Not String.IsNullOrWhiteSpace(s)
            Dim wantNotEmpty = String.Equals(TryCast(parameter, String), "NotEmpty",
                                             StringComparison.OrdinalIgnoreCase)
            Return If(If(wantNotEmpty, notEmpty, Not notEmpty), Visibility.Visible, Visibility.Collapsed)
        End Function

        Public Function ConvertBack(value As Object, targetType As Type,
                                    parameter As Object, culture As CultureInfo) As Object _
            Implements IValueConverter.ConvertBack
            Throw New NotSupportedException()
        End Function
    End Class

End Namespace
