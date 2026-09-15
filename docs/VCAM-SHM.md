# Virtual camera shared-memory contract

The contract between the C# writer (`SharedMemoryFrameWriter.cs`, runs as the interactive
user) and the C++ media source (`windows/native/PhoneBridge.VirtualCam`, runs as
**LOCAL SERVICE inside svchost.exe**).

Microsoft documents no frame-injection mechanism for `MFCreateVirtualCamera` — there is no
`IMFVirtualCamera::WriteFrame`. Every shipping implementation invents this layer. Ours
follows the pattern VL.Video.VirtualCamera documents.

## Named objects

```
Global\PhoneBridgeVCam_<instance>_ctl       4096 bytes
Global\PhoneBridgeVCam_<instance>_frames    slotStride * 3
```

`<instance>` is `default` unless overridden. The `Global\` prefix is **mandatory** — a
`Local\` name resolves per-session, and the reader is in session 0 while the writer is in
the interactive session.

## Security descriptor

```
D:(A;;GR;;;LS)(A;;GR;;;SY)(A;;GA;;;BA)(A;;GA;;;CO)
```

Local Service read, Local System read, Administrators full, creator-owner full.

**`LS` is the one that matters.** Frame Server streams under Local Service. Omit it and
the section is invisible to the reader — the symptom is a black camera with no error
logged anywhere. If the camera is black, check this before anything else.

## Control block (4096 bytes, little-endian)

| Offset | Type | Field |
|---|---|---|
| 0 | u32 | magic `0x50425643` ("PBVC") |
| 4 | u32 | layout version (currently 1) |
| 8 | u32 | width |
| 12 | u32 | height |
| 16 | u32 | fps |
| 20 | u32 | pixel format (1 = BGRA32) |
| 24 | u32 | slot count (3) |
| 32 | u64 | slot stride in bytes |
| 40 | u64 | write index — total frames published |
| 48 | u64 | writer heartbeat, `DateTime.UtcNow.Ticks` |

The reader validates magic and version before trusting anything else, and treats a
heartbeat older than ~2 s as "writer gone" — that is what distinguishes *no new frames*
from *writer died*, so the source can show a placeholder instead of freezing on the last
frame forever.

## Frame slots

Three slots at `slotStride = 64 + width*height*4`:

| Offset | Type | Field |
|---|---|---|
| 0 | u64 | sequence (seqlock) |
| 8 | i64 | PTS, nanoseconds |
| 16..63 | — | padding to a cache line |
| 64.. | bytes | BGRA32, tightly packed, stride = width*4 |

## Seqlock

Reader and writer share no lock, so each slot carries a sequence number: **odd while a
write is in progress, even when settled.**

Writer: `seq |= 1` → barrier → write payload → barrier → `seq = (seq|1)+1`.

Reader:

```c
uint64_t before = slot->seq;
if (before & 1) continue;            // write in progress, try another slot
memcpy(dest, slot->pixels, size);
uint64_t after = slot->seq;
if (before != after) continue;       // torn during the copy, retry
```

The reader takes the **newest settled slot**, computed from the control block's write
index — never a queue drain. Showing the freshest frame and discarding the rest is
correct for a live camera; queueing would only add latency.

## Changing geometry

The writer closes and re-creates both sections. The reader must detect the size change
(width/height in the control block) and re-open its view. Consequently the reader must
re-validate the control block on every frame, not cache it once at startup.
