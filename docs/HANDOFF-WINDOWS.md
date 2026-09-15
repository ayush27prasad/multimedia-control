# Handoff: building and iterating on Windows

Read this first. It tells you what exists, what does not, and the order to attack it in.

## The situation

Every line in this repo was written on a macOS machine with **no Java, no Android SDK and
no .NET**. Nothing has ever been compiled. Your Windows box is the first and only build
environment for **both** apps.

Expect first-build errors — wrong overloads, a renamed API, a NuGet version that does not
exist. That is anticipated, not a sign the design is wrong. The architecture and the wire
protocol were researched carefully; the syntax was not verifiable.

Correctness is documented where it matters: `docs/DECISIONS.md` records what was
established by research, with citations. **If the code disagrees with DECISIONS.md, the
code is wrong.** Three PRD assumptions turned out to be unachievable — D5, D6 and D8 — and
those sections explain what replaced them and why.

## Status by component

| Component | Status | Risk |
|---|---|---|
| Wire protocol (`docs/PROTOCOL.md`) | Complete, both sides implemented against it | Low — the contract is explicit |
| Android app (18 Kotlin files) | Complete | Medium — CameraX/MediaCodec are fiddly |
| `PhoneBridge.Core` (protocol, transport) | Complete | Low — pure logic |
| `PhoneBridge.Adb` (Wi-Fi, tunnel, watcher) | Complete | **High — output parsing is unverified on MIUI 14** |
| `PhoneBridge.Media` — decode, virtual mic | Complete | Medium — NuGet/native deps |
| `PhoneBridge.Media` — vcam shared memory | Complete | Medium — ACLs are easy to get wrong |
| `PhoneBridge.App` (WPF) | Complete | Medium |
| **Native virtual camera DLL** | **NOT IMPLEMENTED — build plan only** | **Highest** |

The native media source is deliberately a plan rather than code. See
`windows/native/PhoneBridge.VirtualCam/README.md` for why and for the step-by-step. M1, M2
and M4 do not depend on it.

## Setup

```powershell
git clone <this repo>
cd multimedia-control
.\scripts\setup-windows.ps1
```

Installs the .NET 8 SDK, JDK 17, Android Studio, and downloads platform-tools into
`windows\tools\adb`. Open a **new** terminal afterwards so PATH changes take effect.

Then set `ANDROID_HOME` once Android Studio has finished its first-run SDK download:

```powershell
setx ANDROID_HOME "$env:LOCALAPPDATA\Android\Sdk"
```

**The Gradle wrapper JAR is not committed** (binary, could not be produced on macOS).
Generate it once:

```powershell
cd android
gradle wrapper --gradle-version 8.9    # or just open the folder in Android Studio
```

### Phone setup (Redmi Note 10 Pro, MIUI 14)

1. Settings → About phone → tap **MIUI version** 7 times.
2. Settings → Additional settings → Developer options → **USB debugging** on.
3. Plug in. Unlock the phone. Tap **Allow** on the RSA prompt (tick "always allow").
4. **MIUI-specific, and the most likely cause of mysterious failures:**
   - Security app → Autostart → enable **PhoneBridge**.
   - Settings → Apps → PhoneBridge → Battery saver → **No restrictions**.

   MIUI kills background services aggressively. Without both of these the control channel
   dies a minute or two after you switch away from the app, and it looks like a transport
   bug.
5. **"USB debugging (Security settings)"** — a separate MIUI toggle that requires signing
   in to a Mi account. Research says it gates `pm grant` and `settings put`, **not**
   `cmd wifi`, so you probably do not need it. If the spike reports DENIED on the Wi-Fi
   commands, this is the first thing to try.

## Do this first: the M0 spike

```powershell
.\scripts\spike-wifi.ps1 -IncludeMutating
```

This is the highest-value 30 seconds in the project. Everything in `WifiController.cs` is
written against AOSP 12 source, but your device is **Android 13 / MIUI 14** — a later
release and a heavily skinned ROM. The spike is authoritative; the docs are not.

It writes `wifi-capability-report.md` with the raw output of every command. Two things to
check:

- **The shipping surface is all OK** — `status`, `list-networks`, `list-scan-results`,
  `start-scan`. If any is DENIED, stop and re-read D5 before writing code.
- **The raw output format matches what the parsers expect.** The parsers in
  `WifiController.cs` are tolerant and every method preserves raw text, but if MIUI's
  column layout differs, fix the parsers against the real output rather than guessing.

`set-wifi-enabled` needs special attention: **exit code 0 is not proof of success.**
`setWifiEnabled` silently returns false when airplane mode is on or a hotspot is active.
The spike checks that the state actually moved, and `SetEnabledAndVerifyAsync` does the
same at runtime.

## Build and run

```powershell
# Desktop
cd windows
dotnet build PhoneBridge.sln
dotnet run --project src\PhoneBridge.App

# Phone
cd android
.\gradlew installDebug
```

The desktop app launches the phone app over ADB automatically, so the normal loop is:
rebuild desktop, run, watch the log pane.

Use the **Copy diagnostics** button before reporting anything — it captures parsed state
plus the raw ADB output, which is what you need for a Wi-Fi parsing bug.

## Milestone gates

Work these in order. Each is a real test, not a code review.

