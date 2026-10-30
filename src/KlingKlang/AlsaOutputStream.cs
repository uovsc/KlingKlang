using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KlingKlang;

internal sealed class AlsaOutputStream : IAudioOutputStream
{
    private const int Interrupted = -4;
    private const int TryAgain = -11;
    private const int NoDevice = -19;
    private const int InvalidDevice = -25;
    private const int Underrun = -32;
    private const int Suspended = -86;
    private const int BytesPerFrame = 4;

    private readonly IAlsaApi _api;
    private readonly byte[] _samples;
    private readonly long _timeoutTicks;
    private IntPtr _handle;
    private GCHandle _pinnedSamples;
    private int _pendingOffset;
    private int _pendingFrames;
    private bool _configured;
    private bool _suspended;
    private bool _deviceLost;
    private bool _started;
    private bool _disposed;
    private long _lastProgressTimestamp;

    [SupportedOSPlatform("linux")]
    public AlsaOutputStream() : this(new AlsaNative()) { }

    internal AlsaOutputStream(IAlsaApi api, TimeSpan? progressTimeout = null)
    {
        _api = api;
        TimeSpan timeout = progressTimeout ?? TimeSpan.FromSeconds(2);
        if (timeout <= TimeSpan.Zero || timeout.TotalSeconds > long.MaxValue / (double)Stopwatch.Frequency)
        {
            throw new ArgumentOutOfRangeException(nameof(progressTimeout));
        }

        _timeoutTicks = Math.Max(1, (long)(timeout.TotalSeconds * Stopwatch.Frequency));
        try
        {
            int openResult = _api.Open(out _handle);
            if (openResult is -2 or -6 or -16 or NoDevice or InvalidDevice)
            {
                throw CreateDeviceLostError(openResult, "Opening the default ALSA playback device");
            }

            Check(openResult, "Opening the default ALSA playback device");
            CheckStream(_api.SetParameters(_handle), "Configuring ALSA for 48 kHz stereo PCM16");
            _configured = true;
            CheckStream(_api.GetParameters(_handle, out nuint frames, out _), "Querying ALSA buffer size");
            if (frames == 0 || frames > (nuint)(Array.MaxLength / BytesPerFrame))
            {
                throw new InvalidOperationException("ALSA returned an invalid buffer size.");
            }

            BufferFrames = (int)frames;
            _samples = new byte[BufferFrames * BytesPerFrame];
            _pinnedSamples = GCHandle.Alloc(_samples, GCHandleType.Pinned);
        }
        catch (Exception initializationFailure)
        {
            try
            {
                Dispose();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("ALSA initialization and cleanup both failed.", initializationFailure, cleanupFailure);
            }

            throw;
        }
    }

    public int BufferFrames { get; }

    public int GetAvailableFrames()
    {
        if (_suspended && !Resume())
        {
            return 0;
        }

        if (_pendingFrames > 0)
        {
            FlushPending();
            if (_pendingFrames > 0 || _suspended)
            {
                return NoProgress();
            }
        }

        nint available = _api.Available(_handle);
        if (available < 0)
        {
            Recover(checked((int)available), "Querying ALSA available frames");
            return NoProgress();
        }

        return available == 0 ? NoProgress() : (int)Math.Min((nuint)available, (nuint)BufferFrames);
    }

    public void Write(float[] samples, int frames)
    {
        if (_pendingFrames != 0 || frames <= 0 || frames > BufferFrames || samples.Length < frames * 2)
        {
            throw new InvalidOperationException("Invalid ALSA write or an earlier write is still pending.");
        }

        for (int i = 0; i < frames * 2; i++)
        {
            float value = Math.Clamp(samples[i], -1f, 1f);
            short pcm = (short)MathF.Round(value < 0 ? value * 32768f : value * 32767f);
            BinaryPrimitives.WriteInt16LittleEndian(_samples.AsSpan(i * 2, 2), pcm);
        }

        _pendingOffset = 0;
        _pendingFrames = frames;
        FlushPending();
    }

