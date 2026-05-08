Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' Source of the most recent price tick.
    ''' </summary>
    Public Enum LivePriceSource
        None = 0
        ''' <summary>MarketHub GatewayQuote (sub-second).</summary>
        Quote = 1
        ''' <summary>2-second history-bar fallback.</summary>
        Bar = 2
    End Enum

    ''' <summary>
    ''' One price emission for an instrument.  Carries the source tag and age in
    ''' milliseconds so consumers can prove the feed is healthy without a separate
    ''' diagnostic channel.
    ''' </summary>
    Public Class LivePriceTick
        ''' <summary>Canonical root contract identifier the subscriber registered with (e.g. "M6E").</summary>
        Public Property ContractId As String = String.Empty
        Public Property Price As Decimal
        Public Property Source As LivePriceSource
        ''' <summary>Age in ms of the underlying datapoint (now − datapoint UTC).</summary>
        Public Property AgeMs As Long
        Public Property TimestampUtc As DateTime
    End Class

    ''' <summary>Price + entry/size/P&amp;L emission for an open position.</summary>
    Public Class LivePnLTick
        Inherits LivePriceTick
        Public Property EntryPrice As Decimal
        ''' <summary>Signed: +N for long, -N for short.</summary>
        Public Property Size As Integer
        ''' <summary>Already accounts for direction × DollarPerPoint.</summary>
        Public Property UnrealisedPnL As Decimal
        Public Property DollarPerPoint As Decimal
    End Class

    ''' <summary>
    ''' Per-instrument live price + unrealised P&amp;L stream.
    ''' Subscribers receive ticks at SignalR cadence (sub-second when active),
    ''' falling back to a 2-second polled bar close if the socket is silent.
    ''' Disposing the returned IDisposable unsubscribes the underlying MarketHub
    ''' subscription if no other consumer remains for the same contract.
    ''' Implementations are thread-safe; the on-tick callback is NOT guaranteed
    ''' to fire on the UI thread — marshal to the dispatcher in the consumer.
    ''' </summary>
    Public Interface ILivePnLService

        ''' <summary>
        ''' Subscribe to price + P&amp;L for an open position.
        ''' </summary>
        ''' <param name="contractId">Canonical root symbol, e.g. "M6E", "MNQ".</param>
        ''' <param name="entryPrice">Initial entry estimate. Will self-correct on
        ''' first Position/searchOpen sync if delta &gt; 1 tick.</param>
        ''' <param name="signedSize">+N for long, -N for short.</param>
        ''' <param name="onTick">Invoked on every price update.</param>
        ''' <param name="accountId">Account whose live position seeds the entry-price self-correction. 0 disables correction.</param>
        Function Subscribe(contractId As String,
                           entryPrice As Decimal,
                           signedSize As Integer,
                           onTick As Action(Of LivePnLTick),
                           Optional accountId As Long = 0) As IDisposable

        ''' <summary>
        ''' Price-only feed (no entry, no P&amp;L) — for views that just need a ticker.
        ''' </summary>
        Function SubscribePrice(contractId As String,
                                onTick As Action(Of LivePriceTick)) As IDisposable

    End Interface

End Namespace
