Imports System.Collections.Concurrent
Imports System.Threading

Namespace TopStepTrader.API.RateLimiting

    ''' <summary>
    ''' Thread-safe sliding-window rate limiter.
    ''' Waits when the window is full rather than throwing.
    ''' </summary>
    Public Class SlidingWindowCounter

        Private ReadOnly _maxRequests As Integer
        Private ReadOnly _window As TimeSpan
        Private ReadOnly _timestamps As New ConcurrentQueue(Of DateTimeOffset)
        Private ReadOnly _semaphore As New SemaphoreSlim(1, 1)

        Public Sub New(maxRequests As Integer, window As TimeSpan)
            _maxRequests = maxRequests
            _window = window
        End Sub

        Public Async Function WaitAsync(cancel As CancellationToken) As Task
            Await _semaphore.WaitAsync(cancel)
            Try
                ' Purge expired timestamps
                Dim cutoff = DateTimeOffset.UtcNow.Subtract(_window)
                Dim old As DateTimeOffset
                While _timestamps.TryPeek(old) AndAlso old < cutoff
                    _timestamps.TryDequeue(old)
                End While

                ' If window is full, wait until the oldest timestamp expires
                While _timestamps.Count >= _maxRequests
                    Dim oldest As DateTimeOffset
                    If _timestamps.TryPeek(oldest) Then
                        Dim waitMs = CInt((oldest.Add(_window) - DateTimeOffset.UtcNow).TotalMilliseconds) + 10
                        If waitMs > 0 Then
                            _semaphore.Release()
                            Await Task.Delay(waitMs, cancel)
                            Await _semaphore.WaitAsync(cancel)
                            ' Re-purge after waiting
                            cutoff = DateTimeOffset.UtcNow.Subtract(_window)
                            While _timestamps.TryPeek(old) AndAlso old < cutoff
                                _timestamps.TryDequeue(old)
                            End While
                        End If
                    End If
                End While

                _timestamps.Enqueue(DateTimeOffset.UtcNow)
            Finally
                _semaphore.Release()
            End Try
        End Function

    End Class

    ''' <summary>
    ''' Dual-window rate limiter matching ProjectX API limits:
    ''' - General: 200 requests / 60 seconds
    ''' - History: 50 requests / 30 seconds (also consumes a general slot)
    ''' </summary>
    Public Class RateLimiter

        Private ReadOnly _generalWindow As New SlidingWindowCounter(200, TimeSpan.FromSeconds(60))
        Private ReadOnly _historyWindow As New SlidingWindowCounter(50, TimeSpan.FromSeconds(30))

        ''' <summary>Acquire a slot for any general API call.</summary>
        Public Function WaitForGeneralSlotAsync(cancel As CancellationToken) As Task
            Return _generalWindow.WaitAsync(cancel)
        End Function

        ''' <summary>Acquire slots for history endpoint (consumes both general and history buckets).</summary>
        Public Async Function WaitForHistorySlotAsync(cancel As CancellationToken) As Task
            ' Acquire history slot first (stricter limit)
            Await _historyWindow.WaitAsync(cancel)
            ' Also consume a general slot
            Await _generalWindow.WaitAsync(cancel)
        End Function

    End Class

End Namespace
