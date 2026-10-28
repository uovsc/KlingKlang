namespace KlingKlang;

internal delegate void AudioRenderCallback(Span<float> output);

internal interface IAudioDevice : IDisposable
{
    Exception? Failure { get; }
    void Start(AudioRenderCallback render);
    void Stop();
}
