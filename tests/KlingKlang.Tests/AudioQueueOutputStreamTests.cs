using System.Runtime.InteropServices;

namespace KlingKlang.Tests;

[TestClass]
public sealed class AudioQueueOutputStreamTests
{
    [TestMethod]
    public void PrefillsBuffersAndReusesOnlyReturnedBuffers()
    {
        var api = new FakeAudioQueue();
        using var stream = new AudioQueueOutputStream(api);
        Assert.AreEqual(2880, stream.BufferFrames);
        Assert.AreEqual(2880, stream.GetAvailableFrames());
        Assert.AreEqual(48_000d, api.Format.SampleRate);
        Assert.AreEqual(0x6c70636du, api.Format.FormatId);
        Assert.AreEqual(9u, api.Format.FormatFlags);
        Assert.AreEqual(2u, api.Format.ChannelsPerFrame);
        Assert.AreEqual(8u, api.Format.BytesPerFrame);
        Assert.AreEqual(32u, api.Format.BitsPerChannel);

        float[] samples = new float[stream.BufferFrames * 2];
        samples[0] = .25f;
        samples[960 * 2] = .5f;
        samples[1920 * 2] = .75f;
        stream.Write(samples, stream.BufferFrames);
        stream.Start();
        Assert.AreEqual(0, stream.GetAvailableFrames());
        Assert.HasCount(3, api.Enqueued);
        Assert.AreEqual(.25f, api.ReadFirstSample(0));
        Assert.AreEqual(.5f, api.ReadFirstSample(1));
        Assert.AreEqual(.75f, api.ReadFirstSample(2));
        Assert.AreEqual(960 * 2 * sizeof(float), api.ReadDataSize(0));

        Task.Run(() => api.Complete(1)).GetAwaiter().GetResult();
        Assert.AreEqual(960, stream.GetAvailableFrames());
        var replacement = new float[960 * 2];
        replacement[0] = -.5f;
        stream.Write(replacement, 960);
        Assert.AreEqual(0, stream.GetAvailableFrames());
        Assert.AreEqual(-.5f, api.ReadFirstSample(1));
        Assert.AreEqual(api.Buffers[1], api.Enqueued[3]);
    }

    [TestMethod]
    public void ReportsUnknownOrDuplicateCallbackBuffers()
    {
        var api = new FakeAudioQueue();
        using var stream = new AudioQueueOutputStream(api);
        stream.Write(new float[stream.BufferFrames * 2], stream.BufferFrames);
        stream.Start();
        api.Complete(0);
        api.Complete(0);
        Assert.ThrowsExactly<InvalidOperationException>(() => stream.GetAvailableFrames());
    }

