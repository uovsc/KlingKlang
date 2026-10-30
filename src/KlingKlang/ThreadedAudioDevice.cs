namespace KlingKlang;

internal sealed class ThreadedAudioDevice : IAudioDevice
{
    private const int ReconnectAttempts = 2;
    private const int RetryDelayMilliseconds = 500;
    private readonly Func<IAudioOutputStream> _openStream;
    private readonly Func<ManualResetEventSlim, int, bool> _retryWait;
    private readonly ManualResetEventSlim _ready = new();
    private readonly ManualResetEventSlim _stop = new();
    private AudioRenderCallback? _render;
    private Thread? _thread;
    private Exception? _startupFailure;
    private AudioDeviceStatus _status = AudioDeviceStatus.Ready;
    private bool _disposed;

    public ThreadedAudioDevice(
        Func<IAudioOutputStream> openStream,
        Func<ManualResetEventSlim, int, bool>? retryWait = null)
    {
        _openStream = openStream;
        _retryWait = retryWait ?? WaitForStop;
    }

    public AudioDeviceStatus Status => Volatile.Read(ref _status);
    internal Exception? Failure => Status.Failure;

    public void Start(AudioRenderCallback render)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(render);
        if (_thread is not null)
        {
            throw new InvalidOperationException("The audio device has already been started.");
        }

        _render = render;
        _thread = new Thread(Run) { IsBackground = true, Name = "KlingKlang audio" };
        try
        {
            _thread.Start();
        }
        catch
        {
            _thread = null;
            throw;
        }

        _ready.Wait();
        if (Volatile.Read(ref _startupFailure) is Exception failure)
        {
            _thread.Join();
            throw new InvalidOperationException("Audio device initialization failed.", failure);
        }
    }

    public void Stop()
    {
        if (_thread is not null)
        {
            _stop.Set();
            _thread.Join();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _stop.Dispose();
        _ready.Dispose();
        _render = null;
        _disposed = true;
    }

    private void Run()
    {
        IAudioOutputStream? stream = null;
        bool startupSucceeded = false;
        try
        {
            stream = OpenStartedStream(renderInitialAudio: true, out float[] buffer);
            startupSucceeded = true;
            _ready.Set();
            while (!_stop.Wait(10))
            {
                try
                {
                    RenderAvailable(stream, buffer, renderAudio: true);
                }
                catch (AudioDeviceLostException loss)
                {
                    Publish(AudioConnectionState.Recovering, loss);
                    IAudioOutputStream lostStream = stream;
                    stream = null;
                    try
                    {
                        lostStream.Dispose();
                    }
                    catch (Exception cleanupFailure)
                    {
                        throw new AggregateException("The lost audio device could not be released.", loss, cleanupFailure);
                    }

                    stream = Reconnect(loss, out buffer);
                    if (stream is null)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            Publish(AudioConnectionState.Faulted, exception);
        }
        finally
        {
            try
            {
                stream?.Dispose();
            }
            catch (Exception exception)
            {
                Exception? previous = Status.Failure;
                Publish(AudioConnectionState.Faulted,
                    previous is null ? exception : new AggregateException(previous, exception));
            }

            if (!startupSucceeded)
            {
                Volatile.Write(ref _startupFailure, Status.Failure);
            }

            _ready.Set();
        }
    }

    private IAudioOutputStream? Reconnect(AudioDeviceLostException loss, out float[] buffer)
    {
        var failures = new List<Exception> { loss };
        buffer = [];
        for (int attempt = 0; attempt < ReconnectAttempts; attempt++)
        {
            if (_stop.IsSet || (attempt > 0 && _retryWait(_stop, RetryDelayMilliseconds)))
            {
                return null;
            }

            try
            {
                IAudioOutputStream stream = OpenStartedStream(renderInitialAudio: false, out buffer);
                Publish(AudioConnectionState.Ready, null);
                return stream;
            }
            catch (AudioDeviceLostException exception)
            {
                failures.Add(exception);
                Publish(AudioConnectionState.Recovering,
                    new AggregateException($"Audio reconnection attempt {attempt + 1} failed.", failures));
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                throw new AggregateException("Audio reconnection encountered a non-recoverable error.", failures);
            }
        }

        throw new AggregateException("Audio reconnection failed after two attempts.", failures);
    }

    private IAudioOutputStream OpenStartedStream(bool renderInitialAudio, out float[] buffer)
    {
        IAudioOutputStream stream = _openStream();
        try
        {
            if (stream.BufferFrames <= 0 || stream.BufferFrames > Array.MaxLength / 2)
            {
                throw new InvalidOperationException("The audio device returned an invalid buffer size.");
            }

            buffer = new float[stream.BufferFrames * 2];
            RenderAvailable(stream, buffer, renderInitialAudio);
            stream.Start();
            return stream;
        }
        catch (Exception initializationFailure)
        {
            try
            {
                stream.Dispose();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Audio stream startup and cleanup both failed.", initializationFailure, cleanupFailure);
            }

            throw;
        }
    }

    private void RenderAvailable(IAudioOutputStream stream, float[] buffer, bool renderAudio)
    {
        int frames = stream.GetAvailableFrames();
        if (frames < 0 || frames > stream.BufferFrames)
        {
            throw new InvalidOperationException("The audio device returned an invalid available frame count.");
        }

        if (frames == 0)
        {
            return;
        }

        Span<float> samples = buffer.AsSpan(0, frames * 2);
        if (renderAudio)
        {
            _render!(samples);
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = Math.Clamp(samples[i], -1f, 1f);
            }
        }
        else
        {
            samples.Clear();
        }

        stream.Write(buffer, frames);
    }

    private void Publish(AudioConnectionState state, Exception? failure) =>
        Volatile.Write(ref _status, new AudioDeviceStatus(state, failure));

    private static bool WaitForStop(ManualResetEventSlim stop, int milliseconds) => stop.Wait(milliseconds);
}
