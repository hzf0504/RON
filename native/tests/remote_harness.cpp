#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <windows.h>
#include <iostream>
extern "C" __declspec(dllimport) int EOS_P2P_SendPacket(void*,const void*);
extern "C" __declspec(dllimport) int EOS_P2P_ReceivePacket(void*,const void*,void*,void*,unsigned char*,void*,unsigned long*);
extern "C" __declspec(dllimport) int TestUdpRoundtrip();
#pragma comment(lib,"ws2_32.lib")
int main(){
    if(!LoadLibraryW(L"EOSSDK-Win64-Shipping.dll"))return 3;
    std::cout<<"waiting for external helper"<<std::endl;
    for(int i=0;i<200&&!GetModuleHandleW(L"RonWgProbe.dll");++i)Sleep(50);
    if(!GetModuleHandleW(L"RonWgProbe.dll"))return 2;
    Sleep(700);WSADATA w;WSAStartup(MAKEWORD(2,2),&w);
    char data[100]{};unsigned long bytes=0;
    int r=EOS_P2P_SendPacket(nullptr,nullptr)+EOS_P2P_ReceivePacket(nullptr,nullptr,nullptr,nullptr,nullptr,data,&bytes)+TestUdpRoundtrip();
    Sleep(2000);std::cout<<"payload="<<data<<" result="<<r<<std::endl;
    Sleep(2000);WSACleanup();return r;
}
