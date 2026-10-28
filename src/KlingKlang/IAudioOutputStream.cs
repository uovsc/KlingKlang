namespace KlingKlang;

internal interface IAudioOutputStream : IDisposable
{
    int BufferFrames { get; }
    int GetAvailableFrames();
    void Write(float[] samples, int frames);
    void Start();
}
