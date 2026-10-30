using System.Runtime.InteropServices;

namespace KlingKlang.Tests;

[TestClass]
public sealed class AlsaOutputStreamTests
{
    [TestMethod]
    public void ConvertsClipsAndRetainsPartialWritesWithoutLosingFrames()
    {
        var api = new FakeAlsa();
        api.Writes.Enqueue(2);
        api.Writes.Enqueue(-11);
        api.Writes.Enqueue(2);
        using var stream = new AlsaOutputStream(api);
        Assert.AreEqual(4, stream.BufferFrames);
        stream.Write([1.5f, -1.5f, .5f, -.5f, .25f, -.25f, 0f, 1f], 4);
        Assert.AreEqual(0, stream.GetAvailableFrames());
        Assert.AreEqual(4, stream.GetAvailableFrames());
        CollectionAssert.AreEqual(new byte[]
        {
            0xff, 0x7f, 0x00, 0x80, 0x00, 0x40, 0x00, 0xc0,
            0x00, 0x20, 0x00, 0xe0, 0x00, 0x00, 0xff, 0x7f
        }, api.Accepted.ToArray());
        CollectionAssert.AreEqual(new nuint[] { 4, 2, 2 }, api.Requested.ToArray());
    }

    [TestMethod]
    public void RecoversUnderrunWithoutDiscardingUnwrittenFrames()
    {
        var api = new FakeAlsa();
        api.Writes.Enqueue(-32);
        api.Writes.Enqueue(1);
        api.Availability.Enqueue(-32);
        api.Availability.Enqueue(4);
        using var stream = new AlsaOutputStream(api);
        stream.Write([.5f, -.5f], 1);
        Assert.AreEqual(1, api.Prepares);
        Assert.AreEqual(0, stream.GetAvailableFrames());
        Assert.AreEqual(2, api.Prepares);
        Assert.AreEqual(4, stream.GetAvailableFrames());
        CollectionAssert.AreEqual(new byte[] { 0x00, 0x40, 0x00, 0xc0 }, api.Accepted.ToArray());
    }

    [TestMethod]
    public void RetriesSuspensionWithoutBlockingOrDroppingPendingData()
    {
        var api = new FakeAlsa();
        api.Writes.Enqueue(-86);
        api.Writes.Enqueue(1);
        api.Resumes.Enqueue(-11);
        api.Resumes.Enqueue(-11);
        api.Resumes.Enqueue(0);
        using var stream = new AlsaOutputStream(api);
        stream.Write([.5f, -.5f], 1);
        Assert.AreEqual(0, stream.GetAvailableFrames());
        Assert.AreEqual(4, stream.GetAvailableFrames());
        Assert.AreEqual(3, api.ResumeCalls);
        Assert.AreEqual(0, api.Prepares);
        CollectionAssert.AreEqual(new byte[] { 0x00, 0x40, 0x00, 0xc0 }, api.Accepted.ToArray());
    }

    [TestMethod]
    public void FallsBackToPrepareWhenResumeIsUnsupported()
    {
        var api = new FakeAlsa();
        api.Availability.Enqueue(-86);
        api.Resumes.Enqueue(-38);
        using var stream = new AlsaOutputStream(api);
        Assert.AreEqual(0, stream.GetAvailableFrames());
        Assert.AreEqual(1, api.Prepares);
        Assert.AreEqual(4, stream.GetAvailableFrames());
    }

    [TestMethod]
    public void PropagatesDeviceErrorsAndClosesAfterInitializationFailure()
    {
        var initApi = new FakeAlsa { SetParametersResult = -19 };
        AudioDeviceLostException startup = Assert.ThrowsExactly<AudioDeviceLostException>(() => new AlsaOutputStream(initApi));
        StringAssert.Contains(startup.Message, "ALSA -19");
        Assert.AreEqual(1, initApi.Closes);
        Assert.AreEqual(0, initApi.Drops);

        var api = new FakeAlsa();
        api.Availability.Enqueue(-19);
        var stream = new AlsaOutputStream(api);
        AudioDeviceLostException error = Assert.ThrowsExactly<AudioDeviceLostException>(() => stream.GetAvailableFrames());
        StringAssert.Contains(error.Message, "ALSA -19");
        stream.Dispose();
        stream.Dispose();
        Assert.AreEqual(1, api.Drops);
        Assert.AreEqual(1, api.Closes);
    }

    [TestMethod]
    public void ClassifiesPhysicalRemovalAndReleasesAnInvalidatedDevice()
    {
        var api = new FakeAlsa { DropResult = -19, CloseResult = -19 };
        api.Writes.Enqueue(-25);
        var stream = new AlsaOutputStream(api);
        AudioDeviceLostException loss = Assert.ThrowsExactly<AudioDeviceLostException>(() => stream.Write([.5f, -.5f], 1));
        StringAssert.Contains(loss.Message, "ALSA -25");
        stream.Dispose();
        Assert.AreEqual(1, api.Drops);
        Assert.AreEqual(1, api.Closes);
    }

