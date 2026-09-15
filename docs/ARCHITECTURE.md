# Architecture

A map of the code. For *why* it is shaped this way see [`DECISIONS.md`](DECISIONS.md); for
the wire format see [`PROTOCOL.md`](PROTOCOL.md).

## Layout

```
android/app/src/main/java/com/superbryn/phonebridge/
  transport/   Protocol.kt          wire constants, frame encoding
               BridgeTransport.kt   socket roles per tunnel mode, reconnect backoff
  control/     Messages.kt          serializable envelope + payloads
               ControlChannel.kt    NDJSON read loop, heartbeat
  state/       Models.kt            shared state models
               StateStore.kt        single source of truth, StateFlow
  media/       VideoEncoder.kt      MediaCodec H.264, hardware encoder selection
               CameraController.kt  CameraX → encoder Surface, focus lock
               AudioCapture.kt      AudioRecord → PCM
  wifi/        WifiObserver.kt      what the app CAN read (scan results, current SSID)
               TetherHelper.kt      deep link + interface detection
  service/     BridgeService.kt     orchestrator: link, media, command execution
  ui/          Theme/Components/ControlPanel/MainActivity

windows/src/
  PhoneBridge.Core/     Protocol/Wire.cs, Models.cs · Transport/BridgeListener.cs,
                        ControlChannel.cs, FrameReader.cs
  PhoneBridge.Adb/      AdbClient.cs, WifiController.cs, AdbTunnel.cs, DeviceWatcher.cs
  PhoneBridge.Media/    Video/FFmpegH264Decoder.cs · Audio/VirtualMicWriter.cs
                        VirtualCam/SharedMemoryFrameWriter.cs, VirtualCameraHost.cs
  PhoneBridge.App/      BridgeSession.cs (orchestrator) · Views/ · Theme/

windows/native/PhoneBridge.VirtualCam/   build plan + ring buffer reader (see README)
```

## The two orchestrators

`BridgeService.kt` and `BridgeSession.cs` are counterparts. Each owns its side's hardware,
executes the commands only it can execute, and mirrors the other's state.

```
                    phone authority              PC authority
                  ┌─────────────────┐         ┌─────────────────┐
  camera, mic,    │  BridgeService  │  NDJSON │  BridgeSession  │  wifi enable,
  tether deeplink │    (Android)    │◀───────▶│    (Windows)    │  list, scan, status
                  └─────────────────┘         └─────────────────┘
```

A command targeting the other side is forwarded, never applied locally. The authority
executes it and broadcasts a full snapshot of its slice; the peer renders that. This is
what makes simultaneous conflicting commands resolve deterministically with no debounce
and no split-brain — see PROTOCOL §4.2.

## Media path

```
sensor → CameraX Preview → MediaCodec input Surface → H.264 Annex-B
      → 12-byte frame header → LocalSocket → adbd → USB → adb server
      → TCP loopback → FrameReader → FFmpeg (LOW_DELAY, slice threading)
      → BGRA → ├─ WriteableBitmap (preview)
               └─ Global\ shared memory → svchost (Local Service) → Frame Server → apps
```

Audio is the same path minus the codec: `AudioRecord` → PCM S16LE → same framing → WASAPI
render into VB-Cable's "CABLE Input" → surfaces as "CABLE Output" to every app.

## Threading

**Android.** The accept/connect loop and the control read loop run on `Dispatchers.IO`.
MediaCodec output arrives on its own callback thread and writes directly to the socket —
`FrameSink` serialises those writes because the control coroutine shares the connection.
CameraX binding must happen on the main thread, hence the `withContext(Dispatchers.Main)`
around `camera.start`.

**Windows.** The listener, control loop, and each media loop are independent tasks. Decoded
frames marshal to the UI thread at `DispatcherPriority.Render` so a burst cannot starve
input — dropping a preview frame is always preferable to a frozen window.

## Failure model

- **Framing has no resync marker.** Any length mismatch or oversized control line tears the
  channel down; the reconnect path rebuilds it. Recovering in place is not possible, so it
  is not attempted.
- **The phone owns retry**, because it is the dialling side. Backoff is 250 ms → 4 s with
  jitter, so three channels reconnecting after a cable jostle do not synchronise.
- **Authorities broadcast full snapshots, never deltas.** Reconnect resynchronises by
  construction; there is no replay log to reconcile.
- **A version mismatch is terminal.** The phone stops retrying and says so, rather than
  reconnecting forever against an incompatible desktop build.
