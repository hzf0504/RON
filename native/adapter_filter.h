#pragma once
#include <winsock2.h>
#include <iphlpapi.h>
#include <cstring>

// Select one real interface, preserving all pointers into the caller-owned GAA
// buffer. Never fabricate an address, gateway, MAC or interface type.
inline bool SelectAdapter(IP_ADAPTER_ADDRESSES* head, ULONG index) {
    for (auto* p = head; p; p = p->Next) {
        if (p->IfIndex != index || p->OperStatus != IfOperStatusUp || !p->FirstUnicastAddress) continue;
        // The caller expects the head at its buffer start. A shallow struct copy
        // is valid: variable-sized child records remain at their original offsets.
        if (p != head) std::memmove(head, p, p->Length < sizeof(*head) ? p->Length : sizeof(*head));
        head->Next = nullptr;
        return true;
    }
    return false;
}
