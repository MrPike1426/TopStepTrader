Namespace TopStepTrader.Core.Trading

    ''' <summary>
    ''' STRAT-42: single source of truth for the coarse UTC session label
    ''' (Asia / London / US-Pre / US-RTH / US-Post) used as an ML feature on
    ''' TradeSetupSnapshot and TradeLifespan. Previously duplicated in
    ''' SuperTrendPlusViewModel, EntryExecutionService, and ExitExecutionService;
    ''' those three local copies have been removed.
    '''
    ''' This is a *labelling* helper only — it does not gate entries. The 23-hour
    ''' coverage audit (STRAT-42) keeps the label values unchanged so all
    ''' downstream consumers (ML training data, postmortem reports, lifespan
    ''' records) are unaffected.
    ''' </summary>
    Public Module SessionWindowResolver

        Public Function Resolve(utc As DateTime) As String
            Dim h As Integer = utc.Hour
            Select Case h
                Case 0 To 6   : Return "Asia"
                Case 7 To 11  : Return "London"
                Case 12 To 13 : Return "US-Pre"
                Case 14 To 20 : Return "US-RTH"
                Case Else     : Return "US-Post"
            End Select
        End Function

    End Module

End Namespace
