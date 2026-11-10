#pragma once
#include <windows.h>
#include <cstdint>
constexpr uint32_t MAGIC = 0x524F4E57;
constexpr uint32_t PROTOCOL = 1;
struct Shared {
    uint32_t magic;            // 0
    uint32_t protocol;         // 4
    volatile LONG mode;       // 8: 0 pass through, 1 observe, 2 WG candidates
    uint32_t interfaceIndex;   // 12: immutable after Start
    volatile LONG64 heartbeat;// 16: GetTickCount64, refreshed by host
    volatile LONG stop;       // 24
    uint32_t reserved;         // 28
    wchar_t logPath[1024];     // 32
};
static_assert(offsetof(Shared, logPath) == 32, "IPC ABI");
