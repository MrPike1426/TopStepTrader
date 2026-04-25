Option Strict On
Option Explicit On

Imports TopStepTrader.Services.AI
Imports Xunit

Namespace TopStepTrader.Tests.AI

    Public Class BestPickParserTests

        <Fact>
        Public Sub Parse_ArrowTradeFormat_ReturnsPersonaAndLine()
            Dim analysis = "→ Trade: Damian · Gold · Multi-Confluence · 5 min. Sharpe: 6.32"
            Dim result = BestPickParser.ParseRecommendation(analysis)
            Assert.NotNull(result)
            Assert.Equal("Damian", result.Persona)
            Assert.Contains("Gold", result.RecommendationLine)
        End Sub

        <Fact>
        Public Sub Parse_SingleRecommendationHeader_ReturnsPersona()
            Dim analysis = "**Single Recommendation:** Lewis - S&P 500 - EMA/RSI Combined - 4 hr"
            Dim result = BestPickParser.ParseRecommendation(analysis)
            Assert.NotNull(result)
            Assert.Equal("Lewis", result.Persona)
        End Sub

        <Fact>
        Public Sub Parse_RecommendWordInSentence_ReturnsPersona()
            Dim analysis = "Based on the results, I recommend Joe with Bitcoin using VIDYA Cross on 1 hr."
            Dim result = BestPickParser.ParseRecommendation(analysis)
            Assert.NotNull(result)
            Assert.Equal("Joe", result.Persona)
        End Sub

        <Fact>
        Public Sub Parse_TopPickBulletFormat_ReturnsPersona()
            Dim analysis = "Top pick: Damian / Oil / Multi-Confluence / 15 min — strong Sharpe and low drawdown"
            Dim result = BestPickParser.ParseRecommendation(analysis)
            Assert.NotNull(result)
            Assert.Equal("Damian", result.Persona)
        End Sub

        <Fact>
        Public Sub Parse_MarkerWithPersonaInLaterLine_SkipsEarlierNonMatchingLine()
            Dim analysis = "→ No persona found here." & vbLf & "→ Best pick: Lewis · Gold · Multi-Confluence · 5 min"
            Dim result = BestPickParser.ParseRecommendation(analysis)
            Assert.NotNull(result)
            Assert.Equal("Lewis", result.Persona)
        End Sub

        <Fact>
        Public Sub Parse_NoRecommendationMarker_ReturnsNothing()
            Dim analysis = "The results show strong performance across multiple combinations."
            Dim result = BestPickParser.ParseRecommendation(analysis)
            Assert.Null(result)
        End Sub

        <Fact>
        Public Sub Parse_EmptyString_ReturnsNothing()
            Dim result = BestPickParser.ParseRecommendation("")
            Assert.Null(result)
        End Sub

        <Fact>
        Public Sub Parse_MarkerWithNoPersona_ReturnsNothing()
            Dim analysis = "→ Trade: Oil · Multi-Confluence · 5 min. Sharpe: 6.32"
            Dim result = BestPickParser.ParseRecommendation(analysis)
            Assert.Null(result)
        End Sub

    End Class

End Namespace
