# PhoneBridge — rules for agents working in this repo

Android phone as a Windows PC's webcam, microphone and Wi-Fi control surface, **over USB
only**. Target: Windows 11 + Redmi Note 10 Pro (Android 13 / MIUI 14).

## Read this before touching anything

**Nothing in this repo has ever been compiled.** It was authored on macOS with no Java, no
Android SDK and no .NET. Build errors on first run are expected and are not evidence the
design is wrong.

Read in this order:
1. `docs/HANDOFF-WINDOWS.md` — setup, build, milestone gates, failure catalogue
2. `docs/DECISIONS.md` — why the architecture is what it is, with citations
3. `docs/PROTOCOL.md` — the wire contract both apps implement

**`DECISIONS.md` outranks the code.** If they disagree, the code is wrong. It records what
was established by research against AOSP source, Microsoft Learn and shipping
implementations — not preferences.

## Three PRD requirements were found unachievable. Do not try to "restore" them.

The PRD (`prd.md`) is the original brief and is **superseded** on these three points.
Each was researched, each has citations in `DECISIONS.md`, and each was signed off by the
project owner. Re-implementing them wastes a session and reintroduces a dead end.

- **D5 — no Wi-Fi connect.** `cmd wifi connect-network` requires the passphrase on every
  call and cannot activate a saved network by id; there is no `select-network` or
  `enable-network` in `cmd wifi` on Android 11/12/13. We ship enumerate + status + scan +
  radio toggle. The user taps the network on the phone. A credential vault and a Shizuku
  tier were both considered and rejected.
- **D6 — no programmatic tethering.** `TETHER_PRIVILEGED` is signature-level and no shell
  command exposes it. `svc usb setFunctions rndis` would reconfigure the USB gadget and
  likely drop the ADB link the whole transport rides on. We deep-link to settings.
- **D8 — no self-contained virtual microphone.** Windows has no user-mode way to create an
  audio capture endpoint. VB-Cable is a required dependency.

## Invariants — do not "simplify" these

Each looks like over-engineering and is not. If one seems redundant, read the comment above
it before removing it.

- **`FrameSink` locks every write** (Android). MediaCodec's callback thread and the control
  coroutine share a connection. A torn frame header desynchronises the stream permanently —
  the framing has no resync marker by design.
- **Neither side applies optimistic local state.** A side that issues a command renders the
  authoritative echo, never its own guess. This is what makes the two UIs structurally
  unable to disagree, and it resolves simultaneous conflicting commands with no debounce.
  See PROTOCOL §4.2.
- **`_suppressEvents` in the WPF code-behind.** Without it every state echo fires a command
  back at the peer and the apps ping-pong.
- **No `INTERNET` permission in the Android manifest.** Its absence is a structural
  guarantee that the build cannot move media off the cable. Never add it.
- **The seqlock in the vcam ring buffer.** Reader and writer share no lock and run in
  different security contexts. Odd = writing, even = settled.
- **adb runs on port 5137, not 5037.** On the default port our bundled adb and the user's
  Android Studio kill each other's servers, surfacing as random disconnects.
- **Socket roles are inverted under `adb reverse`.** adbd is the listener; the phone app is
  the *client*. This is counterintuitive and was gotten wrong once already.

## Build

```powershell
cd windows;  dotnet build PhoneBridge.sln;  dotnet run --project src\PhoneBridge.App
cd android;  .\gradlew installDebug
```

The desktop app launches the phone app over ADB, so the loop is: rebuild desktop, run,
watch the log pane. Use **Copy diagnostics** in the UI before reporting a Wi-Fi problem —
it captures parsed state plus raw ADB output.

## Changing the protocol

Change `docs/PROTOCOL.md` first, bump `PROTOCOL_VERSION` in **both** `Protocol.kt` and
`Wire.cs`, then update both implementations. A mismatch is handled deliberately: the phone
treats it as terminal and says so, rather than reconnecting forever.

## Working style here

- Report what actually happened. If a gate fails, say so with the output — do not describe
  a milestone as met because the code looks right.
- Stay in the assigned milestone. M3 (native virtual camera) is a separate project with its
  own README; do not start it while fixing M1.
- Prefer fixing the parser against real device output over guessing at formats. Raw output
  is preserved on every `WifiController` method for exactly this.
