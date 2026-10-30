# KlingKlang

KlingKlang is an early .NET 10 audio playback library with offline rendering
and experimental live backends selected by `AudioEngine.Create()`.

| Platform | Backend | Verification status |
| --- | --- | --- |
| Windows | WASAPI shared mode | User-confirmed Release DLL playback on Windows 11 Home under KVM on a Fedora 44 host: two WAV files were heard simultaneously. Guest architecture and virtual audio route were not reported; recovery was not tested. |
| Linux | System ALSA `libasound.so.2` | Fedora 44 x86-64, kernel `7.2.11-200.fc44`, alsa-lib 1.2.16.1, ALSA `default` through PipeWire/pipewire-alsa 1.6.9 to the ALC1220 analog output: three open/play/close cycles and an active output link observed. Audible output was not independently confirmed. |
| macOS | System Audio Queue Services | Simulated tests only; real macOS playback has not been tested. |

These are not claims of support for other configurations. Testing on physical
Windows hardware, a real-device smoke test on macOS, and audible confirmation
on Linux are still needed.

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

On Windows, Linux, or macOS, the live sample loads a WAV and plays it through the default playback device:

```sh
dotnet run --project samples/KlingKlang.LiveDemo -- path/to/pcm16.wav
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

- WAV input must be uncompressed little-endian PCM16, mono or stereo, at any
  whole-number rate from 8,000 to 48,000 Hz inclusive. The decoder reads the
  source rate automatically, accepts unknown RIFF chunks, and leaves input
  streams open. Unsupported rates and formats are rejected.
- Clips are converted once at load time to 48 kHz using linear interpolation.
  `AudioClip.SampleRate` and `FrameCount` describe these stored samples, not
  the original WAV. Conversion can expand sample memory by up to six times
  and attenuates some high frequencies. At the end of a converted clip,
  interpolation wraps toward the first source sample even for one-shot
  playback; this does not guarantee click-free loops. The converted duration
  is rounded to the nearest output frame, so each loop can differ from the
  original duration by up to half a 48 kHz frame. Existing 48 kHz samples are
  unchanged.
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
  throw `InvalidOperationException` without applying it. Concurrent producers
  use a short lock; the audio thread never waits for it. Do not call offline
  `Render` concurrently with another `Render` or with disposal.
- Live playback is asynchronous. A stopped or completed voice can still have
  samples already queued in the device buffer. `IsPlaying` reports whether a
  voice is still being mixed, not whether its last sample has reached the
  speakers. Disposing immediately after completion may cut off buffered audio.

## Device loss and recovery

`audio.ConnectionState` reports `Ready`, `Recovering`, `Faulted`, or `Disposed`
without throwing. An offline engine remains `Ready` until it is disposed.
Initial device failures are reported by `Create`.

After a recognized loss of an established live device, the engine releases the
old stream and tries the default output twice: once immediately, then once
more after 500 ms. The delay is interruptible by disposal. If both attempts
fail, or cleanup or another non-recoverable error occurs, the engine becomes
`Faulted`; dispose and recreate it after resolving the error. A later loss
after a successful reconnect gets a fresh retry budget. Opening and closing
native devices may themselves block beyond the retry delay.

While `Recovering` or `Faulted`, playback operations (including `IsPlaying`)
throw `InvalidOperationException` with the underlying error as an inner
exception. Calls accepted just before the state changes remain queued; rejected
calls do not apply their commands. Active voices retain their handles and
resume at their last **mixed** positions after reconnection. Samples already
queued to the old output, or partially written when it failed, may be lost or
partly audible. Disposal is always allowed, but may also report an outstanding
device failure; its connection state is then `Disposed`.

Recovery is triggered only for recognized device-loss errors, not for arbitrary
errors. In particular, a backend watchdog timeout is reported as a failure
instead of being assumed to be a device loss. Actual unplug and reconnect
behavior has not yet been verified on physical devices.

## Windows backend limitations

The backend opens the default multimedia playback endpoint in WASAPI shared
mode. It submits 48 kHz stereo float samples, clamps them at the output
boundary, and requests Windows' built-in channel and sample-rate conversion
when the shared mix format differs. It requests a 50 ms buffer and polls at
10 ms intervals; actual buffering and latency depend on Windows and the
device. No latency or underrun behavior has been measured on Windows.

An invalidated endpoint or stream resource, or a missing default endpoint, is
classified for reconnection. Other WASAPI failures are reported without retries.
Playback was confirmed in the Windows 11 Home KVM guest described above;
physical Windows hardware and device-loss recovery remain untested.

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
ALSA device-removal errors `ENODEV` and `ENOTTY` trigger reconnection;
underrun and suspension continue to be handled in place where possible.

## macOS backend limitations

The experimental backend requests interleaved 48 kHz stereo float PCM from
Audio Queue Services and uses three 20 ms buffers. A callback only returns
buffers for reuse; mixing runs on the library's worker thread. If the queue
stops returning buffers for two seconds, the engine reports an error.
Actual playback, format conversion, latency, device changes, and shutdown have
not yet been verified on macOS. If native queue disposal fails, the error is
reported and the callback context is retained to avoid a possible
use-after-free. An Audio Queue invalid-device error triggers reconnection; a
stalled callback instead faults the engine because its cause is unknown.

## Render benchmark

Run the dependency-free, hardware-independent benchmark in Release mode:

```sh
dotnet run --project benchmarks/KlingKlang.Benchmarks -c Release
```

It reports warmed render-thread allocations, frames per second, and the ratio
to 48 kHz for mono and stereo clips, 0/1/16/64 voices, and 480/960-frame
blocks. Timing varies by machine and is not a CI pass/fail threshold; a
64-voice scenario below real time is marked `BELOW_REALTIME`. Any observed
allocation is marked `ALLOCATED`.

One Release run on Fedora 44 x86-64, .NET 10.0.13, AMD Ryzen 9 9950X3D,
measured zero render-thread bytes in every scenario. The 64-voice stereo
scenarios achieved approximately 15.5–16.8 million frames/s (323–351 times
real time). These measurements are not live-device latency or a guarantee for
other machines.

For manual device-loss testing, use a quiet 48 kHz PCM16 WAV, run the live
sample, then disconnect and reconnect an output device while playback is
active. Observe `ConnectionState` in a debugger or a small test app and record
the OS version, architecture, audio route, reconnect result, and audible
behavior. Do not treat simulated tests as physical-device verification.
