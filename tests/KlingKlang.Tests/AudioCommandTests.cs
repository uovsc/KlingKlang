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

    [TestMethod]
    public void MultipleProducersCanPlayChangeAndStopWhileConsumerRenders()
    {
        var device = new ManualDevice();
        using var engine = AudioEngine.CreateForTesting(device, maxVoices: 64, commandCapacity: 512);
        using var clip = WaveTestData.Clip([16384]);
        using var barrier = new Barrier(9);
        var handles = new VoiceHandle[64];
        var tasks = new Task[8];
        for (int producer = 0; producer < tasks.Length; producer++)
        {
            int current = producer;
            tasks[producer] = Task.Factory.StartNew(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < 8; i++)
                {
                    VoiceHandle voice = engine.Play(clip, loop: true);
                    handles[current * 8 + i] = voice;
                    if (!engine.SetVolume(voice, .5f)) { throw new InvalidOperationException("Voice unexpectedly inactive."); }
                    if ((i & 1) == 0 && !engine.Stop(voice)) { throw new InvalidOperationException("Voice unexpectedly inactive."); }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        barrier.SignalAndWait();
        Task all = Task.WhenAll(tasks);
        var output = new float[2];
        Assert.IsTrue(SpinWait.SpinUntil(() =>
        {
            device.Pump(output);
            return all.IsCompleted;
        }, TimeSpan.FromSeconds(10)));
        all.GetAwaiter().GetResult();
        device.Pump(output);
        CollectionAssert.AreEqual(new[] { 8f, 8f }, output);
        for (int i = 0; i < handles.Length; i++)
        {
            Assert.AreEqual((i & 1) != 0, engine.IsPlaying(handles[i]));
            if ((i & 1) == 0) { Assert.IsFalse(engine.Stop(handles[i])); }
        }

        engine.StopAll();
        device.Pump(output);
        CollectionAssert.AreEqual(new float[2], output);
    }

    [TestMethod]
    public void QueueWraparoundPreservesStoppedAndReusedHandles()
    {
        var device = new ManualDevice();
        using var engine = AudioEngine.CreateForTesting(device, maxVoices: 1, commandCapacity: 2);
        using var clip = WaveTestData.Clip([16384]);
        var output = new float[2];
        VoiceHandle previous = default;
        for (int i = 0; i < 1000; i++)
        {
            VoiceHandle voice = engine.Play(clip, loop: true);
            Assert.IsFalse(engine.Stop(previous));
            Assert.IsTrue(engine.Stop(voice));
            Assert.ThrowsExactly<InvalidOperationException>(() => engine.Play(clip));
            device.Pump(output);
            CollectionAssert.AreEqual(new float[2], output);
            previous = voice;
        }

        VoiceHandle final = engine.Play(clip, loop: true);
        Assert.IsFalse(engine.Stop(previous));
        device.Pump(output);
        CollectionAssert.AreEqual(new[] { .5f, .5f }, output);
        Assert.IsTrue(engine.IsPlaying(final));
    }

    [TestMethod]
    public void ConcurrentStopAllAndPlayRespectAcceptedCommandOrder()
    {
        var device = new ManualDevice();
        using var engine = AudioEngine.CreateForTesting(device, maxVoices: 2);
        using var clip = WaveTestData.Clip([16384]);
        VoiceHandle initial = engine.Play(clip, loop: true);
        using var barrier = new Barrier(3);
        VoiceHandle concurrent = default;
        Task play = Task.Factory.StartNew(() =>
        {
            barrier.SignalAndWait();
            concurrent = engine.Play(clip, loop: true);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task stopAll = Task.Factory.StartNew(() =>
        {
            barrier.SignalAndWait();
            engine.StopAll();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        barrier.SignalAndWait();
        Assert.IsTrue(Task.WhenAll(play, stopAll).Wait(TimeSpan.FromSeconds(3)));
        float[] output = new float[2];
        device.Pump(output);
        Assert.IsFalse(engine.IsPlaying(initial));
        float expected = engine.IsPlaying(concurrent) ? .5f : 0f;
        CollectionAssert.AreEqual(new[] { expected, expected }, output);
    }

    [TestMethod]
    public void ConcurrentDisposeCallsBothWaitForDeviceShutdown()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var device = new BlockingDisposeDevice(entered, release);
        var engine = AudioEngine.CreateForTesting(device);
        Task first = Task.Run(engine.Dispose);
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(3)));
        using var secondStarted = new ManualResetEventSlim();
        Task second = Task.Run(() => { secondStarted.Set(); engine.Dispose(); });
        Assert.IsTrue(secondStarted.Wait(TimeSpan.FromSeconds(3)));
        try
        {
            Assert.IsFalse(second.Wait(TimeSpan.FromMilliseconds(50)));
        }
        finally
        {
            release.Set();
        }

        Assert.IsTrue(Task.WhenAll(first, second).Wait(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(1, device.DisposeCount);
        Assert.AreEqual(AudioConnectionState.Disposed, engine.ConnectionState);
    }

    private sealed class BlockingDisposeDevice(ManualResetEventSlim entered, ManualResetEventSlim release) : IAudioDevice
    {
        public AudioDeviceStatus Status => AudioDeviceStatus.Ready;
        public int DisposeCount { get; private set; }
        public void Start(AudioRenderCallback render) { }
        public void Stop() { }
        public void Dispose()
        {
            DisposeCount++;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(3))) { throw new TimeoutException("Test did not release disposal."); }
        }
    }

    private sealed class ManualDevice : IAudioDevice
    {
        private AudioRenderCallback? _render;
        public Exception? SimulatedFailure { get; set; }
        public AudioDeviceStatus Status => SimulatedFailure is null
            ? AudioDeviceStatus.Ready
            : new AudioDeviceStatus(AudioConnectionState.Faulted, SimulatedFailure);
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
