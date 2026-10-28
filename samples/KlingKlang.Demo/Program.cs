using KlingKlang;

const int sampleRate = 48_000;
const int firstPartFrames = sampleRate / 4;
const int toneFrames = sampleRate;
string outputPath = args.Length > 0 ? args[0] : "klingklang-demo.wav";

var tone = new float[toneFrames];
for (int i = 0; i < tone.Length; i++)
{
    tone[i] = 0.6f * MathF.Sin(2 * MathF.PI * 440 * i / sampleRate);
}

using var source = new MemoryStream();
WaveWriter.Write(source, tone, channels: 1);
source.Position = 0;
using AudioClip clip = AudioClip.LoadWav(source);
using AudioEngine engine = AudioEngine.CreateOffline();

var mixed = new float[(firstPartFrames + toneFrames) * 2];
engine.Play(clip, volume: 0.8f);
engine.Render(mixed.AsSpan(0, firstPartFrames * 2));
engine.Play(clip, volume: 0.8f);
engine.Render(mixed.AsSpan(firstPartFrames * 2));

using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write))
{
    WaveWriter.Write(output, mixed, channels: 2);
}

Console.WriteLine($"Wrote offline stereo WAV: {Path.GetFullPath(outputPath)}");
