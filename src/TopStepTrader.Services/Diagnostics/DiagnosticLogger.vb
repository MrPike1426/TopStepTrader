Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports Microsoft.Extensions.Logging
Imports TopStepTrader.Core.Models.Diagnostics
Imports TopStepTrader.Data.Debug

Namespace TopStepTrader.Services.Diagnostics

    ''' <summary>
    ''' Writes high-fidelity trading diagnostic entries to a JSONL (JSON Lines) file
    ''' during an 8-hour test session.
    '''
    ''' File format: one JSON object per line.  Lines starting with '#' are comments.
    ''' Filename pattern: diag_YYYY-MM-DD_HH-mm-ss_{CONTRACT}_{SESSION}.jsonl
    ''' Output folder:    <solution-root>\Diagnostics\ (resolved via DebugTradeDbContext.ResolveDiagnos
    '''
    ''' Thread-safe: all writes are serialised via SyncLock.
    ''' AutoFlush enabled so no data is lost if the process crashes.
    '''
    ''' ── Analysis workflow ───────────────────────────────────────────────────
    ''' Python / pandas:
    '''   import pandas as pd
    '''   df = pd.read_json("diag_*.jsonl", lines=True, comment="#")
    '''   placed = df[df.EventType == "SIGNAL_PLACED"]
    '''   pm     = df[df.EventType == "POSTMORTEM"]
    '''   merged = placed.merge(pm, on="TradeId", suffixes=("_entry","_close"))
    '''
    ''' Excel Power Query:
    '''   1. Data → Get Data → From File → From JSON
    '''   2. Filter EventType column
    ''' ────────────────────────────────────────────────────────────────────────
    ''' </summary>
    Public Class DiagnosticLogger
        Implements IDisposable

        ' ── Dependencies ──────────────────────────────────────────────────────
        Private ReadOnly _logger As ILogger(Of DiagnosticLogger)

        ' ── Session state ─────────────────────────────────────────────────────
        Private _writer As StreamWriter = Nothing
        Private ReadOnly _lock As New Object()
        Private _filePath As String = String.Empty
        Private _sessionId As String = String.Empty
        Private _entryCount As Integer = 0
        Private _sessionStartUtc As DateTimeOffset = DateTimeOffset.MinValue
        Private _disposed As Boolean = False

        ' ── Open-position CSV log ─────────────────────────────────────────────
        ' Companion file: same folder and session ID as the JSONL, suffix _positions.csv.
        ' Appends one row every 5 minutes per engine instance while a position is open.
        ' Columns: Timestamp, Contract, Side, EntryPrice, CurrentPrice, PriceSource,
        '          PnL_USD, SL_Price, TP_Price, PositionID, SL_Ticks, TP_Ticks
        Private _positionWriter As StreamWriter = Nothing
        Private ReadOnly _positionLock As New Object()

        ' ── JSON serialiser options ────────────────────────────────────────────
        ' WriteIndented=False for compact JSONL.
        ' SnakeCaseLower converts PascalCase property names to snake_case automatically
        '   (e.g. BbUpper → bb_upper, MaxFavorableExcursion → max_favorable_excursion).
        ' WhenWritingNull omits null nested sections (Settings/Outcome) from NO_SIGNAL
        '   entries to keep file size manageable.
        Private Shared ReadOnly _jsonOpts As New JsonSerializerOptions With {
            .WriteIndented = False,
            .DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            .PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        }

        ' ── Public surface ────────────────────────────────────────────────────

        ''' <summary>Full path of the current JSONL log file, or empty when no session is active.</summary>
        Public ReadOnly Property FilePath As String
            Get
                Return _filePath
            End Get
        End Property

        ''' <summary>8-character uppercase session ID embedded in the filename.</summary>
        Public ReadOnly Property SessionId As String
            Get
                Return _sessionId
            End Get
        End Property

        ''' <summary>True when a session file is open and accepting writes.</summary>
        Public ReadOnly Property IsActive As Boolean
            Get
                Return _writer IsNot Nothing
            End Get
        End Property

        ''' <summary>Total entries written this session.</summary>
        Public ReadOnly Property EntryCount As Integer
            Get
                Return _entryCount
            End Get
        End Property

        Public Sub New(logger As ILogger(Of DiagnosticLogger))
            _logger = logger
        End Sub

        ' ── Session management ────────────────────────────────────────────────

        ''' <summary>
        ''' Opens a new JSONL log file for the 8-hour diagnostic session.
        ''' Safe to call multiple times — closes any existing session first.
        ''' </summary>
        Public Sub StartSession(contractId As String, strategyName As String)
            SyncLock _lock
                CloseWriterInternal()

                _sessionId = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper()
                _entryCount = 0
                _sessionStartUtc = DateTimeOffset.UtcNow

                Dim ts = _sessionStartUtc.ToString("yyyy-MM-dd_HH-mm-ss",
                                                   System.Globalization.CultureInfo.InvariantCulture)

                ' Sanitise contractId for use in a filename
                Dim invalidChars = Path.GetInvalidFileNameChars()
                Dim safeContract = String.Join("-", contractId.Split(invalidChars))

                Dim dir = DebugTradeDbContext.ResolveDiagnosticsFolder()

                _filePath = Path.Combine(dir, $"diag_{ts}_{safeContract}_{_sessionId}.jsonl")

                _writer = New StreamWriter(_filePath,
                                           append:=False,
                                           encoding:=Encoding.UTF8) With {.AutoFlush = True}

                ' Open companion position CSV
                Dim posPath = Path.Combine(dir, $"positions_{ts}_{safeContract}_{_sessionId}.csv")
                _positionWriter = New StreamWriter(posPath,
                                                   append:=False,
                                                   encoding:=Encoding.UTF8) With {.AutoFlush = True}
                _positionWriter.WriteLine(
                    "Timestamp,Contract,Side,EntryPrice,CurrentPrice,PriceSource," &
                    "PnL_USD,SL_Price,TP_Price,PositionID,SL_Ticks,TP_Ticks")

                ' Human-readable header comments (stripped by JSONL parsers using comment="#")
                _writer.WriteLine($"# TopStepTrader Diagnostic Log")
                _writer.WriteLine($"# Session   : {_sessionId}")
                _writer.WriteLine($"# Contract  : {contractId}")
                _writer.WriteLine($"# Strategy  : {strategyName}")
                _writer.WriteLine($"# Started   : {_sessionStartUtc:o}")
                _writer.WriteLine($"# Format    : JSONL — one JSON object per line; skip lines starting with '#'")
                _writer.WriteLine($"# EventTypes: TRADE | REJECT | NO_SIGNAL")
                _writer.WriteLine($"# Key fields: trade_id (TRADE records written complete on close with outcome)")
                _writer.WriteLine($"#             noise_check.is_sl_inside_noise=true → SL inside bar noise (Bad Settings)")
                _writer.WriteLine($"#             noise_check.effective_slippage_ratio → Spread/SL (>1 = spread > stop!)")
                _writer.WriteLine($"# JSON policy: snake_case keys; null nested sections omitted (WhenWritingNull)")

                _logger.LogInformation(
                    "[Diagnostics] Session {Id} opened → {Path}", _sessionId, _filePath)
            End SyncLock
        End Sub

        ''' <summary>
        ''' Writes a single <see cref="DiagnosticLogEntry"/> to the JSONL file immediately.
        ''' The SessionId is stamped here so callers don't need to track it.
        ''' No-op when no session is active.
        ''' </summary>
        Public Sub WriteEntry(entry As DiagnosticLogEntry)
            If _writer Is Nothing Then Return

            SyncLock _lock
                Try
                    If _writer Is Nothing Then Return
                    entry.SessionId = _sessionId
                    Dim json = JsonSerializer.Serialize(entry, _jsonOpts)
                    _writer.WriteLine(json)
                    _entryCount += 1
                Catch ex As Exception
                    _logger.LogWarning(ex,
                        "[Diagnostics] Write failed for {Type} entry (TradeId={Id})",
                        entry.EventType, entry.TradeId)
                End Try
            End SyncLock
        End Sub

        ''' <summary>
        ''' Appends one row to the companion positions CSV.
        ''' Called every 5 minutes by StrategyExecutionEngine while a position is open.
        ''' No-op when no session is active.
        ''' </summary>
        Public Sub WritePositionSnapshot(contractId As String,
                                          side As String,
                                          entryPrice As Decimal,
                                          currentPrice As Decimal,
                                          priceSource As String,
                                          pnl As Decimal,
                                          slPrice As Decimal,
                                          tpPrice As Decimal,
                                          positionId As Long?,
                                          slTicks As Integer,
                                          tpTicks As Integer)
            If _positionWriter Is Nothing Then Return
            SyncLock _positionLock
                Try
                    If _positionWriter Is Nothing Then Return
                    Dim ts = DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                    Dim posId = If(positionId.HasValue, positionId.Value.ToString(), "")
                    _positionWriter.WriteLine(
                        $"{ts},{contractId},{side},{entryPrice:F4},{currentPrice:F4},{priceSource}," &
                        $"{pnl:F2},{slPrice:F4},{tpPrice:F4},{posId},{slTicks},{tpTicks}")
                Catch ex As Exception
                    _logger.LogWarning(ex, "[Diagnostics] Position snapshot write failed for {Id}", contractId)
                End Try
            End SyncLock
        End Sub

        ''' <summary>
        ''' Writes a bracket state-transition entry to the JSONL log.
        ''' EventType values: BRACKET_TRAIL | FREE_ROLL_ON | POSITION_CLOSED.
        ''' No-op when no session is active.
        ''' </summary>
        Public Sub WriteBracketEvent(eventType As String,
                                      side As String,
                                      price As Decimal,
                                      stopLoss As Decimal,
                                      takeProfit As Decimal,
                                      statusNote As String)
            If _writer Is Nothing Then Return
            SyncLock _lock
                Try
                    If _writer Is Nothing Then Return
                    Dim entry As New DiagnosticLogEntry With {
                        .EventType = eventType,
                        .Action    = side,
                        .Strategy  = "BRACKET",
                        .Why       = statusNote,
                        .Settings  = New DiagSettings With {
                            .SlPrice = stopLoss,
                            .TpPrice = takeProfit
                        },
                        .MetricsAtEntry = New DiagMetricsAtEntry With {
                            .PriceEntry = price
                        }
                    }
                    entry.SessionId = _sessionId
                    Dim json = JsonSerializer.Serialize(entry, _jsonOpts)
                    _writer.WriteLine(json)
                    _entryCount += 1
                Catch ex As Exception
                    _logger.LogWarning(ex, "[Diagnostics] WriteBracketEvent failed for {Type}", eventType)
                End Try
            End SyncLock
        End Sub

        ''' <summary>
        ''' Writes a summary footer comment and closes the log file.
        ''' Called automatically by Stop() and Dispose().
        ''' </summary>
        Public Sub CloseSession()
            SyncLock _lock
                If _writer IsNot Nothing Then
                    Try
                        Dim durationMin = (DateTimeOffset.UtcNow - _sessionStartUtc).TotalMinutes
                        _writer.WriteLine($"# Session closed: {DateTimeOffset.UtcNow:o} | Duration: {durationMin:F1} min | Total entries: {_entryCount}")
                    Catch
                    End Try
                End If
                CloseWriterInternal()
                _logger.LogInformation(
                    "[Diagnostics] Session {Id} closed — {Count} entries written to {Path}",
                    _sessionId, _entryCount, _filePath)
            End SyncLock
        End Sub

        ' ── Private helpers ───────────────────────────────────────────────────

        Private Sub CloseWriterInternal()
            Try
                _writer?.Flush()
                _writer?.Close()
                _writer?.Dispose()
            Catch
            End Try
            _writer = Nothing
            Try
                _positionWriter?.Flush()
                _positionWriter?.Close()
                _positionWriter?.Dispose()
            Catch
            End Try
            _positionWriter = Nothing
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                CloseSession()
                _disposed = True
            End If
        End Sub

    End Class

End Namespace
