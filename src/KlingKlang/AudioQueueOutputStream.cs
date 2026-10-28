using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KlingKlang;

internal sealed class AudioQueueOutputStream : IAudioOutputStream
{
    private const int QueueBufferCount = 3;
    private const int FramesPerBuffer = 960;
    private const int Channels = 2;
    private const int BytesPerSample = sizeof(float);
    private const int BufferBytes = FramesPerBuffer * Channels * BytesPerSample;
    private static readonly int DataSizeOffset = Marshal.OffsetOf<AudioQueueBuffer>(nameof(AudioQueueBuffer.AudioDataByteSize)).ToInt32();
    private static readonly AudioQueueOutputCallback Callback = OnBufferComplete;
    private static int s_callbackFailures;

    private readonly IAudioQueueApi _api;
    private readonly IntPtr[] _buffers = new IntPtr[QueueBufferCount];
    private readonly IntPtr[] _data = new IntPtr[QueueBufferCount];
    private readonly int[] _inUse = new int[QueueBufferCount];
    private readonly long _timeoutTicks;
    private readonly int _callbackFailureBaseline;
    private GCHandle _callbackHandle;
    private IntPtr _queue;
    private long _lastCallbackTimestamp;
    private int _callbackError;
    private bool _started;
    private bool _disposed;

    [SupportedOSPlatform("macos")]
    public AudioQueueOutputStream() : this(new AudioQueueNative()) { }

    internal AudioQueueOutputStream(IAudioQueueApi api, TimeSpan? callbackTimeout = null)
    {
        _api = api;
        TimeSpan timeout = callbackTimeout ?? TimeSpan.FromSeconds(2);
        if (timeout <= TimeSpan.Zero || timeout.TotalSeconds > long.MaxValue / (double)Stopwatch.Frequency)
        {
            throw new ArgumentOutOfRangeException(nameof(callbackTimeout));
        }

        _timeoutTicks = Math.Max(1, (long)(timeout.TotalSeconds * Stopwatch.Frequency));
        _callbackFailureBaseline = Volatile.Read(ref s_callbackFailures);
        try
        {
            _callbackHandle = GCHandle.Alloc(this);
            var format = new AudioStreamDescription
            {
                SampleRate = 48_000,
                FormatId = 0x6c70636d,
                FormatFlags = 1 | 8,
                BytesPerPacket = Channels * BytesPerSample,
                FramesPerPacket = 1,
                BytesPerFrame = Channels * BytesPerSample,
                ChannelsPerFrame = Channels,
                BitsPerChannel = 32
            };
            Check(_api.Create(ref format, Callback, GCHandle.ToIntPtr(_callbackHandle), out _queue), "Creating the Audio Queue");
            if (_queue == IntPtr.Zero)
            {
                throw new InvalidOperationException("Audio Queue returned a null queue.");
            }

            for (int i = 0; i < QueueBufferCount; i++)
            {
                Check(_api.Allocate(_queue, BufferBytes, out _buffers[i]), "Allocating an Audio Queue buffer");
                if (_buffers[i] == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Audio Queue returned a null buffer.");
                }

                AudioQueueBuffer native = Marshal.PtrToStructure<AudioQueueBuffer>(_buffers[i]);
                if (native.AudioData == IntPtr.Zero || native.AudioDataBytesCapacity < BufferBytes)
                {
                    throw new InvalidOperationException("Audio Queue returned an invalid buffer.");
                }

                _data[i] = native.AudioData;
            }
        }
        catch (Exception initializationFailure)
        {
            try
            {
                Dispose();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Audio Queue initialization and cleanup both failed.", initializationFailure, cleanupFailure);
            }

            throw;
        }
    }

    public int BufferFrames => QueueBufferCount * FramesPerBuffer;

