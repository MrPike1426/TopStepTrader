Namespace TopStepTrader.Services.Backtest

    ''' <summary>
    ''' Market regime classification used by STRAT-30 to route Hydra/AssetBassett engines
    ''' to the appropriate strategy on each bar.
    ''' </summary>
    Public Enum RegimeType
        ''' <summary>Expanding volatility + ADX above threshold — trend-following strategies preferred.</summary>
        Trending
        ''' <summary>Contracting volatility or ADX below threshold — mean-reversion strategies preferred.</summary>
        Ranging
    End Enum

    ''' <summary>
    ''' Classifies the current market regime from ATR and ADX series.
    ''' Trending = current ATR ≥ its 20-bar SMA (expanding volatility) AND ADX ≥ threshold.
    ''' Ranging  = otherwise.
    ''' </summary>
    Public NotInheritable Class RegimeClassifier

        Private Sub New()
        End Sub

        ''' <summary>
        ''' Classify the current regime using the full ATR series and the current ADX reading.
        ''' </summary>
        ''' <param name="atrSeries">Full ATR(14) series from TechnicalIndicators.ATR — NaN during warm-up.</param>
        ''' <param name="adxValue">Current ADX(14) value (NaN if not yet available).</param>
        ''' <param name="adxThreshold">Persona-driven minimum ADX for a trending market.</param>
        Public Shared Function Classify(atrSeries As Single(),
                                        adxValue As Single,
                                        adxThreshold As Single) As RegimeType
            If atrSeries Is Nothing OrElse Single.IsNaN(adxValue) Then Return RegimeType.Ranging

            ' Collect the last 20 valid ATR values for the SMA
            Dim validAtr As New List(Of Single)(20)
            For i = atrSeries.Length - 1 To 0 Step -1
                If Not Single.IsNaN(atrSeries(i)) Then
                    validAtr.Add(atrSeries(i))
                    If validAtr.Count = 20 Then Exit For
                End If
            Next

            If validAtr.Count < 5 Then Return RegimeType.Ranging   ' insufficient warm-up

            Dim atrSma20 = CSng(validAtr.Average())
            Dim atrNow = validAtr(0)   ' most recent (iteration was newest-first)

            Dim expanding = (atrNow >= atrSma20)
            Dim strongTrend = (adxValue >= adxThreshold)

            Return If(expanding AndAlso strongTrend, RegimeType.Trending, RegimeType.Ranging)
        End Function

    End Class

End Namespace
