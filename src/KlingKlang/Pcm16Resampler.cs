using System.Buffers.Binary;

namespace KlingKlang;

internal static class Pcm16Resampler
{
    public static float[] Convert(ReadOnlySpan<byte> pcm, int channels, int sourceRate)
    {
        int sourceFrames = pcm.Length / (channels * sizeof(short));
        int outputFrames = GetOutputFrameCount(sourceFrames, channels, sourceRate);
        var samples = new float[checked(outputFrames * channels)];

        if (sourceRate == AudioClip.MixSampleRate)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * sizeof(short), sizeof(short))) / 32768f;
            }

            return samples;
        }

        for (int outputFrame = 0; outputFrame < outputFrames; outputFrame++)
        {
            long position = (long)outputFrame * sourceRate;
            int sourceFrame = (int)(position / AudioClip.MixSampleRate);
            int nextFrame = sourceFrame + 1 == sourceFrames ? 0 : sourceFrame + 1;
            float fraction = (float)(position % AudioClip.MixSampleRate) / AudioClip.MixSampleRate;
            for (int channel = 0; channel < channels; channel++)
            {
                int currentOffset = (sourceFrame * channels + channel) * sizeof(short);
                int nextOffset = (nextFrame * channels + channel) * sizeof(short);
                float current = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(currentOffset, sizeof(short))) / 32768f;
                float next = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(nextOffset, sizeof(short))) / 32768f;
                samples[outputFrame * channels + channel] = current + (next - current) * fraction;
            }
        }

        return samples;
    }

    internal static int GetOutputFrameCount(int sourceFrames, int channels, int sourceRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceFrames);
        if (channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        if (sourceRate < 8_000 || sourceRate > AudioClip.MixSampleRate)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceRate));
        }

        long outputFrames = checked(((long)sourceFrames * AudioClip.MixSampleRate + sourceRate / 2) / sourceRate);
        if (outputFrames > Array.MaxLength / channels)
        {
            throw new InvalidDataException("The resampled WAV is too large to fit in an audio clip.");
        }

        return (int)outputFrames;
    }
}
