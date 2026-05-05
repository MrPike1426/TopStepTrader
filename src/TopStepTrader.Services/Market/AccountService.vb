Imports Microsoft.Extensions.Logging
Imports TopStepTrader.API.Adapters
Imports TopStepTrader.API.Http.ProjectX
Imports TopStepTrader.Core.Interfaces
Imports TopStepTrader.Core.Models

Namespace TopStepTrader.Services.Market

    ''' <summary>
    ''' TopStepX-only implementation of IAccountService.
    ''' TopStepX is the only active broker.
    ''' </summary>
    Public Class AccountService
        Implements IAccountService

        Private ReadOnly _pxClient As PXAccountClient
        Private ReadOnly _keyStore As IApiKeyStore
        Private ReadOnly _session As ITradingSessionContext
        Private ReadOnly _logger As ILogger(Of AccountService)

        Public Sub New(pxClient As PXAccountClient,
                       keyStore As IApiKeyStore,
                       session As ITradingSessionContext,
                       logger As ILogger(Of AccountService))
            _pxClient = pxClient
            _keyStore = keyStore
            _session = session
            _logger = logger
        End Sub

        ''' <summary>
        ''' Returns accounts filtered by the current AutoExecution mode.
        ''' When disabled only practice accounts (PRAC-*) are returned.
        ''' </summary>
        Public Async Function GetActiveAccountsAsync() As Task(Of IEnumerable(Of Account)) _
            Implements IAccountService.GetActiveAccountsAsync

            Dim keys = _keyStore.Load()

            Dim pxConfigured = Not String.IsNullOrWhiteSpace(keys.TopStepXUsername) AndAlso
                                Not String.IsNullOrWhiteSpace(keys.TopStepXApiKey)
            If Not pxConfigured Then
                _logger.LogWarning("TopStepX credentials not configured — no accounts will be returned")
                Return Enumerable.Empty(Of Account)()
            End If

            Dim all = Await GetTopStepXAccountsAsync()
            If Not _session.AutoExecutionEnabled Then
                Return all.Where(Function(a) a.Name IsNot Nothing AndAlso
                                              a.Name.StartsWith("PRAC-", StringComparison.OrdinalIgnoreCase))
            End If
            Return all
        End Function

        Public Async Function GetAccountAsync(accountId As Long) As Task(Of Account) _
            Implements IAccountService.GetAccountAsync
            Dim all = Await GetActiveAccountsAsync()
            Return all.FirstOrDefault(Function(a) a.Id = accountId)
        End Function

        Private Async Function GetTopStepXAccountsAsync() As Task(Of IEnumerable(Of Account))
            Try
                Dim response = Await _pxClient.SearchAccountsAsync(onlyActive:=True)
                If response Is Nothing OrElse Not response.Success Then
                    _logger.LogWarning("TopStepX account search failed: {Msg}", response?.ErrorMessage)
                    Return Enumerable.Empty(Of Account)()
                End If
                ' Do not filter by IsVisible — TopStepX may not return that field.
                ' The API-level onlyActive=True filter is sufficient.
                Dim accounts = response.Accounts.
                    Select(Function(a) BrokerModelAdapter.FromPX(a)).
                    ToList()
                _logger.LogInformation("TopStepX accounts loaded: {Count}", accounts.Count)
                Return accounts
            Catch ex As Exception
                _logger.LogError(ex, "TopStepX GetActiveAccounts failed")
                Return Enumerable.Empty(Of Account)()
            End Try
        End Function

    End Class

End Namespace
