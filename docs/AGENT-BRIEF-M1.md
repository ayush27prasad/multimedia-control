# Agent brief — M1: control channel + Wi-Fi over USB

First working session on the Windows machine. **Scope is M1 only.**

## Goal

Get the desktop app and the phone app talking over the USB cable, with Wi-Fi state
mirrored in both directions. No video, no audio, no virtual devices.

When this is done the project is de-risked: the transport, the handshake, the control
protocol, the mirrored-state model and the reconnect logic are all proven. Everything
after M1 is media riding on a channel that already works.

## Before you start

Read `CLAUDE.md` in the repo root, then `docs/HANDOFF-WINDOWS.md`.

**Nothing here has ever been compiled.** Expect first-build errors — wrong overloads, a
moved API, an unresolvable NuGet version. Fix them. They are not evidence the design is
wrong. When code and `docs/DECISIONS.md` disagree, the code is wrong.

## Order of work

### 1. Toolchain

```powershell
.\scripts\setup-windows.ps1
```

Then open a **new** terminal. Set `ANDROID_HOME` once Android Studio finishes its first-run
SDK download. Generate the Gradle wrapper (the JAR is not committed — it is binary and
could not be produced on the authoring machine):

```powershell
cd android
gradle wrapper --gradle-version 8.9
```

### 2. Phone setup

Developer options → USB debugging. Plug in, unlock, accept the RSA prompt.

**Then the two MIUI settings that cause the most confusing failures:**
- Security app → Autostart → enable PhoneBridge
- Settings → Apps → PhoneBridge → Battery saver → **No restrictions**

Without both, the control channel dies a minute or two after you leave the app and it looks
exactly like a transport bug.

### 3. Run the spike — do this before writing or debugging any Wi-Fi code

```powershell
.\scripts\spike-wifi.ps1 -IncludeMutating
```

This is the highest-value step in the session. `WifiController.cs` was written against
**AOSP 12** source; this device is **Android 13 / MIUI 14** — a later release and a heavily
skinned ROM. The spike is authoritative and the docs are not.

Two things to check in `wifi-capability-report.md`:

- **All four shipping commands report OK**: `status`, `list-networks`, `list-scan-results`,
  `start-scan`. If any is DENIED, re-read `DECISIONS.md` D5 and try MIUI's
  "USB debugging (Security settings)" toggle before changing code.
- **The raw output matches what the parsers expect.** If MIUI's columns differ, fix the
  parsers against the real text. Every `WifiController` method preserves raw output for
  this reason. Do not guess at formats.

Note: `set-wifi-enabled` returning exit 0 is **not** proof it worked — it silently fails
under airplane mode or an active hotspot. The spike verifies the state actually moved, and
`SetEnabledAndVerifyAsync` does the same at runtime. Keep that behaviour.

### 4. Build both apps, fix until they run

```powershell
cd windows;  dotnet build PhoneBridge.sln
cd android;  .\gradlew installDebug
```

Keep fixes minimal and local. If a fix seems to require changing the protocol or removing
an invariant listed in `CLAUDE.md`, stop and say so rather than doing it.

### 5. Drive the gates below until they pass

## Definition of done

All five, verified by running them — not by reading the code:

1. **Link establishes.** Desktop shows Connected; phone shows Connected. The desktop
   launches the phone app itself over ADB.
2. **Wi-Fi state renders on both.** Saved networks, current SSID, signal strength.
3. **Desktop → phone.** Toggle the radio on the desktop; the phone reflects it within ~1 s.
4. **Phone → desktop.** Request the toggle from the phone; the desktop executes it over ADB
   and both converge on the result.
5. **Reconnect.** Unplug the cable mid-session — both show disconnected. Replug — both
   resync with **no restart of either app**.

## Explicitly out of scope

Do not start any of these, even if M1 finishes early:

- Camera or audio streaming (M2, M4)
- The native virtual camera DLL (M3 — a separate project, see
  `windows/native/PhoneBridge.VirtualCam/README.md`)
- Wi-Fi *connect* — deliberately absent, see `DECISIONS.md` D5
- Programmatic tethering — not possible, see D6
- UI polish beyond what is needed to verify the gates

## What to report back

1. **The spike result** — paste `wifi-capability-report.md`, or at least the summary table.
   This is the single most useful artifact for whoever picks up M2.
2. **Every fix you made**, grouped as: (a) trivial syntax/API corrections, (b) genuine
   design problems found. Group (b) is what matters — if the design was wrong somewhere,
   say where and why.
3. **Gate results, honestly.** Which of the five pass. If one fails, the symptom and the
   output, not a guess at the cause.
4. **Anything in `DECISIONS.md` that turned out to be wrong on real hardware.** That
   document drives every later milestone, so a correction there is worth more than a fix.

## If you get stuck

`docs/HANDOFF-WINDOWS.md` has a symptom → cause table. The three that cost the most time:

- **Phone never connects, desktop waits forever** — the phone app is not running. Under
  `adb reverse` the phone dials out, so nothing happens until it is foregrounded.
- **Connects, then dies about a minute later** — MIUI killed the service. Step 2 above.
- **Random disconnects with no pattern** — adb server version war. We use port 5137
  deliberately; if something changed it to 5037, Android Studio's adb will fight ours.
