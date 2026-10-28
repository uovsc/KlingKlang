namespace KlingKlang.Tests;

[TestClass]
public sealed class AudioEngineTests
{
    [TestMethod]
    public void SumsOverlappingVoicesWithoutClippingAndClearsOutput()
    {
        using var clip = WaveTestData.Clip([16384, -32768]);
        using var engine = AudioEngine.CreateOffline();
        VoiceHandle first = engine.Play(clip);
        VoiceHandle second = engine.Play(clip, .5f);
        float[] output = [9, 9, 9, 9, 9, 9];
        engine.Render(output);
        CollectionAssert.AreEqual(new[] { .75f, .75f, -1.5f, -1.5f, 0f, 0f }, output);
        Assert.IsFalse(engine.IsPlaying(first));
        Assert.IsFalse(engine.IsPlaying(second));
    }

    [TestMethod]
    public void LoopsAcrossAndWithinRenderBlocksAndChangesVolume()
    {
        using var clip = WaveTestData.Clip([16384, -32768]);
        using var engine = AudioEngine.CreateOffline();
        VoiceHandle voice = engine.Play(clip, loop: true);
        float[] first = new float[6];
        engine.Render(first);
        CollectionAssert.AreEqual(new[] { .5f, .5f, -1f, -1f, .5f, .5f }, first);
        Assert.IsTrue(engine.SetVolume(voice, .5f));
        float[] second = new float[4];
        engine.Render(second);
        CollectionAssert.AreEqual(new[] { -.5f, -.5f, .25f, .25f }, second);
        Assert.IsTrue(engine.IsPlaying(voice));
        Assert.IsTrue(engine.Stop(voice));
        engine.Render(second);
        CollectionAssert.AreEqual(new float[4], second);
        Assert.IsFalse(engine.IsPlaying(voice));
        Assert.IsFalse(engine.Stop(voice));
        Assert.IsFalse(engine.SetVolume(voice, 1));
    }

    [TestMethod]
    public void RejectsExhaustionAndStaleOrForeignHandles()
    {
        using var clip = WaveTestData.Clip([1, 2]);
        using var engine = AudioEngine.CreateOffline(maxVoices: 1);
        using var other = AudioEngine.CreateOffline(maxVoices: 1);
        VoiceHandle old = engine.Play(clip);
        Assert.ThrowsExactly<InvalidOperationException>(() => engine.Play(clip));
        Assert.IsFalse(other.Stop(old));
        Assert.IsFalse(other.IsPlaying(old));
        Assert.IsFalse(engine.IsPlaying(default));
        Assert.IsTrue(engine.Stop(old));
        VoiceHandle current = engine.Play(clip);
        Assert.IsFalse(engine.Stop(old));
        Assert.IsFalse(engine.SetVolume(old, .2f));
        Assert.IsTrue(engine.IsPlaying(current));
        engine.StopAll();
        Assert.IsFalse(engine.IsPlaying(current));
        engine.Play(clip);
    }

    [TestMethod]
    public void DefaultPoolSupportsSixtyFourIndependentVoices()
    {
        using var clip = WaveTestData.Clip([16384]);
        using var engine = AudioEngine.CreateOffline();
        for (int i = 0; i < 64; i++)
        {
            engine.Play(clip, .5f);
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => engine.Play(clip));
        float[] output = new float[2];
        engine.Render(output);
        CollectionAssert.AreEqual(new[] { 16f, 16f }, output);
        engine.Play(clip);
    }

    [TestMethod]
    public void ExactBoundaryCompletesVoiceAndFreesSlot()
    {
        using var clip = WaveTestData.Clip([1]);
        using var engine = AudioEngine.CreateOffline(maxVoices: 1);
        VoiceHandle first = engine.Play(clip);
        engine.Render(Span<float>.Empty);
        Assert.IsTrue(engine.IsPlaying(first));
        engine.Render(new float[2]);
        Assert.IsFalse(engine.IsPlaying(first));
        VoiceHandle second = engine.Play(clip);
        Assert.IsTrue(engine.IsPlaying(second));
        Assert.IsFalse(engine.Stop(first));
    }

    [TestMethod]
    public void ClipDisposalKeepsActiveSamplesButRejectsNewPlay()
    {
        AudioClip clip = WaveTestData.Clip([16384, -32768]);
        using var engine = AudioEngine.CreateOffline();
        VoiceHandle voice = engine.Play(clip);
        clip.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => engine.Play(clip));
        float[] output = new float[4];
        engine.Render(output);
        CollectionAssert.AreEqual(new[] { .5f, .5f, -1f, -1f }, output);
        Assert.IsFalse(engine.IsPlaying(voice));
    }

    [TestMethod]
    public void ValidatesArgumentsWithoutAdvancingVoices()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.ThrowsExactly<NotSupportedException>(() => AudioEngine.Create());
        }
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => AudioEngine.Create(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => AudioEngine.CreateOffline(0));
        using var clip = WaveTestData.Clip([16384]);
        using var engine = AudioEngine.CreateOffline();
        Assert.ThrowsExactly<ArgumentNullException>(() => engine.Play(null!));
        foreach (float invalid in new[] { -1f, 1.01f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => engine.Play(clip, invalid));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => engine.SetVolume(default, invalid));
        }

        VoiceHandle voice = engine.Play(clip, 0);
        Assert.ThrowsExactly<ArgumentException>(() => engine.Render(new float[1]));
        Assert.IsTrue(engine.SetVolume(voice, 1));
        float[] output = new float[2];
        engine.Render(output);
        CollectionAssert.AreEqual(new[] { .5f, .5f }, output);
    }

    [TestMethod]
    public void DisposingEngineIsIdempotentAndRejectsFurtherOperations()
    {
        using var clip = WaveTestData.Clip([1]);
        AudioEngine engine = AudioEngine.CreateOffline();
        VoiceHandle voice = engine.Play(clip, loop: true);
        engine.Dispose();
        engine.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => engine.Play(clip));
        Assert.ThrowsExactly<ObjectDisposedException>(() => engine.Play(null!));
        Assert.ThrowsExactly<ObjectDisposedException>(() => engine.IsPlaying(voice));
        Assert.ThrowsExactly<ObjectDisposedException>(() => engine.Stop(voice));
        Assert.ThrowsExactly<ObjectDisposedException>(() => engine.StopAll());
        Assert.ThrowsExactly<ObjectDisposedException>(() => engine.SetVolume(voice, .5f));
        Assert.ThrowsExactly<ObjectDisposedException>(() => engine.SetVolume(voice, float.NaN));
        Assert.ThrowsExactly<ObjectDisposedException>(() => engine.Render(new float[2]));
        Assert.ThrowsExactly<ObjectDisposedException>(() => engine.LoadWav("unused.wav"));
    }

    [TestMethod]
    public void RenderAllocatesNoManagedBytesAfterWarmup()
    {
        using var clip = WaveTestData.Clip([1000, -1000]);
        using var engine = AudioEngine.CreateOffline();
        engine.Play(clip, loop: true);
        float[] output = new float[256];
        for (int i = 0; i < 2_000; i++)
        {
            engine.Render(output);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2_000; i++)
        {
            engine.Render(output);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0L, allocated);
    }
}
