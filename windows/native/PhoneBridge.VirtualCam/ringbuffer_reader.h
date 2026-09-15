// Reference implementation of the READER half of docs/VCAM-SHM.md.
//
// Drop this into the VCamSample-derived media source and call ReadNewestFrame() from
// where the sample currently synthesises a frame. This is the only part of the shared
// memory contract worth handing over as code: it is small, it is exact, and the seqlock
// is easy to get subtly wrong.
//
// Everything else the media source needs (IMFMediaSource and friends) comes from
// VCamSample. See README.md.

#pragma once
#include <windows.h>
#include <cstdint>

namespace phonebridge {

constexpr uint32_t kMagic = 0x50425643;   // "PBVC"
constexpr uint32_t kLayoutVersion = 1;
constexpr int      kSlotHeaderSize = 64;

#pragma pack(push, 1)
struct ControlBlock {
    uint32_t magic;
    uint32_t version;
    uint32_t width;
    uint32_t height;
    uint32_t fps;
    uint32_t pixelFormat;     // 1 = BGRA32
    uint32_t slotCount;
    uint32_t _pad0;
    uint64_t slotStride;
    uint64_t writeIndex;
    uint64_t heartbeatTicks;  // DateTime.UtcNow.Ticks
};
#pragma pack(pop)

class RingBufferReader {
public:
    bool Open(const wchar_t* instance = L"default") {
        Close();

        wchar_t ctlName[128], frameName[128];
        swprintf_s(ctlName,   L"Global\\PhoneBridgeVCam_%s_ctl", instance);
        swprintf_s(frameName, L"Global\\PhoneBridgeVCam_%s_frames", instance);

        ctlHandle_ = OpenFileMappingW(FILE_MAP_READ, FALSE, ctlName);
        if (!ctlHandle_) return false;

        control_ = (const ControlBlock*)MapViewOfFile(ctlHandle_, FILE_MAP_READ, 0, 0, 4096);
        if (!control_) { Close(); return false; }

        if (control_->magic != kMagic || control_->version != kLayoutVersion) {
            Close();
            return false;
        }

        frameHandle_ = OpenFileMappingW(FILE_MAP_READ, FALSE, frameName);
        if (!frameHandle_) { Close(); return false; }

        const size_t total = (size_t)control_->slotStride * control_->slotCount;
        frames_ = (const uint8_t*)MapViewOfFile(frameHandle_, FILE_MAP_READ, 0, 0, total);
        if (!frames_) { Close(); return false; }

        width_ = control_->width;
        height_ = control_->height;
        return true;
    }

    // True if the writer has published recently. Distinguishes "no new frames" from
    // "writer died", so the source can show a placeholder rather than freezing forever.
    bool IsWriterAlive(int64_t staleAfterMs = 2000) const {
        if (!control_) return false;
        FILETIME ft; GetSystemTimeAsFileTime(&ft);
        // FILETIME and .NET ticks share a 100ns unit but differ in epoch by 504911232e9.
        const int64_t nowTicks =
            ((int64_t)ft.dwHighDateTime << 32 | ft.dwLowDateTime) + 504911232000000000LL;
        return (nowTicks - (int64_t)control_->heartbeatTicks) / 10000 < staleAfterMs;
    }

    // Copies the newest settled frame into dest. Returns false if nothing is readable.
    //
    // Deliberately takes only the NEWEST slot and discards the rest: for a live camera,
    // showing the freshest frame is correct and queueing would only add latency.
    bool ReadNewestFrame(uint8_t* dest, size_t destSize, int64_t* ptsNanos = nullptr) {
        if (!control_ || !frames_) return false;

        // Geometry can change under us; the writer re-creates the sections, so re-open.
        if (control_->width != width_ || control_->height != height_) return false;

        const size_t frameBytes = (size_t)width_ * height_ * 4;
        if (destSize < frameBytes) return false;

        const uint64_t index = control_->writeIndex;
        if (index == 0) return false;

        // Walk back from newest. A slot mid-write is skipped rather than waited on.
        for (uint32_t back = 0; back < control_->slotCount; ++back) {
            if (index <= back) break;
            const uint32_t slot = (uint32_t)((index - 1 - back) % control_->slotCount);
            const uint8_t* slotPtr = frames_ + (size_t)slot * control_->slotStride;

            const volatile uint64_t* seqPtr = (const volatile uint64_t*)slotPtr;
            const uint64_t before = *seqPtr;
            if (before & 1) continue;               // write in progress

            const int64_t pts = *(const int64_t*)(slotPtr + 8);
            memcpy(dest, slotPtr + kSlotHeaderSize, frameBytes);

            MemoryBarrier();
            if (*seqPtr != before) continue;        // torn during the copy

            if (ptsNanos) *ptsNanos = pts;
            return true;
        }
        return false;
    }

    uint32_t Width()  const { return width_; }
    uint32_t Height() const { return height_; }

    void Close() {
        if (frames_)      { UnmapViewOfFile(frames_);  frames_ = nullptr; }
        if (control_)     { UnmapViewOfFile(control_); control_ = nullptr; }
        if (frameHandle_) { CloseHandle(frameHandle_); frameHandle_ = nullptr; }
        if (ctlHandle_)   { CloseHandle(ctlHandle_);   ctlHandle_ = nullptr; }
    }

    ~RingBufferReader() { Close(); }

private:
    HANDLE ctlHandle_ = nullptr;
    HANDLE frameHandle_ = nullptr;
    const ControlBlock* control_ = nullptr;
    const uint8_t* frames_ = nullptr;
    uint32_t width_ = 0, height_ = 0;
};

}  // namespace phonebridge
