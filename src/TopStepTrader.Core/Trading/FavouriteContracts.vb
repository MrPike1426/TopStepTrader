Imports TopStepTrader.Core.Enums

Namespace TopStepTrader.Core.Trading

    ''' <summary>
    ''' Master list of favourite instruments for both eToro and TopStepX.
    ''' Each FavouriteContract carries specs for both brokers.
    ''' Use GetActiveContractId(broker) / GetTickSize(broker) etc. at call sites,
    ''' or call GetDefaults(broker) to receive a pre-filtered, broker-labelled list.
    ''' Instruments: OIL (MCLE), GOLD (MGC), SPX500 (MES), EUR/USD (M6E), Bitcoin (MBT).
    ''' </summary>
    Public Class FavouriteContracts

        ''' <summary>Full list of all favourite instruments with both broker specs.</summary>
        Public Shared Function GetDefaults() As List(Of FavouriteContract)
            ' eToro MaxLeverage = FCA/ESMA retail caps: OIL=10x, Gold=20x, Indices=20x, Crypto=2x
            ' CME Globex tick specs: MGC tick=0.10/$1, MCLE tick=0.01/$1, MES tick=0.25/$1.25
            Dim list As New List(Of FavouriteContract)

            ' OIL — MCLE (Micro WTI Crude Oil)  [ProjectX symbolId: F.US.MCLE; roll: monthly; K26=May 2026 front-month]
            ' PxMinStopDollars=$15: 15 ticks × $1.00 — prevents sub-15-tick stops on oil
            list.Add(New FavouriteContract("OIL", "Oil", 17, 0.01D, 0.01D, 1D, 0.5D, 10, _
                "CON.F.US.MCLE.K26", 0.01D, 1.0D, 100D, 0.3D, 15D) With {
                .YahooSymbol = "CL=F",
                .PxRootSymbol = "MCLE",
                .CommissionTickBuffer = 2,
                .MultiConfluenceTimeframeMinutes = 5,
                .RoundTripFee = 1.04D
            })

            ' GOLD.24-7 — MGC (Micro Gold)  [ProjectX symbolId: F.US.MGC; roll: even months G/J/M/Q/V/Z; Q26=Aug 2026 front-month]
            ' PxMinStopDollars=$20: 20 ticks × $1.00 = 2 pts — gold moves ~$5-15/min; 2pt floor is prudent
            list.Add(New FavouriteContract("GOLD.24-7", "Gold", 18, 0.01D, 0.01D, 1D, 0.3D, 20, _
                "CON.F.US.MGC.Q26", 0.1D, 1.0D, 10D, 0.2D, 20D) With {
                .YahooSymbol = "GC=F",
                .PxRootSymbol = "MGC",
                .CommissionTickBuffer = 2,
                .MultiConfluenceTimeframeMinutes = 10,
                .RoundTripFee = 1.24D
            })

            ' SPX500 — MES (Micro S&P 500)  [roll: quarterly H/M/U/Z; U26=Sep 2026 front-month]
            ' PxMinStopDollars=$20: 16 ticks × $1.25 = 4 S&P points minimum
            ' YahooSymbol: ES=F = E-mini S&P 500 futures (CME, trades ~23h/day, reliable 5m data).
            list.Add(New FavouriteContract("SPX500", "S&P 500", 27, 0.01D, 0.01D, 1D, 0.5D, 20, _
                "CON.F.US.MES.U26", 0.25D, 1.25D, 5D, 0.3D, 20D) With {
                .YahooSymbol = "ES=F",
                .PxRootSymbol = "MES",
                .CommissionTickBuffer = 1,
                .MultiConfluenceTimeframeMinutes = 5,
                .RoundTripFee = 0.74D
            })

            ' EURUSD — M6E (Micro EUR/USD)  [ProjectX symbolId: F.US.M6E; roll: quarterly H/M/U/Z; U26=Sep 2026 front-month]
            ' Contract size: 12,500 EUR.  tick=0.0001/$1.25.  Forex pairs trade CME Globex ~23h/day.
            ' PxMinStopDollars=$12.50: 10 ticks × $1.25 — forex is liquid; tight floor appropriate.
            ' eToro: EURUSD CFD, maxLeverage=30 (FCA/ESMA retail FX cap).
            ' NOTE: eToro InstrumentId=1 is a placeholder — verify against live eToro API if needed.
            list.Add(New FavouriteContract("EURUSD", "EUR/USD", 1, 0.00001D, 0.01D, 1D, 0.05D, 30, _
                "CON.F.US.M6E.U26", 0.0001D, 1.25D, 12500D, 0.1D, 12.5D) With {
                .YahooSymbol = "EURUSD=X",
                .PxRootSymbol = "M6E",
                .CommissionTickBuffer = 1,
                .MultiConfluenceTimeframeMinutes = 10,
                .RoundTripFee = 0.74D
            })

            ' Crypto — MBT (Micro Bitcoin): 0.1 BTC/contract, tick=5pts/$0.50
            ' tickValue=$0.50 confirmed via ProjectX API (0.1 BTC × $5/pt index = $0.50/tick)
            ' PxMinStopDollars=$30: 60 ticks × $0.50 = 300 BTC pts — crypto is volatile; 300pt floor prudent
            list.Add(New FavouriteContract("BTC", "Bitcoin", 100000, 1.0D, 1.0D, 1D, 1.0D, 2, _
                "CON.F.US.MBT.U26", 5.0D, 0.5D, 0.1D, 0.5D, 30D) With {
                .IsCrypto = True,
                .YahooSymbol = "BTC-USD",
                .PxRootSymbol = "MBT",
                .MultiConfluenceTimeframeMinutes = 15,
                .RoundTripFee = 2.34D
            })

            Return list
        End Function

        ''' <summary>
        ''' Returns the subset of contracts tradeable on <paramref name="broker"/>,
        ''' with ContractId set to the broker-appropriate identifier.
        ''' For TopStepX, crypto-only entries are excluded.
        ''' </summary>
        Public Shared Function GetDefaults(broker As BrokerType) As List(Of FavouriteContract)
            Return GetDefaults().
                Where(Function(f) f.IsTradableOn(broker)).
                ToList()
        End Function

        ''' <summary>
        ''' Returns the FavouriteContract whose eToro symbol or PX contract ID matches, or whose
        ''' PxRootSymbol is embedded in the given ProjectX contract ID (e.g. "CON.F.US.MCL.K26"
        ''' matches the MCL entry regardless of the expiry month code).
        ''' This root-symbol fallback ensures lookups survive quarterly contract rolls without
        ''' requiring a code change each time.
        ''' </summary>
        Public Shared Function TryGetBySymbol(symbol As String) As FavouriteContract
            Return GetDefaults().FirstOrDefault(
                Function(f) String.Equals(f.EToroContractId, symbol, StringComparison.OrdinalIgnoreCase) OrElse
                            String.Equals(f.PxContractId, symbol, StringComparison.OrdinalIgnoreCase) OrElse
                            (Not String.IsNullOrEmpty(f.PxRootSymbol) AndAlso
                             symbol.StartsWith($"CON.F.US.{f.PxRootSymbol}.", StringComparison.OrdinalIgnoreCase)))
        End Function

        ''' <summary>Returns the FavouriteContract with the given eToro numeric instrumentId, or Nothing.</summary>
        Public Shared Function TryGetByInstrumentId(instrumentId As Integer) As FavouriteContract
            Return GetDefaults().FirstOrDefault(Function(f) f.InstrumentId = instrumentId)
        End Function

    End Class

    ''' <summary>
    ''' A single favourite instrument with specs for both eToro CFD and TopStepX futures trading.
    ''' </summary>
    Public Class FavouriteContract

        ' ── Shared ──────────────────────────────────────────────────────────────────
        Public Property Name As String

        ' ── eToro fields ────────────────────────────────────────────────────────────
        ''' <summary>eToro internalSymbolFull ticker, e.g. "GOLD.24-7".</summary>
        Public Property EToroContractId As String
        ''' <summary>eToro immutable numeric instrument ID.</summary>
        Public Property InstrumentId As Integer
        Public Property EToroTickSize As Decimal
        Public Property EToroTickValue As Decimal
        Public Property EToroPointValue As Decimal
        ''' <summary>Minimum stop-loss distance as % of price (eToro CFD).</summary>
        Public Property MinSlDistancePct As Decimal
        ''' <summary>FCA/ESMA retail leverage cap for this instrument.</summary>
        Public Property MaxLeverage As Integer
        ''' <summary>Minimum notional trade size in USD for eToro (default 1000).</summary>
        Public Property MinNotionalUsd As Decimal = 1000D
        ''' <summary>True for crypto instruments (BTC, ETH, XRP, SOL, BNB).</summary>
        Public Property IsCrypto As Boolean = False

        ' ── Yahoo Finance fields ─────────────────────────────────────────────────────
        ''' <summary>Yahoo Finance ticker symbol used for historical backtest bar downloads, e.g. "^GSPC", "GC=F", "BTC-USD".</summary>
        Public Property YahooSymbol As String = String.Empty

        ' ── TopStepX / ProjectX fields ───────────────────────────────────────────────
        ''' <summary>ProjectX contract ID string, e.g. "CON.F.US.MGC.J26". Empty = not available on TopStepX.</summary>
        Public Property PxContractId As String = String.Empty
        ''' <summary>
        ''' CME root symbol used to search for the active front-month contract via the ProjectX
        ''' /api/Contract/search endpoint (e.g. "MGC", "MES", "MNQ").
        ''' Empty for eToro-only instruments that have no TopStepX equivalent.
        ''' </summary>
        Public Property PxRootSymbol As String = String.Empty

        ''' <summary>
        ''' Preferred bar timeframe in minutes for the Multi-Confluence strategy on this instrument.
        ''' Governs the strategy-evaluation bar series only; indicator display updates on 15-second
        ''' bar closes independently of this value.
        ''' Defaults to 5 (5-minute bars). Override per instrument in GetDefaults():
        '''   Oil/MES: 5 min — highly liquid intraday, 5-min bar noise acceptable.
        '''   Gold/M6E: 10 min — slightly wider bars filter micro-chop on these instruments.
        '''   BTC/MBT: 15 min — crypto volatility requires wider bars to avoid false signals.
        ''' </summary>
        Public Property MultiConfluenceTimeframeMinutes As Integer = 5
        Public Property PxTickSize As Decimal
        Public Property PxTickValue As Decimal
        Public Property PxPointValue As Decimal
        ''' <summary>Minimum stop-loss distance as % of price (futures).</summary>
        Public Property PxMinSlDistancePct As Decimal
        ''' <summary>
        ''' Hard floor on the stop-loss amount in USD for TopStepX orders.
        ''' When the strategy's computed SL dollars (SlDollarBracket ÷ contracts) falls below
        ''' this value the engine clamps up to this floor before converting to ticks.
        ''' Prevents noise-stops on high-price instruments such as NASDAQ and Gold.
        ''' 0 = no floor (use strategy value as-is).
        ''' </summary>
        Public Property PxMinStopDollars As Decimal = 0D

        ' ── Broker-aware helpers ─────────────────────────────────────────────────────

        ''' <summary>Legacy accessor — returns the eToro symbol. TopStepX callers should use
        ''' <see cref="GetActiveContractId"/> or <see cref="PxContractId"/> instead.</summary>
        Public Property ContractId As String
            Get
                Return EToroContractId
            End Get
            Set(value As String)
                EToroContractId = value
            End Set
        End Property

        Public Function IsTradableOn(broker As BrokerType) As Boolean
            If broker = BrokerType.TopStepX Then Return Not String.IsNullOrEmpty(PxContractId)
            Return True
        End Function

        Public Function GetActiveContractId(broker As BrokerType) As String
            Return If(broker = BrokerType.TopStepX, PxContractId, EToroContractId)
        End Function

        Public Function GetTickSize(broker As BrokerType) As Decimal
            Return If(broker = BrokerType.TopStepX, PxTickSize, EToroTickSize)
        End Function

        Public Function GetTickValue(broker As BrokerType) As Decimal
            Return If(broker = BrokerType.TopStepX, PxTickValue, EToroTickValue)
        End Function

        Public Function GetPointValue(broker As BrokerType) As Decimal
            Return If(broker = BrokerType.TopStepX, PxPointValue, EToroPointValue)
        End Function

        Public Function GetMinSlDistancePct(broker As BrokerType) As Decimal
            Return If(broker = BrokerType.TopStepX, PxMinSlDistancePct, MinSlDistancePct)
        End Function

        ''' <summary>
        ''' Number of ticks above breakeven to place the SL when the Free Roll activates.
        ''' Covers round-trip commissions so the trade is genuinely risk-free after activation.
        ''' TopStepX only — no broker overload needed.
        ''' Default = 2 (safe fallback for instruments without an explicit value).
        ''' </summary>
        Public Property CommissionTickBuffer As Integer = 2

        ''' <summary>Returns the commission tick buffer for this instrument (TopStepX).</summary>
        Public Function GetCommissionTickBuffer() As Integer
            Return CommissionTickBuffer
        End Function

        ''' <summary>
        ''' Exchange + clearing + platform commission per side per contract on TopStepX.
        ''' Used by the backtest engine to model round-trip friction (entry + exit = 2×).
        ''' Default $4.50 — standard CME Globex micro futures rate on TopStepX.
        ''' </summary>
        Public Property PxCommissionPerSide As Decimal = 4.5D

        ''' <summary>
        ''' Total round-trip cost per contract on TopStepX (broker commission + exchange fee, entry + exit).
        ''' Used by the backtest engine to model realistic per-trade friction.
        ''' Default $0.80 — conservative mid-range estimate for contracts without a confirmed rate.
        ''' </summary>
        Public Property RoundTripFee As Decimal = 0.80D

        ''' <summary>
        ''' True when this is a leveraged eToro CFD (MaxLeverage ≥ 5).
        ''' Not applicable for TopStepX futures (inherently margined by exchange).
        ''' </summary>
        Public ReadOnly Property IsLeveragedCfd As Boolean
            Get
                Return MaxLeverage >= 5
            End Get
        End Property

        ''' <summary>Returns the minimum absolute SL distance in price units for a given price and broker.</summary>
        Public Function MinSlDistancePoints(currentPrice As Decimal, broker As BrokerType) As Decimal
            Dim pct = GetMinSlDistancePct(broker)
            If currentPrice <= 0D OrElse pct <= 0D Then Return 0D
            Return Math.Round(currentPrice * pct / 100D, 4)
        End Function

        Public Sub New(etoroId As String, name As String, instrumentId As Integer,
                       eTickSz As Decimal, eTickVal As Decimal, ePtVal As Decimal,
                       Optional minSlDistPct As Decimal = 0.5D,
                       Optional maxLeverage As Integer = 20,
                       Optional pxContractId As String = "",
                       Optional pxTickSz As Decimal = 0D,
                       Optional pxTickVal As Decimal = 0D,
                       Optional pxPtVal As Decimal = 0D,
                       Optional pxMinSlDistPct As Decimal = 0D,
                       Optional pxMinStopDollars As Decimal = 0D)
            EToroContractId = etoroId
            Me.Name = name
            Me.InstrumentId = instrumentId
            EToroTickSize = eTickSz
            EToroTickValue = eTickVal
            EToroPointValue = ePtVal
            MinSlDistancePct = minSlDistPct
            Me.MaxLeverage = maxLeverage
            Me.PxContractId = If(pxContractId, String.Empty)
            PxTickSize = pxTickSz
            PxTickValue = pxTickVal
            PxPointValue = pxPtVal
            PxMinSlDistancePct = If(pxMinSlDistPct > 0D, pxMinSlDistPct, minSlDistPct)
            Me.PxMinStopDollars = pxMinStopDollars
        End Sub

    End Class

End Namespace
