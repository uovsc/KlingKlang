namespace KlingKlang;

internal delegate void AudioRenderCallback(Span<float> output);

internal sealed record AudioDeviceStatus(AudioConnectionState State, Exception? Failure)
{
    public static readonly AudioDeviceStatus Ready = new(AudioConnectionState.Ready, null);
}

internal sealed class AudioDeviceLostException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

internal interface IAudioDevice : IDisposable
{
    AudioDeviceStatus Status { get; }
    void Start(AudioRenderCallback render);
    void Stop();
}
