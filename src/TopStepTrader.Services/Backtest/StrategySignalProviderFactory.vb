Imports TopStepTrader.Core.Enums
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Services.Backtest.Strategies

Namespace TopStepTrader.Services.Backtest

    ''' <summary>
    ''' Creates the appropriate <see cref="IStrategySignalProvider"/> for a given strategy condition.
    ''' Each ARCH-01 sub-ticket populates one or more Case branches below.
    ''' Unimplemented strategies throw <see cref="NotImplementedException"/> until their ticket is complete.
    ''' </summary>
    Public Class StrategySignalProviderFactory

        Public Shared Function Create(condition As StrategyConditionType) As IStrategySignalProvider
            Select Case condition
                ' ARCH-01b
                Case StrategyConditionType.EmaRsiWeightedScore : Return New EmaRsiSignalProvider()
                Case StrategyConditionType.MultiConfluence      : Return New MultiConfluenceSignalProvider()
                ' ARCH-01c
                Case StrategyConditionType.TripleEmaCascade     : Return New TripleEmaCascadeSignalProvider()
                Case StrategyConditionType.BbSqueezeScalper     : Return New BbSqueezeSignalProvider()
                Case StrategyConditionType.LultDivergence       : Return New LultDivergenceSignalProvider()
                Case StrategyConditionType.VidyaCross           : Return New VidyaCrossSignalProvider()
                Case StrategyConditionType.NakedTrader          : Return New NakedTraderSignalProvider()
                Case StrategyConditionType.DoubleBubbleButt     : Return New DoubleBubbleButtSignalProvider()
                ' STRAT-29
                Case StrategyConditionType.OpeningRangeBreakout : Return New OrbSignalProvider()
                ' TEST-10
                Case StrategyConditionType.PumpNDump : Return New PumpNDumpSignalProvider()
                ' STRAT-31
                Case StrategyConditionType.VwapMeanReversion : Return New VwapMeanReversionSignalProvider()
                ' FEAT-19
                Case StrategyConditionType.SuperTrendAdx : Return New SuperTrendAdxSignalProvider()
                ' FEAT-35
                Case StrategyConditionType.SuperTrendPlus : Return New SuperTrendPlusSignalProvider()
                Case Else
                    Throw New NotImplementedException(
                        $"No signal provider implemented for strategy '{condition}'.")
            End Select
        End Function

    End Class

End Namespace
