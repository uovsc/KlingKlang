# AGENTS.md — Project instructions for AI coding agents

## Mission

Build a small, dependable, cross-platform audio **playback** library for .NET games, written in C# and easy to distribute. Read `SPEC.md` before designing or changing behavior. Favor a boring, maintainable solution over a feature-rich audio engine.

## Non-negotiable goals

1. One public, platform-neutral C# API for Windows, Linux and macOS.
2. No third-party native binaries bundled with the library or required as a separate application install. Access built-in operating-system audio APIs using .NET interop. On Linux, system-provided ALSA (`libasound`) is an explicitly documented prerequisite; do not pretend it is universally present.
3. No third-party runtime NuGet dependencies in the core package. The .NET runtime and operating-system components are allowed.
4. Prioritize simple deployment, predictable playback, stability and clear errors. Low latency matters, but is not pursued at the expense of reliability.
5. Keep version 0.1 deliberately small: WAV PCM playback, multiple concurrent sounds, volume, stop, loop and clean disposal. No recording, MIDI, MP3, FLAC, 3D audio or effect graph yet.

## Working style

- Work in small, independently testable steps. Before substantial changes, state the intended change and how it will be verified.
- Inspect the existing repository before editing; preserve unrelated work. Do not assume the repository is empty.
- Prefer idiomatic C#, nullable reference types, clear ownership and `IDisposable` for native handles and audio devices.
- Keep backend-specific interop internal. Public APIs must not expose native pointers, platform types or implementation details.
- Do not introduce dependencies, change the target framework, broaden scope or replace the architecture without explaining the trade-off and getting approval.
- Do not claim that a backend works on an OS unless it has been exercised on that OS. Report untested platforms explicitly.
- Do not commit, publish, package or release unless asked. Never silently run system-wide installation commands.
- Update tests and documentation whenever behavior or public API changes.
- On completion, report changed files, tests run, actual results and known gaps.

## Proposed solution layout

```text
src/
  KlingKlang/              Public API, WAV loader, mixer, playback engine
  KlingKlang.Platforms/    Internal Windows/macOS/Linux device backends
tests/
  KlingKlang.Tests/        Decoder, mixer, voice-state, resource-lifetime tests
samples/
  KlingKlang.Demo/         Minimal console demonstration
AGENTS.md
SPEC.md
```

This is a suggested layout, not a requirement to create every project immediately. Start with a minimal solution and extract projects only when separation adds value.

## Implementation strategy

1. Define public types (`AudioEngine`, `AudioClip`, `VoiceHandle`) and an internal device abstraction. Keep the API small.
2. Implement a strict, bounds-checked RIFF/WAVE decoder for uncompressed PCM 16-bit mono/stereo. Normalize decoded samples to interleaved `float` in `[-1, 1]`.
3. Implement a deterministic, allocation-free-in-steady-state software mixer with independent playback cursors, volumes, loops and stops. Start with a `NullAudioDevice` that permits offline tests.
4. Implement one real OS backend end to end and verify it on its actual OS; then add the other two through the same device abstraction. Windows: WASAPI shared mode; macOS: Audio Queue Services or a justified CoreAudio API; Linux: ALSA PCM using the system `libasound`.
5. Handle format negotiation, device loss, underruns, shutdown and repeated start/stop. Document limitations instead of hiding them.

## Audio-thread rules

- No filesystem I/O, blocking game-thread locks, logging, unmanaged handle construction, or per-block heap allocations inside the normal render path.
- Preallocate device buffers, voices and command storage where practical; use bounded communication between game and audio threads.
- Validate inputs on the caller's side. Define behavior for full voice pools and full command queues; never block the render callback waiting for gameplay code.
- Never call user callbacks under internal locks or from a real-time audio callback unless the API explicitly documents that contract.
- Support short device writes, format conversion and recoverable stream errors where required by the backend.
- Use `float` for the internal mixer; clamp or limit only at the output conversion boundary, and test clipping behavior.

## Quality gates

- `dotnet build` succeeds with warnings treated as errors for maintained projects.
- `dotnet test` passes for deterministic tests without requiring physical audio hardware.
- Cover: truncated/malformed WAV, unsupported format, mono/stereo ordering, gain, simultaneous voices, looping boundaries, stops, voice exhaustion and disposal.
- Verify no steady-state allocation in the render/mix method with an appropriate benchmark or allocation test after warmup.
- Run a manual smoke test on each OS before declaring that OS supported. A CI compile is not a playback test.
- Examples should work from a clean checkout using only the documented .NET SDK and available OS audio services.

## Decision rules

- If a choice makes installation simpler but makes playback marginally less flexible, prefer simple installation.
- If a platform limitation appears, isolate it in the backend and document the exact prerequisite or fallback.
- Prefer explicit `NotSupportedException` and informative errors to silent loss of sound, except where an opt-in null device is intentionally selected.
- Favor minimal APIs and readable code over premature micro-optimizations; profile before changing algorithms.
- Record meaningful architectural choices in a short `docs/decisions/` note once that directory exists.

## First task for an AI agent

Create a minimal .NET solution with the public API skeleton, an internal `NullAudioDevice`, a PCM16 WAV decoder, basic mixer tests and a console sample that renders a generated tone to an output WAV file. Do **not** claim that live speaker playback is implemented at this stage. Get this milestone passing before adding OS interop.