**M1 — control channel + Wi-Fi.** No media.
- Desktop shows Connected; the phone shows Connected.
- Toggle the radio on the desktop → the phone reflects it within ~1 s. Then the reverse.
- Saved networks and live signal strengths render on both.
- **Unplug the cable mid-session.** Both show disconnected. Replug → both resync with no
  restart of either app.

**M2 — camera preview.**
- Live video in the desktop preview pane, `fps` counter near 30.
- Switch rear/front from both apps.
- **Measure latency properly:** point the phone at a millisecond stopwatch on the PC
  screen and photograph phone-and-screen together. Expect 35–70 ms; the PRD's 200 ms is a
  ceiling, not a target. If you see 200 ms+, suspect frame threading in the decoder
  (`FFmpegH264Decoder` must use `FF_THREAD.Slice`, never Frame).
- Autofocus should converge once and hold. If it hunts, `lockFocus()` is failing — check
  logcat.

**M3 — virtual camera.** Only after the native DLL exists.
- Appears in **all** of: Windows Camera app (MF-only — this is the one a DirectShow filter
  would fail), OBS, Chrome `getUserMedia`, Zoom.
- **Test Teams with a real remote participant, early.** There is a known unresolved bug
  where local preview works and remote participants see black. It is the one reported
  failure that could invalidate the approach, and finding it here is far cheaper than at
  M5.

**M4 — microphone.**
- `.\scripts\check-vbcable.ps1` reports both endpoints.
- "CABLE Output" selectable as a mic in Zoom; record and play back.
- Mute from either app → remote hears silence, and unmute is instant (we send silence
  rather than stopping the stream, precisely so it is).

## Known failures: symptom → cause

Ordered by how much time each will cost you if you do not know it.

| Symptom | Cause |
|---|---|
| Random disconnects, no pattern | adb server version war. We use port **5137** deliberately (D9). If you changed it to 5037, Android Studio's adb will fight ours. |
| Phone never connects, desktop waits forever | The phone app is not running. Under `adb reverse` the **phone dials out**, so nothing happens until it is foregrounded. The desktop tries `am start`; check it succeeded. |
| Connects then dies a minute later | MIUI killed the service. Autostart + battery "No restrictions". |
| Virtual camera appears but is black | Shared-memory ACL. The reader is **Local Service**; check the SDDL and the `Global\` prefix in `SharedMemoryFrameWriter`. This is the single most common vcam failure. |
| `E_ACCESSDENIED` starting the vcam | DLL file permissions, not signing. Local Service cannot read your user profile. Install to `%ProgramData%\PhoneBridge\` and `regsvr32` from there. |
| `REGDB_E_CLASSNOTREG` | Not registered, or registered under HKCU. Must be HKLM, which needs elevation. |
| Virtual mic silent | VB-Cable format mismatch. Set CABLE Input to 48000 Hz 16-bit stereo in Sound → Playback → Properties → Advanced. |
| No virtual mic device at all | VB-Cable not installed. There is no way around this — Windows has no user-mode API to create an audio capture endpoint (D8). |
| Wi-Fi toggle reports success, nothing happens | Airplane mode or an active hotspot. `SetEnabledAndVerifyAsync` catches this and reports it. |
| `SecurityException` on a `cmd wifi` call | Outside the `NON_PRIVILEGED_COMMANDS` allowlist, or MIUI's "USB debugging (Security settings)". |
| Video decodes but latency is ~200 ms+ | Frame threading in FFmpeg. Must be slice-only. |
| `InvalidCastException` starting the vcam | The `IMFVirtualCamera` IID and method order in `VirtualCameraHost.cs` could **not** be verified on the authoring machine. Take both from `mfvirtualcamera.h` in the Windows 11 SDK. Method order matters as much as the IID — COM dispatches by vtable slot, so a mis-ordered declaration calls the wrong function silently. |
| Stream desyncs and never recovers | The framing has no resync marker by design. Any length mismatch tears the channel down; reconnect rebuilds it. If it recurs, suspect an unsynchronised write on the Android side — `FrameSink` serialises for exactly this reason. |

## Design invariants — do not "simplify" these

Each of these looks like an over-complication and is not:

- **`FrameSink` locks around every write.** MediaCodec's callback thread and the control
  coroutine can both reach a sink. A torn frame header desynchronises the stream
  permanently.
- **Neither side applies optimistic local state.** Commands are sent, and the
  authoritative echo is rendered. This is what makes the two UIs structurally unable to
  disagree (D10, PROTOCOL §4.2).
- **`_suppressEvents` in the WPF code-behind.** Without it, every state echo fires a
  command back at the peer and the apps ping-pong.
- **The Android app has no `INTERNET` permission.** Its absence is a structural guarantee
  that the build cannot move media off the cable.
- **The seqlock in the vcam ring buffer.** Reader and writer share no lock and live in
  different security contexts. Odd = writing, even = settled.

## If you need to change the protocol

Change `docs/PROTOCOL.md` first, bump `PROTOCOL_VERSION` in both `Protocol.kt` and
`Wire.cs`, then update both implementations. A version mismatch is handled deliberately:
the phone treats it as terminal, stops retrying, and says so in the UI — rather than
reconnecting forever against an incompatible build.
