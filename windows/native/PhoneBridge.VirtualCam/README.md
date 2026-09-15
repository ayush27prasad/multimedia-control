# PhoneBridge.VirtualCam — native media source (M3)

**Status: NOT IMPLEMENTED. This is the one component deliberately left as a build plan
rather than blind-written code.**

Everything else in this repo was written on a machine with no Windows SDK, no .NET and no
Android SDK, and can be compiled and corrected on first build. A Media Foundation media
source is different: it is ~1500 lines of COM implementing `IMFMediaSource`,
`IMFMediaStream`, `IMFMediaEventGenerator`, `IMFGetService`, `IMFMediaSourceEx` and the KS
property handlers, all against a contract Microsoft documents incompletely. Blind-writing
it would produce something that looks finished, compiles after a fight, and fails in
`svchost.exe` where it cannot be debugged. A precise plan is more useful than plausible
code.

The C# side of the boundary **is** complete and testable: `SharedMemoryFrameWriter.cs`
(the writer) and `VirtualCameraHost.cs` (registration). The contract between them and this
DLL is fully specified in [`docs/VCAM-SHM.md`](../../../docs/VCAM-SHM.md).

## Build plan

**Start from `smourier/VCamSample`** (<https://github.com/smourier/VCamSample>), not from
scratch and not from the Microsoft sample. It is C++, minimal, and already solves the
registration and ETW-logging problems. The Microsoft sample
(`microsoft/Windows-Camera/Samples/VirtualCamera`) is more complete but also much larger,
and its three modes (synthetic / wrapper / augmented) are all distractions here.

1. Clone VCamSample and confirm its unmodified sample camera appears in the Windows Camera
   app. **Do not write any of our code until that works** — it validates the toolchain,
   the registration path and the machine's camera stack in one step.
2. Replace its frame generator (which draws synthetic frames) with a reader for our ring
   buffer, per `docs/VCAM-SHM.md`. This is the only substantial change: open the two
   `Global\` sections read-only, validate magic and version, take the newest settled slot
   via the seqlock, and copy it into the MF sample.
3. Change the CLSID to `{D9BCA1C4-7E4F-4A1B-9F3E-5B2A7C6D8E10}` to match
   `VirtualCameraHost.MediaSourceClsid`. **These two must agree** — a mismatch gives a
   camera that appears in the list and then fails to start.
4. Negotiate NV12 as well as BGRA32 if it is cheap to do so. Some consumers only request
   NV12, and a source that offers RGB32 alone will be enumerated but unusable by them.

## The failures that will actually happen

Ordered by how likely they are to cost you an afternoon.

- **Black camera, no error anywhere.** The section ACL. The reader is Local Service; if
  the SDDL in `SharedMemoryFrameWriter` is wrong or the `Global\` prefix is missing, the
  reader silently sees nothing. Verify with Process Explorer by finding the section handle
  under the `svchost.exe` hosting Frame Server.
- **`E_ACCESSDENIED` on `Start()`.** File ACLs on the DLL, not signing. Local Service and
  Local System cannot read `C:\Users\<you>\source\repos\...`. Install to
  `%ProgramData%\PhoneBridge\` and `regsvr32` **from there**.
- **`REGDB_E_CLASSNOTREG` (0x80040154).** Not registered, or registered under HKCU. It
  must be HKLM — the DLL is CoCreated by a system service in another account, so a
  per-user registration is invisible to it. Registration therefore needs elevation.
- **Mark of the Web.** A DLL extracted from a downloaded zip that was never unblocked
  fails to load with no useful diagnostic.
- **Camera missing entirely on 22H2+.** Some machines need
  `HKLM\SOFTWARE\Microsoft\Windows Media Foundation\Platform\EnableFrameServerMode = 1`
  (and the WOW6432Node mirror). This one bit OBS too.
- **Teams: local preview works, remote participants see black.** Known, unresolved, and
  reported against the public samples. **Test this early** — it is the one failure that
  could invalidate the whole approach, and finding it at M3 is much cheaper than at M5.

## Debugging

`OutputDebugString` is useless here — the DLL is loaded into at least four processes, none
of them yours. VCamSample uses ETW for exactly this reason; keep its provider and use
`tracelog`/WPA, or PerfView.

## If this proves too costly

Fall back to a **DirectShow filter** (UnityCapture is the template). Also unsigned, also
`regsvr32`, and considerably simpler. The cost is real though: DirectShow filters are
invisible to every WinRT/Media-Foundation-only consumer, including the Windows Camera app
and Qt6 apps. OBS, Zoom and (usually) Chromium-based apps would still see it.

M1, M2 and M4 do not depend on this component at all.
