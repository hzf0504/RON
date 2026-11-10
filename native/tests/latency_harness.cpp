#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "../latency_shared.h"
#include <atomic>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <limits>
#include <string>

struct Player {
    int32_t index, id;
    uint8_t role;
    wchar_t* name;
    int count, capacity;
};
std::atomic<int> calls{0};
float lastArgument = 0;
__declspec(noinline) void Original(Player*, float seconds) {
    ++calls;
    lastArgument = seconds;
    MemoryBarrier();
}
using Ping = void(*)(Player*, float);
Ping volatile dispatch[] = { Original };

int wmain(int argc, wchar_t** argv) {
    if (argc != 3) return 2;
    std::filesystem::path folder(argv[2]);
    std::filesystem::create_directories(folder);
    std::ofstream metadata(folder / "latency-session.json");
    metadata << "{\"Schema\":1,\"SessionId\":\"native-offline-fixture\",\"Pid\":0,\"StartedUtcMs\":1794000000000,\"Source\":\"offline_fixture\",\"Unit\":\"ms\",\"GameSha256\":\"fixture\",\"OfflineFixture\":true}";
    metadata.close();
    int checks = 0, failures = 0;
    auto check = [&](bool value, const char* label) { ++checks; if (!value) ++failures; std::cout << (value ? "PASS " : "FAIL ") << label << '\n'; };
    wchar_t mapName[80]; swprintf_s(mapName, L"Local\\RonLatency-%lu", GetCurrentProcessId());
    HANDLE map = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, 4096, mapName);
    auto shared = static_cast<LatencyShared*>(MapViewOfFile(map, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(LatencyShared)));
    if (!shared) return 3;
    auto base = reinterpret_cast<unsigned char*>(GetModuleHandleW(nullptr));
    auto nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + reinterpret_cast<IMAGE_DOS_HEADER*>(base)->e_lfanew);
    shared->magic = LATENCY_MAGIC; shared->protocol = 1;
    shared->methodRva = static_cast<uint32_t>(reinterpret_cast<unsigned char*>(Original) - base);
    shared->slotCount = 1;
    shared->slots[0] = static_cast<uint32_t>(reinterpret_cast<unsigned char*>(const_cast<Ping*>(dispatch)) - base);
    shared->roleOffset = offsetof(Player, role); shared->playerIdOffset = offsetof(Player, id);
    shared->nameOffset = offsetof(Player, name); shared->objectIndexOffset = offsetof(Player, index);
    shared->timestamp = nt->FileHeader.TimeDateStamp; shared->imageSize = nt->OptionalHeader.SizeOfImage;
    memcpy(shared->signature, reinterpret_cast<void*>(Original), 24);
    wcscpy_s(shared->folder, folder.c_str());
    shared->heartbeat = GetTickCount64();
    HMODULE module = LoadLibraryW(argv[1]);
    auto start = module ? reinterpret_cast<DWORD(WINAPI*)(void*)>(GetProcAddress(module, "RonLatencyStart")) : nullptr;
    if (!start) return 4;
    shared->signature[0] ^= 1;
    check(start(nullptr) == ERROR_REVISION_MISMATCH && dispatch[0] == Original, "unknown method signature is rejected before patching");
    shared->signature[0] ^= 1;
    shared->state = 0;
    check(start(nullptr) == ERROR_SUCCESS, "real latency module starts against fixture virtual dispatch");
    for (int i = 0; i < 100 && shared->state == 0; ++i) Sleep(10);
    check(shared->state == 1 && dispatch[0] != Original, "virtual slot is attached before samples are sent");
    wchar_t first[] = L"Fixture Alice", second[] = L"Fixture Bob";
    Player a{ 101, 11, 3, first, 14, 14 }, b{ 102, 12, 3, second, 12, 12 }, client{ 103, 13, 1, first, 14, 14 };
    dispatch[0](&a, 0.020f); dispatch[0](&a, 1.700f); dispatch[0](&b, 0.035f); dispatch[0](&client, 0.050f);
    check(calls == 4 && lastArgument == 0.050f, "every original call and argument are preserved");
    dispatch[0](&a, std::numeric_limits<float>::quiet_NaN());
    check(calls == 5 && std::isnan(lastArgument), "invalid telemetry still reaches the original engine method");
    shared->stop = 1;
    for (int i = 0; i < 100 && shared->state < 2; ++i) Sleep(10);
    check(shared->state == 2 && dispatch[0] == Original, "explicit stop restores original dispatch and flushes samples");
    std::ifstream input(folder / "latency-000001.jsonl");
    std::string content((std::istreambuf_iterator<char>(input)), {});
    size_t samples = 0, at = 0;
    while ((at = content.find("\"event\":\"rtt\"", at)) != std::string::npos) { ++samples; ++at; }
    check(samples == 4 && content.find("1700") != std::string::npos, "raw samples and the pre-clamping spike are saved");
    check(content.find("\"invalid\":1") != std::string::npos && content.find("\"side\":\"client\"") != std::string::npos,
        "invalid samples are explicit and client/host perspectives remain distinct");
    auto nextFolder = folder / "watchdog"; std::filesystem::create_directories(nextFolder);
    wcscpy_s(shared->folder, nextFolder.c_str()); shared->stop = 0; shared->state = 0; shared->heartbeat = GetTickCount64();
    DWORD restarted = ERROR_ALREADY_EXISTS;
    for (int i = 0; i < 100 && restarted == ERROR_ALREADY_EXISTS; ++i) { Sleep(10); restarted = start(nullptr); }
    check(restarted == ERROR_SUCCESS, "collector can restart in the same process after a clean stop");
    for (int i = 0; i < 100 && shared->state == 0; ++i) Sleep(10);
    shared->heartbeat = GetTickCount64() - 6000;
    for (int i = 0; i < 100 && shared->state < 2; ++i) Sleep(10);
    check(shared->state == 2 && dispatch[0] == Original, "missing helper heartbeat restores original dispatch");
    std::ifstream watchdog(nextFolder / "latency-000001.jsonl");
    std::string watchdogText((std::istreambuf_iterator<char>(watchdog)), {});
    check(watchdogText.find("heartbeat_expired") != std::string::npos, "watchdog stop reason is saved");
    UnmapViewOfFile(shared); CloseHandle(map);
    std::cout << "Checks: " << checks << "; failures: " << failures << "; synthetic engine calls only, no game or network.\n";
    return failures ? 1 : 0;
}
