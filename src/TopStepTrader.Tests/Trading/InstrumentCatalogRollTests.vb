Imports TopStepTrader.API.Models.Responses
Imports TopStepTrader.Core.Trading
Imports TopStepTrader.Services.Trading
Imports Xunit

Namespace TopStepTrader.Tests.Trading

    ''' <summary>
    ''' Regression tests ensuring contract-roll logic in TopStepXInstrumentCatalog
    ''' correctly selects the active front-month for all five favourite instruments.
    '''
    ''' Root cause of BUG-36: SearchForFrontMonthAsync used a cutoff of UtcNow-28d,
    ''' which included rolled (but not yet expired) contracts.  The nearest-expiry
    ''' ordering then selected the rolled contract over the new front-month.
    '''
    ''' Fix: each FavouriteContract carries RollLeadDays (28 for monthly MCL/MGC,
    ''' 7 for quarterly MES/MNQ/M6E/MBT).  SelectBestContract uses
    ''' cutoff = UtcNow + RollLeadDays, excluding contracts in the roll window.
    ''' </summary>
    Public Class InstrumentCatalogRollTests

        ' ── Helpers ───────────────────────────────────────────────────────────────

        Private Shared Function MakeContract(id As String) As ContractDto
            Return New ContractDto With {.ContractId = id, .TickSize = 0.01, .TickValue = 1.0}
        End Function

        ' ── ParseFuturesExpiry ────────────────────────────────────────────────────

        <Theory>
        <InlineData("CON.F.US.MCLE.K26", 2026, 5)>
        <InlineData("CON.F.US.MCLE.M26", 2026, 6)>
        <InlineData("CON.F.US.MGC.M26", 2026, 6)>
        <InlineData("CON.F.US.MES.U26", 2026, 9)>
        <InlineData("CON.F.US.M6E.Z26", 2026, 12)>
        Public Sub ParseFuturesExpiry_DecodesMonthAndYear(contractId As String, expectedYear As Integer, expectedMonth As Integer)
            Dim result = TopStepXInstrumentCatalog.ParseFuturesExpiry(contractId)
            Assert.Equal(New DateTime(expectedYear, expectedMonth, 1), result)
        End Sub

        <Fact>
        Public Sub ParseFuturesExpiry_UnknownCode_ReturnsMaxValue()
            Dim result = TopStepXInstrumentCatalog.ParseFuturesExpiry("CON.F.US.MCLE.A26")
            Assert.Equal(DateTime.MaxValue, result)
        End Sub

        ' ── RollLeadDays metadata ─────────────────────────────────────────────────

        <Fact>
        Public Sub FavouriteContracts_MonthlyContracts_HaveRollLeadDays28()
            Dim oil = FavouriteContracts.TryGetBySymbol("OIL")
            Dim gold = FavouriteContracts.TryGetBySymbol("GOLD.24-7")

            Assert.Equal(28, oil.RollLeadDays)
            Assert.Equal(28, gold.RollLeadDays)
        End Sub

        <Fact>
        Public Sub FavouriteContracts_QuarterlyContracts_HaveRollLeadDays7()
            Dim mes = FavouriteContracts.TryGetBySymbol("SPX500")
            Dim m6j = FavouriteContracts.TryGetBySymbol("M6J")
            Dim mnq = FavouriteContracts.TryGetBySymbol("NQ")
            Dim m2k = FavouriteContracts.TryGetBySymbol("M2K")

            Assert.Equal(7, mes.RollLeadDays)
            Assert.Equal(7, m6j.RollLeadDays)
            Assert.Equal(7, mnq.RollLeadDays)
            Assert.Equal(7, m2k.RollLeadDays)
        End Sub

        ' ── SelectBestContract: monthly MCL (RollLeadDays = 28) ───────────────────

        <Fact>
        Public Sub SelectBestContract_MCL_ExcludesRolledK26_SelectsM26()
            ' Simulate: today = April 28, K26 expires May 20 (within 28-day window), M26 expires June 20
            Dim asOf = New DateTime(2026, 4, 28)
            Dim fav = FavouriteContracts.TryGetBySymbol("OIL")
            Dim candidates = {
                MakeContract("CON.F.US.MCLE.K26"),   ' expiry May 2026  — within 28-day cutoff → excluded
                MakeContract("CON.F.US.MCLE.M26")    ' expiry June 2026 — beyond cutoff → selected
            }

            Dim result = TopStepXInstrumentCatalog.SelectBestContract(candidates, fav, asOf)

            Assert.NotNull(result)
            Assert.Equal("CON.F.US.MCLE.M26", result.ContractId)
        End Sub

        <Fact>
        Public Sub SelectBestContract_MCL_BothExpired_ReturnsNothing()
            Dim asOf = New DateTime(2026, 8, 1)
            Dim fav = FavouriteContracts.TryGetBySymbol("OIL")
            Dim candidates = {
                MakeContract("CON.F.US.MCLE.K26"),
                MakeContract("CON.F.US.MCLE.M26")
            }

            Dim result = TopStepXInstrumentCatalog.SelectBestContract(candidates, fav, asOf)

            Assert.Null(result)
        End Sub

        <Fact>
        Public Sub SelectBestContract_MCL_OnlyM26Available_SelectsIt()
            Dim asOf = New DateTime(2026, 4, 28)
            Dim fav = FavouriteContracts.TryGetBySymbol("OIL")
            Dim candidates = {MakeContract("CON.F.US.MCLE.M26")}

            Dim result = TopStepXInstrumentCatalog.SelectBestContract(candidates, fav, asOf)

            Assert.Equal("CON.F.US.MCLE.M26", result.ContractId)
        End Sub

        ' ── SelectBestContract: monthly MGC (RollLeadDays = 28) ───────────────────

        <Fact>
        Public Sub SelectBestContract_MGC_ExcludesRolledContract_SelectsNextMonth()
            ' Simulate: today = April 28, J26 expires April 2026 (within cutoff), M26 expires June 2026
            Dim asOf = New DateTime(2026, 4, 28)
            Dim fav = FavouriteContracts.TryGetBySymbol("GOLD.24-7")
            Dim candidates = {
                MakeContract("CON.F.US.MGC.J26"),   ' expiry April 2026 — excluded
                MakeContract("CON.F.US.MGC.M26")    ' expiry June 2026  — selected
            }

            Dim result = TopStepXInstrumentCatalog.SelectBestContract(candidates, fav, asOf)

            Assert.Equal("CON.F.US.MGC.M26", result.ContractId)
        End Sub

        ' ── SelectBestContract: quarterly MES (RollLeadDays = 7) ─────────────────

        <Fact>
        Public Sub SelectBestContract_MES_ExcludesRolledH26_SelectsM26()
            ' Simulate: today = March 12 (7-day window excludes H26 expiring March 19)
            Dim asOf = New DateTime(2026, 3, 12)
            Dim fav = FavouriteContracts.TryGetBySymbol("SPX500")
            Dim candidates = {
                MakeContract("CON.F.US.MES.H26"),   ' expiry March 2026  — excluded (March 12 + 7 = March 19 > March 1)
                MakeContract("CON.F.US.MES.M26")    ' expiry June 2026   — selected
            }

            Dim result = TopStepXInstrumentCatalog.SelectBestContract(candidates, fav, asOf)

            Assert.Equal("CON.F.US.MES.M26", result.ContractId)
        End Sub

        <Fact>
        Public Sub SelectBestContract_MES_BeforeRollWindow_KeepsCurrentQuarter()
            ' Simulate: today = Feb 1 — H26 (March) is still the active contract, not in roll window
            Dim asOf = New DateTime(2026, 2, 1)
            Dim fav = FavouriteContracts.TryGetBySymbol("SPX500")
            Dim candidates = {
                MakeContract("CON.F.US.MES.H26"),   ' expiry March 2026 — Feb 1 + 7 = Feb 8 < March 1 → included
                MakeContract("CON.F.US.MES.M26")    ' expiry June 2026
            }

            Dim result = TopStepXInstrumentCatalog.SelectBestContract(candidates, fav, asOf)

            ' H26 is still active — it should be selected (nearest-expiry winner)
            Assert.Equal("CON.F.US.MES.H26", result.ContractId)
        End Sub

        ' ── SelectBestContract: quarterly M6J (RollLeadDays = 7) ─────────────────

        <Fact>
        Public Sub SelectBestContract_M6J_ExcludesRolledQuarter_SelectsNext()
            Dim asOf = New DateTime(2026, 3, 12)
            Dim fav = FavouriteContracts.TryGetBySymbol("M6J")
            Dim candidates = {
                MakeContract("CON.F.US.M6J.H26"),
                MakeContract("CON.F.US.M6J.M26")
            }

            Dim result = TopStepXInstrumentCatalog.SelectBestContract(candidates, fav, asOf)

            Assert.Equal("CON.F.US.M6J.M26", result.ContractId)
        End Sub

        ' ── SelectBestContract: quarterly MNQ (RollLeadDays = 7) ─────────────────

        <Fact>
        Public Sub SelectBestContract_MNQ_ExcludesRolledQuarter_SelectsNext()
            Dim asOf = New DateTime(2026, 3, 12)
            Dim fav = FavouriteContracts.TryGetBySymbol("NQ")
            Dim candidates = {
                MakeContract("CON.F.US.MNQ.H26"),
                MakeContract("CON.F.US.MNQ.M26")
            }

            Dim result = TopStepXInstrumentCatalog.SelectBestContract(candidates, fav, asOf)

            Assert.Equal("CON.F.US.MNQ.M26", result.ContractId)
        End Sub

        ' ── SelectBestContract: quarterly M2K (RollLeadDays = 7) ─────────────────

        <Fact>
        Public Sub SelectBestContract_M2K_ExcludesRolledQuarter_SelectsNext()
            Dim asOf = New DateTime(2026, 6, 12)
            Dim fav = FavouriteContracts.TryGetBySymbol("M2K")
            Dim candidates = {
                MakeContract("CON.F.US.M2K.M26"),   ' expiry June 2026 — excluded (June 12 + 7 = June 19 > June 1)
                MakeContract("CON.F.US.M2K.U26")    ' expiry Sep 2026  — selected
            }

            Dim result = TopStepXInstrumentCatalog.SelectBestContract(candidates, fav, asOf)

            Assert.Equal("CON.F.US.M2K.U26", result.ContractId)
        End Sub

        ' ── SelectBestContract: null / empty guards ───────────────────────────────

        <Fact>
        Public Sub SelectBestContract_NullCandidates_ReturnsNothing()
            Dim fav = FavouriteContracts.TryGetBySymbol("OIL")
            Dim result = TopStepXInstrumentCatalog.SelectBestContract(Nothing, fav)
            Assert.Null(result)
        End Sub

        <Fact>
        Public Sub SelectBestContract_WrongRootSymbol_ReturnsNothing()
            Dim asOf = New DateTime(2026, 4, 28)
            Dim fav = FavouriteContracts.TryGetBySymbol("OIL")   ' root = MCLE
            Dim candidates = {MakeContract("CON.F.US.MGC.M26")}  ' wrong root

            Dim result = TopStepXInstrumentCatalog.SelectBestContract(candidates, fav, asOf)

            Assert.Null(result)
        End Sub

    End Class

End Namespace
