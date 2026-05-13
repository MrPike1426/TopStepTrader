Imports System.Windows
Imports System.Windows.Controls
Imports TopStepTrader.Core.Settings

Namespace TopStepTrader.UI.Controls

    ''' <summary>
    ''' Re-usable P&amp;L Guard selector control. Drop into any view that needs
    ''' a Take-Profit / Max-Loss dollar override on top of the trade-management
    ''' engine. The host view-model exposes a <see cref="PnLGuardSettings"/>
    ''' instance and binds it to the <see cref="Settings"/> dependency property.
    ''' </summary>
    Partial Public Class PnLGuardControl
        Inherits UserControl

        Public Shared ReadOnly SettingsProperty As DependencyProperty =
            DependencyProperty.Register(NameOf(Settings),
                                        GetType(PnLGuardSettings),
                                        GetType(PnLGuardControl),
                                        New PropertyMetadata(Nothing, AddressOf OnSettingsChanged))

        Public Property Settings As PnLGuardSettings
            Get
                Return DirectCast(GetValue(SettingsProperty), PnLGuardSettings)
            End Get
            Set(value As PnLGuardSettings)
                SetValue(SettingsProperty, value)
            End Set
        End Property

        Private _suppressUpdates As Boolean

        Public Sub New()
            InitializeComponent()
            PopulateCombo(TakeProfitCombo)
            PopulateCombo(StopLossCombo)
        End Sub

        Private Shared Sub PopulateCombo(combo As ComboBox)
            combo.Items.Clear()
            For Each opt In PnLGuardSettings.AllThresholds
                combo.Items.Add(New ComboBoxItem With {
                    .Content = FormatThreshold(opt),
                    .Tag = opt
                })
            Next
        End Sub

        Private Shared Function FormatThreshold(t As PnLGuardThreshold) As String
            If t = PnLGuardThreshold.Off Then Return "Off"
            Return $"${CInt(t)}"
        End Function

        Private Shared Sub OnSettingsChanged(d As DependencyObject, e As DependencyPropertyChangedEventArgs)
            Dim ctl = TryCast(d, PnLGuardControl)
            If ctl Is Nothing Then Return
            ctl.SyncFromSettings()
        End Sub

        Private Sub SyncFromSettings()
            Dim s = Settings
            If s Is Nothing Then Return
            _suppressUpdates = True
            Try
                SelectThreshold(TakeProfitCombo, s.TakeProfitThreshold)
                SelectThreshold(StopLossCombo, s.StopLossThreshold)
            Finally
                _suppressUpdates = False
            End Try
        End Sub

        Private Shared Sub SelectThreshold(combo As ComboBox, value As PnLGuardThreshold)
            For Each obj In combo.Items
                Dim item = TryCast(obj, ComboBoxItem)
                If item IsNot Nothing AndAlso item.Tag IsNot Nothing AndAlso
                   CType(item.Tag, PnLGuardThreshold) = value Then
                    combo.SelectedItem = item
                    Return
                End If
            Next
        End Sub

        Private Function ReadSelected(combo As ComboBox) As PnLGuardThreshold
            Dim item = TryCast(combo.SelectedItem, ComboBoxItem)
            If item Is Nothing OrElse item.Tag Is Nothing Then Return PnLGuardThreshold.Off
            Return CType(item.Tag, PnLGuardThreshold)
        End Function

        Private Sub OnTakeProfitChanged(sender As Object, e As SelectionChangedEventArgs)
            If _suppressUpdates OrElse Settings Is Nothing Then Return
            Settings.TakeProfitThreshold = ReadSelected(TakeProfitCombo)
        End Sub

        Private Sub OnStopLossChanged(sender As Object, e As SelectionChangedEventArgs)
            If _suppressUpdates OrElse Settings Is Nothing Then Return
            Settings.StopLossThreshold = ReadSelected(StopLossCombo)
        End Sub

    End Class

End Namespace
