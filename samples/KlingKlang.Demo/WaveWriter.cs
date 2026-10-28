internal static class WaveWriter
{
    public static void Write(Stream stream, ReadOnlySpan<float> samples, short channels)
    {
        const int sampleRate = 48_000;
        if (channels is not (1 or 2) || samples.Length % channels != 0)
        {
            throw new ArgumentException("Samples must contain complete mono or stereo frames.", nameof(channels));
        }

        foreach (float sample in samples)
        {
            if (!float.IsFinite(sample))
            {
                throw new ArgumentException("Samples must be finite.", nameof(samples));
            }
        }

        int dataSize = checked(samples.Length * sizeof(short));
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(checked(36 + dataSize));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * sizeof(short));
        writer.Write((short)(channels * sizeof(short)));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        foreach (float sample in samples)
        {
            float clamped = Math.Clamp(sample, -1f, 1f);
            writer.Write((short)MathF.Round(clamped < 0 ? clamped * 32768f : clamped * 32767f));
        }
    }
}
