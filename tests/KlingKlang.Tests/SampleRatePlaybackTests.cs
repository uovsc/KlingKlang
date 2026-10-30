namespace KlingKlang.Tests;

[TestClass]
public sealed class SampleRatePlaybackTests
{
    private static readonly int[] CommonRates = [8_000, 11_025, 16_000, 22_050, 32_000, 44_100, 48_000];

    [TestMethod]
    public void PreservesOneSecondDurationAndOneKilohertzPitchAtEveryCommonRate()
    {
        const double amplitude = .75;
        const int outputRate = 48_000;
        foreach (int rate in CommonRates)
        {
            foreach (short channels in new short[] { 1, 2 })
            {
                var source = new short[rate * channels];
                for (int frame = 0; frame < rate; frame++)
                {
                    short value = (short)Math.Round(amplitude * 32767 * Math.Sin(2 * Math.PI * 1000 * frame / rate));
                    for (int channel = 0; channel < channels; channel++)
                    {
                        source[frame * channels + channel] = channel == 0 ? value : (short)-value;
                    }
                }

                using var clip = WaveTestData.Clip(source, channels, rate);
                using var engine = AudioEngine.CreateOffline();
                VoiceHandle voice = engine.Play(clip);
                Assert.AreEqual(outputRate, clip.FrameCount, $"Input rate {rate}");
                var output = new float[outputRate * 2];
                engine.Render(output.AsSpan(0, output.Length - 2));
                Assert.IsTrue(engine.IsPlaying(voice), $"Input rate {rate}");
                engine.Render(output.AsSpan(output.Length - 2));
                Assert.IsFalse(engine.IsPlaying(voice), $"Input rate {rate}");

                int risingCrossings = 0;
                double maximumError = 0;
                for (int frame = 0; frame < outputRate; frame++)
                {
                    double expected = amplitude * 32767 / 32768 * Math.Sin(2 * Math.PI * 1000 * frame / outputRate);
                    maximumError = Math.Max(maximumError, Math.Abs(output[frame * 2] - expected));
                    float rightExpected = channels == 1 ? output[frame * 2] : -output[frame * 2];
                    Assert.AreEqual(rightExpected, output[frame * 2 + 1], 0.000001f,
                        $"Input rate {rate}, frame {frame}, channels {channels}");
                    if (frame > 0 && output[(frame - 1) * 2] <= 0 && output[frame * 2] > 0)
                    {
                        risingCrossings++;
                    }
                }

                Assert.IsLessThanOrEqualTo(1, Math.Abs(risingCrossings - 1000), $"Input rate {rate}, crossings {risingCrossings}");
                Assert.IsLessThan(.06, maximumError, $"Input rate {rate}, maximum interpolation error {maximumError}");
            }
        }
    }

    [TestMethod]
    public void LoopsConvertedClipsAcrossRenderBoundaries()
    {
        foreach (int rate in CommonRates)
        {
            foreach (short channels in new short[] { 1, 2 })
            {
                short[] source = channels == 1
                    ? [0, 16384, -8192]
                    : [0, -8192, 16384, 8192, -8192, 16384];
                using var clip = WaveTestData.Clip(source, channels, rate);
                using var engine = AudioEngine.CreateOffline();
                VoiceHandle voice = engine.Play(clip, loop: true);
                var output = new float[clip.FrameCount * 2 * 3];
                engine.Render(output.AsSpan(0, 2));
                engine.Render(output.AsSpan(2));
                Assert.IsTrue(engine.IsPlaying(voice));
                for (int frame = 0; frame < clip.FrameCount; frame++)
                {
                    for (int repeat = 1; repeat < 3; repeat++)
                    {
                        for (int channel = 0; channel < 2; channel++)
                        {
                            Assert.AreEqual(output[frame * 2 + channel], output[(repeat * clip.FrameCount + frame) * 2 + channel],
                                $"Input rate {rate}, source channels {channels}, frame {frame}, repeat {repeat}, output channel {channel}");
                        }
                    }
                }

                Assert.IsTrue(engine.Stop(voice));
                engine.Render(output);
                Assert.IsFalse(engine.IsPlaying(voice));
                Assert.IsTrue(output.All(sample => sample == 0f));
            }
        }
    }

    [TestMethod]
    public void PlaysMixedRatesSimultaneouslyAndCompletesIndependently()
    {
        using var lower = WaveTestData.Clip([0, 16384], sampleRate: 32_000);
        using var native = WaveTestData.Clip([8192, 8192, 8192, 8192]);
        using var engine = AudioEngine.CreateOffline();
        VoiceHandle lowerVoice = engine.Play(lower);
        VoiceHandle nativeVoice = engine.Play(native);
        var first = new float[6];
        engine.Render(first);
        Assert.AreEqual(.25f, first[0]);
        Assert.AreEqual(7f / 12f, first[2], 0.000001f);
        Assert.AreEqual(7f / 12f, first[4], 0.000001f);
        Assert.IsFalse(engine.IsPlaying(lowerVoice));
        Assert.IsTrue(engine.IsPlaying(nativeVoice));
        var last = new float[2];
        engine.Render(last);
        CollectionAssert.AreEqual(new[] { .25f, .25f }, last);
        Assert.IsFalse(engine.IsPlaying(nativeVoice));
    }

    [TestMethod]
    public void ConvertedClipDisposalDoesNotInterruptActiveVoice()
    {
        AudioClip clip = WaveTestData.Clip([0, 16384], sampleRate: 32_000);
        using var engine = AudioEngine.CreateOffline();
        VoiceHandle voice = engine.Play(clip);
        clip.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => engine.Play(clip));
        float[] output = new float[6];
        engine.Render(output);
        Assert.AreEqual(1f / 3f, output[4], 0.000001f);
        Assert.IsFalse(engine.IsPlaying(voice));
    }

    [TestMethod]
    public void SixtyFourConvertedVoicesRemainAllocationFreeOnRenderThread()
    {
        using var clip = WaveTestData.Clip([1000, -1000], sampleRate: 44_100);
        using var engine = AudioEngine.CreateOffline();
        for (int i = 0; i < 64; i++) { engine.Play(clip, .5f, loop: true); }
        float[] output = new float[256];
        for (int i = 0; i < 2000; i++) { engine.Render(output); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++) { engine.Render(output); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0L, allocated);
    }
}
