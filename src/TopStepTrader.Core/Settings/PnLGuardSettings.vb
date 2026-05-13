Imports System.Collections.Generic
Imports System.ComponentModel
Imports System.Runtime.CompilerServices

Namespace TopStepTrader.Core.Settings

    ''' <summary>
    ''' Dollar threshold options for the re-usable P&amp;L Guard.
    ''' Used to drive a flat-the-trade override that sits on top of the
    ''' normal trade-management engine.
    ''' </summary>
    Public Enum PnLGuardThreshold
        [Off] = 0
        D25 = 25
        D50 = 50
        D75 = 75
        D100 = 100
    End Enum

    Public Module PnLGuardThresholdExtensions
        ''' <summary>
        ''' Returns the dollar value of the threshold, or Nothing when set to Off.
        ''' </summary>
        <Extension>
        Public Function ToDollars(value As PnLGuardThreshold) As Decimal?
            If value = PnLGuardThreshold.Off Then Return Nothing
            Return CDec(CInt(value))
        End Function
    End Module

    ''' <summary>
    ''' Re-usable settings object for the P&amp;L Guard feature. Bind a single
    ''' instance to <see cref="PnLGuardControl"/> in the UI and pass the same
    ''' instance to any service that needs to enforce the override.
    '''
    ''' Behaviour:
    ''' • If unrealised P&amp;L &gt;= TakeProfit dollar value → flatten with reason "P&amp;L Close".
    ''' • If unrealised P&amp;L &lt;= -StopLoss dollar value → flatten with reason "P&amp;L Close".
    ''' • If both thresholds are Off, the guard is a no-op (engine rules apply).
    '''
    ''' Defaults: TP = $50, SL = $100. Per-session only — not persisted.
    ''' </summary>
    Public Class PnLGuardSettings
        Implements INotifyPropertyChanged

        Public Const ExitReasonText As String = "P&L Close"

        Public Event PropertyChanged As PropertyChangedEventHandler _
            Implements INotifyPropertyChanged.PropertyChanged

        Private _takeProfit As PnLGuardThreshold = PnLGuardThreshold.D50
        Public Property TakeProfitThreshold As PnLGuardThreshold
            Get
                Return _takeProfit
            End Get
            Set(value As PnLGuardThreshold)
                If _takeProfit <> value Then
                    _takeProfit = value
                    Raise(NameOf(TakeProfitThreshold))
                End If
            End Set
        End Property

        Private _stopLoss As PnLGuardThreshold = PnLGuardThreshold.D100
        Public Property StopLossThreshold As PnLGuardThreshold
            Get
                Return _stopLoss
            End Get
            Set(value As PnLGuardThreshold)
                If _stopLoss <> value Then
                    _stopLoss = value
                    Raise(NameOf(StopLossThreshold))
                End If
            End Set
        End Property

        ''' <summary>True if either threshold is set (i.e. the guard is active).</summary>
        Public ReadOnly Property IsActive As Boolean
            Get
                Return _takeProfit <> PnLGuardThreshold.Off OrElse _stopLoss <> PnLGuardThreshold.Off
            End Get
        End Property

        ''' <summary>
        ''' Evaluates the supplied unrealised P&amp;L against the configured thresholds.
        ''' Returns True when the guard requires the trade to be flattened.
        ''' </summary>
        Public Function ShouldFlatten(unrealisedPnl As Decimal) As Boolean
            Dim tp = _takeProfit.ToDollars()
            If tp.HasValue AndAlso unrealisedPnl >= tp.Value Then Return True

            Dim sl = _stopLoss.ToDollars()
            If sl.HasValue AndAlso unrealisedPnl <= -sl.Value Then Return True

            Return False
        End Function

        ''' <summary>
        ''' All threshold options exposed as a collection for ComboBox binding.
        ''' </summary>
        Public Shared ReadOnly Property AllThresholds As IReadOnlyList(Of PnLGuardThreshold) =
            New PnLGuardThreshold() {
                PnLGuardThreshold.Off,
                PnLGuardThreshold.D25,
                PnLGuardThreshold.D50,
                PnLGuardThreshold.D75,
                PnLGuardThreshold.D100
            }

        Private Sub Raise(name As String)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(name))
        End Sub

    End Class

End Namespace
