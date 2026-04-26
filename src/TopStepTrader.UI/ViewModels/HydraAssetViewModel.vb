Imports System.Threading
Imports System.Windows.Input
Imports System.Windows.Media
Imports TopStepTrader.Core.Events
Imports TopStepTrader.UI.ViewModels.Base

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' Per-asset update-box ViewModel for the Hydra monitoring view.
    ''' Holds the latest confidence snapshot for one instrument and
    ''' the derived market-open / closed state based on a simple schedule rule:
    '''   BTC              → always open (24/7)
    '''   OIL/GOLD/SPX500/EURUSD(M6E) → closed on Saturday and Sunday (CME Globex schedule)
    ''' The rule is isolated here so it can later be replaced with an API-driven check.
    ''' </summary>
    Public Class HydraAssetViewModel
        Inherits ViewModelBase

        ' ── Static identity ───────────────────────────────────────────────────────
        Public ReadOnly Property Symbol As String
        Public ReadOnly Property Icon As String
        Public ReadOnly Property ContractId As String

        ' ── Live confidence state ─────────────────────────────────────────────────
        Private _lastUpdated As String = "—"
        Public Property LastUpdated As String
            Get
                Return _lastUpdated
            End Get
            Set(value As String)
                SetProperty(_lastUpdated, value)
            End Set
        End Property

        Private _summaryLine As String = "Awaiting first bar check…"
        Public Property SummaryLine As String
            Get
                Return _summaryLine
            End Get
            Set(value As String)
                SetProperty(_summaryLine, value)
            End Set
        End Property

        ' ── ADX display ──────────────────────────────────────────────────────────
        Private _adxValueF As Single = -1F   ' sentinel: -1 = no data received yet
        ''' <summary>
        ''' ADX gate threshold from the active risk profile (Lewis=25, Damian=20, Joe=15).
        ''' Received via ConfidenceUpdatedEventArgs.AdxThreshold on each bar tick.
        ''' Default 20.0 (Damian) until first ConfidenceUpdated event arrives.
        ''' </summary>
        Private _adxThreshold As Single = 20.0F

        ''' <summary>
        ''' Minimum confidence % required for a trade to fire (Lewis=90, Damian=80, Joe=70).
        ''' Received via ConfidenceUpdatedEventArgs.MinConfidencePct on each bar tick.
        ''' 0 until the first event arrives (threshold gap label hidden).
        ''' </summary>
        Private _minConfidencePct As Integer = 0

        Public Property AdxValue As Single
            Get
                Return _adxValueF
            End Get
            Set(value As Single)
                Dim safeValue As Single = If(Single.IsNaN(value), -1F, value)
                If safeValue = _adxValueF Then Return
                _adxValueF = safeValue
                NotifyPropertyChanged(NameOf(AdxValue))
                NotifyPropertyChanged(NameOf(AdxDisplay))
                NotifyPropertyChanged(NameOf(AdxLineDisplay))
            End Set
        End Property

        ''' <summary>
        ''' "ADX: X.X ✓" when above profile threshold; "ADX: X.X ✗" when below;
        ''' "ADX: —" until the first value arrives.
        ''' </summary>
        Public ReadOnly Property AdxDisplay As String
            Get
                If _adxValueF < 0 Then Return "ADX: —"
                Dim gate = If(_adxValueF >= _adxThreshold, "✓", "✗")
                Return $"ADX: {_adxValueF:F1} {gate}"
            End Get
        End Property

        ' ── 5-minute price-change buffer ─────────────────────────────────────────
        Private _priceTimes As New List(Of DateTime)()
        Private _pricePrices As New List(Of Decimal)()

        Private _change5mDisplay As String = "5m: —"
        Public Property Change30mDisplay As String   ' name kept for any existing callers
            Get
                Return _change5mDisplay
            End Get
            Private Set(value As String)
                If SetProperty(_change5mDisplay, value) Then
                    NotifyPropertyChanged(NameOf(AdxLineDisplay))
                End If
            End Set
        End Property

        ''' <summary>Combined one-liner: "ADX: 15.3 ✓  |  5m: +0.42%"</summary>
        Public ReadOnly Property AdxLineDisplay As String
            Get
                Return $"{AdxDisplay}  |  {_change5mDisplay}"
            End Get
        End Property

        Private Sub RecordPrice(price As Decimal, timestamp As DateTime)
            _priceTimes.Add(timestamp)
            _pricePrices.Add(price)
            Dim cutoff = timestamp.AddMinutes(-8)
            Dim trim = 0
            While trim < _priceTimes.Count AndAlso _priceTimes(trim) < cutoff
                trim += 1
            End While
            If trim > 0 Then
                _priceTimes.RemoveRange(0, trim)
                _pricePrices.RemoveRange(0, trim)
            End If
            Dim target = timestamp.AddMinutes(-5)
            Dim bestIdx As Integer = -1
            Dim bestDiff = TimeSpan.MaxValue
            For j = 0 To _priceTimes.Count - 1
                Dim diff = (target - _priceTimes(j)).Duration()
                If diff < bestDiff AndAlso diff <= TimeSpan.FromMinutes(2) Then
                    bestDiff = diff
                    bestIdx = j
                End If
            Next
            If bestIdx >= 0 AndAlso _pricePrices(bestIdx) > 0 Then
                Dim pct = ((price - _pricePrices(bestIdx)) / _pricePrices(bestIdx)) * 100D
                Change30mDisplay = If(pct >= 0, $"5m: +{pct:F2}%", $"5m: {pct:F2}%")
            End If
        End Sub

        Private _upPct As Integer = 0
        Private _adxGatePassed As Boolean = True

        Public Property UpPct As Integer
            Get
                Return _upPct
            End Get
            Set(value As Integer)
                If SetProperty(_upPct, value) Then
                    NotifyPropertyChanged(NameOf(ConfidenceColor))
                    NotifyPropertyChanged(NameOf(DirectionForeground))
                    NotifyPropertyChanged(NameOf(DownPct))
                End If
            End Set
        End Property

        ''' <summary>Bear score = 100 - UpPct.</summary>
        Public ReadOnly Property DownPct As Integer
            Get
                Return 100 - _upPct
            End Get
        End Property

        Public Property AdxGatePassed As Boolean
            Get
                Return _adxGatePassed
            End Get
            Set(value As Boolean)
                If SetProperty(_adxGatePassed, value) Then
                    NotifyPropertyChanged(NameOf(ConfidenceColor))
                    NotifyPropertyChanged(NameOf(ThresholdGapVisibility))
                End If
            End Set
        End Property

        ''' <summary>
        ''' Dominant confidence percentage (max of UP / DOWN scores).
        ''' Used by StatusForeground to switch to ForestGreen when ≥ 80%.
        ''' </summary>
        Private _currentConfidencePct As Integer = 0
        Public Property CurrentConfidencePct As Integer
            Get
                Return _currentConfidencePct
            End Get
            Private Set(value As Integer)
                If SetProperty(_currentConfidencePct, value) Then
                    NotifyPropertyChanged(NameOf(StatusForeground))
                    NotifyPropertyChanged(NameOf(TileBackground))
                    NotifyPropertyChanged(NameOf(ThresholdGapText))
                    NotifyPropertyChanged(NameOf(ThresholdGapVisibility))
                End If
            End Set
        End Property

        ' ── Market open / closed state ────────────────────────────────────────────
        Private _isMarketOpen As Boolean = False
        Public Property IsMarketOpen As Boolean
            Get
                Return _isMarketOpen
            End Get
            Private Set(value As Boolean)
                    If SetProperty(_isMarketOpen, value) Then
                        NotifyPropertyChanged(NameOf(AssetStatusText))
                        NotifyPropertyChanged(NameOf(StatusForeground))
                        NotifyPropertyChanged(NameOf(StatusBorderBrush))
                        NotifyPropertyChanged(NameOf(CardForeground))
                        NotifyPropertyChanged(NameOf(ConfidenceColor))
                        NotifyPropertyChanged(NameOf(TileBackground))
                    End If
                End Set
            End Property

        ''' <summary>"OPEN" or "CLOSED" — drives the status badge in the card.</summary>
        Public ReadOnly Property AssetStatusText As String
            Get
                Return If(_isMarketOpen, "OPEN", "CLOSED")
            End Get
        End Property

        ''' <summary>
        ''' Foreground colour for the OPEN / CLOSED status badge:
        '''   CLOSED  → Red
        '''   OPEN    → ForestGreen (always, consistent with whole-card requirement)
        ''' </summary>
        Public ReadOnly Property StatusForeground As SolidColorBrush
            Get
                If Not _isMarketOpen Then
                    Return New SolidColorBrush(Colors.Red)
                End If
                Return New SolidColorBrush(Color.FromRgb(&H22, &H8B, &H22))  ' ForestGreen
            End Get
        End Property

        ''' <summary>
        ''' Base foreground for the entire asset card.
        ''' ForestGreen when the market is open (tradeable); white otherwise.
        ''' Setting this on the container causes it to cascade to all child TextBlocks
        ''' that do not override Foreground explicitly.
        ''' </summary>
        Public ReadOnly Property CardForeground As SolidColorBrush
            Get
                If _isMarketOpen Then
                    Return New SolidColorBrush(Color.FromRgb(&H22, &H8B, &H22))  ' ForestGreen
                End If
                Return New SolidColorBrush(Colors.White)
            End Get
        End Property

        ''' <summary>
        ''' Full tile background: dark green when market is open and confidence ≥ 80%,
        ''' otherwise the standard card colour.
        ''' </summary>
        Public ReadOnly Property TileBackground As SolidColorBrush
            Get
                If _isMarketOpen AndAlso _currentConfidencePct >= 80 Then
                    Return New SolidColorBrush(Color.FromArgb(&HFF, &H0D, &H3A, &H18))
                End If
                Return New SolidColorBrush(Color.FromArgb(&HFF, &H24, &H31, &H56))
            End Get
        End Property

        ''' <summary>
        ''' Text shown below the confidence summary when confidence is close to but below the
        ''' trade threshold — e.g. "need 90%" when sitting at 86% with Lewis.
        ''' Empty string when the gap label should not be shown.
        ''' </summary>
        Public ReadOnly Property ThresholdGapText As String
            Get
                If _minConfidencePct <= 0 OrElse Not _isMarketOpen OrElse Not _adxGatePassed Then
                    Return String.Empty
                End If
                If _currentConfidencePct >= _minConfidencePct Then Return String.Empty
                If _currentConfidencePct < _minConfidencePct - 15 Then Return String.Empty
                Return $"need {_minConfidencePct}%"
            End Get
        End Property

        ''' <summary>
        ''' Visibility for the threshold gap label — Visible only when within 15 points of
        ''' the trade threshold but not yet across it.
        ''' </summary>
        Public ReadOnly Property ThresholdGapVisibility As System.Windows.Visibility
            Get
                Return If(ThresholdGapText.Length > 0,
                          System.Windows.Visibility.Visible,
                          System.Windows.Visibility.Collapsed)
            End Get
        End Property

        ''' <summary>
        ''' Amber foreground for the threshold gap label.
        ''' Bright amber when within 3 points of firing; dim amber when 4–15 points away.
        ''' </summary>
        Public ReadOnly Property ThresholdGapColor As SolidColorBrush
            Get
                If _minConfidencePct > 0 AndAlso (_minConfidencePct - _currentConfidencePct) <= 3 Then
                    Return New SolidColorBrush(Color.FromRgb(&HFF, &H95, &H00))  ' bright amber
                End If
                Return New SolidColorBrush(Color.FromRgb(&HB8, &H86, &H0B))     ' dim amber/gold
            End Get
        End Property

        ''' <summary>
        ''' Top-border accent colour for the asset card:
        '''   Market open  → Forest Green
        '''   Market closed → Red
        ''' </summary>
        Public ReadOnly Property StatusBorderBrush As SolidColorBrush
            Get
                If _isMarketOpen Then
                    Return New SolidColorBrush(Color.FromRgb(&H22, &H8B, &H22))
                End If
                Return New SolidColorBrush(Colors.Red)
            End Get
        End Property

        ''' <summary>
        ''' Foreground colour for the direction / confidence summary line:
        '''   Long bias (upPct > 50)  → Forest Green
        '''   Short bias (upPct &lt; 50) → Red
        '''   Neutral / no data      → muted grey
        ''' </summary>
        Public ReadOnly Property DirectionForeground As SolidColorBrush
            Get
                If _upPct > 50 Then
                    Return New SolidColorBrush(Color.FromRgb(&H22, &H8B, &H22))
                ElseIf _upPct < 50 AndAlso _upPct > 0 Then
                    Return New SolidColorBrush(Colors.Red)
                End If
                Return New SolidColorBrush(Color.FromArgb(&HFF, &H80, &H80, &HA0))
            End Get
        End Property

        ''' <summary>
        ''' Confidence bar colour (unchanged from original — separate from status badge):
        '''   ADX suppressed        → amber  (#FF9500)
        '''   dominant score ≥ 85%  → green  (#27AE60)
        '''   dominant score ≤ 35%  → red    (#E5533A)
        '''   otherwise             → muted  (#8080A0)
        ''' </summary>
        Public ReadOnly Property ConfidenceColor As SolidColorBrush
            Get
                If _isMarketOpen Then
                    Return New SolidColorBrush(Color.FromRgb(&H22, &H8B, &H22))  ' ForestGreen
                End If
                If _upPct = 0 Then
                    Return New SolidColorBrush(Color.FromArgb(&HFF, &H80, &H80, &HA0))
                End If
                If Not _adxGatePassed Then
                    Return New SolidColorBrush(Color.FromRgb(&HFF, &H95, &H00))
                End If
                Dim dominant = Math.Max(_upPct, 100 - _upPct)
                If dominant >= 85 Then
                    Return New SolidColorBrush(Color.FromRgb(&H27, &HAE, &H60))
                End If
                If dominant <= 35 Then
                    Return New SolidColorBrush(Color.FromRgb(&HE5, &H53, &H3A))
                End If
                Return New SolidColorBrush(Color.FromArgb(&HFF, &H80, &H80, &HA0))
            End Get
        End Property

        ''' <summary>
        ''' Updates the Close price and timestamp in the indicator grid from a live price tick
        ''' (e.g. the 15-second bar close).  Called independently of the full ConfidenceUpdated
        ''' cycle so the grid refreshes between strategy-timeframe bar closes.
        ''' </summary>
        Public Sub UpdateLivePrice(price As Decimal)
            If price <= 0D Then Return
            GridClose = $"{price:F2}"
            LastUpdated = DateTime.Now.ToString("HH:mm:ss")
        End Sub

        ' ── Hydra indicator grid display properties ──────────────────────────────

        Private _gridClose As String = "—"
        Public Property GridClose As String
            Get
                Return _gridClose
            End Get
            Private Set(value As String)
                SetProperty(_gridClose, value)
            End Set
        End Property

        Private _gridCloud1 As String = "—"
        Public Property GridCloud1 As String
            Get
                Return _gridCloud1
            End Get
            Private Set(value As String)
                SetProperty(_gridCloud1, value)
            End Set
        End Property

        Private _gridCloud2 As String = "—"
        Public Property GridCloud2 As String
            Get
                Return _gridCloud2
            End Get
            Private Set(value As String)
                SetProperty(_gridCloud2, value)
            End Set
        End Property

        Private _gridTenkan As String = "—"
        Public Property GridTenkan As String
            Get
                Return _gridTenkan
            End Get
            Private Set(value As String)
                SetProperty(_gridTenkan, value)
            End Set
        End Property

        Private _gridKijun As String = "—"
        Public Property GridKijun As String
            Get
                Return _gridKijun
            End Get
            Private Set(value As String)
                SetProperty(_gridKijun, value)
            End Set
        End Property

        Private _gridEma21 As String = "—"
        Public Property GridEma21 As String
            Get
                Return _gridEma21
            End Get
            Private Set(value As String)
                SetProperty(_gridEma21, value)
            End Set
        End Property

        Private _gridEma50 As String = "—"
        Public Property GridEma50 As String
            Get
                Return _gridEma50
            End Get
            Private Set(value As String)
                SetProperty(_gridEma50, value)
            End Set
        End Property

        Private _gridAdx As String = "—"
        Public Property GridAdx As String
            Get
                Return _gridAdx
            End Get
            Private Set(value As String)
                SetProperty(_gridAdx, value)
            End Set
        End Property

        Private _gridDiPlus As String = "—"
        Public Property GridDiPlus As String
            Get
                Return _gridDiPlus
            End Get
            Private Set(value As String)
                SetProperty(_gridDiPlus, value)
            End Set
        End Property

        Private _gridDiMinus As String = "—"
        Public Property GridDiMinus As String
            Get
                Return _gridDiMinus
            End Get
            Private Set(value As String)
                SetProperty(_gridDiMinus, value)
            End Set
        End Property

        Private _gridMacd As String = "—"
        Public Property GridMacd As String
            Get
                Return _gridMacd
            End Get
            Private Set(value As String)
                SetProperty(_gridMacd, value)
            End Set
        End Property

        Private _gridMacdPrev As String = "—"
        Public Property GridMacdPrev As String
            Get
                Return _gridMacdPrev
            End Get
            Private Set(value As String)
                SetProperty(_gridMacdPrev, value)
            End Set
        End Property

        Private _gridStochRsi As String = "—"
        Public Property GridStochRsi As String
            Get
                Return _gridStochRsi
            End Get
            Private Set(value As String)
                SetProperty(_gridStochRsi, value)
            End Set
        End Property

        Private _gridRsi14 As String = "—"
        Public Property GridRsi14 As String
            Get
                Return _gridRsi14
            End Get
            Private Set(value As String)
                SetProperty(_gridRsi14, value)
            End Set
        End Property

        Private _gridLongScore As String = "—"
        Public Property GridLongScore As String
            Get
                Return _gridLongScore
            End Get
            Private Set(value As String)
                SetProperty(_gridLongScore, value)
            End Set
        End Property

        Private _gridShortScore As String = "—"
        Public Property GridShortScore As String
            Get
                Return _gridShortScore
            End Get
            Private Set(value As String)
                SetProperty(_gridShortScore, value)
            End Set
        End Property

        Private _gridVidya As String = "—"
        Public Property GridVidya As String
            Get
                Return _gridVidya
            End Get
            Private Set(value As String)
                SetProperty(_gridVidya, value)
            End Set
        End Property

        Private _gridCmo As String = "—"
        Public Property GridCmo As String
            Get
                Return _gridCmo
            End Get
            Private Set(value As String)
                SetProperty(_gridCmo, value)
            End Set
        End Property

        Private _gridDeltaVol As String = "—"
        Public Property GridDeltaVol As String
            Get
                Return _gridDeltaVol
            End Get
            Private Set(value As String)
                SetProperty(_gridDeltaVol, value)
            End Set
        End Property

        Private _gridConf As String = "—"
        Public Property GridConf As String
            Get
                Return _gridConf
            End Get
            Private Set(value As String)
                SetProperty(_gridConf, value)
            End Set
        End Property

        Private _gridExplain As String = "—"
        Public Property GridExplain As String
            Get
                Return _gridExplain
            End Get
            Private Set(value As String)
                SetProperty(_gridExplain, value)
            End Set
        End Property

        Public Sub New(symbol As String, icon As String, contractId As String)
            Me.Symbol = symbol
            Me.Icon = icon
            Me.ContractId = contractId
            RefreshMarketStatus()
        End Sub

        ''' <summary>
        ''' Crypto assets that trade 24/7 — always considered open regardless of day-of-week.
        ''' Add new tickers here as the universe expands.
        ''' </summary>
        Private Shared ReadOnly CryptoSymbols As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            "BTC", "ETH", "XRP", "SOL", "BNB", "ADA", "DOGE", "AVAX", "MATIC", "DOT"
        }

        ''' <summary>
        ''' Recomputes IsMarketOpen from the current time expressed in US Central Time (America/Chicago).
        ''' Rules:
        '''   Crypto symbols (BTC, ETH, ...) -> always open (24/7).
        '''   All other assets (indices, OIL, GOLD, EUR/USD M6E, ...) follow CME Globex hours
        '''   evaluated in CT so DST transitions are handled correctly:
        '''     - Closed all day Saturday (CT).
        '''     - Closed Sunday before 17:00 CT  (CME reopens 5:00 PM CT).
        '''     - Closed daily 16:00-17:00 CT    (CME maintenance 4:00-5:00 PM CT).
        ''' Isolated here so a future API-driven implementation replaces only this method.
        ''' </summary>
        Public Sub RefreshMarketStatus()
            Dim open As Boolean
            If CryptoSymbols.Contains(Symbol) Then
                open = True
            Else
                ' Convert UTC to US Central Time. "Central Standard Time" is the Windows TZ ID
                ' for America/Chicago and handles CDT (UTC-5) and CST (UTC-6) DST automatically.
                ' Evaluating CME schedule in CT avoids the 1-hour shift that previously caused all
                ' assets to show CLOSED at 21:00 UTC in winter (= 3 PM CST = active trading hours).
                Dim centralTz As TimeZoneInfo
                Try
                    centralTz = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time")
                Catch
                    centralTz = TimeZoneInfo.Utc  ' safe fallback on non-Windows hosts
                End Try
                Dim ctNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, centralTz)
                Dim day = ctNow.DayOfWeek
                Dim hour = ctNow.Hour

                ' Closed all day Saturday
                If day = DayOfWeek.Saturday Then
                    open = False
                ' Closed Sunday before 17:00 CT (CME reopens 5:00 PM CT)
                ElseIf day = DayOfWeek.Sunday AndAlso hour < 17 Then
                    open = False
                ' Closed daily during CME maintenance 16:00-17:00 CT (4:00-5:00 PM CT)
                ElseIf hour = 16 Then
                    open = False
                Else
                    open = True
                End If
            End If
            IsMarketOpen = open
        End Sub

        ''' <summary>
        ''' Called from the engine ConfidenceUpdated event on the UI dispatcher.
        ''' Updates all display properties atomically and refreshes market status.
        ''' When <see cref="ConfidenceUpdatedEventArgs.IsDisplayOnly"/> is True only the
        ''' indicator grid columns are refreshed — scores, SummaryLine, and tile state are
        ''' intentionally left unchanged (this is a 15-second live-bar display tick).
        ''' </summary>
        Public Sub ApplyConfidence(e As ConfidenceUpdatedEventArgs)
            ' ── Live 15-second display-only refresh ───────────────────────────────
            If e.IsDisplayOnly Then
                ' Only update the Close and Ichimoku/EMA/ADX grid columns so the UI shows
                ' intra-bar indicator movement without touching scores or SummaryLine.
                If e.LastClose > 0D Then
                    GridClose = $"{e.LastClose:F2}"
                    LastUpdated = DateTime.Now.ToString("HH:mm:ss")
                    RecordPrice(e.LastClose, DateTime.Now)
                End If
                If e.Cloud1 > 0D Then GridCloud1 = $"{e.Cloud1:F2}"
                If e.Cloud2 > 0D Then GridCloud2 = $"{e.Cloud2:F2}"
                If e.Tenkan > 0D Then GridTenkan = $"{e.Tenkan:F2}"
                If e.Kijun > 0D Then GridKijun = $"{e.Kijun:F2}"
                If e.Ema21 > 0D Then GridEma21 = $"{e.Ema21:F2}"
                If e.Ema50 > 0D Then GridEma50 = $"{e.Ema50:F2}"
                GridAdx = $"{e.AdxValue:F1}"
                GridDiPlus = $"{e.PlusDI:F1}"
                GridDiMinus = $"{e.MinusDI:F1}"
                Return
            End If
            ' ── Market-closed / no-data transition ────────────────────────────────
            ' Fired once by the engine when bars = 0 or last bar becomes stale.
            ' Consult the time-based schedule first: if the market SHOULD be open per
            ' CME hours, the lack of bars is a data issue, not a closure.
            If e.IsMarketClosed Then
                RefreshMarketStatus()
                If IsMarketOpen Then
                    ' Market is open per schedule but the engine has no data — show a
                    ' distinct "awaiting" state so the user knows it is NOT closed.
                    SummaryLine = "⏳ Awaiting data…"
                Else
                    SummaryLine = "⏸ Closed"
                End If
                LastUpdated = DateTime.Now.ToString("HH:mm:ss")
                UpPct = 50
                AdxGatePassed = False
                Return
            End If

            ' ── Sync ADX threshold from the active risk profile ──────────────────────
            ' AdxThreshold is carried on every event so the tile gate indicator (✓/✗)
            ' always reflects the current profile without requiring a separate channel.
            If e.AdxThreshold > 0F AndAlso e.AdxThreshold <> _adxThreshold Then
                _adxThreshold = e.AdxThreshold
                NotifyPropertyChanged(NameOf(AdxDisplay))
                NotifyPropertyChanged(NameOf(AdxLineDisplay))
            End If

            If e.MinConfidencePct > 0 AndAlso e.MinConfidencePct <> _minConfidencePct Then
                _minConfidencePct = e.MinConfidencePct
                NotifyPropertyChanged(NameOf(ThresholdGapText))
                NotifyPropertyChanged(NameOf(ThresholdGapVisibility))
            End If

            Dim isUp = (e.UpPct >= e.DownPct)
            Dim dominant = If(isUp, e.UpPct, e.DownPct)
            Dim tradeLabel = If(isUp, "Long", "Short")
            Dim arrow = If(isUp, "↑", "↓")

            ' ── ADX-gated effective confidence ────────────────────────────────────
            ' For the EmaRsi strategy (TotalConditions = 6) the ADX trend-strength
            ' gate is a binary prerequisite that sits OUTSIDE the six-condition
            ' weighted score.  Displaying the raw score (e.g. 100%) when ADX is
            ' failing is misleading: no trade will fire regardless of how strongly
            ' the other indicators are aligned.
            '
            ' Rule: effectiveDominant = 0 whenever the ADX gate blocks the signal.
            '       Direction (arrow / tradeLabel) is preserved from the raw score
            '       so the tile still reads "↓ Short — 0%" rather than a neutral
            '       50/50 — making it clear the bearish setup is intact but waiting
            '       for trend-strength confirmation.
            '
            ' MultiConfluence (TotalConditions = 7) embeds ADX as a counted
            ' confluence condition so AdxGatePassed is always True there — this
            ' branch never fires for that strategy.
            Dim effectiveDominant As Integer =
                If(e.TotalConditions = 6 AndAlso Not e.AdxGatePassed, 0, dominant)

            SummaryLine = $"{arrow} {tradeLabel} — {effectiveDominant}%"
            LastUpdated = DateTime.Now.ToString("HH:mm:ss")
            UpPct = e.UpPct
            AdxGatePassed = e.AdxGatePassed
            ' VIDYA does not use ADX — keep the sentinel (-1 → "ADX: —") rather than
            ' overwriting it with deltaNow which was piggy-backed into the AdxValue slot.
            If e.TotalConditions <> -1 Then AdxValue = e.AdxValue
            CurrentConfidencePct = effectiveDominant
            If e.LastClose > 0 Then RecordPrice(e.LastClose, DateTime.Now)
            RefreshMarketStatus()

            ' ── Indicator grid display ────────────────────────────────────────────
            If e.LastClose > 0D Then GridClose = $"{e.LastClose:F2}"
            If e.TotalConditions = 7 Then
                ' ── Multi-Confluence: populate all Ichimoku + MACD + StochRSI columns ──
                If e.Cloud1 > 0D Then GridCloud1 = $"{e.Cloud1:F2}"
                If e.Cloud2 > 0D Then GridCloud2 = $"{e.Cloud2:F2}"
                If e.Tenkan > 0D Then GridTenkan = $"{e.Tenkan:F2}"
                If e.Kijun > 0D Then GridKijun = $"{e.Kijun:F2}"
                If e.Ema21 > 0D Then GridEma21 = $"{e.Ema21:F2}"
                If e.Ema50 > 0D Then GridEma50 = $"{e.Ema50:F2}"
                GridAdx = $"{e.AdxValue:F1}"
                GridDiPlus = $"{e.PlusDI:F1}"
                GridDiMinus = $"{e.MinusDI:F1}"
                GridMacd = $"{e.MacdHist:F4}"
                GridMacdPrev = $"{e.MacdHistPrev:F4}"
                GridStochRsi = $"{e.StochRsiK:F1}"
                GridLongScore = $"{e.LongCount}/{e.TotalConditions}"
                GridShortScore = $"{e.ShortCount}/{e.TotalConditions}"
            ElseIf e.TotalConditions = 6 Then
                ' ── EMA/RSI Combined: populate EMA21/50, RSI14, ADX, DI columns ──
                If e.Ema21 > 0D Then GridEma21 = $"{e.Ema21:F2}"
                If e.Ema50 > 0D Then GridEma50 = $"{e.Ema50:F2}"
                GridRsi14 = If(e.Rsi14 > 0F, $"{e.Rsi14:F1}", "—")
                GridAdx = $"{e.AdxValue:F1}"
                If e.PlusDI > 0F Then GridDiPlus = $"{e.PlusDI:F1}"
                If e.MinusDI > 0F Then GridDiMinus = $"{e.MinusDI:F1}"
            ElseIf e.TotalConditions = -1 Then
                ' ── VIDYA Cross: populate VIDYA, CMO, ΔVol columns ──────────────
                If e.VidyaValue > 0D Then
                    Dim crossIndicator = If(e.LastClose > e.VidyaValue, " ▲", " ▼")
                    GridVidya = $"{e.VidyaValue:F2}{crossIndicator}"
                End If
                GridCmo = $"{e.CmoValue:F1}"
                Dim dvSign = If(e.DeltaVol >= 0, "+", "")
                GridDeltaVol = $"{dvSign}{e.DeltaVol * 100:F1}%"
            ElseIf e.AdxValue > 0F Then
                GridAdx = $"{e.AdxValue:F1}"
            End If
            Dim maxPct = Math.Max(e.UpPct, e.DownPct)
            If maxPct > 0 Then
                ' GridConf matches the headline SummaryLine: show effectiveDominant so the
                ' indicator grid column is consistent with the tile confidence badge.
                GridConf = $"{effectiveDominant}% {If(e.UpPct > e.DownPct, "Long", "Short")}"
            End If
            GridExplain = BuildExplainText(e)
        End Sub

        ''' <summary>
        ''' Routes to the correct strategy explain builder based on <see cref="ConfidenceUpdatedEventArgs.TotalConditions"/>.
        ''' TotalConditions = 7 → Multi-Confluence (3-line Ichimoku/MACD/StochRSI summary).
        ''' TotalConditions = 6 → EMA/RSI Combined (3-line EMA/RSI/ADX summary).
        ''' Otherwise → "—".
        ''' </summary>
        Private Shared Function BuildExplainText(e As ConfidenceUpdatedEventArgs) As String
            If e.TotalConditions = 7 Then
                Return BuildMultiConfluenceExplain(e)
            ElseIf e.TotalConditions = 6 Then
                Return BuildEmaRsiExplain(e)
            ElseIf e.TotalConditions = -1 Then
                Return BuildVidyaExplain(e)
            End If
            Return "—"
        End Function

        ''' <summary>
        ''' Status line for the VIDYA Cross strategy showing cross state and gate result.
        ''' </summary>
        Private Shared Function BuildVidyaExplain(e As ConfidenceUpdatedEventArgs) As String
            If e.VidyaValue = 0D Then Return "Waiting for VIDYA data..."
            Const Gate As Single = 0.2F
            Dim crossAbove = e.LastClose > e.VidyaValue
            Dim crossDir = If(crossAbove, "above ▲", "below ▼")
            Dim volGatePass = Math.Abs(e.DeltaVol) >= Gate
            Dim volStr = $"ΔVol={If(e.DeltaVol >= 0, "+", "")}{e.DeltaVol * 100:F1}%"
            Dim gateStr = If(volGatePass, $"{volStr} ✓ (≥±20%)", $"{volStr} ✗ (need ±20%)")
            Return $"Price {crossDir} VIDYA({e.VidyaValue:F2})" & vbLf & gateStr
        End Function

        ''' <summary>
        ''' Negative-only summary for the EMA/RSI Combined strategy.
        ''' Only conditions that are failing (✗) are shown; returns "—" when all pass.
        ''' </summary>
        Private Shared Function BuildEmaRsiExplain(e As ConfidenceUpdatedEventArgs) As String
            Dim isLong = e.UpPct >= e.DownPct
            Dim negatives As New List(Of String)()
            If isLong Then
                If Not (e.Ema21 > e.Ema50) Then negatives.Add($"EMA21({e.Ema21:F0}) below EMA50({e.Ema50:F0}) ✗")
                If Not (e.LastClose > e.Ema21) Then negatives.Add($"Price below EMA21({e.Ema21:F0}) ✗")
                If Not (e.LastClose > e.Ema50) Then negatives.Add($"Price below EMA50({e.Ema50:F0}) ✗")
                If Not (e.Rsi14 >= 50F AndAlso e.Rsi14 < 70F) Then negatives.Add($"RSI14={e.Rsi14:F1} outside 50-70 trend zone ✗")
                If Not e.Ema21Rising Then negatives.Add("EMA21 falling ✗")
                If Not e.RecentCandlesBullish Then negatives.Add("3-bar bias: majority red ✗")
                If Not e.AdxGatePassed Then negatives.Add($"Trend strength too weak (ADX={e.AdxValue:F1}) ✗")
            Else
                If Not (e.Ema21 < e.Ema50) Then negatives.Add($"EMA21({e.Ema21:F0}) above EMA50({e.Ema50:F0}) ✗")
                If Not (e.LastClose < e.Ema21) Then negatives.Add($"Price above EMA21({e.Ema21:F0}) ✗")
                If Not (e.LastClose < e.Ema50) Then negatives.Add($"Price above EMA50({e.Ema50:F0}) ✗")
                If Not (e.Rsi14 >= 30F AndAlso e.Rsi14 < 50F) Then negatives.Add($"RSI14={e.Rsi14:F1} outside 30-50 downtrend zone ✗")
                If e.Ema21Rising Then negatives.Add("EMA21 rising ✗")
                If e.RecentCandlesBullish Then negatives.Add("3-bar bias: majority green ✗")
                If Not e.AdxGatePassed Then negatives.Add($"Trend strength too weak (ADX={e.AdxValue:F1}) ✗")
            End If
            Return If(negatives.Count > 0, String.Join(vbLf, negatives), "—")
        End Function

        ''' <summary>
        ''' Negative-only summary for the Multi-Confluence strategy.
        ''' Only conditions that are failing (✗) are shown; returns "—" when all pass.
        ''' </summary>
        Private Shared Function BuildMultiConfluenceExplain(e As ConfidenceUpdatedEventArgs) As String
            Dim isLong = e.UpPct >= e.DownPct
            Dim negatives As New List(Of String)()
            If isLong Then
                Dim cloudOk = e.LastClose > e.Cloud1
                Dim tkKjOk = e.Tenkan > e.Kijun
                Dim adxOk = CSng(e.AdxValue) >= e.AdxThreshold AndAlso e.PlusDI > e.MinusDI
                Dim macdOk = CSng(e.MacdHist) > 0F AndAlso CSng(e.MacdHist) > CSng(e.MacdHistPrev)
                Dim stochOk = CSng(e.StochRsiK) < 0.8F
                If Not cloudOk Then negatives.Add("Price hasn't cleared the Ichimoku cloud ✗")
                If Not tkKjOk Then negatives.Add("Tenkan is below Kijun ✗")
                If Not adxOk Then negatives.Add($"Trend strength (ADX) not yet strong enough / no bullish bias — need ADX≥{e.AdxThreshold:F0} ✗")
                If Not macdOk Then negatives.Add("MACD needs to turn up ✗")
                If Not stochOk Then negatives.Add("StochRSI is overbought — wait ✗")
            Else
                Dim cloudOk = e.LastClose < e.Cloud2
                Dim tkKjOk = e.Tenkan < e.Kijun
                Dim adxOk = CSng(e.AdxValue) >= e.AdxThreshold AndAlso e.MinusDI > e.PlusDI
                Dim macdOk = CSng(e.MacdHist) < 0F AndAlso CSng(e.MacdHist) < CSng(e.MacdHistPrev)
                Dim stochOk = CSng(e.StochRsiK) > 0.2F
                If Not cloudOk Then negatives.Add("Price hasn't broken under the Ichimoku cloud ✗")
                If Not tkKjOk Then negatives.Add("Tenkan is above Kijun ✗")
                If Not adxOk Then negatives.Add($"Trend strength (ADX) not yet strong enough / no bearish bias — need ADX≥{e.AdxThreshold:F0} ✗")
                If Not macdOk Then negatives.Add("MACD needs to turn down ✗")
                If Not stochOk Then negatives.Add("StochRSI is oversold — wait ✗")
            End If
            Return If(negatives.Count > 0, String.Join(vbLf, negatives), "—")
        End Function

        ' ── Live position / trail bracket display ─────────────────────────────────

        Private _tradeStatusLine As String = "—  No position"
        Public Property TradeStatusLine As String
            Get
                Return _tradeStatusLine
            End Get
            Private Set(value As String)
                SetProperty(_tradeStatusLine, value)
            End Set
        End Property

        ' ── P&L flash foreground ──────────────────────────────────────────────────
        Private Shared ReadOnly _pnlDefaultBrush As New SolidColorBrush(Color.FromRgb(&H27, &HAE, &H60))  ' AccentBrush
        Private Shared ReadOnly _pnlProfitBrush  As New SolidColorBrush(Color.FromRgb(&H90, &HEE, &H90))  ' LightGreen
        Private Shared ReadOnly _pnlLossBrush    As New SolidColorBrush(Colors.Red)

        Private _tradeStatusForeground As SolidColorBrush = _pnlDefaultBrush
        Private _pnlFlashCts As CancellationTokenSource

        Public Property TradeStatusForeground As SolidColorBrush
            Get
                Return _tradeStatusForeground
            End Get
            Private Set(value As SolidColorBrush)
                SetProperty(_tradeStatusForeground, value)
            End Set
        End Property

        Private Async Sub FlashPnlAsync(isProfit As Boolean)
            _pnlFlashCts?.Cancel()
            _pnlFlashCts = New CancellationTokenSource()
            Dim ct = _pnlFlashCts.Token
            TradeStatusForeground = If(isProfit, _pnlProfitBrush, _pnlLossBrush)
            Try
                Await Task.Delay(500, ct)
                TradeStatusForeground = _pnlDefaultBrush
            Catch ex As OperationCanceledException
            End Try
        End Sub

        ' Counts open positions: 1 on initial entry, increments on each scale-in.
        ' Reset to 0 by CloseTrade so the next session starts fresh.
        Private _positionCount As Integer = 0

        ''' <summary>
        ''' Most recent engine-computed unrealised P&amp;L (set every PositionSynced tick, ~5 s).
        ''' Used by HydraViewModel.ForceCloseMonitorLoopAsync so it operates on the same
        ''' real-time MarketHub price as the tile display instead of the stale 15-second bar.
        ''' </summary>
        Public Property LastLivePnl As Decimal = 0D

        ''' <summary>True once at least one PositionSynced tick has supplied a P&amp;L value.</summary>
        Public Property HasLivePnl As Boolean = False

        ' Set by MarkAsPrePopulated() when CheckExistingPositionsAsync pre-populates the
        ' tile at view load (before the engine starts). Cleared on the next OpenTrade call
        ' so the engine's startup TradeOpened resets count to 1 instead of incrementing
        ' on top of the pre-populated state (which would display ×2 for a single position).
        Private _wasPrePopulated As Boolean = False

        ''' <summary>
        ''' Marks this tile as pre-populated from the view-load position check.
        ''' The next OpenTrade call (engine startup TradeOpened) will reset the
        ''' position count to 1 rather than doubling it.
        ''' </summary>
        Public Sub MarkAsPrePopulated()
            _wasPrePopulated = True
        End Sub

        ''' <summary>
        ''' Called when the engine opens a new position on this asset.
        ''' Also called for each scale-in — position count increments each time.
        ''' If the tile was pre-populated at view load, the count is reset first so
        ''' the engine's attach counts as 1, not 2.
        ''' </summary>
        Public Sub OpenTrade(side As Core.Enums.OrderSide, entryPrice As Decimal, amount As Decimal)
            If _wasPrePopulated Then
                _positionCount = 0
                _wasPrePopulated = False
            End If
            _positionCount += 1
            Dim sideLabel = If(side = Core.Enums.OrderSide.Buy, "LONG", "SHORT")
            Dim countSuffix = If(_positionCount > 1, $" (×{_positionCount})", "")
            Dim sizeStr = $"{Math.Max(1, CInt(Math.Ceiling(amount)))}x"
            TradeStatusLine = $"{sideLabel}{countSuffix}  {sizeStr}  P&L: —"
            NotifyPropertyChanged(NameOf(HasOpenPosition))
        End Sub

        ''' <summary>
        ''' Called each PositionSynced tick to update the live P&amp;L and position size on the card.
        ''' When amount/positionCount are supplied the size portion of the status line is rebuilt
        ''' from the broker snapshot, so the tile self-corrects after scale-ins, partial closes,
        ''' or if the initial TradeOpened event carried stale data.
        ''' </summary>
        Public Sub UpdateTradePnl(unrealizedPnlUsd As Decimal,
                                   Optional amount As Decimal = -1D,
                                   Optional isBuy As Boolean = True,
                                   Optional positionCount As Integer = -1)
            ' Cache the engine-computed P&L so the force close monitor reads the same
            ' real-time MarketHub price as the tile — not the stale 15-second bar.
            LastLivePnl = unrealizedPnlUsd
            HasLivePnl = True

            ' Allow self-heal: if the broker confirms positions are still open, let the
            ' update through even if _positionCount was incorrectly zeroed by a premature
            ' TradeClosed (e.g. a transient API miss that crossed SyncMissThreshold).
            If _positionCount = 0 AndAlso positionCount < 1 Then Return

            ' Rebuild the full status line from live broker data when position metadata is available.
            If amount >= 0D AndAlso positionCount >= 1 Then
                _positionCount = positionCount
                Dim sideLabel = If(isBuy, "LONG", "SHORT")
                Dim countSuffix = If(_positionCount > 1, $" (×{_positionCount})", "")
                Dim sizeStr = $"{Math.Max(1, CInt(Math.Ceiling(amount)))}x"   ' futures format (leverage=0)
                Dim sign = If(unrealizedPnlUsd >= 0D, "+", "")
                TradeStatusLine = $"{sideLabel}{countSuffix}  {sizeStr}  P&L: {sign}${unrealizedPnlUsd:F2}"
                FlashPnlAsync(unrealizedPnlUsd >= 0D)
                Return
            End If

            ' Fallback: update P&L portion only (legacy path / no snapshot metadata).
            Dim signFb = If(unrealizedPnlUsd >= 0D, "+", "")
            Dim pnlIdx = _tradeStatusLine.LastIndexOf("P&L:", StringComparison.Ordinal)
            Dim baseText = If(pnlIdx >= 0, _tradeStatusLine.Substring(0, pnlIdx), _tradeStatusLine & " ")
            TradeStatusLine = $"{baseText.TrimEnd()}  P&L: {signFb}${unrealizedPnlUsd:F2}"
            FlashPnlAsync(unrealizedPnlUsd >= 0D)
        End Sub

        ''' <summary>Called when the engine closes the position (SL/TP, trail, reversal, or neutral exit).</summary>
        Public Sub CloseTrade(Optional exitReason As String = "Closed", Optional finalPnl As Decimal = 0D)
            _positionCount = 0
            _wasPrePopulated = False
            LastLivePnl = 0D
            HasLivePnl = False
            TradeStatusLine = "—  No position"
            ClearTurtleStatus()
            ClearSlStatus()
            NotifyPropertyChanged(NameOf(HasOpenPosition))
        End Sub

        ' ── Turtle bracket status ─────────────────────────────────────────────────

        Private _turtleStatusLine As String = String.Empty
        Public Property TurtleStatusLine As String
            Get
                Return _turtleStatusLine
            End Get
            Private Set(value As String)
                If SetProperty(_turtleStatusLine, value) Then
                    NotifyPropertyChanged(NameOf(HasTurtleStatus))
                End If
            End Set
        End Property

        Public ReadOnly Property HasTurtleStatus As Boolean
            Get
                Return Not String.IsNullOrEmpty(_turtleStatusLine)
            End Get
        End Property

        ' ── Bracket price display ────────────────────────────────────────────────

        Private _bracketPriceDisplay As String = String.Empty
        ''' <summary>
        ''' Current Turtle bracket prices as a formatted string, e.g. "SL: 8420.50  TP: 8445.00".
        ''' Set on every <see cref="ApplyTurtleBracket"/> call (initial, advance, reattachment).
        ''' Cleared when the position closes.
        ''' </summary>
        Public Property BracketPriceDisplay As String
            Get
                Return _bracketPriceDisplay
            End Get
            Private Set(value As String)
                If SetProperty(_bracketPriceDisplay, value) Then
                    NotifyPropertyChanged(NameOf(HasBracketPrices))
                End If
            End Set
        End Property

        ''' <summary>True when bracket SL/TP prices are available to display.</summary>
        Public ReadOnly Property HasBracketPrices As Boolean
            Get
                Return Not String.IsNullOrEmpty(_bracketPriceDisplay)
            End Get
        End Property

        ' ── Free Ride state ───────────────────────────────────────────────────────

        Private _isFreeRide As Boolean = False
        ''' <summary>
        ''' True when the Turtle bracket SL has advanced past the entry price (CurrentSlDollars &gt; 0),
        ''' meaning the position is guaranteed to close in profit even if price hits the SL right now.
        ''' Bound to the "Free Ride" badge visibility on the asset tile.
        ''' </summary>
        Public Property IsFreeRide As Boolean
            Get
                Return _isFreeRide
            End Get
            Private Set(value As Boolean)
                SetProperty(_isFreeRide, value)
            End Set
        End Property

        ' ── SL status ────────────────────────────────────────────────────────────

        Private _slStatusLine As String = String.Empty
        Public Property SlStatusLine As String
            Get
                Return _slStatusLine
            End Get
            Private Set(value As String)
                SetProperty(_slStatusLine, value)
            End Set
        End Property

        ' Numeric SL/TP prices — stored so the Nudge command can compute a 10% step
        ' without parsing the display string. Cleared on position close.
        Private _currentSlPrice As Decimal = 0D
        Private _currentTpPrice As Decimal = 0D

        ''' <summary>Current stop-loss price as a number. 0 when no position is open.</summary>
        Public ReadOnly Property CurrentSlPrice As Decimal
            Get
                Return _currentSlPrice
            End Get
        End Property

        ''' <summary>Current take-profit price as a number. 0 when no position is open.</summary>
        Public ReadOnly Property CurrentTpPrice As Decimal
            Get
                Return _currentTpPrice
            End Get
        End Property

        ''' <summary>True when a position is currently open on this asset tile.</summary>
        Public ReadOnly Property HasOpenPosition As Boolean
            Get
                Return _positionCount > 0
            End Get
        End Property

        ''' <summary>
        ''' Command to manually close this asset's live position.
        ''' Set by HydraViewModel after engine initialisation.
        ''' </summary>
        Public Property CloseCommand As ICommand

        ''' <summary>
        ''' Command to tighten this asset's stop-loss by 10%.
        ''' Set by HydraViewModel after engine initialisation.
        ''' </summary>
        Public Property NudgeBracketCommand As ICommand

        ''' <summary>Clears the Turtle status line (called on position close).</summary>
        Public Sub ClearTurtleStatus()
            TurtleStatusLine = String.Empty
        End Sub

        ''' <summary>
        ''' Called whenever the engine sets or advances the bracket SL/TP.
        ''' The Turtle status message is only shown when <paramref name="isAdvance"/> is True
        ''' (a genuine bracket step driven by price action).  Initial placement and reattachment
        ''' on engine restart are silent — the bracket is simply in effect.
        ''' <paramref name="isFreeRide"/> lights the Free Ride badge once SL has cleared break-even.
        ''' </summary>
        Public Sub ApplySl(slPrice As Decimal, tpPrice As Decimal, isAdvance As Boolean, isFreeRide As Boolean)
            If isAdvance Then
                SlStatusLine = $"SL Applied: {DateTime.Now:HH:mm:ss}"
                TopStepTrader.UI.Infrastructure.TurtleClickSound.PlayAsync()
            End If
            IsFreeRide = isFreeRide
            ' Store numeric values so the Nudge command can compute steps without string parsing.
            If slPrice > 0D Then _currentSlPrice = slPrice
            If tpPrice > 0D Then _currentTpPrice = tpPrice
            ' Always update the display — fires on initial placement, reattachment, and advances.
            If slPrice > 0D Then
                BracketPriceDisplay = If(tpPrice > 0D,
                    $"SL: {slPrice:F2}  TP: {tpPrice:F2}",
                    $"SL: {slPrice:F2}")
            End If
        End Sub

        ''' <summary>Clears the SL status, Free Ride badge, numeric prices, and SL price display (called on position close).</summary>
        Public Sub ClearSlStatus()
            SlStatusLine = String.Empty
            IsFreeRide = False
            _currentSlPrice = 0D
            _currentTpPrice = 0D
            BracketPriceDisplay = String.Empty
        End Sub

    End Class

End Namespace

