namespace KlingKlang.Tests;

[TestClass]
public sealed class Pcm16ResamplerTests
{
    [TestMethod]
    public void InterpolatesMonoAndWrapsTheEndForOneShotPlayback()
    {
        using var clip = WaveTestData.Clip([0, 16384], sampleRate: 32_000);
        using var engine = AudioEngine.CreateOffline();
        VoiceHandle voice = engine.Play(clip);
        float[] output = new float[8];
        engine.Render(output);

        Assert.AreEqual(3, clip.FrameCount);
        Assert.AreEqual(0f, output[0]);
        Assert.AreEqual(1f / 3f, output[2], 0.000001f);
        Assert.AreEqual(1f / 3f, output[4], 0.000001f);
        Assert.AreEqual(0f, output[6]);
        Assert.AreEqual(output[0], output[1]);
        Assert.AreEqual(output[2], output[3]);
        Assert.AreEqual(output[4], output[5]);
        Assert.IsFalse(engine.IsPlaying(voice));
    }

    [TestMethod]
    public void InterpolatesEachStereoChannelIndependentlyAtAllSelectedRates()
    {
        short[] source = [0, -16384, 16384, 8192, -8192, 24576];
        foreach (int rate in new[] { 8_000, 8_001, 11_025, 12_345, 16_000, 22_050, 32_000, 44_100, 47_999, 48_000 })
        {
            using var clip = WaveTestData.Clip(source, channels: 2, sampleRate: rate);
            using var engine = AudioEngine.CreateOffline();
            engine.Play(clip);
            float[] output = new float[clip.FrameCount * 2];
            engine.Render(output);

            for (int frame = 0; frame < clip.FrameCount; frame++)
            {
                double position = frame * (double)rate / 48_000;
                int current = (int)Math.Floor(position);
                int next = (current + 1) % 3;
                double fraction = position - current;
                for (int channel = 0; channel < 2; channel++)
                {
                    double first = source[current * 2 + channel] / 32768.0;
                    double second = source[next * 2 + channel] / 32768.0;
                    float expected = (float)(first + (second - first) * fraction);
                    Assert.AreEqual(expected, output[frame * 2 + channel], 0.000001f,
                        $"Input rate {rate}, frame {frame}, channel {channel}");
                }
            }
        }
    }

    [TestMethod]
    public void RoundsFrameCountsToNearestWithHalfTiesUpAndWrapsSingleFrame()
    {
        Assert.AreEqual(2, Pcm16Resampler.GetOutputFrameCount(1, 1, 32_000));
        Assert.AreEqual(5, Pcm16Resampler.GetOutputFrameCount(3, 2, 32_000));
        Assert.AreEqual(1, Pcm16Resampler.GetOutputFrameCount(1, 1, 44_100));
        Assert.AreEqual(4, Pcm16Resampler.GetOutputFrameCount(4, 1, 44_100));

        using var roundedUp = WaveTestData.Clip([1, 2, 3], sampleRate: 32_000);
        using var roundedDown = WaveTestData.Clip([1, 2, 3, 4], sampleRate: 44_100);
        Assert.AreEqual(5, roundedUp.FrameCount);
        Assert.AreEqual(4, roundedDown.FrameCount);

        using var clip = WaveTestData.Clip([16384], sampleRate: 8_000);
        using var engine = AudioEngine.CreateOffline();
        engine.Play(clip);
        float[] output = new float[12];
        engine.Render(output);
        Assert.AreEqual(6, clip.FrameCount);
        CollectionAssert.AreEqual(Enumerable.Repeat(.5f, 12).ToArray(), output);
    }

    [TestMethod]
    public void RejectsOversizedConvertedArraysBeforeAllocating()
    {
        int maxStereoFrames = Array.MaxLength / 2;
        Assert.AreEqual(maxStereoFrames, Pcm16Resampler.GetOutputFrameCount(maxStereoFrames, 2, 48_000));
        InvalidDataException stereo = Assert.ThrowsExactly<InvalidDataException>(
            () => Pcm16Resampler.GetOutputFrameCount(maxStereoFrames + 1, 2, 48_000));
        StringAssert.Contains(stereo.Message, "too large");
        Assert.ThrowsExactly<InvalidDataException>(
            () => Pcm16Resampler.GetOutputFrameCount(Array.MaxLength / 6 + 1, 1, 8_000));
        Assert.ThrowsExactly<InvalidDataException>(
            () => Pcm16Resampler.GetOutputFrameCount(int.MaxValue, 2, 8_000));
    }

    [TestMethod]
    public void PreservesExactPcmValuesAt48Khz()
    {
        short[] source = [short.MinValue, short.MaxValue, -1, 1, 0, 12345];
        using var clip = WaveTestData.Clip(source, channels: 2);
        using var engine = AudioEngine.CreateOffline();
        engine.Play(clip);
        float[] output = new float[source.Length];
        engine.Render(output);

        Assert.AreEqual(3, clip.FrameCount);
        CollectionAssert.AreEqual(source.Select(sample => sample / 32768f).ToArray(), output);
    }
}
