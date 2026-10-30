namespace KlingKlang.Tests;

[TestClass]
public sealed class AudioRecoveryTests
{
    [TestMethod]
    public void RetriesOnceAndResumesAcceptedCommandsAndVoicePosition()
    {
        using var waiting = new ManualResetEventSlim();
        using var resumeRetry = new ManualResetEventSlim();
        var oldStream = new ControlledStream();
        var newStream = new ControlledStream();
        int opens = 0;
        int retryDelay = 0;
        var device = new ThreadedAudioDevice(() =>
        {
            return Interlocked.Increment(ref opens) switch
            {
                1 => oldStream,
                2 => throw new AudioDeviceLostException("No default device."),
                3 => newStream,
                _ => throw new InvalidOperationException("Unexpected open.")
            };
        }, (stop, milliseconds) =>
        {
            retryDelay = milliseconds;
            waiting.Set();
            return WaitHandle.WaitAny([stop.WaitHandle, resumeRetry.WaitHandle]) == 0;
        });

        using var engine = AudioEngine.CreateForTesting(device);
        using var clip = WaveTestData.Clip([16384, -16384]);
        Assert.AreEqual(AudioConnectionState.Ready, engine.ConnectionState);
        VoiceHandle voice = engine.Play(clip, loop: true);
        oldStream.Request(1);
        Assert.IsTrue(oldStream.WaitForWrites(2));
        CollectionAssert.AreEqual(new[] { .5f, .5f }, oldStream.GetWrite(1));
        Assert.IsTrue(engine.SetVolume(voice, .5f));
        oldStream.LoseOnNextRead();
        Assert.IsTrue(waiting.Wait(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(500, retryDelay);
        Assert.AreEqual(AudioConnectionState.Recovering, engine.ConnectionState);
        Assert.AreEqual(1, oldStream.DisposeCount);
        Assert.ThrowsExactly<InvalidOperationException>(() => engine.IsPlaying(voice));
        Assert.ThrowsExactly<InvalidOperationException>(() => engine.Play(clip));

        resumeRetry.Set();
        Assert.IsTrue(SpinWait.SpinUntil(() => engine.ConnectionState == AudioConnectionState.Ready, TimeSpan.FromSeconds(3)));
        Assert.AreEqual(3, Volatile.Read(ref opens));
        CollectionAssert.AreEqual(new float[4], newStream.GetWrite(0));
        Assert.IsTrue(engine.IsPlaying(voice));
        newStream.Request(1);
        Assert.IsTrue(newStream.WaitForWrites(2));
        CollectionAssert.AreEqual(new[] { -.25f, -.25f }, newStream.GetWrite(1));
    }

    [TestMethod]
    public void TwoFailedAttemptsReportTerminalErrorAndReleaseDevice()
    {
        var oldStream = new ControlledStream();
        int opens = 0;
        int waits = 0;
        var device = new ThreadedAudioDevice(() =>
        {
            if (Interlocked.Increment(ref opens) == 1) { return oldStream; }
            throw new AudioDeviceLostException("No replacement device.");
        }, (_, milliseconds) =>
        {
            Assert.AreEqual(500, milliseconds);
            Interlocked.Increment(ref waits);
            return false;
        });
        var engine = AudioEngine.CreateForTesting(device);
        oldStream.LoseOnNextRead();
        Assert.IsTrue(SpinWait.SpinUntil(() => engine.ConnectionState == AudioConnectionState.Faulted, TimeSpan.FromSeconds(3)));
        Assert.AreEqual(3, Volatile.Read(ref opens));
        Assert.AreEqual(1, Volatile.Read(ref waits));
        Assert.AreEqual(1, oldStream.DisposeCount);
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() => engine.IsPlaying(default));
        var failures = error.InnerException as AggregateException;
        Assert.IsNotNull(failures);
        Assert.HasCount(3, failures.InnerExceptions);
        Assert.ThrowsExactly<InvalidOperationException>(engine.Dispose);
        Assert.AreEqual(AudioConnectionState.Disposed, engine.ConnectionState);
        engine.Dispose();
    }

    [TestMethod]
    public void DisposalInterruptsReconnectDelayAndReleasesResources()
    {
        using var waiting = new ManualResetEventSlim();
        var oldStream = new ControlledStream();
        int opens = 0;
        var device = new ThreadedAudioDevice(() =>
        {
            if (Interlocked.Increment(ref opens) == 1) { return oldStream; }
            throw new AudioDeviceLostException("No replacement device.");
        }, (stop, milliseconds) =>
        {
            Assert.AreEqual(500, milliseconds);
            waiting.Set();
            return stop.Wait(TimeSpan.FromSeconds(3));
        });
        var engine = AudioEngine.CreateForTesting(device);
        oldStream.LoseOnNextRead();
        Assert.IsTrue(waiting.Wait(TimeSpan.FromSeconds(3)));
        Task<InvalidOperationException> dispose = Task.Run(() => Assert.ThrowsExactly<InvalidOperationException>(engine.Dispose));
        Assert.IsTrue(dispose.Wait(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(AudioConnectionState.Disposed, engine.ConnectionState);
        Assert.AreEqual(2, Volatile.Read(ref opens));
        Assert.AreEqual(1, oldStream.DisposeCount);
    }

    [TestMethod]
    public void DisposalDuringNativeOpenClosesTheNewStreamBeforeReturning()
    {
        using var opening = new ManualResetEventSlim();
        using var finishOpen = new ManualResetEventSlim();
        var oldStream = new ControlledStream();
        var newStream = new ControlledStream();
        int opens = 0;
        var engine = AudioEngine.CreateForTesting(new ThreadedAudioDevice(() =>
        {
            if (Interlocked.Increment(ref opens) == 1) { return oldStream; }
            opening.Set();
            if (!finishOpen.Wait(TimeSpan.FromSeconds(3))) { throw new TimeoutException("Test did not finish opening."); }
            return newStream;
        }));
        oldStream.LoseOnNextRead();
        Assert.IsTrue(opening.Wait(TimeSpan.FromSeconds(3)));
        Task dispose = Task.Run(engine.Dispose);
        try
        {
            Assert.IsTrue(SpinWait.SpinUntil(() => engine.ConnectionState == AudioConnectionState.Disposed, TimeSpan.FromSeconds(3)));
            Assert.IsFalse(dispose.IsCompleted);
        }
        finally
        {
            finishOpen.Set();
        }

        Assert.IsTrue(dispose.Wait(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(1, oldStream.DisposeCount);
        Assert.AreEqual(1, newStream.DisposeCount);
    }

    [TestMethod]
    public void UnrelatedFailuresDoNotTriggerReconnection()
    {
        var oldStream = new ControlledStream();
        int opens = 0;
        var engine = AudioEngine.CreateForTesting(new ThreadedAudioDevice(() =>
        {
            Interlocked.Increment(ref opens);
            return oldStream;
        }));
        oldStream.FailOnNextRead();
        Assert.IsTrue(SpinWait.SpinUntil(() => engine.ConnectionState == AudioConnectionState.Faulted, TimeSpan.FromSeconds(3)));
        Assert.AreEqual(1, Volatile.Read(ref opens));
        Assert.ThrowsExactly<InvalidOperationException>(engine.Dispose);
        Assert.AreEqual(1, oldStream.DisposeCount);
    }

    [TestMethod]
    public void ASecondLossReceivesANewRetryBudget()
    {
        var streams = new[] { new ControlledStream(), new ControlledStream(), new ControlledStream() };
        int opens = 0;
        int waits = 0;
        var engine = AudioEngine.CreateForTesting(new ThreadedAudioDevice(() =>
            Interlocked.Increment(ref opens) switch
            {
                1 => streams[0],
                2 or 4 => throw new AudioDeviceLostException("Simulated unavailable device."),
                3 => streams[1],
                5 => streams[2],
                _ => throw new InvalidOperationException("Unexpected open.")
            }, (_, milliseconds) =>
            {
                Assert.AreEqual(500, milliseconds);
                Interlocked.Increment(ref waits);
                return false;
            }));
        try
        {
            streams[0].LoseOnNextRead();
            Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref opens) >= 3 && engine.ConnectionState == AudioConnectionState.Ready, TimeSpan.FromSeconds(3)));
            streams[1].LoseOnNextRead();
            Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref opens) >= 5 && engine.ConnectionState == AudioConnectionState.Ready, TimeSpan.FromSeconds(3)));
        }
        finally
        {
            engine.Dispose();
        }

