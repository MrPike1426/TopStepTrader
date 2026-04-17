Imports System.IO
Imports System.Media
Imports System.Threading.Tasks

Namespace TopStepTrader.UI.Infrastructure

    ''' <summary>
    ''' Plays a short click/tick sound whenever the Turtle bracket advances.
    ''' The WAV is generated entirely in memory — no external audio files are required.
    '''
    ''' Sound design: a 60 ms sine burst at 1 kHz with a fast exponential decay
    ''' (~20 ms half-life).  The result is a crisp "tick" rather than a sustained beep.
    ''' Playback is fire-and-forget on a thread-pool thread; audio failures are silently
    ''' swallowed so they can never surface to the trading UI.
    ''' </summary>
    Friend Module TurtleClickSound

        ''' <summary>
        ''' Fires a non-blocking tick sound.  Returns immediately; the sound plays in the background.
        ''' </summary>
        Public Sub PlayAsync()
            Task.Run(Sub()
                         Try
                             Using ms = BuildWav()
                                 Using player As New SoundPlayer(ms)
                                     player.PlaySync()   ' sync is fine — we are already on a pool thread
                                 End Using
                             End Using
                         Catch
                             ' Non-critical — audio failure must never propagate to the trading engine.
                         End Try
                     End Sub)
        End Sub

        ' ── WAV generation ─────────────────────────────────────────────────────────

        ''' <summary>
        ''' Builds a minimal 16-bit mono PCM WAV stream containing the click waveform.
        ''' </summary>
        Private Function BuildWav() As MemoryStream
            Const SampleRate  As Integer = 44100
            Const DurationMs  As Integer = 60         ' total length — silent tail ensures clean fade
            Const FrequencyHz As Double  = 1000.0     ' 1 kHz — crisp without being harsh
            Const DecayRate   As Double  = 55.0       ' exp(−t × 55) ≈ 0 at 60 ms
            Const Amplitude   As Integer = 14000      ' ~43 % of Int16 range — audible but not jarring

            Dim numSamples As Integer = SampleRate * DurationMs \ 1000   ' = 2 646 samples
            Dim dataBytes  As Integer = numSamples * 2                    ' 16-bit = 2 bytes / sample

            Dim ms As New MemoryStream(44 + dataBytes)
            Dim w  As New BinaryWriter(ms)

            ' ── RIFF / WAVE / fmt  chunk ──────────────────────────────────────────
            w.Write(Text.Encoding.ASCII.GetBytes("RIFF"))
            w.Write(CInt(36 + dataBytes))          ' overall file size − 8
            w.Write(Text.Encoding.ASCII.GetBytes("WAVE"))
            w.Write(Text.Encoding.ASCII.GetBytes("fmt "))
            w.Write(CInt(16))                      ' fmt chunk size (PCM)
            w.Write(CShort(1))                     ' AudioFormat  = PCM
            w.Write(CShort(1))                     ' NumChannels  = mono
            w.Write(CInt(SampleRate))              ' SampleRate
            w.Write(CInt(SampleRate * 2))          ' ByteRate     = SampleRate × BlockAlign
            w.Write(CShort(2))                     ' BlockAlign   = 2 bytes (16-bit mono)
            w.Write(CShort(16))                    ' BitsPerSample

            ' ── data chunk ────────────────────────────────────────────────────────
            w.Write(Text.Encoding.ASCII.GetBytes("data"))
            w.Write(CInt(dataBytes))

            For i As Integer = 0 To numSamples - 1
                Dim t      = CDbl(i) / SampleRate
                Dim decay  = Math.Exp(-t * DecayRate)
                Dim sine   = Math.Sin(2.0 * Math.PI * FrequencyHz * t)
                Dim sample = CShort(Math.Round(Amplitude * decay * sine))
                w.Write(sample)
            Next

            ms.Position = 0
            Return ms
        End Function

    End Module

End Namespace
