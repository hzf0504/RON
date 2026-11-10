#pragma once
#include <windows.h>
#include <cstdint>
#include <cstddef>

constexpr uint32_t LATENCY_MAGIC = 0x4c4e4f52;
struct LatencyShared {
    uint32_t magic, protocol;
    volatile LONG64 heartbeat;
    volatile LONG stop, state; // 0 starting, 1 recording, 2 stopped, 3 failed
    uint32_t methodRva, slotCount, slots[8];
    uint32_t roleOffset, playerIdOffset, nameOffset, objectIndexOffset;
    uint32_t timestamp, imageSize;
    unsigned char signature[24];
    wchar_t folder[1024];
};
static_assert(offsetof(LatencyShared, folder) == 112, "Latency IPC ABI");
