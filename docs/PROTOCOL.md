# PhoneBridge Wire Protocol v1

The shared contract. Android and Windows are implemented independently against this
document; change it here first, bump `PROTOCOL_VERSION`, and update both sides.

All multi-byte integers are **big-endian**. All strings are UTF-8.

---

## 1. Transport

The PC creates an ADB **reverse** tunnel:

```
adb reverse localabstract:phonebridge tcp:<localPort>
```

**Socket roles under reverse are the opposite of the intuitive reading.** `adb reverse`
makes **adbd on the device** the listener on `localabstract:phonebridge`. The Android app
is therefore a **client**: it calls `LocalSocket.connect(LocalSocketAddress("phonebridge",
ABSTRACT))`. The PC is the listener, on `127.0.0.1:<localPort>`.

The phone dialling out is the point: with `adb forward`, the PC's `connect()` succeeds
whenever the forward rule exists even if nothing is listening on the device, so a forward
tunnel cannot signal readiness. Reverse gives a genuine connect event.

Fallback, for devices where `adb reverse` fails:

```
adb forward tcp:<localPort> localabstract:phonebridge
```

Under **forward** the roles swap back: the Android app becomes the **server**
(`LocalServerSocket("phonebridge")`) and the PC connects. In this mode the PC MUST treat
"socket connected" as meaningless until the handshake completes, under a 3000 ms timeout.

Both modes use the same abstract-namespace socket name and the same handshake, so only
the four lines that create the socket differ. See `TunnelMode` in
`transport/BridgeTransport.kt` and `AdbTunnel.cs`.

Never a TCP listener on the device: the abstract namespace needs no `INTERNET`
permission, cannot collide with another app's port, and never interacts with VPN or
firewall apps. A normal (non-shell, non-root) app is permitted to use it — AOSP sepolicy
carries `allow adbd appdomain:unix_stream_socket connectto`.

**Stale tunnels** are prevented by the PC removing its own reverse/forward rules at
startup (`adb reverse --remove-all`) before creating fresh ones, so there is no session
id on the wire. Session identity is established by `hello` on the control channel.

### 1.1 Channels

The phone opens **three separate connections** to the same socket name. Each declares its
channel in the handshake.

| Id | Channel | Direction | Payload |
|----|---------|-----------|---------|
| 1 | video | phone → PC | H.264 Annex-B, framed (§3) |
| 2 | audio | phone → PC | PCM S16LE, framed (§3) |
| 3 | control | bidirectional | NDJSON (§4) |

Separate connections rather than one multiplexed stream: head-of-line blocking between
audio and video becomes structurally impossible, and any one channel can be torn down and
re-established without disturbing the others — stopping the camera leaves the control
channel up.

### 1.2 Handshake

Sent by the phone immediately on connect, 10 bytes:

```
offset size  field
0      4     magic     "PBRG" (0x50 0x42 0x52 0x47)
4      1     version   PROTOCOL_VERSION = 1
5      1     channel   1=video, 2=audio, 3=control
6      4     reserved  zero
```

PC replies, 6 bytes:

```
0      4     magic     "PBOK", or "PBNO" on rejection
4      1     version   PROTOCOL_VERSION
5      1     status    0=ok, 1=bad magic, 2=version mismatch, 3=channel already open
```

On any non-zero status both sides close the connection; the phone retries with backoff
(§5). A version mismatch is terminal — the phone stops retrying and surfaces it in the UI
rather than reconnecting forever against an incompatible desktop build.

## 2. Stream headers

Sent once per media connection, immediately after the handshake, before any frames.

**Video** (16 bytes):
```
0   4   "PBV1"
4   4   codec fourcc   "h264" (0x68323634)
8   4   width
12  4   height
```

**Audio** (20 bytes):
```
0   4   "PBA1"
4   4   format         1 = PCM_S16LE
8   4   sample rate    48000
12  4   channels       1 or 2
16  4   bits per sample 16
```

---

## 3. Frame framing

Borrowed from scrcpy, which has had this validated across a very large device matrix.
Every media packet on channels 1 and 2 carries a 12-byte header:

```
byte 0   bit 7    1 = media packet, 0 = session packet
         bit 6    config packet (SPS/PPS; video only)
         bit 5    keyframe (video only)
         bits 4-0 PTS bits 60..56
bytes 1-7         PTS bits 55..0        (61-bit PTS, nanoseconds)
bytes 8-11        payload length (uint32)
```

Payload follows immediately: Annex-B H.264 (start codes `00 00 00 01`) for video, raw
interleaved PCM for audio.

**Session packet** (video only, byte 0 bit 7 = 0) — sent on rotation or resolution change:

```
byte 0    0x00
bytes 1-3 reserved
bytes 4-7 width
bytes 8-11 height
```

The decoder must flush and reconfigure on a session packet.

The encoder sets `KEY_PREPEND_HEADER_TO_SYNC_FRAMES`, so SPS/PPS ride along with every
IDR. A PC that connects or recovers mid-stream can therefore start decoding at the next
keyframe with no out-of-band configuration.

---

## 4. Control channel (NDJSON)

One JSON object per line, `\n`-terminated, UTF-8, bidirectional. Max line 256 KiB.

### 4.1 Envelope

```json
{ "t": "<type>", "seq": 42, "origin": "phone"|"pc", "ts": 1694600000000, "p": { } }
```

`seq` is per-origin and monotonic. `ts` is epoch milliseconds from the sender's clock —
used only for display and staleness checks, never for ordering.

