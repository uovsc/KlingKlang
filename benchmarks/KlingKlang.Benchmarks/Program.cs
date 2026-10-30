using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using KlingKlang;

const int sampleRate = 48_000;
const int warmupBlocks = 256;
const int measuredBlocks = 512;

Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}; OS: {RuntimeInformation.OSDescription}; " +
    $"architecture: {RuntimeInformation.ProcessArchitecture}; logical processors: {Environment.ProcessorCount}");
Console.WriteLine($"Warmup: {warmupBlocks} blocks; measurement: {measuredBlocks} blocks per scenario");
Console.WriteLine("channels voices frames frames/s realtime allocated-bytes result");

foreach (short channels in new short[] { 1, 2 })
{
    using AudioClip clip = CreateClip(channels);
    foreach (int blockFrames in new[] { 480, 960 })
    {
        foreach (int voiceCount in new[] { 0, 1, 16, 64 })
        {
            using var engine = AudioEngine.CreateOffline();
            for (int i = 0; i < voiceCount; i++)
            {
                engine.Play(clip, volume: .01f, loop: true);
            }

            var output = new float[blockFrames * 2];
            for (int i = 0; i < warmupBlocks; i++)
            {
                engine.Render(output);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < measuredBlocks; i++)
            {
                engine.Render(output);
            }

            long elapsed = Stopwatch.GetTimestamp() - start;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            double framesPerSecond = (double)blockFrames * measuredBlocks * Stopwatch.Frequency / elapsed;
            double ratio = framesPerSecond / sampleRate;
            string result = allocated != 0 ? "ALLOCATED" : voiceCount == 64 && ratio < 1 ? "BELOW_REALTIME" : "ok";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{channels} {voiceCount} {blockFrames} {framesPerSecond:F0} {ratio:F2} {allocated} {result}"));
        }
    }
}

static AudioClip CreateClip(short channels)
{
    const int frames = 1024;
    const int sampleRate = 48_000;
    int dataSize = frames * channels * sizeof(short);
    using var stream = new MemoryStream();
    using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
    {
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
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
        for (int frame = 0; frame < frames; frame++)
        {
            short sample = (short)(MathF.Sin(2 * MathF.PI * frame / 64) * 10_000);
            for (int channel = 0; channel < channels; channel++)
            {
                writer.Write(sample);
            }
        }
    }

    stream.Position = 0;
    return AudioClip.LoadWav(stream);
}
