using System.Text;

namespace KlingKlang.Tests;

internal static class WaveTestData
{
    public static byte[] Pcm(short[] samples, short channels = 1, int sampleRate = 48_000)
    {
        return Wave(Format(channels, sampleRate), Data(samples));
    }

    public static (string Id, byte[] Payload) Format(
        short channels = 1,
        int sampleRate = 48_000,
        short encoding = 1,
        short bits = 16,
        short? blockAlign = null,
        int? byteRate = null)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
        short alignment = blockAlign ?? (short)(channels * 2);
        writer.Write(encoding);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate ?? sampleRate * alignment);
        writer.Write(alignment);
        writer.Write(bits);
        return ("fmt ", buffer.ToArray());
    }

    public static (string Id, byte[] Payload) Data(params short[] samples)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
        foreach (short sample in samples)
        {
            writer.Write(sample);
        }

        return ("data", buffer.ToArray());
    }

    public static byte[] Wave(params (string Id, byte[] Payload)[] chunks)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(0);
        writer.Write("WAVE"u8);
        foreach ((string id, byte[] payload) in chunks)
        {
            writer.Write(Encoding.ASCII.GetBytes(id));
            writer.Write(payload.Length);
            writer.Write(payload);
            if ((payload.Length & 1) != 0)
            {
                writer.Write((byte)0);
            }
        }

        buffer.Position = 4;
        writer.Write(checked((int)buffer.Length - 8));
        return buffer.ToArray();
    }

    public static AudioClip Clip(short[] samples, short channels = 1) =>
        AudioClip.LoadWav(new MemoryStream(Pcm(samples, channels)));
}
