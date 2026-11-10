// Captures the engine's PlayerState::UpdatePing input before its averaging/clamping.
// Virtual dispatch and the original argument are preserved. No packets are sent.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "latency_shared.h"
#include <atomic>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

namespace {
using UpdatePing = void(*)(void*, float);
UpdatePing original = nullptr;
std::atomic<bool> running{false}, accepting{false};
std::atomic<unsigned> callbacks{0};
LatencyShared* shared = nullptr;
HANDLE mapping = nullptr;
std::vector<void**> patched;
SRWLOCK queueLock = SRWLOCK_INIT;
struct Sample {
    uint64_t sequence, tick, utcMs;
    int32_t playerId, objectIndex;
    uint8_t role;
    float rttMs;
    wchar_t name[65];
};
constexpr size_t QueueSize = 8192;
Sample queue[QueueSize];
size_t queueHead = 0, queueCount = 0;
std::atomic<uint64_t> sequence{0}, dropped{0}, invalid{0};

uint64_t UtcMs() {
    FILETIME time; GetSystemTimePreciseAsFileTime(&time);
    ULARGE_INTEGER value; value.LowPart = time.dwLowDateTime; value.HighPart = time.dwHighDateTime;
    return value.QuadPart / 10000 - 11644473600000ULL;
}

// SEH is confined to POD reads. Unexpected layouts never escape into game code.
bool ReadPlayer(void* player, Sample* sample) {
    __try {
        auto bytes = static_cast<unsigned char*>(player);
        sample->role = bytes[shared->roleOffset];
        sample->playerId = *reinterpret_cast<int32_t*>(bytes + shared->playerIdOffset);
        sample->objectIndex = *reinterpret_cast<int32_t*>(bytes + shared->objectIndexOffset);
        auto name = *reinterpret_cast<wchar_t**>(bytes + shared->nameOffset);
        int count = *reinterpret_cast<int*>(bytes + shared->nameOffset + sizeof(void*));
        if (sample->role < 1 || sample->role > 3 || sample->objectIndex < 0 || count < 0 || count > 1024) return false;
        int length = count > 0 ? (count - 1 < 64 ? count - 1 : 64) : 0;
        for (int i = 0; i < length; ++i) sample->name[i] = name[i];
        sample->name[length] = 0;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

void ObservePing(void* player, float seconds) {
    struct ActiveCall {
        ActiveCall() { ++callbacks; }
        ~ActiveCall() { --callbacks; }
    } activeCall;
    // This pointer remains valid for in-flight callbacks even during restoration.
    original(player, seconds);
    if (!accepting.load(std::memory_order_acquire)) return;
    Sample sample{};
    sample.sequence = ++sequence;
    sample.tick = GetTickCount64();
    sample.utcMs = UtcMs();
    sample.rttMs = seconds * 1000.0f;
    if (!std::isfinite(sample.rttMs) || sample.rttMs < 0 || !ReadPlayer(player, &sample)) { ++invalid; return; }
    if (!TryAcquireSRWLockExclusive(&queueLock)) { ++dropped; return; }
    if (queueCount < QueueSize) { queue[(queueHead + queueCount) % QueueSize] = sample; ++queueCount; }
    else ++dropped;
    ReleaseSRWLockExclusive(&queueLock);
}

std::string JsonName(const wchar_t* name) {
    int size = WideCharToMultiByte(CP_UTF8, 0, name, -1, nullptr, 0, nullptr, nullptr);
    std::string utf8(size > 0 ? size : 1, '\0');
    if (size > 0) WideCharToMultiByte(CP_UTF8, 0, name, -1, utf8.data(), size, nullptr, nullptr);
    std::ostringstream result;
    for (unsigned char ch : utf8) {
        if (!ch) break;
        if (ch == '"' || ch == '\\') result << '\\' << ch;
        else if (ch < 32) result << "\\u" << std::hex << std::setw(4) << std::setfill('0') << static_cast<int>(ch) << std::dec;
        else result << ch;
    }
    return result.str();
}

bool Patch(void** slot, void* expected, void* replacement) {
    DWORD old;
    if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &old)) return false;
    bool changed = InterlockedCompareExchangePointer(slot, replacement, expected) == expected;
    DWORD ignored; VirtualProtect(slot, sizeof(void*), old, &ignored);
    return changed;
}

size_t Restore() {
    accepting.store(false, std::memory_order_release);
    size_t restored = 0;
    for (auto slot : patched)
        if (Patch(slot, reinterpret_cast<void*>(ObservePing), reinterpret_cast<void*>(original))) ++restored;
    return restored;
}

bool ValidateImage() {
    auto base = reinterpret_cast<unsigned char*>(GetModuleHandleW(nullptr));
    auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    auto nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || nt->Signature != IMAGE_NT_SIGNATURE ||
        nt->FileHeader.TimeDateStamp != shared->timestamp || nt->OptionalHeader.SizeOfImage != shared->imageSize ||
        shared->imageSize < 4096 || shared->slotCount == 0 || shared->slotCount > 8 || shared->methodRva > shared->imageSize - 24 ||
        memcmp(base + shared->methodRva, shared->signature, 24) != 0) return false;
    if (shared->roleOffset > 4096 || shared->playerIdOffset > 4096 || shared->nameOffset > 4096 || shared->objectIndexOffset > 256) return false;
    auto method = reinterpret_cast<UpdatePing>(base + shared->methodRva);
    if (original && original != method) return false;
    if (!original) original = method;
    for (uint32_t i = 0; i < shared->slotCount; ++i) {
        auto rva = shared->slots[i];
        if (rva % sizeof(void*) || rva > shared->imageSize - sizeof(void*) ||
            *reinterpret_cast<void**>(base + rva) != reinterpret_cast<void*>(original)) return false;
    }
    return true;
}