### 4.2 Authority model

This resolves PRD Open Question #3 (simultaneous conflicting commands) structurally
rather than with a debounce.

| State slice | Authority | Why |
|---|---|---|
| `wifi` | **PC** | Only the PC can run `cmd wifi` via ADB. The phone app literally cannot read saved networks or toggle the radio. |
| `device` (camera, mic, tether) | **Phone** | The phone owns the hardware. |

**A side that issues a command never updates its own state optimistically.** It sends a
`cmd` and renders the authoritative `state` that comes back. Two simultaneous conflicting
commands therefore resolve as last-write-wins *at the authority*, with no split-brain and
no possibility of the two UIs disagreeing. Between send and echo the originating UI shows
a pending affordance.

The authority broadcasts a full snapshot of its slice on every change, plus a heartbeat
every 1000 ms. Reconnect resynchronises by construction — there is no replay log to
reconcile.

### 4.3 Message types

**`hello`** — first message on the control channel, both directions.
```json
{ "t":"hello", "p": { "role":"phone", "app":"1.0.0", "protocol":1,
                      "device":"Redmi Note 10 Pro", "android":"13",
                      "caps":["video","audio","wifi.observe","tether.deeplink"] } }
```

**`state`** — authority → peer. Full snapshot of one slice.
```json
{ "t":"state", "p": { "scope":"wifi", "wifi": { … } } }
{ "t":"state", "p": { "scope":"device", "camera": { … }, "mic": { … }, "tether": { … } } }
```

`wifi` slice (PC-authored):
```json
{ "enabled": true, "connected": true, "ssid": "home-5g", "bssid": "aa:bb:…",
  "rssi": -52, "linkSpeedMbps": 433, "frequencyMhz": 5180, "networkId": 3,
  "savedNetworks": [ { "networkId":3, "ssid":"home-5g", "security":"wpa2" } ],
  "scanResults":  [ { "ssid":"home-5g", "rssi":-52, "security":"wpa2", "frequencyMhz":5180 } ],
  "lastError": null, "lastUpdated": 1694600000000 }
```

`device` slice (phone-authored):
```json
{ "camera": { "streaming":false, "facing":"back", "width":1920, "height":1080,
              "fps":30, "bitrateKbps":12000, "available":["back","front"] },
  "mic":    { "streaming":false, "muted":false, "source":"unprocessed",
              "sampleRate":48000, "channels":2 },
  "tether": { "active":false, "canDeepLink":true } }
```

**`cmd`** — peer → authority.

| Command | Target | Params |
|---|---|---|
| `wifi.setEnabled` | PC | `{"enabled":bool}` |
| `wifi.refresh` | PC | `{}` — re-run `list-networks` + `status` |
| `wifi.scan` | PC | `{}` — `start-scan`, then poll `list-scan-results` |
| `camera.setStreaming` | phone | `{"on":bool}` |
| `camera.setFacing` | phone | `{"facing":"front"\|"back"}` |
| `camera.setFormat` | phone | `{"width":int,"height":int,"fps":int,"bitrateKbps":int}` |
| `mic.setStreaming` | phone | `{"on":bool}` |
| `mic.setMuted` | phone | `{"muted":bool}` |
| `mic.setSource` | phone | `{"source":"unprocessed"\|"voice_recognition"\|"mic"\|"camcorder"}` |
| `tether.openSettings` | phone | `{}` — deep-links into MIUI tethering settings |

```json
{ "t":"cmd", "seq":7, "origin":"pc", "p": { "cmd":"wifi.setEnabled",
                                            "args": { "enabled": true } } }
```

**`ack`** — authority → originator, referencing the command's `seq`.
```json
{ "t":"ack", "p": { "ackSeq":7, "ok":false, "error":"airplane mode is on" } }
```
An `ack` is an acknowledgement of *execution*, not of the resulting state. The state
change arrives separately as a `state` message. `ok:true` with no state change is
legitimate (e.g. setting a value it already had).

**`ping` / `pong`** — liveness. `{ "t":"ping", "p":{"nonce":"…"} }`. The peer echoes the
nonce in a `pong`. Three missed pings (3 s) marks the link dead.

### 4.4 Explicitly NOT in the protocol

- **`wifi.connect`** — deliberately absent. `cmd wifi connect-network` requires the
  passphrase on every call and cannot activate a network by saved `networkId`; there is
  no `select-network`/`enable-network` in `cmd wifi`. Connecting is done by the user
  tapping the network on the phone. See `docs/DECISIONS.md` D5.
- **`tether.setEnabled`** — deliberately absent. `TETHER_PRIVILEGED` is signature-level
  and no shell command exposes it. See D6.

---

## 5. Reconnection (FR12)

The phone owns retry, because it is the dialling side.

- Backoff: 250 ms, 500 ms, 1 s, 2 s, 4 s, then steady 4 s, with ±20% jitter.
- The control channel reconnects independently of the media channels. Media channels are
  re-established only if the corresponding stream was active.
- On control-channel reconnect both sides re-send `hello`, then each authority immediately
  broadcasts a full `state` snapshot. No delta replay.
- The PC tears down and recreates the ADB tunnel on device detach/attach, calling
  `adb reverse --remove-all` first. Clearing the rule is what makes a stale session fail
  fast: without a tunnel the phone's `connect()` is refused immediately, rather than
  half-attaching to a dead listener.
