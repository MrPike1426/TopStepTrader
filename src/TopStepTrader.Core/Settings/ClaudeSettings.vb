Namespace TopStepTrader.Core.Settings

    ''' <summary>
    ''' Settings for the Anthropic Claude API used for AI strategy review.
    ''' Bound from appsettings.json section "Claude".
    ''' </summary>
    Public Class ClaudeSettings

        ''' <summary>Anthropic API key (entered by user in Settings tab — never committed to git).</summary>
        Public Property ApiKey As String = String.Empty

        ''' <summary>
        ''' Model to use. Defaults to claude-haiku for speed and cost-efficiency.
        ''' Can be upgraded to claude-sonnet or claude-opus in Settings.
        ''' </summary>
        Public Property Model As String = "claude-haiku-4-5"

        ''' <summary>Maximum tokens in the AI review response.</summary>
        Public Property MaxTokens As Integer = 500

    End Class

End Namespace
