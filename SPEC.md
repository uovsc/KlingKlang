# SPEC.md — KlingKlang (working title)

## 1. Vision

A small C# library that makes audio playback in desktop games as straightforward as loading a sound and calling `Play`. Distribution should be close to copying the application's managed assemblies: no BASS, SDL, OpenAL, separate audio server installer or platform-specific binaries shipped by this project.

This is an AI-assisted learning/hobby project. The first deliverable is a reliable, understandable baseline, not a complete replacement for ManagedBass.

## 2. Supported environments

Android is a future milestone, not part of v0.1 or current live playback.

| Platform | Planned backend | Distribution assumption |
| --- | --- | --- |
| Windows (current supported desktop releases) | WASAPI shared mode | OS audio components already present |
| macOS (current supported desktop releases) | Audio Queue Services / CoreAudio | OS frameworks already present |
| Linux desktop | ALSA PCM | `libasound` and a working configured audio output must be available; PipeWire/PulseAudio compatibility depends on system configuration |
| Android 8.0 / API 26 and later (planned) | AAudio, not yet implemented | OS-provided audio API; a compatible .NET Android app and runtime are required |

Target framework for the initial project: `net10.0`. Use only the base class library and standard .NET interop at runtime. The consuming app still needs a compatible .NET runtime or self-contained .NET deployment. A portable managed DLL does not remove these OS/runtime requirements.

## 3. MVP scope (v0.1)

### Must have

- Load WAV files containing uncompressed PCM 16-bit little-endian, mono or stereo, with robust RIFF chunk parsing.
- Decode once at load time to an immutable in-memory `AudioClip` with interleaved float samples.
- Play multiple copies of one clip and different clips simultaneously; target a configurable capacity of 64 active voices.
- Play once or loop; set per-voice gain; stop individual voices; stop all; query whether a handle is still active.
- A small public API shared across all platforms; automatic OS backend selection, with meaningful initialization errors.
- Internal stereo float32 mixing, safe handling of overlapping clips, end-of-clip boundaries and clipping on device output.
- Clean start/stop and disposal without orphaned audio threads or native handles.
- Deterministic unit tests that run without audio hardware, and a tiny console sample.
- Document actual support status separately for each OS and its tested configurations.

### Not in v0.1

Recording, microphone input, MP3/FLAC/Vorbis/Opus decoding, streaming large assets from disk, spatial/3D audio, pitch shifting, effects, MIDI, device enumeration UI, ASIO, exclusive mode, mobile/browser platforms and guaranteed ultra-low latency.

## 4. Example public API (design target, subject to tests)

```csharp
using KlingKlang;

using var audio = AudioEngine.Create();
using var explosion = audio.LoadWav("assets/explosion.wav");

VoiceHandle first = audio.Play(explosion);
VoiceHandle second = audio.Play(explosion, volume: 0.5f, loop: true);

audio.SetVolume(second, 0.25f);
audio.Stop(second);
```

Public API constraints:

- `AudioEngine` owns the device, command queue and voices; `AudioClip` owns decoded samples; disposing an engine stops its voices and closes the device.
- Specify explicitly what happens if a clip is disposed while it is playing; preferred MVP behavior: active voices retain access to their immutable samples until they finish or stop.
- Handles must use a generation/version to prevent stale references from accidentally controlling a recycled voice slot.
- Validate volume as finite and nonnegative; default `1.0f`. Specify a sensible upper bound or clamp policy in code and tests.
- Define whether `Play` becomes audible asynchronously (expected) and whether a full voice pool rejects playback or replaces an existing voice (preferred MVP: reject with a documented result).
- Do not expose backend details to game callers.

## 5. Internal audio model

- Standard mix format: interleaved stereo, float32, nominal 48 kHz.
- Backend must negotiate actual device format and convert as needed. Do not assume every device supports the nominal format.
- The decoder detects WAV source rates and converts supported inputs to 48 kHz before mixing. Never reinterpret samples at the wrong playback speed; reject unsupported rates with a clear error.
- One active voice has clip reference, frame cursor, gain, loop flag and generation ID.
- The mixer sums active voices into an output buffer that is zeroed once per render block. Mono inputs are duplicated to both output channels.
- No allocations or lock contention inside normal render blocks after warmup. Game commands cross a bounded queue and are applied at safe block boundaries.
- Final output conversion handles supported hardware sample types, clipping and interleaving.

