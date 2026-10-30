using System.Runtime.Versioning;

namespace KlingKlang;

public sealed class AudioEngine : IDisposable
{
    private const int DefaultCommandCapacity = 1024;
    private readonly object _identity = new();
    private readonly object _producerGate = new();
    private readonly Voice[] _voices;
    private readonly long[] _activeGenerations;
    private readonly long[] _lastGenerations;
    private readonly VoiceCommand[] _commands;
    private readonly IAudioDevice _device;
    private int _readIndex;
    private int _writeIndex;
    private bool _disposed;

    private AudioEngine(int maxVoices, IAudioDevice device, int commandCapacity = DefaultCommandCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxVoices);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(commandCapacity);
        _voices = new Voice[maxVoices];
        _activeGenerations = new long[maxVoices];
        _lastGenerations = new long[maxVoices];
        _commands = new VoiceCommand[checked(commandCapacity + 1)];
        _device = device;
        try
        {
            _device.Start(Mix);
        }
        catch (Exception startFailure)
        {
            try
            {
                _device.Dispose();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Audio device startup and cleanup both failed.", startFailure, cleanupFailure);
            }

            throw;
        }
    }

    public static AudioEngine Create(int maxVoices = 64)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxVoices);
        if (OperatingSystem.IsWindows())
        {
            return new AudioEngine(maxVoices, new ThreadedAudioDevice(OpenWindowsStream));
        }

        if (OperatingSystem.IsLinux())
        {
            return new AudioEngine(maxVoices, new ThreadedAudioDevice(OpenLinuxStream));
        }

        if (OperatingSystem.IsMacOS())
        {
            return new AudioEngine(maxVoices, new ThreadedAudioDevice(OpenMacStream));
        }

        throw new NotSupportedException("Live playback is not implemented on this operating system. Use CreateOffline for offline rendering.");
    }

    public static AudioEngine CreateOffline(int maxVoices = 64) => new(maxVoices, new NullAudioDevice());

    public AudioConnectionState ConnectionState =>
        Volatile.Read(ref _disposed) ? AudioConnectionState.Disposed : _device.Status.State;

    internal static AudioEngine CreateForTesting(IAudioDevice device, int maxVoices = 64, int commandCapacity = DefaultCommandCapacity) =>
        new(maxVoices, device, commandCapacity);

    [SupportedOSPlatform("windows")]
    private static IAudioOutputStream OpenWindowsStream() => new WasapiOutputStream();

    [SupportedOSPlatform("linux")]
    private static IAudioOutputStream OpenLinuxStream()
    {
        try
        {
            return new AlsaOutputStream();
        }
        catch (DllNotFoundException exception)
        {
            throw new InvalidOperationException("Linux playback requires the system ALSA library libasound.so.2.", exception);
        }
    }

    [SupportedOSPlatform("macos")]
    private static IAudioOutputStream OpenMacStream() => new AudioQueueOutputStream();

    public AudioClip LoadWav(string path)
    {
        ThrowIfUnavailable();
        return AudioClip.LoadWav(path);
    }

    public VoiceHandle Play(AudioClip clip, float volume = 1f, bool loop = false)
    {
        lock (_producerGate)
        {
            ThrowIfUnavailable();
            ArgumentNullException.ThrowIfNull(clip);
            ValidateVolume(volume);
            float[] samples = clip.GetSamples();
            for (int i = 0; i < _voices.Length; i++)
            {
                if (Volatile.Read(ref _activeGenerations[i]) != 0)
                {
                    continue;
                }

                long generation = _lastGenerations[i] == long.MaxValue ? 1 : _lastGenerations[i] + 1;
                _lastGenerations[i] = generation;
                Volatile.Write(ref _activeGenerations[i], generation);
                var command = new VoiceCommand(CommandKind.Play, i, generation, samples, clip.Channels, volume, loop);
                if (!TryEnqueue(command))
                {
                    Interlocked.CompareExchange(ref _activeGenerations[i], 0, generation);
                    throw new InvalidOperationException("The audio command queue is full.");
                }

                return new VoiceHandle(_identity, i, generation);
            }

            throw new InvalidOperationException("The audio voice pool is full.");
        }
    }

    public bool IsPlaying(VoiceHandle handle)
    {
        lock (_producerGate)
        {
            ThrowIfUnavailable();
            return IsCurrent(handle);
        }
    }

    public bool SetVolume(VoiceHandle handle, float volume)
    {
        lock (_producerGate)
        {
            ThrowIfUnavailable();
            ValidateVolume(volume);
            if (!IsCurrent(handle))
            {
                return false;
            }

            EnqueueOrThrow(new VoiceCommand(CommandKind.Volume, handle.Slot, handle.Generation, Volume: volume));
            return true;
        }
    }

    public bool Stop(VoiceHandle handle)
    {
        lock (_producerGate)
        {
            ThrowIfUnavailable();
            if (!IsCurrent(handle))
            {
                return false;
            }

            EnqueueOrThrow(new VoiceCommand(CommandKind.Stop, handle.Slot, handle.Generation));
            Interlocked.CompareExchange(ref _activeGenerations[handle.Slot], 0, handle.Generation);
            return true;
        }
    }

    public void StopAll()
    {
        lock (_producerGate)
        {
            ThrowIfUnavailable();
            EnqueueOrThrow(new VoiceCommand(CommandKind.StopAll));
            for (int i = 0; i < _activeGenerations.Length; i++)
            {
                Interlocked.Exchange(ref _activeGenerations[i], 0);
            }
        }
    }

    public void Render(Span<float> interleavedStereo)
    {
        ThrowIfUnavailable();
        if ((interleavedStereo.Length & 1) != 0)
        {
            throw new ArgumentException("The output must contain complete stereo frames.", nameof(interleavedStereo));
        }

        if (_device is not NullAudioDevice offline)
        {
            throw new InvalidOperationException("Render is only available on an offline engine.");
        }

        offline.Render(interleavedStereo);
    }

    public void Dispose()
    {
        lock (_producerGate)
        {
            if (_disposed)
            {
                return;
            }

            Volatile.Write(ref _disposed, true);

            try
            {
                _device.Dispose();
            }
            finally
            {
                Array.Clear(_voices);
                Array.Clear(_commands);
                Array.Clear(_activeGenerations);
            }

            if (_device.Status.Failure is Exception failure)
            {
                throw new InvalidOperationException("The audio device failed during playback or shutdown.", failure);
            }
        }
    }

    private void Mix(Span<float> output)
    {
        ApplyPendingCommands();
        output.Clear();
        for (int i = 0; i < _voices.Length; i++)
        {
            ref Voice voice = ref _voices[i];
            float[]? samples = voice.Samples;
            if (samples is null)
            {
                continue;
            }

            int frameCount = samples.Length / voice.Channels;
            for (int frame = 0; frame < output.Length / 2; frame++)
            {
                int sampleIndex = voice.Position * voice.Channels;
                float left = samples[sampleIndex] * voice.Volume;
                float right = voice.Channels == 1 ? left : samples[sampleIndex + 1] * voice.Volume;
                output[frame * 2] += left;
                output[frame * 2 + 1] += right;

                voice.Position++;
                if (voice.Position == frameCount)
                {
                    if (voice.Loop)
                    {
                        voice.Position = 0;
                    }
                    else
                    {
                        voice.Samples = null;
                        Interlocked.CompareExchange(ref _activeGenerations[i], 0, voice.Generation);
                        break;
                    }
                }
            }
        }
    }

    private void ApplyPendingCommands()
    {
        int end = Volatile.Read(ref _writeIndex);
        int index = _readIndex;
        while (index != end)
        {
            VoiceCommand command = _commands[index];
            _commands[index] = default;
            index = NextIndex(index);
            Volatile.Write(ref _readIndex, index);

            switch (command.Kind)
            {
                case CommandKind.Play:
                    _voices[command.Slot] = new Voice
                    {
                        Samples = command.Samples,
                        Channels = command.Channels,
                        Generation = command.Generation,
                        Volume = command.Volume,
                        Loop = command.Loop
                    };
                    break;
                case CommandKind.Volume:
                    ref Voice volumeVoice = ref _voices[command.Slot];
                    if (volumeVoice.Samples is not null && volumeVoice.Generation == command.Generation)
                    {
                        volumeVoice.Volume = command.Volume;
                    }
                    break;
                case CommandKind.Stop:
                    ref Voice stoppedVoice = ref _voices[command.Slot];
                    if (stoppedVoice.Generation == command.Generation)
                    {
                        stoppedVoice.Samples = null;
                    }
                    break;
                case CommandKind.StopAll:
                    Array.Clear(_voices);
                    break;
            }
        }
    }

    private bool TryEnqueue(VoiceCommand command)
    {
        int next = NextIndex(_writeIndex);
        if (next == Volatile.Read(ref _readIndex))
        {
            return false;
        }

        _commands[_writeIndex] = command;
        Volatile.Write(ref _writeIndex, next);
        return true;
    }

    private void EnqueueOrThrow(VoiceCommand command)
    {
        if (!TryEnqueue(command))
        {
            throw new InvalidOperationException("The audio command queue is full.");
        }
    }

    private int NextIndex(int index) => index + 1 == _commands.Length ? 0 : index + 1;

    private bool IsCurrent(VoiceHandle handle) =>
        ReferenceEquals(handle.Owner, _identity) &&
        (uint)handle.Slot < (uint)_activeGenerations.Length &&
        Volatile.Read(ref _activeGenerations[handle.Slot]) == handle.Generation;

    private static void ValidateVolume(float volume)
    {
        if (!float.IsFinite(volume) || volume < 0f || volume > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be finite and between 0 and 1.");
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        AudioDeviceStatus status = _device.Status;
        if (status.State != AudioConnectionState.Ready)
        {
            throw new InvalidOperationException(
                status.State == AudioConnectionState.Recovering
                    ? "The audio device is reconnecting."
                    : "The audio device failed during playback.",
                status.Failure);
        }
    }

    private enum CommandKind { Play, Volume, Stop, StopAll }

    private readonly record struct VoiceCommand(
        CommandKind Kind,
        int Slot = 0,
        long Generation = 0,
        float[]? Samples = null,
        int Channels = 0,
        float Volume = 0,
        bool Loop = false);

    private struct Voice
    {
        public float[]? Samples;
        public int Channels;
        public int Position;
        public float Volume;
        public bool Loop;
        public long Generation;
    }
}