DWORD WINAPI Record(void*) {
    const char* reason = "user_stop";
    std::ofstream log;
    try {
        std::filesystem::path folder(shared->folder);
        unsigned segment = 0;
        auto open = [&] {
            std::ostringstream name; name << "latency-" << std::setw(6) << std::setfill('0') << ++segment << ".jsonl";
            log.open(folder / name.str(), std::ios::out | std::ios::binary | std::ios::app);
            log.exceptions(std::ios::badbit | std::ios::failbit);
        };
        open();
        log << "{\"schema\":1,\"event\":\"source_start\",\"source\":\"ue_playerstate_update_ping\",\"unit\":\"ms\",\"utcMs\":" << UtcMs() << ",\"tickMs\":" << GetTickCount64() << "}\n";
        auto base = reinterpret_cast<unsigned char*>(GetModuleHandleW(nullptr));
        for (uint32_t i = 0; i < shared->slotCount; ++i) {
            auto slot = reinterpret_cast<void**>(base + shared->slots[i]);
            if (!Patch(slot, reinterpret_cast<void*>(original), reinterpret_cast<void*>(ObservePing))) throw std::runtime_error("virtual slot changed");
            patched.push_back(slot);
        }
        accepting.store(true, std::memory_order_release);
        InterlockedExchange(&shared->state, 1);
        std::vector<Sample> batch; batch.reserve(QueueSize);
        for (;;) {
            bool stop = shared->stop != 0;
            if (GetTickCount64() - static_cast<uint64_t>(shared->heartbeat) > 5000) { stop = true; reason = "heartbeat_expired"; }
            if (stop) {
                size_t restored = Restore();
                while (callbacks.load() != 0) Sleep(1);
                if (restored != patched.size()) reason = "restore_conflict";
            }
            AcquireSRWLockExclusive(&queueLock);
            for (size_t i = 0; i < queueCount; ++i) batch.push_back(queue[(queueHead + i) % QueueSize]);
            queueHead = (queueHead + queueCount) % QueueSize; queueCount = 0;
            ReleaseSRWLockExclusive(&queueLock);
            for (const auto& sample : batch) {
                log << "{\"schema\":1,\"event\":\"rtt\",\"sequence\":" << sample.sequence
                    << ",\"utcMs\":" << sample.utcMs << ",\"tickMs\":" << sample.tick
                    << ",\"peerId\":\"" << sample.playerId << '-' << sample.objectIndex
                    << "\",\"playerLabel\":\"" << JsonName(sample.name)
                    << "\",\"side\":\"" << (sample.role == 3 ? "host" : "client")
                    << "\",\"rttMs\":" << std::setprecision(9) << sample.rttMs << "}\n";
            }
            batch.clear();
            auto lost = dropped.exchange(0), bad = invalid.exchange(0);
            if (lost || bad) log << "{\"schema\":1,\"event\":\"gap\",\"tickMs\":" << GetTickCount64() << ",\"utcMs\":" << UtcMs() << ",\"dropped\":" << lost << ",\"invalid\":" << bad << "}\n";
            log.flush();
            if (stop) break;
            if (log.tellp() >= 16 * 1024 * 1024) { log.close(); log.clear(); open(); }
            Sleep(100);
        }
        log << "{\"schema\":1,\"event\":\"source_end\",\"tickMs\":" << GetTickCount64()
            << ",\"utcMs\":" << UtcMs() << ",\"reason\":\"" << reason << "\"}\n";
        log.flush();
        InterlockedExchange(&shared->state, 2);
    } catch (...) {
        Restore();
        while (callbacks.load() != 0) Sleep(1);
        InterlockedExchange(&shared->state, 3);
    }
    UnmapViewOfFile(shared); shared = nullptr;
    CloseHandle(mapping); mapping = nullptr;
    // Pinning keeps a previously fetched virtual function pointer callable after stop.
    running.store(false, std::memory_order_release);
    return 0;
}
}

extern "C" __declspec(dllexport) DWORD WINAPI RonLatencyStart(void*) {
    bool expected = false;
    if (!running.compare_exchange_strong(expected, true)) return ERROR_ALREADY_EXISTS;
    wchar_t name[80]; swprintf_s(name, L"Local\\RonLatency-%lu", GetCurrentProcessId());
    HANDLE nextMapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name);
    auto next = nextMapping ? static_cast<LatencyShared*>(MapViewOfFile(nextMapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(LatencyShared))) : nullptr;
    if (!next || next->magic != LATENCY_MAGIC || next->protocol != 1 || next->folder[1023] != 0) {
        if (next) UnmapViewOfFile(next);
        if (nextMapping) CloseHandle(nextMapping);
        running = false; return ERROR_INVALID_DATA;
    }
    if (shared) UnmapViewOfFile(shared);
    if (mapping) CloseHandle(mapping);
    shared = next; mapping = nextMapping;
    if (!ValidateImage()) { InterlockedExchange(&shared->state, 3); running = false; return ERROR_REVISION_MISMATCH; }
    patched.clear(); sequence = 0; dropped = 0; invalid = 0; queueHead = queueCount = 0;
    HMODULE owner;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&RonLatencyStart), &owner);
    auto thread = CreateThread(nullptr, 0, Record, nullptr, 0, nullptr);
    if (!thread) { running = false; return GetLastError(); }
    CloseHandle(thread);
    return ERROR_SUCCESS;
}
