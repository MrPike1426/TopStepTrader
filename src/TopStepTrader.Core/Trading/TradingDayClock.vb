Namespace TopStepTrader.Core.Trading

    ''' <summary>
    ''' ARCH-21: TopStep trading-day boundary. The trading day resets at 17:00 US
    ''' Central (5:00 PM CT, when CME Globex reopens), matching TopStep's daily-loss
    ''' accounting. DST-aware via the same Central-timezone resolution used by
    ''' <see cref="ContractSessionHours"/>.
    ''' </summary>
    Public Module TradingDayClock

        Private ReadOnly RolloverTime As TimeSpan = TimeSpan.FromHours(17)

        ''' <summary>
        ''' Returns the UTC instant of the most recent 17:00 US Central at or before
        ''' <paramref name="nowUtc"/> — the start of the trading day containing that instant.
        ''' </summary>
        Public Function TradingDayStartUtc(nowUtc As DateTimeOffset) As DateTimeOffset
            Dim tz = TryGetCentralTimeZone()
            Dim ctNow As DateTime = TimeZoneInfo.ConvertTimeFromUtc(nowUtc.UtcDateTime, tz)
            Dim startDateCt As DateTime = If(ctNow.TimeOfDay >= RolloverTime, ctNow.Date, ctNow.Date.AddDays(-1))
            ' 17:00 CT never falls inside a US DST transition (those happen at 02:00
            ' local), so the conversion back to UTC is unambiguous.
            Dim startCt As DateTime = DateTime.SpecifyKind(startDateCt.Add(RolloverTime), DateTimeKind.Unspecified)
            Return New DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(startCt, tz), TimeSpan.Zero)
        End Function

        ''' <summary>
        ''' Stable key for the trading day containing <paramref name="nowUtc"/>:
        ''' the "yyyy-MM-dd" of the CT calendar date the trading day *ends* on
        ''' (a day starting 17:00 CT on Jul 14 keys as "2026-07-15").
        ''' </summary>
        Public Function TradingDayKey(nowUtc As DateTimeOffset) As String
            Dim tz = TryGetCentralTimeZone()
            Dim ctNow As DateTime = TimeZoneInfo.ConvertTimeFromUtc(nowUtc.UtcDateTime, tz)
            Dim endDateCt As DateTime = If(ctNow.TimeOfDay >= RolloverTime, ctNow.Date.AddDays(1), ctNow.Date)
            Return endDateCt.ToString("yyyy-MM-dd")
        End Function

        Private Function TryGetCentralTimeZone() As TimeZoneInfo
            Try
                Return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time")
            Catch
                Try
                    Return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago")
                Catch
                    Return TimeZoneInfo.Utc
                End Try
            End Try
        End Function

    End Module

End Namespace
