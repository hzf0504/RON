#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <iphlpapi.h>
#include <windows.h>
#include <iostream>
#include <filesystem>
#include <fstream>
#include <vector>
#include "../shared.h"
#include "../adapter_filter.h"
#pragma comment(lib,"ws2_32.lib")
#pragma comment(lib,"iphlpapi.lib")
extern "C" __declspec(dllimport) int EOS_P2P_SendPacket(void*,const void*);
extern "C" __declspec(dllimport) int EOS_P2P_GetNextReceivedPacketSize(void*,const void*,unsigned long*);
extern "C" __declspec(dllimport) int EOS_P2P_ReceivePacket(void*,const void*,void*,void*,unsigned char*,void*,unsigned long*);
extern "C" __declspec(dllimport) int TestAdapterCount(unsigned long*);
extern "C" __declspec(dllimport) int TestUdpRoundtrip();
int failed=0;
void check(bool ok,const char* name){std::cout<<(ok?"PASS ":"FAIL ")<<name<<"\n";if(!ok)++failed;}
int wmain(int argc,wchar_t** argv){
    if(argc!=3&&argc!=4){std::cerr<<"harness <probe.dll> <log.jsonl> [--offline]\n";return 2;}
    bool offline=argc==4&&wcscmp(argv[3],L"--offline")==0;
    // An actual linked-list test, including a selected adapter after the head.
    IP_ADAPTER_ADDRESSES nodes[3]{};IP_ADAPTER_UNICAST_ADDRESS unicast{};
    for(int i=0;i<3;i++){nodes[i].Length=sizeof(nodes[i]);nodes[i].IfIndex=10+i;nodes[i].OperStatus=IfOperStatusUp;nodes[i].FirstUnicastAddress=&unicast;if(i<2)nodes[i].Next=&nodes[i+1];}
    check(!SelectAdapter(nodes,999)&&nodes[0].Next==&nodes[1],"missing adapter leaves original list intact");
    check(SelectAdapter(nodes,12)&&nodes[0].IfIndex==12&&nodes[0].Next==nullptr&&nodes[0].FirstUnicastAddress==&unicast,"adapter selection keeps child pointers");
    unsigned long first=0;int baseline=TestAdapterCount(&first);check(baseline>1,"real host has multiple adapters for filter test");
    wchar_t name[80];swprintf_s(name,L"Local\\RonWg-%lu",GetCurrentProcessId());
    auto map=CreateFileMappingW(INVALID_HANDLE_VALUE,nullptr,PAGE_READWRITE,0,4096,name);
    auto* shared=static_cast<Shared*>(MapViewOfFile(map,FILE_MAP_ALL_ACCESS,0,0,4096));
    shared->magic=MAGIC;shared->protocol=PROTOCOL;shared->mode=2;shared->interfaceIndex=first;shared->heartbeat=GetTickCount64();
    wcscpy_s(shared->logPath,std::filesystem::absolute(argv[2]).c_str());
    auto dll=LoadLibraryW(argv[1]);check(dll!=nullptr,"native module loads");if(!dll)return 1;
    auto start=reinterpret_cast<DWORD(WINAPI*)(void*)>(GetProcAddress(dll,"RonWgStart"));
    check(start&&start(nullptr)==0,"native module starts from shared control block");Sleep(600);
    unsigned long filtered=0;check(TestAdapterCount(&filtered)==1&&filtered==first,"SDK import receives selected interface only");
    shared->interfaceIndex=0x7fffffff;check(TestAdapterCount(nullptr)==baseline,"missing interface preserves OS result");shared->interfaceIndex=first;
    check(EOS_P2P_SendPacket(nullptr,nullptr)==0,"SendPacket return preserved");
    unsigned long next=0;check(EOS_P2P_GetNextReceivedPacketSize(nullptr,nullptr,&next)==0&&next==12,"receive queue query preserved");
    char data[100]{};unsigned long bytes=0;check(EOS_P2P_ReceivePacket(nullptr,nullptr,nullptr,nullptr,nullptr,data,&bytes)==0&&std::string(data)=="test-packet","ReceivePacket payload and return preserved");
    WSADATA wsa;WSAStartup(MAKEWORD(2,2),&wsa);if(!offline)check(TestUdpRoundtrip()==0,"Winsock hooks preserve real UDP roundtrip");else std::cout<<"SKIP UDP test (offline mode)\n";
    Sleep(1200);shared->heartbeat=GetTickCount64();
    shared->mode=0;check(TestAdapterCount(nullptr)==baseline,"pass-through mode restores visible adapters immediately");
    // Simulate a crashed/closed assistant: expiry must remove every IAT patch.
    shared->heartbeat=GetTickCount64()-6000;Sleep(700);
    check(TestAdapterCount(nullptr)==baseline,"heartbeat expiry restores adapters");
    check(EOS_P2P_SendPacket(nullptr,nullptr)==0,"P2P works after restore");
    std::ifstream log{std::filesystem::path(argv[2])};std::string text((std::istreambuf_iterator<char>(log)),{});
    check(text.find("\"eosSendAccepted\":1")!=std::string::npos,"P2P activity recorded");
    check(text.find("\"eosPolls\":1")!=std::string::npos,"P2P polling recorded separately from actual packets");
    if(!offline)check(text.find("\"event\":\"eos_udp\"")!=std::string::npos,"SDK socket endpoints recorded");
    check(text.find("\"event\":\"adapter_view\"")!=std::string::npos,"visible adapter metadata recorded");
    check(text.find("\"utcMs\":")!=std::string::npos,"native UTC timestamps recorded");
    check(text.find("\"reason\":\"heartbeat_expired\"")!=std::string::npos,"watchdog restore reason recorded");
    check(text.find("\"event\":\"restored\"")!=std::string::npos,"restore acknowledgement recorded");
    WSACleanup();UnmapViewOfFile(shared);CloseHandle(map);
    std::cout<<"Failures: "<<failed<<"\n";return failed?1:0;
}
