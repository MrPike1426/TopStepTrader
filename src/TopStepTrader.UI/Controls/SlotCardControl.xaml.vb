Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input

Namespace TopStepTrader.UI.Controls

    ''' <summary>
    ''' Reusable position slot card.
    ''' Bind DataContext to a SlotBoxVm; pass the AI check command via AiCommand DP.
    ''' </summary>
    Partial Public Class SlotCardControl
        Inherits UserControl

        Public Shared ReadOnly AiCommandProperty As DependencyProperty =
            DependencyProperty.Register(NameOf(AiCommand), GetType(ICommand),
                                        GetType(SlotCardControl), New PropertyMetadata(Nothing))

        ''' <summary>The per-slot AI sense-check command (e.g. AiCheckSlot1Command).</summary>
        Public Property AiCommand As ICommand
            Get
                Return DirectCast(GetValue(AiCommandProperty), ICommand)
            End Get
            Set(value As ICommand)
                SetValue(AiCommandProperty, value)
            End Set
        End Property

        Public Sub New()
            InitializeComponent()
        End Sub

    End Class

End Namespace
