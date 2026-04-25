Namespace TopStepTrader.Core.Models

    ''' <summary>
    ''' Context package passed to ClaudeReviewService.PreTradeCheckAsync for a
    ''' pre-trade AI sanity check immediately before an entry order is placed.
    ''' All technical gates have already passed when this is built — the AI check
    ''' is a final macro/session filter only.
    ''' </summary>
    Public Class PreTradeContext
        ''' <summary>eToro / ProjectX contract identifier, e.g. "MGC" or "GOLD.24-7".</summary>
        Public Property ContractId As String = String.Empty
        ''' <summary>Human-readable instrument name, e.g. "Micro Gold Futures".</summary>
        Public Property ContractDescription As String = String.Empty
        ''' <summary>"BUY" or "SELL".</summary>
        Public Property Side As String = "BUY"
        ''' <summary>Last bar close price at signal time.</summary>
        Public Property Price As Decimal
        ''' <summary>ATR(14) in price points at signal time. 0 = unavailable (warm-up).</summary>
        Public Property AtrValue As Decimal
        ''' <summary>Configured SL multiple of N (ATR), e.g. 1.5 for Lewis.</summary>
        Public Property SlMultiple As Decimal
        ''' <summary>Configured TP multiple of N (ATR), e.g. 3.0 for Lewis.</summary>
        Public Property TpMultiple As Decimal
        ''' <summary>Current ADX(14) reading. 0 = unavailable for this strategy type.</summary>
        Public Property AdxValue As Single
        ''' <summary>Configured minimum ADX required for entry (persona-driven gate).</summary>
        Public Property AdxThreshold As Single
        ''' <summary>Signal confidence score 0–100 that triggered this entry.</summary>
        Public Property ConfidencePct As Integer
        ''' <summary>Configured minimum confidence required for entry.</summary>
        Public Property MinConfidencePct As Integer
        ''' <summary>Bar period in minutes, e.g. 5 or 15.</summary>
        Public Property TimeframeMinutes As Integer
        ''' <summary>Strategy name, e.g. "Multi-Confluence Engine".</summary>
        Public Property StrategyName As String = String.Empty
        ''' <summary>Active persona name: "Lewis", "Damian", "Joe", or empty if not set.</summary>
        Public Property PersonaName As String = String.Empty
        ''' <summary>UTC timestamp at the moment the signal fired.</summary>
        Public Property UtcNow As DateTimeOffset
        ''' <summary>
        ''' Cumulative realised P&amp;L (USD) for this engine's session since Start() was called.
        ''' Negative values indicate a losing session. 0 = first trade of the session.
        ''' </summary>
        Public Property SessionPnlUsd As Decimal = 0D
        ''' <summary>
        ''' Number of completed (closed) trades this engine has recorded since Start().
        ''' Used by the AI check to detect consecutive-loss streaks.
        ''' </summary>
        Public Property SessionTradeCount As Integer = 0

        ' ── Phase 4–5 enrichment fields ────────────────────────────────────────

        ''' <summary>Rolling win-rate (0.0–1.0) across recent trades this session. Nothing = insufficient data.</summary>
        Public Property RollingWinRate As Decimal?
        ''' <summary>Realised P&amp;L of the most recent closed trade in USD.</summary>
        Public Property RecentPnL As Decimal?
        ''' <summary>Number of consecutive losses immediately before this signal.</summary>
        Public Property ConsecutiveLosses As Integer = 0
        ''' <summary>Total completed trades this session (same value as SessionTradeCount — kept for clarity).</summary>
        Public Property TotalTradesThisSession As Integer = 0
        ''' <summary>Effective minimum confidence threshold after persona/circuit-breaker adjustments.</summary>
        Public Property EffectiveMinConfidence As Integer
    End Class

End Namespace
