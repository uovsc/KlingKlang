namespace KlingKlang;

internal sealed class ThreadedAudioDevice(Func<IAudioOutputStream> openStream) : IAudioDevice
{
    private readonly ManualResetEventSlim _ready = new();
    private readonly ManualResetEventSlim _stop = new();
    private AudioRenderCallback? _render;
    private Thread? _thread;
    private Exception? _failure;
    private bool _disposed;

    public Exception? Failure => Volatile.Read(ref _failure);

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
        if (Failure is Exception failure)
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
        try
        {
            stream = openStream();
            if (stream.BufferFrames <= 0 || stream.BufferFrames > Array.MaxLength / 2)
            {
                throw new InvalidOperationException("The audio device returned an invalid buffer size.");
            }

            var buffer = new float[stream.BufferFrames * 2];
            RenderAvailable(stream, buffer);
            stream.Start();
            _ready.Set();
            while (!_stop.Wait(10))
            {
                RenderAvailable(stream, buffer);
            }
        }
        catch (Exception exception)
        {
            RecordFailure(exception);
        }
        finally
        {
            try
            {
                stream?.Dispose();
            }
            catch (Exception exception)
            {
                RecordFailure(exception);
            }

            _ready.Set();
        }
    }

    private void RenderAvailable(IAudioOutputStream stream, float[] buffer)
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
        _render!(samples);
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = Math.Clamp(samples[i], -1f, 1f);
        }

        stream.Write(buffer, frames);
    }

    private void RecordFailure(Exception exception)
    {
        Exception? previous = _failure;
        Volatile.Write(ref _failure, previous is null ? exception : new AggregateException(previous, exception));
    }
}
