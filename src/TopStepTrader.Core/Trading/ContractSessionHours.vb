Namespace TopStepTrader.Core.Trading

    ''' <summary>
    ''' STRAT-42 F4: per-contract trading-window check used by each strategy's
    ''' per-symbol scan loop. Today every TopStepX favourite (MES, MNQ, M2K, MBT,
    ''' MGC, M6E, MCL) follows the same CME Globex schedule, so this is a single
    ''' static lookup. A follow-up will replace the static schedule with a
    ''' TopStepX session-status API call once that endpoint is wired in.
    '''
    ''' Closed windows (US Central Time, DST-aware):
    '''   - Saturday all day.
    '''   - Sunday before 17:00 CT (CME reopens 5:00 PM CT = 22:00–23:00 UTC depending on DST).
    '''   - Daily 16:00–17:00 CT maintenance (21:00–22:00 UTC depending on DST).
    '''
    ''' Note: the SuperTrend+ engine separately enforces a 21:10 → 22:00 UTC
    ''' close window via SessionCloseTime/SessionResumeTime. The daily-maintenance
    ''' clause here is the per-contract equivalent for other orchestrators.
    ''' </summary>
    Public Module ContractSessionHours

        ''' <summary>
        ''' Returns True if the given contract is inside its trading window at the
        ''' supplied UTC instant.
        ''' </summary>
        Public Function IsContractTradingNow(symbol As String, utcNow As DateTime) As Boolean
            Dim ctNow = ToCentralTime(utcNow)
            Return IsCmeGlobexOpen(ctNow)
        End Function

        ''' <summary>
        ''' Returns the next time the contract is expected to be open at, at or after
        ''' <paramref name="utcNow"/>. When the contract is already open, returns
        ''' <paramref name="utcNow"/>.
        ''' </summary>
        Public Function NextOpenUtc(symbol As String, utcNow As DateTime) As DateTime
            Dim centralTz = TryGetCentralTimeZone()
            Dim ctNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), centralTz)
            If IsCmeGlobexOpen(ctNow) Then Return utcNow

            ' Step forward in CT until the next open minute, then convert back to UTC.
            Dim probe As DateTime = ctNow
            ' Cap the search at 72 hours to avoid pathological loops.
            For i As Integer = 0 To 72 * 60
                probe = probe.AddMinutes(1)
                If IsCmeGlobexOpen(probe) Then
                    Return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(probe, DateTimeKind.Unspecified), centralTz)
                End If
            Next
            Return utcNow
        End Function

        Private Function IsCmeGlobexOpen(ctTime As DateTime) As Boolean
            Dim day = ctTime.DayOfWeek
            Dim hour = ctTime.Hour
            If day = DayOfWeek.Saturday Then Return False
            If day = DayOfWeek.Sunday AndAlso hour < 17 Then Return False
            If hour = 16 Then Return False
            Return True
        End Function

        Private Function ToCentralTime(utcNow As DateTime) As DateTime
            Dim centralTz = TryGetCentralTimeZone()
            Return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), centralTz)
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