    public void Start()
    {
        // snd_pcm_set_params configures an automatic start threshold.
        _lastProgressTimestamp = Stopwatch.GetTimestamp();
        _started = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        int dropResult = 0;
        int closeResult = 0;
        try
        {
            if (_handle != IntPtr.Zero)
            {
                try
                {
                    if (_configured)
                    {
                        dropResult = _api.Drop(_handle);
                    }
                }
                finally
                {
                    closeResult = _api.Close(_handle);
                    _handle = IntPtr.Zero;
                }
            }
        }
        finally
        {
            if (_pinnedSamples.IsAllocated)
            {
                _pinnedSamples.Free();
            }
        }

        if (_deviceLost)
        {
            if (dropResult is NoDevice or InvalidDevice) { dropResult = 0; }
            if (closeResult is NoDevice or InvalidDevice) { closeResult = 0; }
        }

        if (dropResult < 0 && closeResult < 0)
        {
            throw new AggregateException(CreateError(dropResult, "Stopping ALSA"), CreateError(closeResult, "Closing ALSA"));
        }

        Check(dropResult, "Stopping ALSA");
        Check(closeResult, "Closing ALSA");
    }

    private void FlushPending()
    {
        IntPtr position = IntPtr.Add(_pinnedSamples.AddrOfPinnedObject(), _pendingOffset * BytesPerFrame);
        nint written = _api.Write(_handle, position, (nuint)_pendingFrames);
        if (written < 0)
        {
            Recover(checked((int)written), "Writing ALSA audio");
            return;
        }

        if (written > _pendingFrames)
        {
            throw new InvalidOperationException("ALSA reported writing more frames than requested.");
        }

        _pendingOffset += (int)written;
        _pendingFrames -= (int)written;
        if (written > 0)
        {
            _lastProgressTimestamp = Stopwatch.GetTimestamp();
        }
    }

    private void Recover(int error, string operation)
    {
        switch (error)
        {
            case Interrupted:
            case TryAgain:
                return;
            case Underrun:
                CheckStream(_api.Prepare(_handle), "Recovering from an ALSA underrun");
                return;
            case Suspended:
                _suspended = true;
                Resume();
                return;
            case NoDevice:
            case InvalidDevice:
                _deviceLost = true;
                throw CreateDeviceLostError(error, operation);
            default:
                throw CreateError(error, operation);
        }
    }

    private bool Resume()
    {
        int result = _api.Resume(_handle);
        if (result is TryAgain or Interrupted)
        {
            return false;
        }

        if (result < 0)
        {
            CheckStream(result, "Resuming a suspended ALSA device", allowOtherErrors: true);
            CheckStream(_api.Prepare(_handle), "Recovering from an ALSA suspension");
        }

        _suspended = false;
        _lastProgressTimestamp = Stopwatch.GetTimestamp();
        return true;
    }

    private int NoProgress()
    {
        if (_started && !_suspended && Stopwatch.GetTimestamp() - _lastProgressTimestamp > _timeoutTicks)
        {
            throw new InvalidOperationException("The ALSA playback device stopped accepting audio.");
        }

        return 0;
    }

    private void Check(int result, string operation)
    {
        if (result < 0)
        {
            throw CreateError(result, operation);
        }
    }

    private void CheckStream(int result, string operation, bool allowOtherErrors = false)
    {
        if (result is NoDevice or InvalidDevice)
        {
            _deviceLost = true;
            throw CreateDeviceLostError(result, operation);
        }

        if (!allowOtherErrors)
        {
            Check(result, operation);
        }
    }

    private AudioDeviceLostException CreateDeviceLostError(int error, string operation)
    {
        InvalidOperationException cause = CreateError(error, operation);
        return new AudioDeviceLostException(cause.Message, cause);
    }

    private InvalidOperationException CreateError(int error, string operation) =>
        new($"{operation} failed (ALSA {error.ToString(CultureInfo.InvariantCulture)}: {_api.ErrorString(error)}).");
}
