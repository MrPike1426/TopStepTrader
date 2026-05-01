Imports System.Net.Http
Imports System.Net.Http.Json
Imports System.Text.Json.Serialization
Imports System.Threading
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models
Imports TopStepTrader.Core.Settings

Namespace TopStepTrader.Services.AI

    ''' <summary>
    ''' Sends a strategy definition to the Anthropic Claude API and returns
    ''' plain-text improvement suggestions. Uses the model configured in ClaudeSettings
    ''' (defaults to claude-haiku for cost efficiency).
    ''' </summary>
    Public Class ClaudeReviewService
        Implements IClaudeReviewService

        Private ReadOnly _settings As ClaudeSettings
        Private ReadOnly _apiKeyStore As IApiKeyStore
        Private ReadOnly _logger As ILogger(Of ClaudeReviewService)
        Private Shared ReadOnly _http As New HttpClient()

        Private Const AnthropicMessagesUrl As String = "https://api.anthropic.com/v1/messages"
        Private Const AnthropicVersion As String = "2023-06-01"

        Private Const SystemPrompt As String =
            "You are an expert futures and crypto trading strategy advisor. " &
            "Analyze the following trading strategy and give 2-4 concise, " &
            "actionable improvement suggestions covering: entry logic, exit " &
            "placement (take-profit / stop-loss), risk sizing, and timing " &
            "(session hours, news events). Be specific about numbers where " &
            "possible. Keep your total response under 200 words."

        Private Const ConfidenceSystemPrompt As String =
            "You are an experienced micro-futures day trader. Given a contract symbol, provide a " &
            "brief confidence assessment in 3-4 bullet points covering: (1) what macro factors " &
            "typically drive this instrument, (2) best session windows (London/NY overlap etc.), " &
            "(3) any known seasonal or structural tendencies right now, and (4) your overall bias " &
            "(🟢 Long / 🔴 Short / 🟡 Neutral) with one-sentence rationale. " &
            "Note your knowledge has a cutoff date and you cannot access live data — base your assessment on historical patterns and known tendencies only. " &
            "Keep your total response under 150 words."

        Public Sub New(options As IOptions(Of ClaudeSettings), apiKeyStore As IApiKeyStore, logger As ILogger(Of ClaudeReviewService))
            _settings = options.Value
            _apiKeyStore = apiKeyStore
            _logger = logger
        End Sub

        ''' <summary>
        ''' Resolves the active Claude API key — prefers the key stored on the API Keys
        ''' page (local apikeys.json) and falls back to appsettings.json for backward
        ''' compatibility with existing deployments.
        ''' </summary>
        Private Function ResolveApiKey() As String
            Dim stored = _apiKeyStore.Load().ClaudeApiKey
            If Not String.IsNullOrWhiteSpace(stored) Then Return stored
            Return _settings.ApiKey
        End Function

        ''' <summary>
        ''' Calls Claude to review the strategy. Returns suggestion text, or a
        ''' user-friendly message if the API key is not yet configured.
        ''' </summary>
        Public Async Function ReviewStrategyAsync(strategy As StrategyDefinition,
                                                   Optional cancel As CancellationToken = Nothing) As Task(Of String) Implements IClaudeReviewService.ReviewStrategyAsync
            Dim apiKey = ResolveApiKey()
            If String.IsNullOrWhiteSpace(apiKey) Then
                Return "⚠️  Claude API key not configured — add it on the API Keys page."
            End If

            Dim userMessage = BuildUserMessage(strategy)

            Try
                Dim requestBody = New ClaudeRequest With {
                    .Model = _settings.Model,
                    .MaxTokens = _settings.MaxTokens,
                    .System = SystemPrompt,
                    .Messages = New List(Of ClaudeMessage) From {
                        New ClaudeMessage With {.Role = "user", .Content = userMessage}
                    }
                }

                Using request As New HttpRequestMessage(HttpMethod.Post, AnthropicMessagesUrl)
                    request.Headers.Add("x-api-key", apiKey)
                    request.Headers.Add("anthropic-version", AnthropicVersion)
                    request.Content = JsonContent.Create(requestBody)

                    Dim response = Await _http.SendAsync(request, cancel)

                    If Not response.IsSuccessStatusCode Then
                        Dim errorBody = Await response.Content.ReadAsStringAsync(cancel)
                        _logger.LogWarning("Claude API returned {Status}: {Body}", response.StatusCode, errorBody)
                        Return $"⚠️  Claude API error {CInt(response.StatusCode)} — check your API key on the API Keys page."
                    End If

                    Dim result = Await response.Content.ReadFromJsonAsync(Of ClaudeResponse)(cancellationToken:=cancel)
                    Dim text = result?.Content?.FirstOrDefault()?.Text

                    If String.IsNullOrWhiteSpace(text) Then
                        Return "⚠️  Claude returned an empty response. Please try again."
                    End If

                    Return text

                End Using

            Catch ex As TaskCanceledException
                Return "⚠️  Request timed out. Check your internet connection and try again."
            Catch ex As Exception
                _logger.LogError(ex, "ClaudeReviewService error")
                Return $"⚠️  Unexpected error: {ex.Message}"
            End Try
        End Function

        ''' <summary>
        ''' Asks Claude for a quick confidence / sentiment check on the given contract.
        ''' Returns bullet-point market context — does NOT access live data.
        ''' </summary>
        Public Async Function ConfidenceCheckAsync(contractId As String,
                                                    Optional cancel As CancellationToken = Nothing) As Task(Of String) Implements IClaudeReviewService.ConfidenceCheckAsync
            Dim apiKey = ResolveApiKey()
            If String.IsNullOrWhiteSpace(apiKey) Then
                Return "⚠️  Claude API key not configured — add it on the API Keys page."
            End If

            Dim userMessage = $"Contract: {contractId}{Environment.NewLine}" &
                              "Provide your confidence assessment for trading this instrument right now."

            Try
                Dim requestBody = New ClaudeRequest With {
                    .Model = _settings.Model,
                    .MaxTokens = _settings.MaxTokens,
                    .System = ConfidenceSystemPrompt,
                    .Messages = New List(Of ClaudeMessage) From {
                        New ClaudeMessage With {.Role = "user", .Content = userMessage}
                    }
                }

                Using request As New HttpRequestMessage(HttpMethod.Post, AnthropicMessagesUrl)
                    request.Headers.Add("x-api-key", apiKey)
                    request.Headers.Add("anthropic-version", AnthropicVersion)
                    request.Content = JsonContent.Create(requestBody)

                    Dim response = Await _http.SendAsync(request, cancel)

                    If Not response.IsSuccessStatusCode Then
                        Dim errorBody = Await response.Content.ReadAsStringAsync(cancel)
                        _logger.LogWarning("Claude API returned {Status}: {Body}", response.StatusCode, errorBody)
                        Return $"⚠️  Claude API error {CInt(response.StatusCode)} — check your API key on the API Keys page."
                    End If

                    Dim result = Await response.Content.ReadFromJsonAsync(Of ClaudeResponse)(cancellationToken:=cancel)
                    Dim text = result?.Content?.FirstOrDefault()?.Text

                    If String.IsNullOrWhiteSpace(text) Then
                        Return "⚠️  Claude returned an empty response. Please try again."
                    End If

                    Return text

                End Using

            Catch ex As TaskCanceledException
                Return "⚠️  Request timed out. Check your internet connection and try again."
            Catch ex As Exception
                _logger.LogError(ex, "ClaudeReviewService.ConfidenceCheckAsync error")
                Return $"⚠️  Unexpected error: {ex.Message}"
            End Try
        End Function

        ''' <summary>
        ''' Sends a formatted backtest-results table to Claude Haiku and returns a concise analysis:
        ''' top 3 combinations, overfitting warnings, and one live-trade recommendation.
        ''' </summary>
        Public Async Function AnalyseBacktestResultsAsync(resultsSummary As String,
                                                           Optional cancel As CancellationToken = Nothing) As Task(Of String) Implements IClaudeReviewService.AnalyseBacktestResultsAsync
            Dim apiKey = ResolveApiKey()
            If String.IsNullOrWhiteSpace(apiKey) Then
                Return "⚠️  Claude API key not configured — add it on the API Keys page."
            End If

            Const analysisSystemPrompt As String =
                "You are a quantitative trading analyst specialising in retail futures and CFD trading. " &
                "You are given backtest results across multiple instruments, strategies, and timeframes. " &
                "Your job is to: " &
                "(1) Identify the top 3 most promising instrument/strategy/timeframe combinations and briefly explain why. " &
                "(2) Flag any results that look suspiciously perfect — very high win rate with high trade count often indicates overfitting or look-ahead bias. " &
                "(3) Give one clear recommendation: which single combination should be prioritised for live trading and why. " &
                "Be specific, direct, and concise — under 300 words total."

            Try
                Dim requestBody = New ClaudeRequest With {
                    .Model = "claude-haiku-4-5-20251001",
                    .MaxTokens = 700,
                    .System = analysisSystemPrompt,
                    .Messages = New List(Of ClaudeMessage) From {
                        New ClaudeMessage With {.Role = "user", .Content = resultsSummary}
                    }
                }

                Using request As New HttpRequestMessage(HttpMethod.Post, AnthropicMessagesUrl)
                    request.Headers.Add("x-api-key", apiKey)
                    request.Headers.Add("anthropic-version", AnthropicVersion)
                    request.Content = JsonContent.Create(requestBody)

                    Dim response = Await _http.SendAsync(request, cancel)

                    If Not response.IsSuccessStatusCode Then
                        Dim errorBody = Await response.Content.ReadAsStringAsync(cancel)
                        _logger.LogWarning("Claude API returned {Status}: {Body}", response.StatusCode, errorBody)
                        Return $"⚠️  Claude API error {CInt(response.StatusCode)} — check your API key on the API Keys page."
                    End If

                    Dim result = Await response.Content.ReadFromJsonAsync(Of ClaudeResponse)(cancellationToken:=cancel)
                    Dim text = result?.Content?.FirstOrDefault()?.Text
                    Return If(Not String.IsNullOrWhiteSpace(text), text, "⚠️  Claude returned an empty response.")

                End Using

            Catch ex As TaskCanceledException
                Return "⚠️  Request timed out."
            Catch ex As Exception
                _logger.LogError(ex, "ClaudeReviewService.AnalyseBacktestResultsAsync error")
                Return $"⚠️  Unexpected error: {ex.Message}"
            End Try
        End Function

        Private Const PreTradeSystemPrompt As String =
            "You are a pre-trade risk filter for an automated futures trading system. " &
            "A fully automated strategy has passed all its technical gates (ADX, confidence, ATR sizing) " &
            "and is about to place a live order. You receive the session P&L and completed trade count " &
            "for this engine's current run, which tells you whether the system is in a drawdown streak." & vbLf &
            "Rules:" & vbLf &
            "- Respond with ""PROCEED"" or ""VETO"" as the FIRST WORD of your response." & vbLf &
            "- Follow immediately with 1-2 sentences of plain-text rationale (no bullet points, no headers)." & vbLf &
            "- Issue a VETO for any of these reasons:" & vbLf &
            "  1. SESSION DRAWDOWN — session P&L is negative AND 2 or more trades have already been completed " &
            "     with a loss rate of 75% or more. A deteriorating session suggests adverse market conditions " &
            "     (news, regime change, or trend exhaustion) that technical indicators cannot detect." & vbLf &
            "  2. WRONG SESSION — the session is clearly incompatible with this instrument " &
            "     (e.g. Asian session for equity-index futures, thin overnight window for commodities)." & vbLf &
            "  3. SIGNAL DIRECTION vs INSTRUMENT CHARACTER — a fundamental incompatibility between the " &
            "     signal direction and well-known persistent instrument behaviour at this session." & vbLf &
            "- Your knowledge has a cutoff date — you cannot see live prices or today's news. " &
            "  For session P&L drawdown judgements, trust the numbers provided — they are real." & vbLf &
            "Example PROCEED: ""PROCEED The London/NY overlap is the highest-liquidity window for Gold and no session drawdown pattern is present.""" & vbLf &
            "Example VETO (drawdown): ""VETO Session P&L is -$240 across 3 trades on this engine — a 100% loss rate strongly suggests an adverse market regime that the technical gates cannot filter out.""" & vbLf &
            "Example VETO (session): ""VETO Micro Gold Futures in the Asian session produce excessive noise that undermines ATR sizing assumptions."""

        ''' <summary>
        ''' Calls Claude Haiku for a pre-trade macro/session sanity check. Returns (Proceed=True)
        ''' on API error or missing key so connectivity issues never silently block trading.
        ''' The prompt instructs the model to respond with "PROCEED" or "VETO" as its first word.
        ''' </summary>
        Public Async Function PreTradeCheckAsync(ctx As PreTradeContext,
                                                  Optional cancel As CancellationToken = Nothing) As Task(Of (Proceed As Boolean, Reasoning As String)) Implements IClaudeReviewService.PreTradeCheckAsync
            Dim apiKey = ResolveApiKey()
            If String.IsNullOrWhiteSpace(apiKey) Then
                Return (True, "Claude API key not configured — pre-trade check skipped.")
            End If

            Dim userMessage = BuildPreTradeUserMessage(ctx)

            Try
                Dim requestBody = New ClaudeRequest With {
                    .Model      = "claude-haiku-4-5-20251001",
                    .MaxTokens  = 150,
                    .System     = PreTradeSystemPrompt,
                    .Messages   = New List(Of ClaudeMessage) From {
                        New ClaudeMessage With {.Role = "user", .Content = userMessage}
                    }
                }

                Using request As New HttpRequestMessage(HttpMethod.Post, AnthropicMessagesUrl)
                    request.Headers.Add("x-api-key", apiKey)
                    request.Headers.Add("anthropic-version", AnthropicVersion)
                    request.Content = JsonContent.Create(requestBody)

                    Dim response = Await _http.SendAsync(request, cancel)

                    If Not response.IsSuccessStatusCode Then
                        Dim errorBody = Await response.Content.ReadAsStringAsync(cancel)
                        _logger.LogWarning("PreTradeCheck API error {Status}: {Body}", response.StatusCode, errorBody)
                        Return (True, $"API error {CInt(response.StatusCode)} — defaulting to PROCEED.")
                    End If

                    Dim result = Await response.Content.ReadFromJsonAsync(Of ClaudeResponse)(cancellationToken:=cancel)
                    Dim text = result?.Content?.FirstOrDefault()?.Text?.Trim()

                    If String.IsNullOrWhiteSpace(text) Then
                        Return (True, "Empty AI response — defaulting to PROCEED.")
                    End If

                    ' First word determines the verdict; remainder is the rationale
                    Dim proceed = Not text.StartsWith("VETO", StringComparison.OrdinalIgnoreCase)
                    Dim rationale = text.Trim()
                    ' Strip the leading verdict word so the log message isn't redundant
                    If rationale.Length > 7 AndAlso (rationale.StartsWith("PROCEED", StringComparison.OrdinalIgnoreCase) OrElse
                                                      rationale.StartsWith("VETO",    StringComparison.OrdinalIgnoreCase)) Then
                        Dim spaceIdx = rationale.IndexOf(" "c)
                        If spaceIdx > 0 Then rationale = rationale.Substring(spaceIdx + 1).Trim()
                    End If

                    Return (proceed, rationale)

                End Using

            Catch ex As TaskCanceledException
                Return (True, "Request timed out — defaulting to PROCEED.")
            Catch ex As Exception
                _logger.LogError(ex, "PreTradeCheckAsync error")
                Return (True, $"Unexpected error ({ex.Message}) — defaulting to PROCEED.")
            End Try
        End Function

        Private Shared Function BuildPreTradeUserMessage(ctx As PreTradeContext) As String
            Dim sb As New System.Text.StringBuilder()
            Dim session = GetTradingSession(ctx.UtcNow)
            Dim personaDesc = GetPersonaDescription(ctx.PersonaName)
            Dim slDist = If(ctx.AtrValue > 0D, ctx.SlMultiple * ctx.AtrValue, 0D)
            Dim tpDist = If(ctx.AtrValue > 0D, ctx.TpMultiple * ctx.AtrValue, 0D)

            sb.AppendLine("PRE-TRADE SIGNAL — APPROVAL REQUESTED")
            sb.AppendLine()
            sb.AppendLine($"Instrument:    {ctx.ContractDescription} ({ctx.ContractId})")
            sb.AppendLine($"Direction:     {ctx.Side}")
            sb.AppendLine($"Price:         {ctx.Price:F4} (last bar close)")
            sb.AppendLine($"Timeframe:     {ctx.TimeframeMinutes}-minute bars")
            sb.AppendLine($"Strategy:      {ctx.StrategyName}")
            If Not String.IsNullOrEmpty(ctx.PersonaName) Then
                sb.AppendLine($"Persona:       {ctx.PersonaName} — {personaDesc}")
            End If
            sb.AppendLine()
            sb.AppendLine("TECHNICAL GATES (all passed before this check):")
            If ctx.AdxValue > 0F Then
                sb.AppendLine($"  ADX(14):     {ctx.AdxValue:F1} ≥ {ctx.AdxThreshold:F0} ✓ (trending market confirmed)")
            End If
            sb.AppendLine($"  Confidence:  {ctx.ConfidencePct}% ≥ {ctx.MinConfidencePct}% ✓")
            If ctx.AtrValue > 0D Then
                sb.AppendLine($"  ATR(14):     {ctx.AtrValue:F4} pts")
            End If
            sb.AppendLine()
            sb.AppendLine("RISK PARAMETERS:")
            If Not String.IsNullOrEmpty(ctx.ExitStrategyDescription) Then
                sb.AppendLine($"  {ctx.ExitStrategyDescription}")
            ElseIf slDist > 0D Then
                sb.AppendLine($"  Stop Loss:   {ctx.SlMultiple:F2} × ATR = {slDist:F4} pts from entry")
                sb.AppendLine($"  Take Profit: {ctx.TpMultiple:F2} × ATR = {tpDist:F4} pts from entry")
            Else
                sb.AppendLine($"  SL multiple: {ctx.SlMultiple:F2} × ATR  |  TP multiple: {ctx.TpMultiple:F2} × ATR")
            End If
            sb.AppendLine()
            sb.AppendLine("MARKET CONTEXT:")
            sb.AppendLine($"  Session:     {session}")
            sb.AppendLine($"  Day/Time:    {ctx.UtcNow:ddd dd-MMM-yyyy HH:mm} UTC")
            sb.AppendLine()
            sb.AppendLine("SESSION PERFORMANCE (this engine since last Start):")
            If ctx.SessionTradeCount = 0 Then
                sb.AppendLine($"  Trades:      0 completed — this would be the first entry this session")
            Else
                Dim lossRate = If(ctx.SessionPnlUsd < 0D AndAlso ctx.SessionTradeCount > 0,
                                  $" (session P&L negative — possible adverse conditions)",
                                  If(ctx.SessionPnlUsd > 0D, " (session profitable)", " (flat)"))
                sb.AppendLine($"  Trades:      {ctx.SessionTradeCount} completed")
                sb.AppendLine($"  Session P&L: {If(ctx.SessionPnlUsd >= 0D, "+", "")}${ctx.SessionPnlUsd:F2}{lossRate}")
            End If
            sb.AppendLine()
            sb.AppendLine("Should this trade PROCEED or be VETOED?")
            Return sb.ToString()
        End Function

        Private Shared Function GetTradingSession(utc As DateTimeOffset) As String
            Dim h = utc.Hour + utc.Minute / 60.0
            Select Case utc.DayOfWeek
                Case DayOfWeek.Saturday, DayOfWeek.Sunday
                    Return "Weekend — equity/futures markets closed (crypto only)"
            End Select
            If h >= 12.0 AndAlso h < 16.5 Then Return "London/New York overlap (12:00–16:30 UTC) — peak liquidity"
            If h >= 7.0 AndAlso h < 16.5 Then Return "London session (07:00–16:30 UTC)"
            If h >= 13.5 AndAlso h < 20.0 Then Return "New York session (13:30–20:00 UTC)"
            If h >= 0.0 AndAlso h < 7.0 Then Return "Asian session (00:00–07:00 UTC) — typically thin for US futures"
            Return "Post-New York (20:00–24:00 UTC) — typically thin"
        End Function

        Private Shared Function GetPersonaDescription(personaName As String) As String
            Select Case If(personaName, String.Empty).ToLowerInvariant()
                Case "lewis"  : Return "Risk Averse (ADX≥25, 90% confidence, SL=1.5×ATR, TP=3.0×ATR, R:R 1:2.0)"
                Case "damian" : Return "Moderate (ADX≥20, 80% confidence, SL=1.0×ATR, TP=2.5×ATR, R:R 1:2.5)"
                Case "joe"    : Return "Aggressive (ADX≥15, 70% confidence, SL=0.75×ATR, TP=2.0×ATR, R:R 1:2.67)"
                Case Else     : Return "Custom"
            End Select
        End Function

        Private Const TradeAdviceSystemPrompt As String =
            "You are a short-term futures trading analyst. " &
            "You will receive up to 1 hour of 5-minute OHLCV bar data for a futures contract. " &
            "Give a single trade direction. Respond with BUY or SELL as the very first word. " &
            "Follow immediately with a one-sentence rationale of at most 25 words. " &
            "Do not hedge or suggest both directions. Do not add disclaimers."

        ''' <summary>
        ''' Sends the last 1 hour of 5-minute bar data to Claude Haiku and returns a
        ''' (Direction="BUY"|"SELL"|"—", Rationale) tuple.  Always uses Haiku for cost.
        ''' </summary>
        Public Async Function TradeAdviceAsync(contractName As String,
                                               bars As IReadOnlyList(Of MarketBar),
                                               Optional cancel As CancellationToken = Nothing) As Task(Of (Direction As String, Rationale As String)) Implements IClaudeReviewService.TradeAdviceAsync
            Dim apiKey = ResolveApiKey()
            If String.IsNullOrWhiteSpace(apiKey) Then
                Return ("—", "⚠️ Claude API key not configured — add it on the API Keys page.")
            End If

            If bars Is Nothing OrElse bars.Count = 0 Then
                Return ("—", "No bar data available.")
            End If

            Dim userMessage = BuildTradeAdviceMessage(contractName, bars)

            Try
                Dim requestBody = New ClaudeRequest With {
                    .Model = "claude-haiku-4-5-20251001",
                    .MaxTokens = 80,
                    .System = TradeAdviceSystemPrompt,
                    .Messages = New List(Of ClaudeMessage) From {
                        New ClaudeMessage With {.Role = "user", .Content = userMessage}
                    }
                }

                Using request As New HttpRequestMessage(HttpMethod.Post, AnthropicMessagesUrl)
                    request.Headers.Add("x-api-key", apiKey)
                    request.Headers.Add("anthropic-version", AnthropicVersion)
                    request.Content = JsonContent.Create(requestBody)

                    Dim response = Await _http.SendAsync(request, cancel)

                    If Not response.IsSuccessStatusCode Then
                        Dim errorBody = Await response.Content.ReadAsStringAsync(cancel)
                        _logger.LogWarning("TradeAdviceAsync API error {Status}: {Body}", response.StatusCode, errorBody)
                        Return ("—", $"⚠️ Claude API error {CInt(response.StatusCode)} — check your API key.")
                    End If

                    Dim result = Await response.Content.ReadFromJsonAsync(Of ClaudeResponse)(cancellationToken:=cancel)
                    Dim text = result?.Content?.FirstOrDefault()?.Text?.Trim()

                    If String.IsNullOrWhiteSpace(text) Then
                        Return ("—", "⚠️ Empty response from Claude.")
                    End If

                    ' First word must be BUY or SELL; the remainder is the rationale.
                    Dim direction = "—"
                    Dim rationale = text
                    Dim spaceIdx = text.IndexOf(" "c)
                    If spaceIdx > 0 Then
                        Dim firstWord = text.Substring(0, spaceIdx).ToUpperInvariant()
                        If firstWord = "BUY" OrElse firstWord = "SELL" Then
                            direction = firstWord
                            rationale = text.Substring(spaceIdx + 1).Trim()
                        End If
                    ElseIf text.ToUpperInvariant() = "BUY" OrElse text.ToUpperInvariant() = "SELL" Then
                        direction = text.ToUpperInvariant()
                        rationale = String.Empty
                    End If

                    Return (direction, rationale)
                End Using

            Catch ex As TaskCanceledException
                Return ("—", "⚠️ Request timed out.")
            Catch ex As Exception
                _logger.LogError(ex, "TradeAdviceAsync error")
                Return ("—", $"⚠️ Error: {ex.Message}")
            End Try
        End Function

        Private Shared Function BuildTradeAdviceMessage(contractName As String, bars As IReadOnlyList(Of MarketBar)) As String
            Dim sb As New System.Text.StringBuilder()
            sb.AppendLine($"Contract: {contractName}")
            sb.AppendLine($"Timeframe: 5-minute bars ({bars.Count} bars = ~{bars.Count * 5} minutes)")
            sb.AppendLine()
            sb.AppendLine("Timestamp (UTC),Open,High,Low,Close,Volume")
            For Each b In bars
                sb.AppendLine($"{b.Timestamp:yyyy-MM-dd HH:mm},{b.Open:F4},{b.High:F4},{b.Low:F4},{b.Close:F4},{b.Volume}")
            Next
            sb.AppendLine()
            sb.AppendLine("Should the next trade be BUY or SELL?")
            Return sb.ToString()
        End Function

        Private Shared Function BuildUserMessage(strategy As StrategyDefinition) As String
            Dim sb As New System.Text.StringBuilder()
            sb.AppendLine("STRATEGY TO REVIEW:")
            sb.AppendLine($"  Name:        {strategy.Name}")
            sb.AppendLine($"  Contract:    {strategy.ContractId}")
            sb.AppendLine($"  Timeframe:   {strategy.TimeframeMinutes}-minute bars")
            sb.AppendLine($"  Duration:    {strategy.DurationHours} hours")
            sb.AppendLine($"  Indicator:   {strategy.Indicator} (period={strategy.IndicatorPeriod}, mult={strategy.IndicatorMultiplier})")
            sb.AppendLine($"  Condition:   {strategy.Condition}")
            sb.AppendLine($"  Long entry:  {strategy.GoLongWhenBelowBands}")
            sb.AppendLine($"  Short entry: {strategy.GoShortWhenAboveBands}")
            sb.AppendLine($"  Take Profit: {If(strategy.TpMultipleOfN > 0, $"{strategy.TpMultipleOfN:F2}×ATR", "None")}")
            sb.AppendLine($"  Stop Loss:   {If(strategy.SlMultipleOfN > 0, $"{strategy.SlMultipleOfN:F2}×ATR", "None")}")
            sb.AppendLine($"  Quantity:    {strategy.Quantity} contract(s)")
            If Not String.IsNullOrWhiteSpace(strategy.RawDescription) Then
                sb.AppendLine()
                sb.AppendLine("ORIGINAL DESCRIPTION:")
                sb.AppendLine(strategy.RawDescription)
            End If
            Return sb.ToString()
        End Function

        ''' <summary>
        ''' Stub post-trade analysis: calls Claude Haiku with a summary of the closed trade
        ''' and returns AI commentary. On failure defaults to a Succeeded=False result.
        ''' </summary>
        Public Async Function PostTradeAnalysisAsync(ctx As PostTradeContext,
                                                      Optional cancel As CancellationToken = Nothing) As Task(Of PostTradeAnalysisResult) Implements IClaudeReviewService.PostTradeAnalysisAsync
            Dim apiKey = ResolveApiKey()
            If String.IsNullOrWhiteSpace(apiKey) Then
                Return PostTradeAnalysisResult.Failure("Claude API key not configured.")
            End If

            Const sysPrompt As String =
                "You are a post-trade analyst for a retail futures trader. " &
                "Given a summary of a closed trade, provide 2-3 concise observations: " &
                "what went well, what could be improved, and whether the exit was well-timed. " &
                "Be direct and specific. Keep your response under 120 words."

            Dim userMsg As New System.Text.StringBuilder()
            userMsg.AppendLine($"Instrument: {ctx.ContractDescription} ({ctx.ContractId})")
            userMsg.AppendLine($"Direction: {ctx.Side}")
            userMsg.AppendLine($"Entry: {ctx.EntryPrice:F4}  Exit: {ctx.ExitPrice:F4}")
            userMsg.AppendLine($"P&L: {If(ctx.RealizedPnlUsd >= 0, "+", "")}${ctx.RealizedPnlUsd:F2}")
            userMsg.AppendLine($"Hold: {ctx.HoldDurationMinutes:F0} min  Strategy: {ctx.StrategyName}")
            userMsg.AppendLine($"Confidence at entry: {ctx.ConfidencePct}%  ADX: {ctx.AdxValue:F1}")
            userMsg.AppendLine($"Session P&L after trade: {If(ctx.SessionPnlUsd >= 0, "+", "")}${ctx.SessionPnlUsd:F2}")

            Try
                Dim requestBody = New ClaudeRequest With {
                    .Model = "claude-haiku-4-5-20251001",
                    .MaxTokens = 200,
                    .System = sysPrompt,
                    .Messages = New List(Of ClaudeMessage) From {
                        New ClaudeMessage With {.Role = "user", .Content = userMsg.ToString()}
                    }
                }

                Using request As New HttpRequestMessage(HttpMethod.Post, AnthropicMessagesUrl)
                    request.Headers.Add("x-api-key", apiKey)
                    request.Headers.Add("anthropic-version", AnthropicVersion)
                    request.Content = JsonContent.Create(requestBody)

                    Dim response = Await _http.SendAsync(request, cancel)

                    If Not response.IsSuccessStatusCode Then
                        Dim errorBody = Await response.Content.ReadAsStringAsync(cancel)
                        _logger.LogWarning("PostTradeAnalysisAsync API error {Status}: {Body}", response.StatusCode, errorBody)
                        Return PostTradeAnalysisResult.Failure($"API error {CInt(response.StatusCode)}.")
                    End If

                    Dim result = Await response.Content.ReadFromJsonAsync(Of ClaudeResponse)(cancellationToken:=cancel)
                    Dim text = result?.Content?.FirstOrDefault()?.Text?.Trim()
                    If String.IsNullOrWhiteSpace(text) Then
                        Return PostTradeAnalysisResult.Failure("Empty AI response.")
                    End If
                    Return New PostTradeAnalysisResult With {.Analysis = text, .Succeeded = True}
                End Using

            Catch ex As TaskCanceledException
                Return PostTradeAnalysisResult.Failure("Request timed out.")
            Catch ex As Exception
                _logger.LogError(ex, "PostTradeAnalysisAsync error")
                Return PostTradeAnalysisResult.Failure($"Unexpected error: {ex.Message}")
            End Try
        End Function

        ' ── Mid-Trade Sense Check ────────────────────────────────────────────────

        Private Const MidTradeSystemPrompt As String =
            "You are an experienced day trader reviewing a live futures position." & vbLf &
            "You receive bar history and the current position state." & vbLf &
            "Respond with EXACTLY ONE LINE in this format:" & vbLf &
            "GREEN: <brief explanation>. Suggested action: <action>" & vbLf &
            "or" & vbLf &
            "AMBER: <brief explanation>. Suggested action: <action>" & vbLf &
            "or" & vbLf &
            "RED: <brief explanation>. Suggested action: <action>" & vbLf &
            "GREEN = position healthy, hold. AMBER = caution, consider tightening. RED = exit recommended." & vbLf &
            "No disclaimers. No extra lines. One line only."

        ''' <summary>
        ''' Mid-trade sense check. Sends bar history + live position state to Claude Haiku.
        ''' Returns (Verdict=GREEN|AMBER|RED, Explanation, SuggestedAction).
        ''' On any error defaults to (GREEN, error message, "Continue monitoring").
        ''' </summary>
        Public Async Function MidTradeCheckAsync(instrument As String,
                                                  side As String,
                                                  adxVal As Single,
                                                  plusDi As Single,
                                                  minusDi As Single,
                                                  stopPhaseLabel As String,
                                                  unrealizedPnl As Decimal,
                                                  bars As IReadOnlyList(Of MarketBar),
                                                  Optional cancel As CancellationToken = Nothing) As Task(Of (Verdict As String, Explanation As String, SuggestedAction As String)) Implements IClaudeReviewService.MidTradeCheckAsync
            Dim apiKey = ResolveApiKey()
            If String.IsNullOrWhiteSpace(apiKey) Then
                Return ("GREEN", "Claude API key not configured — check skipped.", "Continue monitoring.")
            End If

            Dim userMessage = BuildMidTradeMessage(instrument, side, adxVal, plusDi, minusDi, stopPhaseLabel, unrealizedPnl, bars)

            Try
                Dim requestBody = New ClaudeRequest With {
                    .Model = "claude-haiku-4-5-20251001",
                    .MaxTokens = 120,
                    .System = MidTradeSystemPrompt,
                    .Messages = New List(Of ClaudeMessage) From {
                        New ClaudeMessage With {.Role = "user", .Content = userMessage}
                    }
                }

                Using request As New HttpRequestMessage(HttpMethod.Post, AnthropicMessagesUrl)
                    request.Headers.Add("x-api-key", apiKey)
                    request.Headers.Add("anthropic-version", AnthropicVersion)
                    request.Content = JsonContent.Create(requestBody)

                    Dim response = Await _http.SendAsync(request, cancel)
                    If Not response.IsSuccessStatusCode Then
                        Dim errorBody = Await response.Content.ReadAsStringAsync(cancel)
                        _logger.LogWarning("MidTradeCheckAsync API error {Status}: {Body}", response.StatusCode, errorBody)
                        Return ("GREEN", $"API error {CInt(response.StatusCode)} — defaulting to GREEN.", "Continue monitoring.")
                    End If

                    Dim result = Await response.Content.ReadFromJsonAsync(Of ClaudeResponse)(cancellationToken:=cancel)
                    Dim text = result?.Content?.FirstOrDefault()?.Text?.Trim()
                    If String.IsNullOrWhiteSpace(text) Then
                        Return ("GREEN", "Empty AI response.", "Continue monitoring.")
                    End If

                    Return ParseMidTradeResponse(text)
                End Using

            Catch ex As TaskCanceledException
                Return ("GREEN", "Request timed out.", "Continue monitoring.")
            Catch ex As Exception
                _logger.LogError(ex, "MidTradeCheckAsync error")
                Return ("GREEN", $"Unexpected error: {ex.Message}", "Continue monitoring.")
            End Try
        End Function

        Private Shared Function BuildMidTradeMessage(instrument As String,
                                                      side As String,
                                                      adxVal As Single,
                                                      plusDi As Single,
                                                      minusDi As Single,
                                                      stopPhaseLabel As String,
                                                      unrealizedPnl As Decimal,
                                                      bars As IReadOnlyList(Of MarketBar)) As String
            Dim sb As New System.Text.StringBuilder()
            sb.AppendLine("MID-TRADE SENSE CHECK")
            sb.AppendLine()
            sb.AppendLine($"Instrument:   {instrument}")
            sb.AppendLine($"Direction:    {side}")
            sb.AppendLine($"ADX(14):      {adxVal:F1}")
            sb.AppendLine($"+DI:          {plusDi:F1}")
            sb.AppendLine($"-DI:          {minusDi:F1}")
            sb.AppendLine($"Stop Phase:   {stopPhaseLabel}")
            sb.AppendLine($"Unrealised PnL: {If(unrealizedPnl >= 0, "+", "")}${unrealizedPnl:F2}")
            sb.AppendLine()
            sb.AppendLine($"BAR HISTORY (last {Math.Min(bars.Count, 20)} bars, newest last):")
            sb.AppendLine("Time (UTC)          Open      High      Low       Close     Volume")
            Dim recent = bars.Skip(Math.Max(0, bars.Count - 20)).ToList()
            For Each b In recent
                sb.AppendLine($"{b.Timestamp:yyyy-MM-dd HH:mm}  {b.Open,8:F4}  {b.High,8:F4}  {b.Low,8:F4}  {b.Close,8:F4}  {b.Volume,8}")
            Next
            sb.AppendLine()
            sb.AppendLine("Should this position be held (GREEN), monitored with caution (AMBER), or exited (RED)?")
            Return sb.ToString()
        End Function

        Private Shared Function ParseMidTradeResponse(text As String) As (Verdict As String, Explanation As String, SuggestedAction As String)
            Dim verdict As String = "GREEN"
            If text.StartsWith("AMBER", StringComparison.OrdinalIgnoreCase) Then verdict = "AMBER"
            If text.StartsWith("RED", StringComparison.OrdinalIgnoreCase) Then verdict = "RED"

            ' Strip verdict word
            Dim body = text
            Dim colonIdx = text.IndexOf(":"c)
            If colonIdx > 0 AndAlso colonIdx < 10 Then
                body = text.Substring(colonIdx + 1).Trim()
            End If

            ' Split on "Suggested action:" if present
            Dim actionKey = "Suggested action:"
            Dim actionIdx = body.IndexOf(actionKey, StringComparison.OrdinalIgnoreCase)
            If actionIdx >= 0 Then
                Dim explanation = body.Substring(0, actionIdx).Trim().TrimEnd("."c).Trim()
                Dim action = body.Substring(actionIdx + actionKey.Length).Trim()
                Return (verdict, explanation, action)
            End If

            Return (verdict, body, "Continue monitoring.")
        End Function

        ' ── JSON DTOs ────────────────────────────────────────────────────────────

        Private Class ClaudeRequest
            <JsonPropertyName("model")>
            Public Property Model As String

            <JsonPropertyName("max_tokens")>
            Public Property MaxTokens As Integer

            <JsonPropertyName("system")>
            Public Property System As String

            <JsonPropertyName("messages")>
            Public Property Messages As List(Of ClaudeMessage)
        End Class

        Private Class ClaudeMessage
            <JsonPropertyName("role")>
            Public Property Role As String

            <JsonPropertyName("content")>
            Public Property Content As String
        End Class

        Private Class ClaudeResponse
            <JsonPropertyName("content")>
            Public Property Content As List(Of ClaudeContent)
        End Class

        Private Class ClaudeContent
            <JsonPropertyName("text")>
            Public Property Text As String
        End Class

    End Class

End Namespace
