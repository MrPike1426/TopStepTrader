Imports System.Globalization

Namespace TopStepTrader.Core.Trading

    ''' <summary>
    ''' BUG-90 F5: pure formatter for the single structured log line emitted on every
    ''' <c>ReleaseSlotAsync</c> invocation. Every release channel — hub, MissCount,
    ''' SnapshotStalenessGuard, broker sweep, ExitEngine, P&amp;L Guard, manual — must funnel
    ''' through this format so the next recurrence of a stuck-slot incident is diagnosable
    ''' from log scraping alone.
    ''' </summary>
    ''' <remarks>
    ''' Format:
    '''   <c>slot=&lt;idx&gt; instrument=&lt;sym&gt; reason="&lt;reason&gt;" trigger=&lt;trigger&gt; lastSnapshotOk=&lt;utc|never&gt; missCount=&lt;n&gt; netPosLastSeen=&lt;n&gt;</c>
    ''' Stable across releases — log scrapers and the BUG-90 F6 test parse this string
    ''' as the contract.
    ''' </remarks>
    Public Module ReleaseLogFormatter

        Public Const NeverStamp As String = "never"

        Public Function Format(slotIndex As Integer,
                                instrument As String,
                                reason As String,
                                trigger As String,
                                lastSnapshotOkUtc As DateTime,
                                missCount As Integer,
                                netPosLastSeen As Integer) As String
            Dim safeInstrument = If(instrument, String.Empty)
            Dim safeReason = If(reason, String.Empty)
            Dim safeTrigger = If(trigger, String.Empty)
            Dim stamp As String
            If lastSnapshotOkUtc = DateTime.MinValue Then
                stamp = NeverStamp
            Else
                stamp = lastSnapshotOkUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
            End If
            Return String.Format(CultureInfo.InvariantCulture,
                "slot={0} instrument={1} reason=""{2}"" trigger={3} lastSnapshotOk={4} missCount={5} netPosLastSeen={6}",
                slotIndex, safeInstrument, safeReason, safeTrigger, stamp, missCount, netPosLastSeen)
        End Function

    End Module

End Namespace
