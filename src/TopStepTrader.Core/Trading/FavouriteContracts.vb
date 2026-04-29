Namespace TopStepTrader.Core.Trading

    ''' <summary>
    ''' Master list of TopStepX favourite instruments.
    ''' Instruments: OIL (MCLE), GOLD (MGC), SILVER (SIL), SPX500 (MES), EUR/USD (M6E), NQ (MNQ), Bitcoin (MBT).
    ''' </summary>
    Public Class FavouriteContracts

        ''' <summary>Full list of all favourite instruments with both broker specs.</summary>
        Public Shared Function GetDefaults() As List(Of FavouriteContract)
            ' CME Globex tick specs: MGC tick=0.10/$1, MCLE tick=0.01/$1, MES tick=0.25/$1.25
            Dim list As New List(Of FavouriteContract)

            ' OIL — MCLE (Micro WTI Crude Oil)  [ProjectX symbolId: F.US.MCLE; roll: monthly; M26=Jun 2026 front-month]
            ' PxMinStopDollars=$15: 15 ticks × $1.00 — prevents sub-15-tick stops on oil
            list.Add(New FavouriteContract("OIL", "Oil", "CON.F.US.MCLE.M26", 0.01D, 1.0D, 100D, 0.3D, 15D) With {
                .PxRootSymbol = "MCLE",
                .CommissionTickBuffer = 2,
                .MultiConfluenceTimeframeMinutes = 5,
                .RoundTripFee = 1.04D,
                .RollLeadDays = 28
            })

            ' GOLD.24-7 — MGC (Micro Gold)  [ProjectX symbolId: F.US.MGC; roll: even months G/J/M/Q/V/Z; M26=Jun 2026 front-month]
            ' PxMinStopDollars=$20: 20 ticks × $1.00 = 2 pts — gold moves ~$5-15/min; 2pt floor is prudent
            list.Add(New FavouriteContract("GOLD.24-7", "Gold", "CON.F.US.MGC.M26", 0.1D, 1.0D, 10D, 0.2D, 20D) With {
                .PxRootSymbol = "MGC",
                .CommissionTickBuffer = 2,
                .MultiConfluenceTimeframeMinutes = 10,
                .RoundTripFee = 1.24D,
                .RollLeadDays = 28
            })

            ' SILVER — SIL (Micro Silver)  [ProjectX symbolId: F.US.SIL; roll: monthly; nearest front-month]
            ' CME Micro Silver: 1,000 troy oz, tick=0.005/$5.00
            ' PxMinStopDollars=$25: 5 ticks × $5.00 = $25 floor — silver is volatile intraday
            list.Add(New FavouriteContract("SILVER", "Silver", "CON.F.US.SIL.N26", 0.005D, 5.0D, 1000D, 0.3D, 25D) With {
                .PxRootSymbol = "SIL",
                .CommissionTickBuffer = 2,
                .MultiConfluenceTimeframeMinutes = 10,
                .RoundTripFee = 1.24D,
                .RollLeadDays = 28
            })

            ' SPX500
            ' PxMinStopDollars=$20: 16 ticks × $1.25 = 4 S&P points minimum
            list.Add(New FavouriteContract("SPX500", "S&P 500", "CON.F.US.MES.U26", 0.25D, 1.25D, 5D, 0.3D, 20D) With {
                .PxRootSymbol = "MES",
                .CommissionTickBuffer = 1,
                .MultiConfluenceTimeframeMinutes = 5,
                .RoundTripFee = 0.74D
            })

            ' EURUSD — M6E (Micro EUR/USD)  [ProjectX symbolId: F.US.M6E; roll: quarterly H/M/U/Z; U26=Sep 2026 front-month]
            ' Contract size: 12,500 EUR.  tick=0.0001/$1.25.  Forex pairs trade CME Globex ~23h/day.
            ' PxMinStopDollars=$12.50: 10 ticks × $1.25 — forex is liquid; tight floor appropriate.
            list.Add(New FavouriteContract("EURUSD", "EUR/USD", "CON.F.US.M6E.U26", 0.0001D, 1.25D, 12500D, 0.1D, 12.5D) With {
                .PxRootSymbol = "M6E",
                .CommissionTickBuffer = 1,
                .MultiConfluenceTimeframeMinutes = 10,
                .RoundTripFee = 0.74D
            })

            ' NQ — MNQ (Micro Nasdaq-100)  [ProjectX symbolId: F.US.MNQ; roll: quarterly H/M/U/Z; U26=Sep 2026 front-month]
            ' PxMinStopDollars=$25: 50 ticks × $0.50 = 12.5 NQ points minimum
            list.Add(New FavouriteContract("NQ", "NQ", "CON.F.US.MNQ.U26", 0.25D, 0.5D, 2.0D, 0.3D, 25D) With {
                .PxRootSymbol = "MNQ",
                .CommissionTickBuffer = 1,
                .MultiConfluenceTimeframeMinutes = 5,
                .RoundTripFee = 0.74D
            })

            ' Crypto — MBT (Micro Bitcoin): 0.1 BTC/contract, tick=5pts/$0.50
            ' tickValue=$0.50 confirmed via ProjectX API (0.1 BTC × $5/pt index = $0.50/tick)
            ' PxMinStopDollars=$30: 60 ticks × $0.50 = 300 BTC pts — crypto is volatile; 300pt floor prudent
            list.Add(New FavouriteContract("BTC", "Bitcoin", "CON.F.US.MBT.U26", 5.0D, 0.5D, 0.1D, 0.5D, 30D) With {
                .IsCrypto = True,
                .PxRootSymbol = "MBT",
                .MultiConfluenceTimeframeMinutes = 15,
                .RoundTripFee = 2.34D
            })

            Return list
        End Function

        '''
        ''' Returns the FavouriteContract whose Name or PxContractId matches the given symbol,
        ''' or whose PxRootSymbol matches directly (e.g. "MGC", "MES"), or whose PxRootSymbol
        ''' is embedded in the given ProjectX contract ID (e.g. "CON.F.US.MCL.K26" matches the
        ''' MCL entry regardless of expiry month). The name-match fallback covers the short
        ''' instrument names used as keys in GetDefaults (e.g. "OIL", "GOLD.24-7").
        ''' This root-symbol fallback ensures lookups survive quarterly contract rolls.
        ''' </summary>
        Public Shared Function TryGetBySymbol(symbol As String) As FavouriteContract
            Return GetDefaults().FirstOrDefault(
                Function(f) String.Equals(f.Name, symbol, StringComparison.OrdinalIgnoreCase) OrElse
                            String.Equals(f.PxContractId, symbol, StringComparison.OrdinalIgnoreCase) OrElse
                            (Not String.IsNullOrEmpty(f.PxRootSymbol) AndAlso
                             (String.Equals(f.PxRootSymbol, symbol, StringComparison.OrdinalIgnoreCase) OrElse
                              symbol.StartsWith($"CON.F.US.{f.PxRootSymbol}.", StringComparison.OrdinalIgnoreCase))))
        End Function

    End Class

    ''' <summary>
    ''' A single favourite instrument with TopStepX futures trading specs.
    ''' </summary>
    Public Class FavouriteContract

        ' ── Shared ──────────────────────────────────────────────────────────────────
        Public Property Name As String

        ''' <summary>True for crypto instruments (BTC, ETH, XRP, SOL, BNB).</summary>
        Public Property IsCrypto As Boolean = False

        ' ── TopStepX / ProjectX fields ───────────────────────────────────────────────
        ''' <summary>ProjectX contract ID string, e.g. "CON.F.US.MGC.J26".</summary>
        Public Property PxContractId As String = String.Empty
        ''' <summary>
        ''' CME root symbol used to search for the active front-month contract via the ProjectX
        ''' /api/Contract/search endpoint (e.g. "MGC", "MES", "MNQ").
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
        ''' When the ATR-derived SL falls below this value the engine clamps up to this floor
        ''' before converting to ticks. Prevents noise-stops on high-price instruments such as
        ''' NASDAQ and Gold. 0 = no floor.
        ''' </summary>
        Public Property PxMinStopDollars As Decimal = 0D

        ' ── Commission / risk helpers ─────────────────────────────────────────────────────

        '''
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
        ''' Number of calendar days before contract expiry at which the catalog should stop
        ''' selecting this contract as the active front-month (i.e. the roll lead time).
        ''' Monthly contracts (MCL, MGC) roll ~28 days before expiry; set to 28.
        ''' Quarterly contracts (MES, MNQ, M6E, MBT) roll ~7 days before expiry; default = 7.
        ''' </summary>
        Public Property RollLeadDays As Integer = 7

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

        Public Sub New(name As String, displayName As String,
                       pxContractId As String,
                       pxTickSz As Decimal,
                       pxTickVal As Decimal,
                       pxPtVal As Decimal,
                       Optional pxMinSlDistPct As Decimal = 0D,
                       Optional pxMinStopDollars As Decimal = 0D)
            Me.Name = name
            Me.PxContractId = If(pxContractId, String.Empty)
            PxTickSize = pxTickSz
            PxTickValue = pxTickVal
            PxPointValue = pxPtVal
            PxMinSlDistancePct = pxMinSlDistPct
            Me.PxMinStopDollars = pxMinStopDollars
        End Sub

    End Class

End Namespace
