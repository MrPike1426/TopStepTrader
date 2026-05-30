Namespace TopStepTrader.Core.Trading

    ''' <summary>
    ''' FEAT-72: superset registry of all TopStepX-tradable instruments the strategies
    ''' may consider, organised into three tiers. <see cref="CoreFavourite"/> mirrors
    ''' <see cref="FavouriteContracts.GetDefaults"/> exactly (so existing behaviour is
    ''' preserved when the adaptive toggle is off). <see cref="Extended"/> adds
    ''' broadly tradable micros that the opportunity scorer may surface. <see cref="Experimental"/>
    ''' is reserved for lightly traded contracts gated behind a setting.
    '''
    ''' Pinned/Blacklisted overrides live in user preferences, not in this static
    ''' registry. The adaptive watchlist service merges the two at scoring time.
    ''' </summary>
    Public Enum UniverseTier
        CoreFavourite = 0
        Extended = 1
        Experimental = 2
    End Enum

    Public Class UniverseEntry
        Public Property Contract As FavouriteContract
        Public Property Tier As UniverseTier
        Public Property PinnedByUser As Boolean = False
        Public Property BlacklistedByUser As Boolean = False
    End Class

    Public Class InstrumentUniverse

        ''' <summary>
        ''' Returns the full universe. Core entries are sourced directly from
        ''' <see cref="FavouriteContracts.GetDefaults"/>; Extended entries are a small,
        ''' hand-curated set of CME micros known to be available on TopStepX.
        ''' Experimental is empty for now — slot reserved for future additions.
        ''' </summary>
        Public Shared Function GetAll() As IList(Of UniverseEntry)
            Dim list As New List(Of UniverseEntry)

            For Each fav In FavouriteContracts.GetDefaults()
                list.Add(New UniverseEntry With {
                    .Contract = fav,
                    .Tier = UniverseTier.CoreFavourite
                })
            Next

            For Each fav In BuildExtendedTier()
                list.Add(New UniverseEntry With {
                    .Contract = fav,
                    .Tier = UniverseTier.Extended
                })
            Next

            Return list
        End Function

        ''' <summary>Returns only the entries belonging to <paramref name="tier"/>.</summary>
        Public Shared Function GetByTier(tier As UniverseTier) As IList(Of UniverseEntry)
            Return GetAll().Where(Function(e) e.Tier = tier).ToList()
        End Function

        ''' <summary>
        ''' Hand-curated CME micro contracts that exist on TopStepX but are not in
        ''' <see cref="FavouriteContracts.GetDefaults"/>. Baked-in PxContractId values
        ''' are syntactic fallbacks only — <see cref="FavouriteContracts.TryGetBySymbolResolved"/>
        ''' replaces them with the live front-month from the contract cache.
        ''' </summary>
        Private Shared Function BuildExtendedTier() As List(Of FavouriteContract)
            Dim list As New List(Of FavouriteContract)

            ' MYM — Micro E-mini Dow  [quarterly H/M/U/Z]
            ' Was in defaults previously; dropped due to ~0.94 correlation with MES.
            ' Re-added to Extended so the scorer can surface it when MES is choppy.
            list.Add(New FavouriteContract("MYM", "Dow Jones", "CON.F.US.MYM.U26", 1.0D, 0.5D, 0.5D, 0.3D, 20D) With {
                .PxRootSymbol = "MYM",
                .CommissionTickBuffer = 1,
                .MultiConfluenceTimeframeMinutes = 5,
                .RoundTripFee = 0.74D
            })

            ' M6B — Micro GBP/USD  [quarterly H/M/U/Z]
            ' 6,250 GBP per contract; tick = 0.0001 USD/GBP = $0.625.
            list.Add(New FavouriteContract("M6B", "GBP/USD", "CON.F.US.M6B.U26", 0.0001D, 0.625D, 6250D, 0.1D, 12.5D) With {
                .PxRootSymbol = "M6B",
                .CommissionTickBuffer = 1,
                .MultiConfluenceTimeframeMinutes = 10,
                .RoundTripFee = 0.52D
            })

            ' M6A — Micro AUD/USD  [quarterly H/M/U/Z]
            ' 10,000 AUD per contract; tick = 0.0001 USD/AUD = $1.00.
            list.Add(New FavouriteContract("M6A", "AUD/USD", "CON.F.US.M6A.U26", 0.0001D, 1.0D, 10000D, 0.1D, 12.5D) With {
                .PxRootSymbol = "M6A",
                .CommissionTickBuffer = 1,
                .MultiConfluenceTimeframeMinutes = 10,
                .RoundTripFee = 0.52D
            })

            ' MHG — Micro Copper  [monthly H/K/N/U/Z]
            ' Contract: 2,500 lb. Tick = 0.0005 = $1.25.
            list.Add(New FavouriteContract("MHG", "Copper", "CON.F.US.MHG.U26", 0.0005D, 1.25D, 2500D, 0.2D, 20D) With {
                .PxRootSymbol = "MHG",
                .CommissionTickBuffer = 2,
                .MultiConfluenceTimeframeMinutes = 10,
                .RoundTripFee = 1.04D,
                .RollLeadDays = 28
            })

            Return list
        End Function

    End Class

End Namespace