    [TestMethod]
    public void RejectsInvalidBufferSizesAndImpossibleWriteCounts()
    {
        var invalid = new FakeAlsa { Frames = 0 };
        Assert.ThrowsExactly<InvalidOperationException>(() => new AlsaOutputStream(invalid));
        Assert.AreEqual(1, invalid.Closes);
        var api = new FakeAlsa();
        api.Writes.Enqueue(2);
        using var stream = new AlsaOutputStream(api);
        Assert.ThrowsExactly<InvalidOperationException>(() => stream.Write([0, 0], 1));
    }

    [TestMethod]
    public void ReportsADeviceThatStopsAcceptingAudio()
    {
        var api = new FakeAlsa { DefaultAvailability = 0 };
        using var stream = new AlsaOutputStream(api, TimeSpan.FromMilliseconds(1));
        stream.Start();
        Thread.Sleep(20);
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() => stream.GetAvailableFrames());
        StringAssert.Contains(error.Message, "stopped accepting");
    }

    [TestMethod]
    public void SteadyStateConversionAndWritesDoNotAllocate()
    {
        var api = new FakeAlsa { CaptureWrites = false };
        using var stream = new AlsaOutputStream(api);
        float[] samples = [.5f, -.5f, .25f, -.25f];
        for (int i = 0; i < 2000; i++)
        {
            stream.Write(samples, 2);
            stream.GetAvailableFrames();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++)
        {
            stream.Write(samples, 2);
            stream.GetAvailableFrames();
        }

        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [TestMethod]
    public void ClosesEvenWhenStoppingFails()
    {
        var api = new FakeAlsa { DropResult = -19, CloseResult = -5 };
        var stream = new AlsaOutputStream(api);
        AggregateException error = Assert.ThrowsExactly<AggregateException>(stream.Dispose);
        Assert.HasCount(2, error.InnerExceptions);
        Assert.AreEqual(1, api.Drops);
        Assert.AreEqual(1, api.Closes);
        stream.Dispose();

        var throwingApi = new FakeAlsa { ThrowOnDrop = true };
        var throwingStream = new AlsaOutputStream(throwingApi);
        Assert.ThrowsExactly<InvalidOperationException>(throwingStream.Dispose);
        Assert.AreEqual(1, throwingApi.Closes);
    }

    private sealed class FakeAlsa : IAlsaApi
    {
        public readonly Queue<nint> Writes = new();
        public readonly Queue<nint> Availability = new();
        public readonly Queue<int> Resumes = new();
        public readonly List<nuint> Requested = [];
        public readonly List<byte> Accepted = [];
        public nuint Frames { get; init; } = 4;
        public nint? DefaultAvailability { get; init; }
        public bool CaptureWrites { get; init; } = true;
        public int SetParametersResult { get; init; }
        public int DropResult { get; init; }
        public int CloseResult { get; init; }
        public bool ThrowOnDrop { get; init; }
        public int Prepares { get; private set; }
        public int ResumeCalls { get; private set; }
        public int Drops { get; private set; }
        public int Closes { get; private set; }
        public int Open(out IntPtr handle) { handle = (IntPtr)1; return 0; }
        public int SetParameters(IntPtr handle) => SetParametersResult;
        public int GetParameters(IntPtr handle, out nuint bufferFrames, out nuint periodFrames)
        {
            bufferFrames = Frames;
            periodFrames = 1;
            return 0;
        }
        public nint Available(IntPtr handle) => Availability.TryDequeue(out nint value) ? value : DefaultAvailability ?? (nint)Frames;
        public nint Write(IntPtr handle, IntPtr samples, nuint frames)
        {
            if (CaptureWrites)
            {
                Requested.Add(frames);
            }
            nint result = Writes.TryDequeue(out nint value) ? value : (nint)frames;
            if (CaptureWrites && result > 0 && (nuint)result <= frames)
            {
                var bytes = new byte[(int)result * 4];
                Marshal.Copy(samples, bytes, 0, bytes.Length);
                Accepted.AddRange(bytes);
            }

            return result;
        }
        public int Prepare(IntPtr handle) { Prepares++; return 0; }
        public int Resume(IntPtr handle) { ResumeCalls++; return Resumes.TryDequeue(out int value) ? value : 0; }
        public int Drop(IntPtr handle)
        {
            Drops++;
            if (ThrowOnDrop) { throw new InvalidOperationException("Simulated drop exception."); }
            return DropResult;
        }
        public int Close(IntPtr handle) { Closes++; return CloseResult; }
        public string ErrorString(int error) => "simulated failure";
    }
}
