# KlingKlang

KlingKlang is an early .NET 10 audio playback library with offline rendering
and experimental live backends selected by `AudioEngine.Create()`.

| Platform | Backend | Verification status |
| --- | --- | --- |
| Windows | WASAPI shared mode | Simulated tests only; real Windows playback has not been tested. |
| Linux | System ALSA `libasound.so.2` | Fedora 44 x86-64, kernel `7.2.11-200.fc44`, alsa-lib 1.2.16.1, ALSA `default` through PipeWire/pipewire-alsa 1.6.9 to the ALC1220 analog output: three open/play/close cycles and an active output link observed. Audible output was not independently confirmed. |
| macOS | System Audio Queue Services | Simulated tests only; real macOS playback has not been tested. |

These are not claims of support for other configurations. A real-device smoke
test on Windows and macOS and audible confirmation on Linux are still needed.

## Build, test, and listen

Install the .NET 10 SDK. The core library uses only the .NET base class library;
the test project restores test-only MSTest NuGet dependencies.

```sh
dotnet build KlingKlang.slnx
dotnet test KlingKlang.slnx
dotnet run --project samples/KlingKlang.Demo -- klingklang-demo.wav
```

The sample generates a tone, mixes two overlapping voices offline, and writes a
48 kHz stereo PCM16 WAV. Open the file in an external audio player to listen.
It refuses to overwrite an existing output file; pass a different output path
or remove the old file yourself.

On Windows, Linux, or macOS, the live sample opens and closes the default
playback device three times. Each time it loops a WAV, reduces its volume, and
stops it:

```sh
dotnet run --project samples/KlingKlang.LiveDemo -- path/to/48khz-pcm16.wav
```

Begin with a quiet WAV and a safe system volume. Automated tests use simulated
devices and do not require audio hardware.

## Playback API

```csharp
using KlingKlang;

using var audio = AudioEngine.Create(maxVoices: 64);
using var clip = audio.LoadWav("sound.wav");
VoiceHandle voice = audio.Play(clip, volume: 0.5f, loop: true);
audio.SetVolume(voice, 0.25f);
audio.Stop(voice);
```

For offline rendering instead, use `AudioEngine.CreateOffline()` and call
`Render` with a float buffer containing complete interleaved stereo frames.
`Render` is not available on a live engine.

- WAV input must be uncompressed little-endian PCM16, mono or stereo, at
  48 kHz. Other input sample rates and formats are rejected; WAV loading does
  not resample. The decoder accepts unknown RIFF chunks and leaves input
  streams open.
- Offline rendering is synchronous and advances voices by the requested number
  of frames. Samples are summed as floats without clipping; clamp when
  converting to an output format, as in the sample. Empty output does not
  advance playback.
- Volumes must be finite and between 0 and 1 inclusive. The default is 1.
  When the configured voice pool is full, `Play` throws
  `InvalidOperationException` rather than replacing a voice.
- A completed, stopped, default, stale, or foreign handle is inactive.
  `Stop` and `SetVolume` return `false` for an inactive handle. Disposing a
  clip prevents new plays; active voices retain its samples until completion
  or stop. Disposing the engine stops all voices and subsequent operations
  throw `ObjectDisposedException`.
- Playback commands are queued and processed at the next render block. The
  bounded queue holds 1024 commands; a full queue causes the operation to
  throw `InvalidOperationException` without applying it. Producers may call
  playback methods concurrently, but must not call offline `Render`
  concurrently with another `Render` or with disposal.
- Live playback is asynchronous. A stopped or completed voice can still have
  samples already queued in the device buffer. `IsPlaying` reports whether a
  voice is still being mixed, not whether its last sample has reached the
  speakers. Startup errors are reported by `Create`; subsequent device errors
  are reported by the next engine operation or disposal.

## Windows backend limitations

The backend opens the default multimedia playback endpoint in WASAPI shared
mode. It submits 48 kHz stereo float samples, clamps them at the output
boundary, and requests Windows' built-in channel and sample-rate conversion
when the shared mix format differs. It requests a 50 ms buffer and polls at
10 ms intervals; actual buffering and latency depend on Windows and the
device. No latency or underrun behavior has been measured on Windows.

Device loss or a stream failure stops playback and is surfaced as an exception;
automatic device switching and recovery are not implemented. Dispose the
engine and create a new one after resolving the underlying problem.

## Linux backend limitations

Linux requires the system-provided `libasound.so.2` and a working ALSA
`default` playback device. Neither is guaranteed to be installed or configured.
PipeWire/PulseAudio compatibility depends on the system's ALSA configuration;
no separate audio server or native library is bundled by this project.

The backend requests nonblocking 48 kHz stereo PCM16 with a 50 ms target
buffer. ALSA's configured device or plug-in must accept that format, including
any necessary sample-rate conversion; unsupported formats fail explicitly.
Short writes are retained for later retries. ALSA underruns are prepared
again, and suspended streams are resumed or prepared. A device that stops
accepting frames for two seconds reports an error, except while awaiting
recovery from a suspended stream. Actual buffering, latency, recovery, and
audibility depend on the device and system configuration.

## macOS backend limitations

The experimental backend requests interleaved 48 kHz stereo float PCM from
Audio Queue Services and uses three 20 ms buffers. A callback only returns
buffers for reuse; mixing runs on the library's worker thread. If the queue
stops returning buffers for two seconds, the engine reports an error.
Actual playback, format conversion, latency, device changes, and shutdown have
not yet been verified on macOS. If native queue disposal fails, the error is
reported and the callback context is retained to avoid a possible
use-after-free.

Across all backends, automatic device switching or reconnection is not
implemented. Dispose the engine and recreate it after resolving device errors.