    public int GetAvailableFrames()
    {
        if (Volatile.Read(ref _callbackError) != 0 || Volatile.Read(ref s_callbackFailures) != _callbackFailureBaseline)
        {
            throw new InvalidOperationException("The Audio Queue callback failed or returned an unexpected buffer.");
        }

        int free = 0;
        for (int i = 0; i < QueueBufferCount; i++)
        {
            if (Volatile.Read(ref _inUse[i]) == 0)
            {
                free++;
            }
        }

        if (free == 0 && _started &&
            Stopwatch.GetTimestamp() - Volatile.Read(ref _lastCallbackTimestamp) > _timeoutTicks)
        {
            throw new InvalidOperationException("The Audio Queue stopped returning playback buffers.");
        }

        return free * FramesPerBuffer;
    }

    public void Write(float[] samples, int frames)
    {
        if (frames <= 0 || frames > BufferFrames || frames % FramesPerBuffer != 0 || samples.Length < frames * Channels)
        {
            throw new InvalidOperationException("Audio Queue writes must contain complete playback buffers.");
        }

        int remaining = frames / FramesPerBuffer;
        int sampleOffset = 0;
        for (int i = 0; i < QueueBufferCount && remaining > 0; i++)
        {
            if (Interlocked.CompareExchange(ref _inUse[i], 1, 0) != 0)
            {
                continue;
            }

            try
            {
                Marshal.Copy(samples, sampleOffset, _data[i], FramesPerBuffer * Channels);
                Marshal.WriteInt32(_buffers[i], DataSizeOffset, BufferBytes);
                Check(_api.Enqueue(_queue, _buffers[i]), "Enqueuing an Audio Queue buffer");
            }
            catch
            {
                Interlocked.Exchange(ref _inUse[i], 0);
                throw;
            }

            sampleOffset += FramesPerBuffer * Channels;
            remaining--;
        }

        if (remaining != 0)
        {
            throw new InvalidOperationException("Audio Queue had fewer free buffers than requested.");
        }
    }

    public void Start()
    {
        Check(_api.Start(_queue), "Starting the Audio Queue");
        Volatile.Write(ref _lastCallbackTimestamp, Stopwatch.GetTimestamp());
        _started = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        int stopResult = 0;
        int disposeResult = 0;
        try
        {
            if (_queue != IntPtr.Zero)
            {
                try
                {
                    if (_started)
                    {
                        stopResult = _api.Stop(_queue);
                    }
                }
                finally
                {
                    disposeResult = _api.Dispose(_queue);
                    if (disposeResult == 0)
                    {
                        _queue = IntPtr.Zero;
                    }
                }
            }
        }
        finally
        {
            // A failed native disposal may leave callbacks able to use this context.
            if (_queue == IntPtr.Zero && _callbackHandle.IsAllocated)
            {
                _callbackHandle.Free();
            }
        }

        if (stopResult != 0 && disposeResult != 0)
        {
            throw new AggregateException(CreateError(stopResult, "Stopping the Audio Queue"), CreateError(disposeResult, "Disposing the Audio Queue"));
        }

        Check(stopResult, "Stopping the Audio Queue");
        Check(disposeResult, "Disposing the Audio Queue");
    }

    private static void OnBufferComplete(IntPtr userData, IntPtr queue, IntPtr buffer)
    {
        try
        {
            if (GCHandle.FromIntPtr(userData).Target is AudioQueueOutputStream stream)
            {
                stream.ReturnBuffer(queue, buffer);
            }
            else
            {
                Interlocked.Increment(ref s_callbackFailures);
            }
        }
        catch (Exception)
        {
            Interlocked.Increment(ref s_callbackFailures);
        }
    }

    private void ReturnBuffer(IntPtr queue, IntPtr buffer)
    {
        for (int i = 0; i < QueueBufferCount; i++)
        {
            if (queue == _queue && buffer == _buffers[i])
            {
                Volatile.Write(ref _lastCallbackTimestamp, Stopwatch.GetTimestamp());
                if (Interlocked.CompareExchange(ref _inUse[i], 0, 1) != 1)
                {
                    Volatile.Write(ref _callbackError, 1);
                }

                return;
            }
        }

        Volatile.Write(ref _callbackError, 1);
    }

    private static void Check(int result, string operation)
    {
        if (result != 0)
        {
            throw CreateError(result, operation);
        }
    }

    private static InvalidOperationException CreateError(int result, string operation) =>
        new($"{operation} failed (OSStatus {result.ToString(CultureInfo.InvariantCulture)}).");
}
