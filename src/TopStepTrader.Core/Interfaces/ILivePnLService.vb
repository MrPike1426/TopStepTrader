Namespace TopStepTrader.Core.Interfaces

    ''' <summary>
    ''' Source of the most recent price tick.
    ''' </summary>
    Public Enum LivePriceSource
        ''' <summary>
        ''' Metadata-only tick (e.g. entry-price correction emitted before any
        ''' quote/bar has arrived). <c>Price = 0</c> and <c>AgeMs = 0</c> in this
        ''' case — consumers MUST NOT overwrite their displayed price from such
        ''' a tick. Use only the entry/size/P&amp;L fields.
        ''' </summary>
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
    ''' ARCH-07 — Failure-visibility surface for <see cref="ILivePnLService"/>.
    ''' Returned by <see cref="ILivePnLService.GetDiagnostics(String)"/> so that
    ''' a UI status strip can prove the feed is healthy (or surface exactly which
    ''' layer has gone silent: hub disconnected, subscribe rejected, bar polls
    ''' returning zero, etc.).
    ''' </summary>
    Public Class LivePnLDiagnostics
        ''' <summary>SignalR HubConnectionState as a string, or "Unknown" if unavailable.</summary>
        Public Property HubState As String = "Unknown"
        ''' <summary>Last <c>SubscribeContractAsync</c> failure message, if any.</summary>
        Public Property SubscribeError As String = String.Empty
        ''' <summary>The PxContractId the service is currently subscribed to for this root, or empty.</summary>
        Public Property SubscribedContractId As String = String.Empty
        ''' <summary>Rolling 5-second quotes-per-second rate.</summary>
        Public Property QuotesPerSec5s As Double = 0D
        ''' <summary>UTC of the most recent quote received for this root (DateTime.MinValue if none).</summary>
        Public Property LastQuoteUtc As DateTime = DateTime.MinValue
        ''' <summary>UTC of the most recent successful (price &gt; 0) bar poll (DateTime.MinValue if none).</summary>
        Public Property LastBarUtc As DateTime = DateTime.MinValue
        ''' <summary>Consecutive bar polls that returned 0; reset by any &gt; 0 fetch.</summary>
        Public Property BarFetchZeroCount As Integer = 0
        ''' <summary>Short summary of the most recent bar-poll outcome (e.g. "BarFetch=2351.0", "BarFetch=0 (count=3)").</summary>
        Public Property LastBarPollResult As String = String.Empty
        ''' <summary>Recent diagnostic events (bar polls, subscribe errors). Capped at 200 entries.</summary>
        Public Property RecentEvents As IReadOnlyList(Of String) = Array.Empty(Of String)()
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

        ''' <summary>
        ''' ARCH-07 — Returns a snapshot of feed health for the given root key
        ''' (e.g. "MNQ"). Returns a <see cref="LivePnLDiagnostics"/> with default
        ''' values when the root has no active state yet.
        ''' </summary>
        Function GetDiagnostics(rootKey As String) As LivePnLDiagnostics

    End Interface

End Namespace
