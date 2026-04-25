Option Strict On
Option Explicit On

Namespace TopStepTrader.Services.AI

    Public Class BestPickCandidate
        Public Property Persona As String = ""
        Public Property RecommendationLine As String = ""
    End Class

    Public Module BestPickParser

        Private ReadOnly _markers As String() = {
            "→", "recommend", "best pick", "top pick",
            "live trade", "single recommendation", "action:"
        }

        Private ReadOnly _personas As String() = {"Lewis", "Damian", "Joe"}

        ''' <summary>
        ''' Scans a Claude Haiku analysis for a recommendation line and returns the first
        ''' line containing both a recommendation marker and a known persona name.
        ''' Returns Nothing when no match is found.
        ''' </summary>
        Public Function ParseRecommendation(analysis As String) As BestPickCandidate
            If String.IsNullOrWhiteSpace(analysis) Then Return Nothing

            Dim lines = analysis.Split({vbLf, vbCr}, StringSplitOptions.RemoveEmptyEntries)
            For Each line In lines
                Dim lower = line.ToLower()
                If Not _markers.Any(Function(m) lower.Contains(m)) Then Continue For

                Dim persona = _personas.FirstOrDefault(
                    Function(p) line.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)
                If String.IsNullOrEmpty(persona) Then Continue For

                Return New BestPickCandidate With {
                    .Persona = persona,
                    .RecommendationLine = line
                }
            Next
            Return Nothing
        End Function

    End Module

End Namespace
