namespace KlingKlang;

public sealed class AudioClip : IDisposable
{
    internal const int MixSampleRate = 48_000;
    private float[]? _samples;

    internal AudioClip(float[] samples, int channels)
    {
        _samples = samples;
        Channels = channels;
        FrameCount = samples.Length / channels;
    }

    public int Channels { get; }
    public int SampleRate => MixSampleRate;
    public int FrameCount { get; }

    public static AudioClip LoadWav(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return LoadWav(stream);
    }

    public static AudioClip LoadWav(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The WAV stream must be readable.", nameof(stream));
        }

        return WavDecoder.Decode(stream);
    }

    internal float[] GetSamples()
    {
        float[]? samples = Volatile.Read(ref _samples);
        ObjectDisposedException.ThrowIf(samples is null, this);
        return samples;
    }

    public void Dispose() => Interlocked.Exchange(ref _samples, null);
}