        Assert.AreEqual(5, Volatile.Read(ref opens));
        Assert.AreEqual(2, Volatile.Read(ref waits));
        foreach (ControlledStream stream in streams) { Assert.AreEqual(1, stream.DisposeCount); }
    }

    [TestMethod]
    public void NonrecoverableReplacementFailureKeepsTheOriginalLoss()
    {
        var oldStream = new ControlledStream();
        int opens = 0;
        var engine = AudioEngine.CreateForTesting(new ThreadedAudioDevice(() =>
        {
            if (Interlocked.Increment(ref opens) == 1) { return oldStream; }
            throw new InvalidOperationException("Invalid replacement format.");
        }));
        oldStream.LoseOnNextRead();
        Assert.IsTrue(SpinWait.SpinUntil(() => engine.ConnectionState == AudioConnectionState.Faulted, TimeSpan.FromSeconds(3)));
        Assert.AreEqual(2, Volatile.Read(ref opens));
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() => engine.IsPlaying(default));
        var failures = error.InnerException as AggregateException;
        Assert.IsNotNull(failures);
        Assert.HasCount(2, failures.InnerExceptions);
        Assert.IsInstanceOfType<AudioDeviceLostException>(failures.InnerExceptions[0]);
        Assert.AreEqual("Invalid replacement format.", failures.InnerExceptions[1].Message);
        Assert.ThrowsExactly<InvalidOperationException>(engine.Dispose);
    }

    [TestMethod]
    public void LossDuringWriteResumesAfterLastMixedFrame()
    {
        var oldStream = new ControlledStream();
        var newStream = new ControlledStream();
        int opens = 0;
        using var engine = AudioEngine.CreateForTesting(new ThreadedAudioDevice(() =>
            Interlocked.Increment(ref opens) == 1 ? oldStream : newStream));
        using var clip = WaveTestData.Clip([16384, -16384]);
        VoiceHandle voice = engine.Play(clip, loop: true);
        oldStream.LoseOnNextWrite();
        oldStream.Request(1);
        Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref opens) >= 2 && engine.ConnectionState == AudioConnectionState.Ready, TimeSpan.FromSeconds(3)));
        Assert.IsTrue(engine.IsPlaying(voice));
        CollectionAssert.AreEqual(new float[4], newStream.GetWrite(0));
        newStream.Request(1);
        Assert.IsTrue(newStream.WaitForWrites(2));
        CollectionAssert.AreEqual(new[] { -.5f, -.5f }, newStream.GetWrite(1));
    }

    [TestMethod]
    public void FailedReplacementStartupIsDisposedBeforeSecondAttempt()
    {
        var oldStream = new ControlledStream();
        var failedStream = new ControlledStream { LoseOnStart = true };
        var newStream = new ControlledStream();
        int opens = 0;
        int waited = 0;
        using var engine = AudioEngine.CreateForTesting(new ThreadedAudioDevice(() =>
            Interlocked.Increment(ref opens) switch
            {
                1 => oldStream,
                2 => failedStream,
                3 => newStream,
                _ => throw new InvalidOperationException("Unexpected open.")
            }, (_, milliseconds) => { waited = milliseconds; return false; }));
        oldStream.LoseOnNextRead();
        Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref opens) >= 3 && engine.ConnectionState == AudioConnectionState.Ready, TimeSpan.FromSeconds(3)));
        Assert.AreEqual(500, waited);
        Assert.AreEqual(1, oldStream.DisposeCount);
        Assert.AreEqual(1, failedStream.DisposeCount);
        Assert.AreEqual(0, newStream.DisposeCount);
    }

    [TestMethod]
    public void CleanupFailureStopsRecoveryAndPreservesBothErrors()
    {
        var oldStream = new ControlledStream { FailDispose = true };
        int opens = 0;
        var engine = AudioEngine.CreateForTesting(new ThreadedAudioDevice(() =>
        {
            Interlocked.Increment(ref opens);
            return oldStream;
        }));
        oldStream.LoseOnNextRead();
        Assert.IsTrue(SpinWait.SpinUntil(() => engine.ConnectionState == AudioConnectionState.Faulted, TimeSpan.FromSeconds(3)));
        Assert.AreEqual(1, Volatile.Read(ref opens));
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() => engine.IsPlaying(default));
        var failures = error.InnerException as AggregateException;
        Assert.IsNotNull(failures);
        Assert.HasCount(2, failures.InnerExceptions);
        Assert.IsInstanceOfType<AudioDeviceLostException>(failures.InnerExceptions[0]);
        Assert.IsInstanceOfType<IOException>(failures.InnerExceptions[1]);
        Assert.ThrowsExactly<InvalidOperationException>(engine.Dispose);
        Assert.AreEqual(1, oldStream.DisposeCount);
    }

    [TestMethod]
    public void OfflineConnectionStateReflectsDisposal()
    {
        var engine = AudioEngine.CreateOffline();
        Assert.AreEqual(AudioConnectionState.Ready, engine.ConnectionState);
        engine.Dispose();
        Assert.AreEqual(AudioConnectionState.Disposed, engine.ConnectionState);
    }

    private sealed class ControlledStream : IAudioOutputStream
    {
        private readonly object _gate = new();
        private readonly List<float[]> _writes = [];
        private int _available;
        private int _nextFailure;
        private int _nextWriteFailure;
        private int _disposeCount;
        private bool _started;
        public int BufferFrames => 2;
        public bool LoseOnStart { get; init; }
        public bool FailDispose { get; init; }
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public int GetAvailableFrames()
        {
            int failure = Interlocked.Exchange(ref _nextFailure, 0);
            if (failure == 1) { throw new AudioDeviceLostException("Simulated unplug."); }
            if (failure == 2) { throw new IOException("Unrelated simulated failure."); }
            return _started ? Interlocked.Exchange(ref _available, 0) : BufferFrames;
        }

        public void Write(float[] samples, int frames)
        {
            if (Interlocked.Exchange(ref _nextWriteFailure, 0) != 0)
            {
                throw new AudioDeviceLostException("Simulated unplug during write.");
            }

            lock (_gate)
            {
                _writes.Add(samples.AsSpan(0, frames * 2).ToArray());
            }
        }

        public void Start()
        {
            if (LoseOnStart) { throw new AudioDeviceLostException("Simulated startup device loss."); }
            _started = true;
        }
        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            if (FailDispose) { throw new IOException("Simulated cleanup failure."); }
        }
        public void Request(int frames) => Interlocked.Exchange(ref _available, frames);
        public void LoseOnNextRead() => Interlocked.Exchange(ref _nextFailure, 1);
        public void LoseOnNextWrite() => Interlocked.Exchange(ref _nextWriteFailure, 1);
        public void FailOnNextRead() => Interlocked.Exchange(ref _nextFailure, 2);
        public bool WaitForWrites(int count) =>
            SpinWait.SpinUntil(() => { lock (_gate) { return _writes.Count >= count; } }, TimeSpan.FromSeconds(3));
        public float[] GetWrite(int index) { lock (_gate) { return _writes[index]; } }
    }
}
