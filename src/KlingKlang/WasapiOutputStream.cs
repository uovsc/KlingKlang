using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KlingKlang;

[SupportedOSPlatform("windows")]
internal sealed class WasapiOutputStream : IAudioOutputStream
{
    private const uint ClassContextAll = 0x17;
    private const uint AutoConvertPcm = 0x80000000;
    private const uint DefaultQualityResampling = 0x08000000;
    private const long BufferDurationHundredNanoseconds = 500_000;
    private const int DeviceInvalidated = unchecked((int)0x88890004);
    private const int ResourcesInvalidated = unchecked((int)0x88890026);
    private const int EndpointNotFound = unchecked((int)0x80070490);

    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _device;
    private IAudioClient? _client;
    private IAudioRenderClient? _renderClient;
    private bool _comInitialized;
    private bool _started;
    private bool _deviceLost;
    private bool _disposed;

    public WasapiOutputStream()
    {
        Check(CoInitializeEx(IntPtr.Zero, 0), "Initializing COM");
        _comInitialized = true;
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            Check(_enumerator.GetDefaultAudioEndpoint(0, 1, out _device), "Finding the default playback device");
            Guid clientId = typeof(IAudioClient).GUID;
            Check(_device.Activate(ref clientId, ClassContextAll, IntPtr.Zero, out _client), "Activating the audio client");

            var format = new WaveFormat
            {
                FormatTag = 3,
                Channels = 2,
                SampleRate = 48_000,
                AverageBytesPerSecond = 384_000,
                BlockAlign = 8,
                BitsPerSample = 32
            };
            Check(_client.Initialize(0, AutoConvertPcm | DefaultQualityResampling,
                BufferDurationHundredNanoseconds, 0, ref format, IntPtr.Zero), "Initializing WASAPI shared mode");
            Check(_client.GetBufferSize(out uint frames), "Getting the WASAPI buffer size");
            if (frames == 0 || frames > Array.MaxLength / 2)
            {
                throw new InvalidOperationException("WASAPI returned an invalid buffer size.");
            }

            BufferFrames = (int)frames;
            Guid renderId = typeof(IAudioRenderClient).GUID;
            Check(_client.GetService(ref renderId, out _renderClient), "Getting the WASAPI render service");
        }
        catch (Exception initializationFailure)
        {
            try
            {
                Dispose();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("WASAPI initialization and cleanup both failed.", initializationFailure, cleanupFailure);
            }

            throw;
        }
    }

    public int BufferFrames { get; }

    public int GetAvailableFrames()
    {
        CheckStream(_client!.GetCurrentPadding(out uint padding), "Querying WASAPI buffer padding");
        if (padding > BufferFrames)
        {
            throw new InvalidOperationException("WASAPI returned padding larger than its buffer.");
        }

        return BufferFrames - (int)padding;
    }

    public void Write(float[] samples, int frames)
    {
        CheckStream(_renderClient!.GetBuffer((uint)frames, out IntPtr destination), "Acquiring a WASAPI buffer");
        try
        {
            Marshal.Copy(samples, 0, destination, checked(frames * 2));
        }
        catch (Exception copyFailure)
        {
            int releaseResult = _renderClient.ReleaseBuffer(0, 0);
            if (releaseResult < 0)
            {
                throw new AggregateException("WASAPI buffer copy and release both failed.",
                    copyFailure, CreateError(releaseResult, "Releasing the WASAPI buffer"));
            }

            throw;
        }

        CheckStream(_renderClient.ReleaseBuffer((uint)frames, 0), "Releasing the WASAPI buffer");
    }

    public void Start()
    {
        Check(_client!.Start(), "Starting the WASAPI stream");
        _started = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_started && !_deviceLost)
            {
                Check(_client!.Stop(), "Stopping the WASAPI stream");
            }
        }
        finally
        {
            try
            {
                Release(ref _renderClient);
            }
            finally
            {
                try
                {
                    Release(ref _client);
                }
                finally
                {
                    try
                    {
                        Release(ref _device);
                    }
                    finally
                    {
                        try
                        {
                            Release(ref _enumerator);
                        }
                        finally
                        {
                            if (_comInitialized)
                            {
                                CoUninitialize();
                                _comInitialized = false;
                            }
                        }
                    }
                }
            }
        }
    }

    private static void Release<T>(ref T? value) where T : class
    {
        if (value is not null)
        {
            Marshal.ReleaseComObject(value);
            value = null;
        }
    }

    private static void Check(int result, string operation)
    {
        if (result < 0)
        {
            throw CreateError(result, operation);
        }
    }

    private void CheckStream(int result, string operation)
    {
        if (result is DeviceInvalidated or ResourcesInvalidated)
        {
            _deviceLost = true;
        }

        Check(result, operation);
    }

    internal static InvalidOperationException CreateError(int result, string operation)
    {
        string message = $"{operation} failed (HRESULT 0x{unchecked((uint)result):X8}).";
        Exception? cause = Marshal.GetExceptionForHR(result);
        return result is DeviceInvalidated or ResourcesInvalidated or EndpointNotFound
            ? new AudioDeviceLostException(message, cause)
            : new InvalidOperationException(message, cause);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SampleRate;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, [MarshalAs(UnmanagedType.Interface)] out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint classContext, IntPtr activationParameters,
            [MarshalAs(UnmanagedType.Interface)] out IAudioClient client);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, ref WaveFormat format, IntPtr sessionId);
        [PreserveSig]
        int GetBufferSize(out uint frames);
        [PreserveSig]
        int GetStreamLatency(out long latency);
        [PreserveSig]
        int GetCurrentPadding(out uint frames);
        [PreserveSig]
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig]
        int GetMixFormat(out IntPtr format);
        [PreserveSig]
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig]
        int Start();
        [PreserveSig]
        int Stop();
        [PreserveSig]
        int Reset();
        [PreserveSig]
        int SetEventHandle(IntPtr eventHandle);
        [PreserveSig]
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IAudioRenderClient renderClient);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioRenderClient
    {
        [PreserveSig]
        int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig]
        int ReleaseBuffer(uint frames, uint flags);
    }
}