    [TestMethod]
    public void ReportsAQueueThatStopsReturningBuffers()
    {
        var api = new FakeAudioQueue();
        using var stream = new AudioQueueOutputStream(api, TimeSpan.FromMilliseconds(1));
        stream.Write(new float[stream.BufferFrames * 2], stream.BufferFrames);
        stream.Start();
        Thread.Sleep(20);
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() => stream.GetAvailableFrames());
        StringAssert.Contains(error.Message, "stopped returning");
    }

    [TestMethod]
    public void CleansUpAllocatedBuffersWhenInitializationFails()
    {
        var api = new FakeAudioQueue { FailAllocationAt = 2 };
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() => new AudioQueueOutputStream(api));
        StringAssert.Contains(error.Message, "OSStatus -50");
        Assert.AreEqual(1, api.DisposeCalls);
        Assert.AreEqual(0, api.StopCalls);
        Assert.AreEqual(0, api.LiveAllocations);
    }

    [TestMethod]
    public void ReportsStopFailureButStillDisposesQueue()
    {
        var api = new FakeAudioQueue { StopResult = -50 };
        var stream = new AudioQueueOutputStream(api);
        stream.Write(new float[stream.BufferFrames * 2], stream.BufferFrames);
        stream.Start();
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(stream.Dispose);
        StringAssert.Contains(error.Message, "OSStatus -50");
        Assert.AreEqual(1, api.StopCalls);
        Assert.AreEqual(1, api.DisposeCalls);
        Assert.AreEqual(0, api.LiveAllocations);
        stream.Dispose();
    }

    [TestMethod]
    public void RejectsInvalidAndUnavailableBufferWrites()
    {
        var api = new FakeAudioQueue();
        using var stream = new AudioQueueOutputStream(api);
        Assert.ThrowsExactly<InvalidOperationException>(() => stream.Write(new float[2], 1));
        stream.Write(new float[stream.BufferFrames * 2], stream.BufferFrames);
        Assert.ThrowsExactly<InvalidOperationException>(() => stream.Write(new float[960 * 2], 960));
    }

    [TestMethod]
    public void SteadyStateCallbacksAndWritesDoNotAllocate()
    {
        var api = new FakeAudioQueue();
        api.Enqueued.EnsureCapacity(5000);
        using var stream = new AudioQueueOutputStream(api);
        var block = new float[960 * 2];
        stream.Write(new float[stream.BufferFrames * 2], stream.BufferFrames);
        stream.Start();
        for (int i = 0; i < 2000; i++)
        {
            api.Complete(0);
            stream.GetAvailableFrames();
            stream.Write(block, 960);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++)
        {
            api.Complete(0);
            stream.GetAvailableFrames();
            stream.Write(block, 960);
        }

        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed class FakeAudioQueue : IAudioQueueApi
    {
        private readonly List<IntPtr> _data = [];
        private AudioQueueOutputCallback? _callback;
        private IntPtr _userData;
        private IntPtr _queue;
        public readonly List<IntPtr> Buffers = [];
        public readonly List<IntPtr> Enqueued = [];
        public AudioStreamDescription Format { get; private set; }
        public int FailAllocationAt { get; init; }
        public int StopResult { get; init; }
        public int StopCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public int LiveAllocations => _data.Count;

        public int Create(ref AudioStreamDescription format, AudioQueueOutputCallback callback, IntPtr userData, out IntPtr queue)
        {
            Format = format;
            _callback = callback;
            _userData = userData;
            _queue = (IntPtr)1;
            queue = _queue;
            return 0;
        }

        public int Allocate(IntPtr queue, uint bytes, out IntPtr buffer)
        {
            if (FailAllocationAt == Buffers.Count + 1)
            {
                buffer = IntPtr.Zero;
                return -50;
            }

            IntPtr data = Marshal.AllocHGlobal((int)bytes);
            buffer = Marshal.AllocHGlobal(Marshal.SizeOf<AudioQueueBuffer>());
            Marshal.StructureToPtr(new AudioQueueBuffer { AudioDataBytesCapacity = bytes, AudioData = data }, buffer, false);
            _data.Add(data);
            Buffers.Add(buffer);
            return 0;
        }

        public int Enqueue(IntPtr queue, IntPtr buffer) { Enqueued.Add(buffer); return 0; }
        public int Start(IntPtr queue) => 0;
        public int Stop(IntPtr queue) { StopCalls++; return StopResult; }
        public int Dispose(IntPtr queue)
        {
            DisposeCalls++;
            foreach (IntPtr data in _data) { Marshal.FreeHGlobal(data); }
            foreach (IntPtr buffer in Buffers) { Marshal.FreeHGlobal(buffer); }
            _data.Clear();
            return 0;
        }
        public void Complete(int index) => _callback!(_userData, _queue, Buffers[index]);
        public float ReadFirstSample(int index)
        {
            var value = new float[1];
            Marshal.Copy(_data[index], value, 0, 1);
            return value[0];
        }
        public int ReadDataSize(int index) => checked((int)Marshal.PtrToStructure<AudioQueueBuffer>(Buffers[index]).AudioDataByteSize);
    }
}
