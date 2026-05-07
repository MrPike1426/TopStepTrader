Namespace TopStepTrader.Core.Settings

    ''' <summary>
    ''' Configuration for ScalperExitManager — the reusable scalping TP/exit
    ''' management service. Strategy-neutral: any view that opens a short-term
    ''' position can hand its slot off to the Scalper for exit management.
    ''' All thresholds here are independent of SuperTrendPlusConfig so the
    ''' scalping ladder can be tuned without affecting other strategies.
    ''' </summary>
    Public Class ScalperConfig

        ''' <summary>Profit (in R) at which the stop snaps to breakeven (entry). Default 0.5R.</summary>
        Public Property BreakevenTriggerR As Decimal = 0.5D

        ''' <summary>Profit (in R) at which the stop transitions to the BB-band trail. Default 1.5R.</summary>
        Public Property ProfitLockTriggerR As Decimal = 1.5D

        ''' <summary>Bollinger Bands period (15s timeframe). Default 10.</summary>
        Public Property BBLength As Integer = 10

        ''' <summary>BB std-dev multiplier in normal mode. Default 2.0.</summary>
        Public Property BBMultNormal As Decimal = 2D

        ''' <summary>BB std-dev multiplier after the ScaredyCat one-way trigger fires. Default 1.5.</summary>
        Public Property BBMultCautious As Decimal = 1.5D

        ''' <summary>Number of consecutive 15s bars the BB middle must move against the trade
        ''' direction before the ScaredyCat trigger arms (combined with a disagreeing 15s ST). Default 8.</summary>
        Public Property ScaredyLookbackBars As Integer = 8

    End Class

End Namespace
