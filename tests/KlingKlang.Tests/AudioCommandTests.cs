namespace KlingKlang.Tests;

[TestClass]
public sealed class AudioCommandTests
{
    [TestMethod]
    public void QueuedCommandsPreserveOrderAndProtectReusedVoices()
    {
        var device = new ManualDevice();
        using var engine = AudioEngine.CreateForTesting(device, maxVoices: 1, commandCapacity: 16);
        using var clip = WaveTestData.Clip([16384]);
        VoiceHandle first = engine.Play(clip, loop: true);
        Assert.IsTrue(engine.SetVolume(first, .5f));
        Assert.IsTrue(engine.Stop(first));
        VoiceHandle second = engine.Play(clip, loop: true);
        Assert.IsTrue(engine.SetVolume(second, .25f));
        Assert.IsFalse(engine.Stop(first));

        float[] output = new float[2];
        device.Pump(output);
        CollectionAssert.AreEqual(new[] { .125f, .125f }, output);
        Assert.IsTrue(engine.IsPlaying(second));

        engine.StopAll();
        VoiceHandle third = engine.Play(clip, loop: true);
        device.Pump(output);
        CollectionAssert.AreEqual(new[] { .5f, .5f }, output);
        Assert.IsFalse(engine.IsPlaying(second));
        Assert.IsTrue(engine.IsPlaying(third));
    }

    [TestMethod]
    public void FullQueueRejectsCommandsWithoutChangingVoiceState()
    {
        var device = new ManualDevice();
        using var engine = AudioEngine.CreateForTesting(device, maxVoices: 1, commandCapacity: 2);
        using var clip = WaveTestData.Clip([16384]);
        VoiceHandle voice = engine.Play(clip, loop: true);
        Assert.IsTrue(engine.SetVolume(voice, .5f));
        Assert.ThrowsExactly<InvalidOperationException>(() => engine.Stop(voice));
        Assert.ThrowsExactly<InvalidOperationException>(() => engine.StopAll());
        Assert.IsTrue(engine.IsPlaying(voice));
        float[] output = new float[2];
        device.Pump(output);
        CollectionAssert.AreEqual(new[] { .25f, .25f }, output);
        Assert.IsTrue(engine.Stop(voice));
        device.Pump(output);
        CollectionAssert.AreEqual(new float[2], output);
    }

    [TestMethod]
    public void RejectedPlayReleasesItsReservedSlot()
    {
        var device = new ManualDevice();
        using var engine = AudioEngine.CreateForTesting(device, maxVoices: 1, commandCapacity: 1);
        using var clip = WaveTestData.Clip([16384]);
        engine.StopAll();
        Assert.ThrowsExactly<InvalidOperationException>(() => engine.Play(clip));
        device.Pump(Span<float>.Empty);
        VoiceHandle voice = engine.Play(clip);
        Assert.IsTrue(engine.IsPlaying(voice));
    }

    [TestMethod]
    public void PendingVoiceRetainsClipAfterDisposal()
    {
        var device = new ManualDevice();
        using var engine = AudioEngine.CreateForTesting(device);
        var clip = WaveTestData.Clip([16384]);
        VoiceHandle voice = engine.Play(clip);
        clip.Dispose();
        float[] output = new float[2];
        device.Pump(output);
        CollectionAssert.AreEqual(new[] { .5f, .5f }, output);
        Assert.IsFalse(engine.IsPlaying(voice));
        Assert.ThrowsExactly<ObjectDisposedException>(() => engine.Play(clip));
    }

    [TestMethod]
    public void RenderDoesNotWaitForProducerGate()
    {
        var device = new ManualDevice();
        using var engine = AudioEngine.CreateForTesting(device);
        object gate = typeof(AudioEngine).GetField("_producerGate",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(engine)!;
        Task render;
        lock (gate)
        {
            render = Task.Run(() => device.Pump(new float[2]));
            Assert.IsTrue(render.Wait(TimeSpan.FromSeconds(2)), "The render path waited for a gameplay lock.");
        }

        render.GetAwaiter().GetResult();
    }

    [TestMethod]
    public void ConcurrentProducerAndConsumerPreserveLoopingVoice()
    {
        var device = new ManualDevice();
        using var engine = AudioEngine.CreateForTesting(device, commandCapacity: 8);
        using var clip = WaveTestData.Clip([16384]);
        VoiceHandle voice = engine.Play(clip, loop: true);
        Task producer = Task.Run(() =>
        {
            for (int i = 0; i < 5000;)
            {
                try
                {
                    if (!engine.SetVolume(voice, (i & 1) == 0 ? 1f : .5f))
                    {
                        throw new InvalidOperationException("The looping voice became inactive.");
                    }

                    i++;
                }
                catch (InvalidOperationException exception) when (exception.Message == "The audio command queue is full.")
                {
                    Thread.Yield();
                }
            }
        });

        float[] output = new float[2];
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!producer.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            device.Pump(output);
        }

        Assert.IsTrue(producer.IsCompleted, "The concurrent producer did not finish.");
        producer.GetAwaiter().GetResult();
        device.Pump(output);
        CollectionAssert.AreEqual(new[] { .25f, .25f }, output);
        Assert.IsTrue(engine.IsPlaying(voice));
    }

    [TestMethod]
    public void DeviceFailureIsReportedByOperationsAndDisposal()
    {
        var device = new ManualDevice();
        var engine = AudioEngine.CreateForTesting(device);
        var failure = new IOException("device unavailable");
        device.SimulatedFailure = failure;
        InvalidOperationException operation = Assert.ThrowsExactly<InvalidOperationException>(() => engine.IsPlaying(default));
        Assert.AreSame(failure, operation.InnerException);
        InvalidOperationException disposal = Assert.ThrowsExactly<InvalidOperationException>(engine.Dispose);
        Assert.AreSame(failure, disposal.InnerException);
        Assert.IsTrue(device.Disposed);
        engine.Dispose();
    }

    private sealed class ManualDevice : IAudioDevice
    {
        private AudioRenderCallback? _render;
        public Exception? SimulatedFailure { get; set; }
        public Exception? Failure => SimulatedFailure;
        public bool Disposed { get; private set; }
        public void Start(AudioRenderCallback render) => _render = render;
        public void Stop() => _render = null;
        public void Dispose()
        {
            Stop();
            Disposed = true;
        }
        public void Pump(Span<float> output) => _render!(output);
    }
}
