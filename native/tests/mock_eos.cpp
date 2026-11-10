#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <iphlpapi.h>
#include <vector>
#include <cstring>
#pragma comment(lib,"ws2_32.lib")
#pragma comment(lib,"iphlpapi.lib")
extern "C" __declspec(dllexport) const char* EOS_GetVersion(){return "MOCK-TEST-NOT-GAME";}
extern "C" __declspec(dllexport) int EOS_P2P_SendPacket(void*,const void*){return 0;}
extern "C" __declspec(dllexport) int EOS_P2P_GetNextReceivedPacketSize(void*,const void*,unsigned long* bytes){if(bytes)*bytes=12;return 0;}
extern "C" __declspec(dllexport) int EOS_P2P_ReceivePacket(void*,const void*,void*,void*,unsigned char*,void* data,unsigned long* bytes){
    const char payload[]="test-packet";if(data)memcpy(data,payload,sizeof(payload));if(bytes)*bytes=sizeof(payload);return 0;
}
extern "C" __declspec(dllexport) int TestAdapterCount(unsigned long* firstIndex){
    ULONG size=16384;std::vector<unsigned char> data(size);ULONG result;
    for(int i=0;i<4;++i){result=GetAdaptersAddresses(AF_INET,0,nullptr,reinterpret_cast<IP_ADAPTER_ADDRESSES*>(data.data()),&size);if(result!=ERROR_BUFFER_OVERFLOW)break;data.resize(size);}
    if(result!=NO_ERROR)return -static_cast<int>(result);
    auto* p=reinterpret_cast<IP_ADAPTER_ADDRESSES*>(data.data());int count=0;if(firstIndex)*firstIndex=p->IfIndex;for(;p;p=p->Next)++count;return count;
}
extern "C" __declspec(dllexport) int TestUdpRoundtrip(){
    SOCKET a=socket(AF_INET,SOCK_DGRAM,IPPROTO_UDP),b=socket(AF_INET,SOCK_DGRAM,IPPROTO_UDP);
    sockaddr_in dest{};dest.sin_family=AF_INET;dest.sin_addr.s_addr=htonl(INADDR_LOOPBACK);
    if(bind(b,reinterpret_cast<sockaddr*>(&dest),sizeof(dest))!=0)return 1;
    int len=sizeof(dest);getsockname(b,reinterpret_cast<sockaddr*>(&dest),&len);
    DWORD timeout=1000;setsockopt(b,SOL_SOCKET,SO_RCVTIMEO,reinterpret_cast<char*>(&timeout),sizeof(timeout));
    const char message[]="EOS-UDP-HARNESS";char buffer[128]{};sockaddr_in from{};int fromlen=sizeof(from);
    int sent=sendto(a,message,sizeof(message),0,reinterpret_cast<sockaddr*>(&dest),sizeof(dest));
    int received=recvfrom(b,buffer,sizeof(buffer),0,reinterpret_cast<sockaddr*>(&from),&fromlen);
    closesocket(a);closesocket(b);return sent==sizeof(message)&&received==sent&&memcmp(buffer,message,sent)==0?0:2;
}
