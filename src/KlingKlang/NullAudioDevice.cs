namespace KlingKlang;

internal sealed class NullAudioDevice : IAudioDevice
{
    private AudioRenderCallback? _render;
    private bool _disposed;

    public AudioDeviceStatus Status => AudioDeviceStatus.Ready;

    public void Start(AudioRenderCallback render)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(render);
        if (_render is not null)
        {
            throw new InvalidOperationException("The device is already started.");
        }

        _render = render;
    }

    public void Render(Span<float> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AudioRenderCallback render = _render ?? throw new InvalidOperationException("The device is not started.");
        render(output);
    }

    public void Stop() => _render = null;

    public void Dispose()
    {
        Stop();
        _disposed = true;
    }
}
