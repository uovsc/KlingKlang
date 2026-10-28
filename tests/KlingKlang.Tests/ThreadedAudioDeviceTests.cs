namespace KlingKlang.Tests;

[TestClass]
public sealed class ThreadedAudioDeviceTests
{
    [TestMethod]
    public void PrefillsAndClampsOnWorkerAndDisposesOnSameThread()
    {
        var stream = new TestStream();
        var device = new ThreadedAudioDevice(() => stream);
        device.Start(output =>
        {
            output[0] = -1.5f;
            output[1] = -.5f;
            output[2] = .5f;
            output[3] = 1.5f;
        });
        device.Dispose();
        device.Dispose();
        CollectionAssert.AreEqual(new[] { -1f, -.5f, .5f, 1f }, stream.FirstWrite);
        Assert.AreNotEqual(Environment.CurrentManagedThreadId, stream.StartThread);
        Assert.AreEqual(stream.StartThread, stream.WriteThread);
        Assert.AreEqual(stream.StartThread, stream.DisposeThread);
        Assert.IsTrue(stream.Disposed);
        Assert.IsNull(device.Failure);
    }

    [TestMethod]
    public void InitializationFailureClosesStreamAndReachesCaller()
    {
        var stream = new TestStream { FailStart = true };
        var device = new ThreadedAudioDevice(() => stream);
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(
            () => AudioEngine.CreateForTesting(device));
        Assert.IsInstanceOfType<IOException>(error.InnerException);
        Assert.IsTrue(stream.Disposed);
        Assert.AreEqual(stream.StartThread, stream.DisposeThread);
    }

    [TestMethod]
    public void WritesOnlyAvailableFramesWhenBufferIsPartlyFull()
    {
        var stream = new TestStream { FramesAfterStart = 1 };
        var device = new ThreadedAudioDevice(() => stream);
        device.Start(output => output.Fill(2f));
        Assert.IsTrue(SpinWait.SpinUntil(() => stream.WriteCount >= 2, TimeSpan.FromSeconds(3)));
        device.Dispose();
        Assert.AreEqual(1, stream.SecondWriteFrames);
        Assert.IsTrue(stream.Disposed);
        Assert.IsNull(device.Failure);
    }

    [TestMethod]
    public void InitializationAndCleanupFailuresAreBothPreserved()
    {
        var stream = new TestStream { FailStart = true, FailDispose = true };
        var device = new ThreadedAudioDevice(() => stream);
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(
            () => AudioEngine.CreateForTesting(device));
        var aggregate = error.InnerException as AggregateException;
        Assert.IsNotNull(aggregate);
        Assert.HasCount(2, aggregate.InnerExceptions);
        Assert.IsTrue(stream.Disposed);
    }

    [TestMethod]
    public void RuntimeFailureIsReportedAndStreamIsClosed()
    {
        var stream = new TestStream { FailAfterStart = true };
        var device = new ThreadedAudioDevice(() => stream);
        var engine = AudioEngine.CreateForTesting(device);
        Assert.IsTrue(SpinWait.SpinUntil(() => device.Failure is not null, TimeSpan.FromSeconds(3)));
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() => engine.IsPlaying(default));
        Assert.IsInstanceOfType<IOException>(error.InnerException);
        Assert.ThrowsExactly<InvalidOperationException>(engine.Dispose);
        Assert.IsTrue(stream.Disposed);
        Assert.AreEqual(stream.StartThread, stream.DisposeThread);
    }

    [TestMethod]
    public void RepeatedOpenAndCloseDoesNotLeaveWorkersRunning()
    {
        for (int i = 0; i < 20; i++)
        {
            var stream = new TestStream();
            using (var engine = AudioEngine.CreateForTesting(new ThreadedAudioDevice(() => stream)))
            {
                Assert.IsFalse(engine.IsPlaying(default));
            }

            Assert.IsTrue(stream.Disposed);
            Assert.AreEqual(stream.StartThread, stream.DisposeThread);
        }
    }

    private sealed class TestStream : IAudioOutputStream
    {
        private bool _started;
        private int _writeCount;
        public int BufferFrames => 2;
        public bool FailStart { get; init; }
        public bool FailAfterStart { get; init; }
        public bool FailDispose { get; init; }
        public int FramesAfterStart { get; init; } = 2;
        public bool Disposed { get; private set; }
        public int StartThread { get; private set; }
        public int WriteThread { get; private set; }
        public int DisposeThread { get; private set; }
        public float[]? FirstWrite { get; private set; }
        public int WriteCount => Volatile.Read(ref _writeCount);
        public int SecondWriteFrames { get; private set; }

        public int GetAvailableFrames()
        {
            if (_started && FailAfterStart)
            {
                throw new IOException("Simulated device failure.");
            }

            return _started ? FramesAfterStart : BufferFrames;
        }

        public void Write(float[] samples, int frames)
        {
            WriteThread = Environment.CurrentManagedThreadId;
            FirstWrite ??= samples.AsSpan(0, frames * 2).ToArray();
            if (_writeCount == 1)
            {
                SecondWriteFrames = frames;
            }

            Volatile.Write(ref _writeCount, _writeCount + 1);
        }

        public void Start()
        {
            StartThread = Environment.CurrentManagedThreadId;
            if (FailStart)
            {
                throw new IOException("Simulated initialization failure.");
            }

            _started = true;
        }

        public void Dispose()
        {
            DisposeThread = Environment.CurrentManagedThreadId;
            Disposed = true;
            if (FailDispose)
            {
                throw new IOException("Simulated cleanup failure.");
            }
        }
    }
}