## 6. Device backend contract

Conceptual interface (not necessarily the exact final C# signature):

```csharp
internal interface IAudioDevice : IDisposable
{
    AudioDeviceFormat Format { get; }
    void Start(AudioRenderCallback render);
    void Stop();
}

internal delegate void AudioRenderCallback(Span<float> interleavedOutput);
```

This is a conceptual callback contract; the actual implementation must respect callback lifetime, thread ownership and native ABI details. Backends may internally use event-driven loops or synchronous writes rather than a native callback.

The backend must report negotiated sample rate/channel format, provide a steady output cycle, handle partial writes/underruns where applicable, propagate failures safely and release every native resource. Use a `NullAudioDevice` for tests and offline rendering.

## 7. Nonfunctional requirements

1. **Deployment:** No third-party runtime NuGet packages or bundled unmanaged audio DLLs. Document the .NET runtime requirement and Linux ALSA prerequisite clearly.
2. **Reliability:** Never read beyond sample buffers. Malformed assets return controlled exceptions. A failed device initialization does not leak handles or leave a thread running.
3. **Latency:** Aim for responsive gameplay. A nominal 10–20 ms render block is a starting point, not a guarantee of end-to-end latency; measure the actual backend buffering.
4. **Performance:** Support at least 64 concurrent short clips on ordinary desktop hardware without render-thread allocations, subject to benchmark verification.
5. **Compatibility:** No compile-time platform-specific public types; desktop backend selection via `OperatingSystem.IsWindows/IsLinux/IsMacOS`. Android selection is planned in M6.
6. **Observability:** Provide non-realtime-safe diagnostics for startup failures and counts of underruns/voices without logging directly from the audio render path.

## 8. Milestones and acceptance criteria

### M0 — Offline vertical slice

- Repository builds with .NET 10; tests run without an audio device.
- WAV PCM16 mono/stereo decoding works, including unknown RIFF chunks and malformed input handling.
- A generated tone and two overlapping voices can be mixed and exported as a valid WAV for listening.
- Volume, looping, voice completion and stop are covered by tests.

### M1 — First live backend

- WASAPI shared-mode audio on a real Windows machine.
- A console sample loads a WAV, plays it repeatedly, changes volume and exits cleanly.
- Repeated opening/closing works; device initialization failures are intelligible.

### M2 — Three-platform playback

- Add and smoke-test ALSA on Linux and Audio Queue/CoreAudio on macOS.
- Document precise tested OS versions, architectures, hardware paths and Linux prerequisites.
- Build artifacts contain no third-party native binary payloads.

### M3 — Harden for games

- Introduce bounded, nonblocking command handling and generation-safe voice handles if not already present.
- Test simultaneous plays/stops from gameplay threads, exhaustion behavior, underrun recovery and device-unplug scenarios where feasible.
- Benchmark render allocations and throughput; document known device limitations.

### M4 — Common WAV sample rates

- Detect the source sample rate automatically from the WAV format chunk; callers do not need to specify it.
- Support every whole-number rate from 8 to 48 kHz inclusive, including 8, 11.025, 16, 22.05, 32, 44.1 and 48 kHz, for PCM16 mono and stereo WAV files.
- Convert once at load time to the 48 kHz mix format using linear interpolation, preserving playback speed and pitch within frame rounding. Round output duration to the nearest frame, with half-frame ties rounded up; interpolate from the final source sample toward the first for both one-shot and looping clips.
- Reject invalid or unsupported sample rates and output sizes with a clear error; rates below 8 kHz or above 48 kHz are outside this milestone.
- Add hardware-independent tests for each listed rate, covering detection, playback duration and pitch, mono/stereo ordering and looping boundaries. Preserve existing 48 kHz behavior.

### M5 — MP3 loading (planned)

- Add `AudioClip.LoadMp3` for paths and readable streams, and `AudioEngine.LoadMp3` for paths. Preserve existing `LoadWav` behavior and leave caller-owned streams open.
- Consider a separate generic `Load` API that detects supported file types from their content rather than their extension. If added, it must also work with non-seekable streams; its exact API remains to be decided.
- Support MP3 (MPEG Layer III) decoding for common constant- and variable-bitrate files, including files with and without metadata. Document the supported MPEG versions, sample rates, channel counts, and handling of encoder delay and padding; reject unsupported or malformed input with clear errors.
- Decode at load time into the existing 48 kHz in-memory `AudioClip` format. Playback, looping and mixing should reuse the existing engine without decoding on the audio thread. Streaming large assets is not part of this milestone.
- Preserve the deployment goals: no third-party runtime NuGet dependencies, bundled native binaries, or separately installed codecs. Evaluate the decoder approach and its maintenance cost; measure and document the DLL size change.
- Add hardware-independent tests using known decoded reference samples, including mono/stereo, bitrate and metadata cases, duration, looping, non-seekable streams, and malformed or truncated files. Verify existing WAV behavior remains unchanged.

### M6 — Android playback (planned)

- Add live playback on Android 8.0 / API 26 and later using the OS-provided AAudio C API. A consuming app needs a compatible .NET Android runtime, SDK and workload; their supported Android versions may be more restrictive. Report unsupported versions and initialization failures clearly, without silently selecting the null device or desktop ALSA backend.
- Select the Android backend in `AudioEngine.Create()` while preserving `CreateOffline()` and the existing platform-neutral public API, mixer and playback behavior. Keep the core `net10.0` target, with no third-party runtime NuGet dependencies, bundled native audio binaries or separate audio installation.
- Negotiate and verify the device stream format, converting the 48 kHz stereo mix as needed or reporting unsupported configurations. Handle short writes or callback buffering as appropriate, device disconnection and recovery, startup failures and safe shutdown. Preserve the audio-thread allocation and locking requirements; verify native callback and resource lifetimes against Android's API contract.
- The host app manages audio focus and activity lifecycle, including stopping voices or disposing and recreating the engine as appropriate. Document this contract and the available APIs; do not promise automatic ducking, pause/resume, background playback or preservation of stopped voice positions.
- Add hardware-independent tests for Android backend selection, format handling, partial output, disconnect and recovery, failures and disposal. Preserve existing desktop and offline behavior.
- Provide Android app integration and smoke-test instructions. On a real Android device, verify audible playback, overlapping voices, volume, looping, stop and disposal, repeated open/close, host lifecycle handling, and an output-route change or interruption where feasible. Record the device, OS/API version, architecture, .NET runtime, audio route, results and remaining gaps. Compilation or emulation alone does not establish device support.

## 9. Explicit risks and decisions to revisit

- **Linux portability:** ALSA is a system library, not guaranteed everywhere. Avoid promising literally zero Linux prerequisites; consider PipeWire native support only if real user feedback warrants it.
- **Sample rates:** Load-time linear interpolation supports integer WAV rates from 8 to 48 kHz; other rates remain unsupported. It increases clip memory and can attenuate high frequencies, so revisit quality and memory costs if needed.
- **Device format:** `float32` is an internal format; WASAPI/ALSA/macOS backends must negotiate and/or convert correctly.
- **Platform testing:** Code generation and cross-compilation are not equivalent to playback verification on real systems.
- **AI-generated implementation:** Every interop signature, buffer lifetime, callback convention and failure path must be checked against primary OS documentation and tested. Generated code is a draft until verified.

## 10. Definition of done for v0.1

An example app can load and play short WAV PCM16 sounds, overlap them, change volume, loop and stop, on Windows, Linux and macOS with a single public API. The project ships no third-party native audio binaries, its prerequisites are documented, automated offline tests pass, and real-device smoke tests have passed separately on all three platforms. Until then, mark unsupported or untested backends honestly.
