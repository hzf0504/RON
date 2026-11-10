// RON WG experimental native candidate selector and metadata-only observer.
// No export replacement, inline instruction patch, payload logging or new transport.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <iphlpapi.h>
#include <delayimp.h>
#include <atomic>
#include <vector>
#include <map>
#include <string>
#include <fstream>
#include <sstream>
#include <mutex>
#include <filesystem>
#include <algorithm>
#include "shared.h"
#include "adapter_filter.h"
#pragma comment(lib,"ws2_32.lib")
#pragma comment(lib,"iphlpapi.lib")

namespace {
Shared* shared = nullptr; // Kept mapped until process exit: an in-flight hook may still read it.
HANDLE mapping = nullptr;
std::atomic<bool> started{false}, enabled{false};
std::atomic<uint64_t> eosSend{0}, eosSendOk{0}, eosRecv{0}, eosBytes{0};
std::atomic<uint64_t> eosPolls{0}, eosReceiveCalls{0};
std::atomic<uint64_t> enumCalls{0}, enumFiltered{0}, enumMissing{0}, droppedMetadata{0};
std::atomic<uint32_t> patchCount{0};
using SendPacket = int(*)(void*, const void*);
using ReceivePacket = int(*)(void*, const void*, void*, void*, uint8_t*, void*, uint32_t*);
using NextSize = int(*)(void*, const void*, uint32_t*);
SendPacket realSendPacket = nullptr;
ReceivePacket realReceivePacket = nullptr;
NextSize realNextSize = nullptr;
auto realGaa = &GetAdaptersAddresses;
auto realSendTo = &sendto;
auto realRecvFrom = &recvfrom;
auto realSend = &send;
auto realRecv = &recv;
struct Patch { void** slot; void* original; void* replacement; };
std::vector<Patch> patches;
std::mutex flowsMutex;
struct Flow { std::string direction, local, remote; uint64_t packets=0, bytes=0; uint32_t routeIndex=0; uint64_t routeAt=0; };
std::map<std::string, Flow> flows;
std::mutex adaptersMutex;
std::string adaptersView;
uint64_t utcMs() {
    FILETIME ft;GetSystemTimePreciseAsFileTime(&ft);ULARGE_INTEGER time{};time.LowPart=ft.dwLowDateTime;time.HighPart=ft.dwHighDateTime;
    return (time.QuadPart-116444736000000000ULL)/10000;
}
void saveAdapters(IP_ADAPTER_ADDRESSES* head,ULONG family,ULONG flags,uint32_t originalCount,bool filtered) {
    std::ostringstream s;
    s<<"\"family\":"<<family<<",\"flags\":"<<flags<<",\"originalCount\":"<<originalCount<<",\"filterApplied\":"<<(filtered?"true":"false")<<",\"visible\":[";
    bool first=true;
    for(auto* p=head;p;p=p->Next){
        if(!first)s<<',';first=false;
        s<<"{\"index\":"<<p->IfIndex<<",\"ifType\":"<<p->IfType<<",\"status\":"<<p->OperStatus<<",\"ipv4\":[";
        bool firstIp=true;
        for(auto* u=p->FirstUnicastAddress;u;u=u->Next){
            if(!u->Address.lpSockaddr||u->Address.lpSockaddr->sa_family!=AF_INET)continue;
            auto* a=reinterpret_cast<sockaddr_in*>(u->Address.lpSockaddr);char ip[INET_ADDRSTRLEN]{};
            if(inet_ntop(AF_INET,&a->sin_addr,ip,sizeof(ip))){if(!firstIp)s<<',';firstIp=false;s<<'"'<<ip<<'"';}
        }
        s<<"]}";
    }
    s<<']';std::unique_lock<std::mutex> lock(adaptersMutex,std::try_to_lock);
    if(lock.owns_lock())adaptersView=s.str();else ++droppedMetadata;
}

bool alive() {
    return enabled.load(std::memory_order_relaxed) && shared &&
        !shared->stop && GetTickCount64() - static_cast<uint64_t>(InterlockedCompareExchange64(&shared->heartbeat, 0, 0)) < 5000;
}
std::string address(const sockaddr* a) {
    char text[INET6_ADDRSTRLEN]{}; uint16_t port=0;
    if (!a) return "unknown";
    if (a->sa_family == AF_INET) {
        const auto* v = reinterpret_cast<const sockaddr_in*>(a);
        inet_ntop(AF_INET, &v->sin_addr, text, sizeof(text)); port=ntohs(v->sin_port);
    } else if (a->sa_family == AF_INET6) {
        const auto* v = reinterpret_cast<const sockaddr_in6*>(a);
        inet_ntop(AF_INET6, &v->sin6_addr, text, sizeof(text)); port=ntohs(v->sin6_port);
    } else return "unsupported";
    return std::string(text) + ":" + std::to_string(port);
}
void record(SOCKET s, const sockaddr* remote, int remoteLength, int bytes, bool outgoing) {
    if (!alive() || bytes < 0 || !remote || remoteLength < sizeof(sockaddr_in)) return;
    if (remote->sa_family != AF_INET && remote->sa_family != AF_INET6) return;
    if (remote->sa_family == AF_INET6 && remoteLength < sizeof(sockaddr_in6)) return;
    sockaddr_storage local{}; int len=sizeof(local);
    if (getsockname(s, reinterpret_cast<sockaddr*>(&local), &len) != 0) return;
    std::unique_lock<std::mutex> lock(flowsMutex, std::try_to_lock);
    if (!lock.owns_lock()) { ++droppedMetadata; return; }
    auto l=address(reinterpret_cast<sockaddr*>(&local)), r=address(remote);
    auto key=std::to_string(static_cast<uint64_t>(s)) + (outgoing ? "tx" : "rx") + l + "-" + r;
    if (flows.size() >= 2048 && flows.find(key)==flows.end()) { ++droppedMetadata; return; }
    auto& f=flows[key]; f.local=l; f.remote=r; f.direction=outgoing?"tx":"rx";
    ++f.packets; f.bytes+=bytes;
    if (GetTickCount64()-f.routeAt > 1000 || !f.routeAt) {
        SOCKADDR_INET dest{}, src{}; MIB_IPFORWARD_ROW2 route{}; SOCKADDR_INET best{};
        memcpy(&dest, remote, remote->sa_family==AF_INET ? sizeof(sockaddr_in) : sizeof(sockaddr_in6));
        memcpy(&src, &local, local.ss_family==AF_INET ? sizeof(sockaddr_in) : sizeof(sockaddr_in6));
        // Route is an OS prediction at observation time, NOT packet capture.
        bool wildcard=(src.si_family==AF_INET && src.Ipv4.sin_addr.s_addr==INADDR_ANY) ||
            (src.si_family==AF_INET6 && IN6_IS_ADDR_UNSPECIFIED(&src.Ipv6.sin6_addr));
        auto rc=GetBestRoute2(nullptr, 0, wildcard?nullptr:&src, &dest, 0, &route, &best);
        f.routeIndex=rc==NO_ERROR ? route.InterfaceIndex : 0; f.routeAt=GetTickCount64();
    }
}
ULONG WINAPI hookGaa(ULONG family, ULONG flags, PVOID reserved, PIP_ADAPTER_ADDRESSES addresses, PULONG size) {
    auto result=realGaa(family,flags,reserved,addresses,size);
    auto error=GetLastError();
    if (alive()) {
        ++enumCalls;
        uint32_t originalCount=0;bool filtered=false;
        if(result==NO_ERROR)for(auto* p=addresses;p;p=p->Next)++originalCount;
        if (result==NO_ERROR && addresses && shared->mode==2) {
            if (SelectAdapter(addresses,shared->interfaceIndex)) {++enumFiltered;filtered=true;}
            else ++enumMissing; // Preserve original result if WG disappears; report failure.
        }
        if(result==NO_ERROR){try{saveAdapters(addresses,family,flags,originalCount,filtered);}catch(...){++droppedMetadata;}}
    }
    SetLastError(error); return result;
}
int hookSendPacket(void* handle, const void* options) {
    int result=realSendPacket(handle,options);
    if (alive()) { ++eosSend; if (result==0) ++eosSendOk; }
    return result;
}
int hookReceivePacket(void* handle, const void* options, void* peer, void* socket, uint8_t* channel, void* data, uint32_t* bytes) {
    if (alive()) ++eosReceiveCalls;
    int result=realReceivePacket(handle,options,peer,socket,channel,data,bytes);
    if (alive() && result==0) { ++eosRecv; if(bytes) eosBytes+=*bytes; }
    return result;
}
int hookNextSize(void* handle, const void* options, uint32_t* bytes) {
    if (alive()) ++eosPolls;
    return realNextSize(handle,options,bytes);
}
int WSAAPI hookSendTo(SOCKET s, const char* buf, int len, int flags, const sockaddr* to, int tolen) {
    int r=realSendTo(s,buf,len,flags,to,tolen), e=WSAGetLastError();
    if(r>=0){try{record(s,to,tolen,r,true);}catch(...){++droppedMetadata;}} WSASetLastError(e); return r;
}
int WSAAPI hookRecvFrom(SOCKET s, char* buf, int len, int flags, sockaddr* from, int* fromlen) {
    int r=realRecvFrom(s,buf,len,flags,from,fromlen), e=WSAGetLastError();
    if(r>=0 && fromlen){try{record(s,from,*fromlen,r,false);}catch(...){++droppedMetadata;}} WSASetLastError(e); return r;
}
void recordConnected(SOCKET s,int bytes,bool outgoing) {
    int type=0, n=sizeof(type); sockaddr_storage a{}; int len=sizeof(a);
    if(getsockopt(s,SOL_SOCKET,SO_TYPE,reinterpret_cast<char*>(&type),&n)==0 && type==SOCK_DGRAM &&
       getpeername(s,reinterpret_cast<sockaddr*>(&a),&len)==0) record(s,reinterpret_cast<sockaddr*>(&a),len,bytes,outgoing);
}
int WSAAPI hookSend(SOCKET s,const char* b,int len,int flags) {
    int r=realSend(s,b,len,flags), e=WSAGetLastError();
    if(r>=0){try{recordConnected(s,r,true);}catch(...){++droppedMetadata;}} WSASetLastError(e); return r;
}
int WSAAPI hookRecv(SOCKET s,char* b,int len,int flags) {
    int r=realRecv(s,b,len,flags), e=WSAGetLastError();
    if(r>=0){try{recordConnected(s,r,false);}catch(...){++droppedMetadata;}} WSASetLastError(e); return r;
}
bool exchange(void** slot,void* expected,void* replacement) {
    DWORD old=0;
    if(!VirtualProtect(slot,sizeof(void*),PAGE_READWRITE,&old)) return false;
    void* got=InterlockedCompareExchangePointer(slot,replacement,expected);
    DWORD ignored; VirtualProtect(slot,sizeof(void*),old,&ignored);
    return got==expected;
}
void patch(void** slot,void* expected,void* replacement) {
    if(*slot==replacement) return;
    if(exchange(slot,expected,replacement)) {
        auto found=std::find_if(patches.begin(),patches.end(),[&](const Patch& p){return p.slot==slot;});
        if(found==patches.end()) patches.push_back({slot,expected,replacement});
        patchCount=static_cast<uint32_t>(patches.size());
    }
}
struct Target { const char* name; void* original; void* replacement; };
void patchImports(HMODULE module,const std::vector<Target>& targets,bool delay) {
    auto* base=reinterpret_cast<BYTE*>(module);
    auto* dos=reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if(dos->e_magic!=IMAGE_DOS_SIGNATURE) return;
    auto* nt=reinterpret_cast<IMAGE_NT_HEADERS64*>(base+dos->e_lfanew);
    if(nt->Signature!=IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic!=IMAGE_NT_OPTIONAL_HDR64_MAGIC) return;
    auto dir=nt->OptionalHeader.DataDirectory[delay?IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT:IMAGE_DIRECTORY_ENTRY_IMPORT];
    if(!dir.VirtualAddress) return;
    auto visit=[&](DWORD iatRva,DWORD nameRva) {
        if(!iatRva || !nameRva) return;
        auto* iat=reinterpret_cast<IMAGE_THUNK_DATA64*>(base+iatRva);
        auto* names=reinterpret_cast<IMAGE_THUNK_DATA64*>(base+nameRva);
        for(size_t i=0;names[i].u1.AddressOfData;++i) {
            auto** slot=reinterpret_cast<void**>(&iat[i].u1.Function);
            const char* name=nullptr;
            if(!IMAGE_SNAP_BY_ORDINAL64(names[i].u1.Ordinal)) name=reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base+names[i].u1.AddressOfData)->Name;
            for(const auto& t:targets) {
                if(!t.original) continue;
                if(*slot==t.original) patch(slot,t.original,t.replacement);
                else if(delay && name && strcmp(name,t.name)==0) {
                    // Unresolved delay thunk must be in this module, not another hook.
                    auto v=reinterpret_cast<uintptr_t>(*slot), b=reinterpret_cast<uintptr_t>(base);
                    if(v>=b && v<b+nt->OptionalHeader.SizeOfImage) patch(slot,*slot,t.replacement);
                }
            }
        }
    };
    if(delay) {
        for(auto* d=reinterpret_cast<ImgDelayDescr*>(base+dir.VirtualAddress);d->rvaDLLName;++d) {
            if(d->grAttrs & 1) visit(d->rvaIAT,d->rvaINT);
        }
    } else {
        for(auto* d=reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base+dir.VirtualAddress);d->Name;++d) visit(d->FirstThunk,d->OriginalFirstThunk);
    }
}
DWORD WINAPI worker(void*) {
    std::ofstream log(std::filesystem::path(shared->logPath),std::ios::app);
    if(!log) { enabled=false; return ERROR_OPEN_FAILED; }
    log<<"{\"event\":\"start\",\"schema\":2,\"utcMs\":"<<utcMs()<<",\"tickMs\":"<<GetTickCount64()<<",\"pid\":"<<GetCurrentProcessId()<<",\"mode\":"<<shared->mode<<",\"interfaceIndex\":"<<shared->interfaceIndex<<"}\n"; log.flush();
    HMODULE eos=nullptr; uint64_t last=0;std::string lastAdapters,stopReason="controller_stop_or_heartbeat_expired";
    std::vector<Target> network={
        {"GetAdaptersAddresses",reinterpret_cast<void*>(realGaa),reinterpret_cast<void*>(hookGaa)},
        {"sendto",reinterpret_cast<void*>(realSendTo),reinterpret_cast<void*>(hookSendTo)},
        {"recvfrom",reinterpret_cast<void*>(realRecvFrom),reinterpret_cast<void*>(hookRecvFrom)},
        {"send",reinterpret_cast<void*>(realSend),reinterpret_cast<void*>(hookSend)},
        {"recv",reinterpret_cast<void*>(realRecv),reinterpret_cast<void*>(hookRecv)}};
    while(alive()) {
        if(!eos) {
            auto m=GetModuleHandleW(L"EOSSDK-Win64-Shipping.dll");
            if(m) {
                // Pin SDK while pointers/hooks exist. Does not change its exports.
                GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,reinterpret_cast<LPCWSTR>(m),&eos);
                realSendPacket=reinterpret_cast<SendPacket>(GetProcAddress(eos,"EOS_P2P_SendPacket"));
                realReceivePacket=reinterpret_cast<ReceivePacket>(GetProcAddress(eos,"EOS_P2P_ReceivePacket"));
                realNextSize=reinterpret_cast<NextSize>(GetProcAddress(eos,"EOS_P2P_GetNextReceivedPacketSize"));
                auto version=reinterpret_cast<const char*(*)()>(GetProcAddress(eos,"EOS_GetVersion"));
                log<<"{\"event\":\"eos_loaded\",\"utcMs\":"<<utcMs()<<",\"tickMs\":"<<GetTickCount64()<<",\"version\":\""<<(version?version():"unknown")<<"\"}\n";
            }
        }
        if(eos) {
            patchImports(eos,network,false);
            std::vector<Target> p2p={{"EOS_P2P_SendPacket",reinterpret_cast<void*>(realSendPacket),reinterpret_cast<void*>(hookSendPacket)},
                {"EOS_P2P_ReceivePacket",reinterpret_cast<void*>(realReceivePacket),reinterpret_cast<void*>(hookReceivePacket)},
                {"EOS_P2P_GetNextReceivedPacketSize",reinterpret_cast<void*>(realNextSize),reinterpret_cast<void*>(hookNextSize)}};
            patchImports(GetModuleHandleW(nullptr),p2p,true);
            patchImports(GetModuleHandleW(nullptr),p2p,false);
        }
        auto tick=GetTickCount64();
        if(tick-last>=1000) {
            log<<"{\"event\":\"sample\",\"utcMs\":"<<utcMs()<<",\"tickMs\":"<<tick<<",\"mode\":"<<shared->mode<<",\"hooks\":"<<patchCount
               <<",\"eosSendCalls\":"<<eosSend<<",\"eosSendAccepted\":"<<eosSendOk<<",\"eosReceivePackets\":"<<eosRecv
               <<",\"eosReceiveBytes\":"<<eosBytes<<",\"eosReceiveCalls\":"<<eosReceiveCalls<<",\"eosPolls\":"<<eosPolls<<",\"adapterCalls\":"<<enumCalls<<",\"adapterFiltered\":"<<enumFiltered
               <<",\"adapterMissing\":"<<enumMissing<<",\"metadataDropped\":"<<droppedMetadata<<"}\n";
            {
                std::lock_guard<std::mutex> lock(flowsMutex);
                for(const auto& item:flows) {
                    const auto& f=item.second;
                    log<<"{\"event\":\"eos_udp\",\"utcMs\":"<<utcMs()<<",\"tickMs\":"<<tick<<",\"direction\":\""<<f.direction<<"\",\"local\":\""<<f.local
                       <<"\",\"remote\":\""<<f.remote<<"\",\"packets\":"<<f.packets<<",\"bytes\":"<<f.bytes
                       <<",\"predictedInterface\":"<<f.routeIndex<<"}\n";
                }
                flows.clear();
            }
            {
                std::lock_guard<std::mutex> lock(adaptersMutex);
                if(!adaptersView.empty()&&adaptersView!=lastAdapters){lastAdapters=adaptersView;log<<"{\"event\":\"adapter_view\",\"utcMs\":"<<utcMs()<<",\"tickMs\":"<<tick<<','<<adaptersView<<"}\n";}
            }
            log.flush(); last=tick;
            if(log.tellp()>64*1024*1024 || !log) {
                stopReason="log_limit_or_write_error";
                log<<"{\"event\":\"log_limit_or_write_error\",\"utcMs\":"<<utcMs()<<"}\n";
                break;
            }
        }
        Sleep(250);
    }
    enabled=false;
    InterlockedExchange(&shared->mode,0);
    size_t restored=0;
    for(auto i=patches.rbegin();i!=patches.rend();++i) if(exchange(i->slot,i->replacement,i->original)) ++restored;
    if(stopReason=="controller_stop_or_heartbeat_expired")stopReason=shared->stop?"controller_stop":"heartbeat_expired";
    log<<"{\"event\":\"restored\",\"utcMs\":"<<utcMs()<<",\"tickMs\":"<<GetTickCount64()<<",\"reason\":\""<<stopReason<<"\",\"slots\":"<<restored<<",\"total\":"<<patches.size()<<",\"restartGameRequired\":true}\n";
    // DLL stays resident. Never FreeLibrary while another thread may have fetched a hook pointer.
    return 0;
}
}
extern "C" __declspec(dllexport) DWORD WINAPI RonWgStart(void*) {
    bool expected=false;
    if(!started.compare_exchange_strong(expected,true)) return ERROR_ALREADY_EXISTS;
    wchar_t name[80]; swprintf_s(name,L"Local\\RonWg-%lu",GetCurrentProcessId());
    mapping=OpenFileMappingW(FILE_MAP_ALL_ACCESS,FALSE,name);
    if(!mapping) return GetLastError();
    shared=static_cast<Shared*>(MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(Shared)));
    if(!shared || shared->magic!=MAGIC || shared->protocol!=PROTOCOL || shared->logPath[1023]!=0) return ERROR_INVALID_DATA;
    enabled=true;
    HANDLE t=CreateThread(nullptr,0,worker,nullptr,0,nullptr);
    if(!t) {enabled=false;return GetLastError();}
    CloseHandle(t); return 0;
}
BOOL WINAPI DllMain(HINSTANCE self,DWORD reason,LPVOID) {
    if(reason==DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(self);
    return TRUE;
}
