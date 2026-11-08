#pragma once
#include <windows.h>
#include <stdint.h>
#include <stddef.h>

namespace yuki {
constexpr uint32_t Magic = 0x594B5044;
constexpr uint32_t Version = 1;
constexpr uint32_t Rate = 48000;
constexpr uint32_t Capacity = 131072;
constexpr size_t HeaderBytes = 4096;
struct alignas(64) Header {
    uint32_t magic, version, capacity, sampleRate;
    volatile LONG generation, active, writerPid, reserved;
    volatile LONG64 publishedFrames, startQpc, qpcFrequency, heartbeatMs;
    float gain;
    uint32_t reserved2;
    volatile LONG64 lastProcessMs, processCalls, injectedFrames;
    volatile LONG activeInstances, lastError, channels, deviceRate;
    uint8_t padding[HeaderBytes - 112];
};
static_assert(sizeof(Header) == HeaderBytes, "Protocol header size");
static_assert(offsetof(Header, lastProcessMs) == 72, "Protocol telemetry offset");
struct Shared { Header header; float samples[Capacity * 2]; };
inline LONG64 Read64(volatile LONG64* value) { return InterlockedCompareExchange64(value, 0, 0); }
inline LONG Read32(volatile LONG* value) { return InterlockedCompareExchange(value, 0, 0); }
}
