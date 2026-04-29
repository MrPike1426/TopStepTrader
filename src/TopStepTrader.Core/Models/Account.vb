Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.Core.Models

    Public Class Account
        Public Property Id As Long
        Public Property Name As String = String.Empty
        Public Property Balance As Decimal
        ''' <summary>Total portfolio value: available cash + invested amount + unrealised P&L.</summary>
        Public Property TotalValue As Decimal
        Public Property CanTrade As Boolean
        Public Property IsVisible As Boolean
        Public Property StartingBalance As Decimal
        ''' <summary>True when this is a live/funded account (not simulated/paper).</summary>
        Public Property IsLive As Boolean
        ''' <summary>Which broker this account belongs to.</summary>
        Public Property Broker As BrokerType

        ''' <summary>Display string for the dropdown: "Name (Broker)".</summary>
        Public ReadOnly Property DisplayName As String
            Get
                Return If(String.IsNullOrEmpty(Name), "TopStepX", $"{Name} (TopStepX)")
            End Get
        End Property

        Public Overrides Function ToString() As String
            Return DisplayName
        End Function
    End Class

End Namespace
