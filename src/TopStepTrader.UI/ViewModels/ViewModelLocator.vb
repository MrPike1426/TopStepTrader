Imports Microsoft.Extensions.DependencyInjection
Imports TopStepTrader.UI.Views

Namespace TopStepTrader.UI.ViewModels

    ''' <summary>
    ''' Singleton service that creates and caches one view per navigation section.
    ''' Uses IServiceScopeFactory to resolve Scoped dependencies (EF Core, repositories)
    ''' inside a proper scope rather than from the root container.
    ''' </summary>
    Public Class ViewModelLocator
        Implements IDisposable

        Private ReadOnly _scopeFactory As IServiceScopeFactory
        Private ReadOnly _scopes As New Dictionary(Of String, IServiceScope)()
        Private ReadOnly _instances As New Dictionary(Of String, Object)()
        Private _disposed As Boolean = False

        Public Sub New(scopeFactory As IServiceScopeFactory)
            _scopeFactory = scopeFactory
        End Sub

        ''' <summary>
        ''' Returns the same view/VM instance for a given key on every call.
        ''' The scope and the resolved instance are both cached so that navigating
        ''' away and back to a page never recreates it (preventing state loss).
        ''' </summary>
        Private Function Resolve(Of T)(key As String) As T
            If Not _instances.ContainsKey(key) Then
                If Not _scopes.ContainsKey(key) Then
                    _scopes(key) = _scopeFactory.CreateScope()
                End If
                _instances(key) = _scopes(key).ServiceProvider.GetRequiredService(Of T)()
            End If
            Return CType(_instances(key), T)
        End Function

        Public ReadOnly Property DashboardView As DashboardView
            Get
                Return Resolve(Of DashboardView)("Dashboard")
            End Get
        End Property

        Public ReadOnly Property OrderBookView As OrderBookView
            Get
                Return Resolve(Of OrderBookView)("Orders")
            End Get
        End Property


        Public ReadOnly Property BacktestView As BacktestView
            Get
                Return Resolve(Of BacktestView)("Backtest")
            End Get
        End Property

        Public ReadOnly Property SettingsView As SettingsView
            Get
                Return Resolve(Of SettingsView)("Settings")
            End Get
        End Property


        Public ReadOnly Property TestTradeView As TestTradeView
            Get
                Return Resolve(Of TestTradeView)("TestTrade")
            End Get
        End Property

        Public ReadOnly Property ScalperTestView As ScalperTestView
            Get
                Return Resolve(Of ScalperTestView)("ScalperTest")
            End Get
        End Property

        Public ReadOnly Property PriceTrackerView As PriceTrackerView
            Get
                Return Resolve(Of PriceTrackerView)("PriceTracker")
            End Get
        End Property

        Public ReadOnly Property SniperView As SniperView
            Get
                Return Resolve(Of SniperView)("Sniper")
            End Get
        End Property

        Public ReadOnly Property PumpNDumpView As Views.PumpNDumpView
            Get
                Return Resolve(Of Views.PumpNDumpView)("PumpNDump")
            End Get
        End Property

        Public ReadOnly Property SuperTrendPlusView As Views.SuperTrendPlusView
            Get
                Return Resolve(Of Views.SuperTrendPlusView)("SuperTrendPlus")
            End Get
        End Property

        Public ReadOnly Property HydraView As Views.HydraView
            Get
                Return Resolve(Of Views.HydraView)("Hydra")
            End Get
        End Property

        Public ReadOnly Property AssetBassettView As Views.AssetBassettView
            Get
                Return Resolve(Of Views.AssetBassettView)("AssetBassett")
            End Get
        End Property

        Public ReadOnly Property CryptoJoeView As Views.CryptoJoeView
            Get
                Return Resolve(Of Views.CryptoJoeView)("CryptoJoe")
            End Get
        End Property

        Public ReadOnly Property ApiKeysView As Views.ApiKeysView
            Get
                Return Resolve(Of Views.ApiKeysView)("ApiKeys")
            End Get
        End Property

        Public ReadOnly Property PersonaView As Views.PersonaView
            Get
                Return Resolve(Of Views.PersonaView)("Persona")
            End Get
        End Property

        Public ReadOnly Property ProTraderView As Views.ProTraderView
            Get
                Return Resolve(Of Views.ProTraderView)("ProTrader")
            End Get
        End Property

        Public ReadOnly Property DebugTradeViewerView As Views.DebugTradeViewerView
            Get
                Return Resolve(Of Views.DebugTradeViewerView)("DebugTradeViewer")
            End Get
        End Property

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                _instances.Clear()
                For Each scope In _scopes.Values
                    scope.Dispose()
                Next
                _scopes.Clear()
                _disposed = True
            End If
        End Sub

    End Class

End Namespace
