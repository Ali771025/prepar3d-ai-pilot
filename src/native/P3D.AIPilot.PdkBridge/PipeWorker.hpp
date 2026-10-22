#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>
#include <sddl.h>
#include <atomic>
#include <fstream>
#include <filesystem>
#include <mutex>
#include <string>
#include <thread>
#include "BridgeCore.hpp"

namespace aipilot {
class Handle {HANDLE value_=INVALID_HANDLE_VALUE;public: explicit Handle(HANDLE v=INVALID_HANDLE_VALUE):value_(v){}~Handle(){if(value_!=INVALID_HANDLE_VALUE&&value_)CloseHandle(value_);}Handle(const Handle&)=delete;Handle& operator=(const Handle&)=delete;HANDLE get()const{return value_;}operator bool()const{return value_&&value_!=INVALID_HANDLE_VALUE;}};
template<class T,size_t N>class Spsc {
    std::array<T,N+1> slots_{};std::atomic<size_t> read_{0},write_{0};
public:
    bool push(const T& value)noexcept{auto w=write_.load(std::memory_order_relaxed);auto next=(w+1)%(N+1);if(next==read_.load(std::memory_order_acquire))return false;slots_[w]=value;write_.store(next,std::memory_order_release);return true;}
    bool pop(T& value)noexcept{auto r=read_.load(std::memory_order_relaxed);if(r==write_.load(std::memory_order_acquire))return false;value=slots_[r];read_.store((r+1)%(N+1),std::memory_order_release);return true;}
};
struct Request {Type type{};uint64_t sequence=0,epoch=0;int64_t timestamp=0;Control control{};};
struct Ack {uint64_t sequence=0;uint32_t result=0;uint64_t lease=0,epoch=0;};
struct Shared {
    std::mutex snapshotMutex;Snapshot snapshot{};
    Spsc<Request,8> requests;Spsc<Ack,16> acks;
    std::atomic<bool> connected{false},releaseRequested{false};
    std::atomic<uint64_t> heartbeatTick{0},lease{0},epoch{0},callbackNanoseconds{0},dropped{0};
    bool publish(const Snapshot& value)noexcept{if(!snapshotMutex.try_lock()){++dropped;return false;}snapshot=value;snapshotMutex.unlock();return true;}
};
inline int64_t nowMs()noexcept{FILETIME f;GetSystemTimeAsFileTime(&f);ULARGE_INTEGER n;n.LowPart=f.dwLowDateTime;n.HighPart=f.dwHighDateTime;return int64_t(n.QuadPart/10000)-11644473600000LL;}
class PipeWorker {
    Shared& shared_;std::wstring pipe_,logPath_;Handle stop_{CreateEventW(nullptr,TRUE,FALSE,nullptr)};std::thread thread_;
    uint64_t sequence_=0;
    bool io(HANDLE pipe,void* buffer,DWORD length,bool write,DWORD timeout=100){
        Handle event(CreateEventW(nullptr,TRUE,FALSE,nullptr));if(!event)return false;
        DWORD offset=0;
        while(offset<length){OVERLAPPED o{};o.hEvent=event.get();ResetEvent(event.get());DWORD count=0;
            BOOL ok=write?WriteFile(pipe,static_cast<uint8_t*>(buffer)+offset,length-offset,&count,&o):ReadFile(pipe,static_cast<uint8_t*>(buffer)+offset,length-offset,&count,&o);
            if(!ok){if(GetLastError()!=ERROR_IO_PENDING)return false;HANDLE waits[]={stop_.get(),event.get()};DWORD result=WaitForMultipleObjects(2,waits,FALSE,timeout);
                if(result!=WAIT_OBJECT_0+1){CancelIoEx(pipe,&o);GetOverlappedResult(pipe,&o,&count,TRUE);return false;}
                if(!GetOverlappedResult(pipe,&o,&count,FALSE))return false;}
            if(!count)return false;offset+=count;
        }return true;
    }
    bool send(HANDLE pipe,Type type,const std::vector<uint8_t>& payload={},int64_t timestamp=0){auto bytes=encode(Message{type,++sequence_,timestamp?timestamp:nowMs(),payload});return io(pipe,bytes.data(),DWORD(bytes.size()),true);}
    void log(std::ofstream& out,const char* event){out<<"{\"timestampMs\":"<<nowMs()<<",\"component\":\"pdk\",\"event\":\""<<event<<"\"}\n";out.flush();}
    void session(HANDLE pipe,std::ofstream& out){
        uint64_t receiveSequence=0,start=GetTickCount64(),lastLog=0,lastHeartbeat=0,lastSnapshot=0;bool hello=false;Snapshot snapshot;
        while(WaitForSingleObject(stop_.get(),0)==WAIT_TIMEOUT){
            DWORD available=0;if(!PeekNamedPipe(pipe,nullptr,0,nullptr,&available,nullptr))break;
            if(available>=32){std::vector<uint8_t> bytes(32);if(!io(pipe,bytes.data(),32,false))break;
                uint32_t length=0;std::memcpy(&length,bytes.data()+24,4);if(length>MaxPayload)break;bytes.resize(32+length);if(length&&!io(pipe,bytes.data()+32,length,false))break;
                Message m=decode(bytes);auto tick=GetTickCount64();if(m.sequence<=receiveSequence||m.timestamp<nowMs()-1000||m.timestamp>nowMs()+1000)break;receiveSequence=m.sequence;
                if(!hello){if(m.type!=Type::Hello||!m.payload.empty())break;hello=true;sequence_=0;++shared_.epoch;shared_.releaseRequested=true;shared_.heartbeatTick=tick;shared_.connected=true;if(!send(pipe,Type::HelloAck))break;log(out,"ipc_connected");}
                else if(m.type==Type::Heartbeat){if(!m.payload.empty())break;shared_.heartbeatTick=tick;}
                else if(m.type==Type::Acquire||m.type==Type::Release||m.type==Type::Command){
                    Request request;request.type=m.type;request.sequence=m.sequence;request.timestamp=m.timestamp;request.epoch=shared_.epoch.load();
                    if(m.type==Type::Command)request.control=readControl(m.payload);else if(!m.payload.empty())break;
                    if(m.type==Type::Release)shared_.releaseRequested=true;
                    if(!shared_.requests.push(request)){std::vector<uint8_t> ack;put(ack,m.sequence);put(ack,8u);put(ack,shared_.lease.load());if(!send(pipe,Type::CommandAck,ack))break;}
                } else if(m.type==Type::Reset){if(!send(pipe,Type::Error))break;}else break;
            }
            auto tick=GetTickCount64();if(!hello){if(tick-start>2000)break;WaitForSingleObject(stop_.get(),10);continue;}
            if(tick-shared_.heartbeatTick.load()>1000){shared_.releaseRequested=true;break;}
            {std::lock_guard<std::mutex> lock(shared_.snapshotMutex);snapshot=shared_.snapshot;}
            if(snapshot.timestamp&&uint64_t(snapshot.timestamp)!=lastSnapshot){lastSnapshot=uint64_t(snapshot.timestamp);if(!send(pipe,Type::Telemetry,telemetry(snapshot),snapshot.timestamp))break;}
            if(snapshot.timestamp&&nowMs()-snapshot.timestamp>1000){shared_.releaseRequested=true;shared_.lease=0;snapshot.controls=false;}
            Ack ack;while(shared_.acks.pop(ack)){if(ack.epoch!=shared_.epoch.load())continue;std::vector<uint8_t> payload;put(payload,ack.sequence);put(payload,ack.result);put(payload,ack.lease);if(!send(pipe,Type::CommandAck,payload))return;}
            if(tick-lastHeartbeat>=250){lastHeartbeat=tick;std::vector<uint8_t> status;put(status,shared_.lease.load());if(!send(pipe,Type::Status,status)||!send(pipe,Type::Heartbeat))break;}
            if(tick-lastLog>=1000){lastLog=tick;std::vector<uint8_t> caps;put(caps,snapshot.supported);put(caps,(snapshot.controls?1u:0u)|(snapshot.ownedTarget?2u:0u));put(caps,snapshot.rate);if(!send(pipe,Type::Capabilities,caps))break;
                out<<"{\"timestampMs\":"<<nowMs()<<",\"component\":\"pdk\",\"event\":\"telemetry\",\"frame\":"<<snapshot.frame<<",\"aircraftId\":"<<snapshot.aircraftId<<",\"ownedTarget\":"<<(snapshot.ownedTarget?"true":"false")<<",\"supportedMask\":"<<snapshot.supported<<",\"altitudeFt\":"<<snapshot.values[3]<<",\"iasKts\":"<<snapshot.values[5]<<",\"headingDeg\":"<<snapshot.values[11]<<",\"callbackNs\":"<<shared_.callbackNanoseconds.load()<<",\"dropped\":"<<shared_.dropped.load()<<"}\n";out.flush();}
            WaitForSingleObject(stop_.get(),10);
        }
    }
    void run()noexcept{
        try {
            std::ofstream out(std::filesystem::path(logPath_),std::ios::app);log(out,"plugin_worker_started");
            Handle token;HANDLE rawToken=nullptr;if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&rawToken))throw std::runtime_error("token unavailable");Handle ownedToken(rawToken);
            DWORD required=0;GetTokenInformation(rawToken,TokenUser,nullptr,0,&required);std::vector<uint8_t> user(required);if(!GetTokenInformation(rawToken,TokenUser,user.data(),required,&required))throw std::runtime_error("token identity unavailable");
            LPWSTR sid=nullptr;if(!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(user.data())->User.Sid,&sid))throw std::runtime_error("SID unavailable");
            std::wstring sddl=L"D:P(A;;GA;;;SY)(A;;GA;;;"+std::wstring(sid)+L")";LocalFree(sid);
            PSECURITY_DESCRIPTOR descriptor=nullptr;if(!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(),SDDL_REVISION_1,&descriptor,nullptr))throw std::runtime_error("pipe ACL unavailable");
            struct LocalOwner{PSECURITY_DESCRIPTOR value;~LocalOwner(){LocalFree(value);}} owner{descriptor};
            SECURITY_ATTRIBUTES security{sizeof(SECURITY_ATTRIBUTES),descriptor,FALSE};
            while(WaitForSingleObject(stop_.get(),0)==WAIT_TIMEOUT){
                Handle pipe(CreateNamedPipeW(pipe_.c_str(),PIPE_ACCESS_DUPLEX|FILE_FLAG_OVERLAPPED|FILE_FLAG_FIRST_PIPE_INSTANCE,PIPE_TYPE_BYTE|PIPE_READMODE_BYTE|PIPE_WAIT|PIPE_REJECT_REMOTE_CLIENTS,1,16384,16384,0,&security));
                if(!pipe){log(out,"pipe_creation_failed");WaitForSingleObject(stop_.get(),1000);continue;}
                Handle event(CreateEventW(nullptr,TRUE,FALSE,nullptr));OVERLAPPED o{};o.hEvent=event.get();BOOL connected=ConnectNamedPipe(pipe.get(),&o);DWORD error=GetLastError();
                if(!connected&&error==ERROR_IO_PENDING){HANDLE waits[]={stop_.get(),event.get()};DWORD wait;
                    do { wait=WaitForMultipleObjects(2,waits,FALSE,1000);if(wait==WAIT_TIMEOUT){Snapshot s;{std::lock_guard<std::mutex> lock(shared_.snapshotMutex);s=shared_.snapshot;}out<<"{\"timestampMs\":"<<nowMs()<<",\"component\":\"pdk\",\"event\":\"smoke\",\"frame\":"<<s.frame<<",\"supportedMask\":"<<s.supported<<",\"altitudeFt\":"<<s.values[3]<<",\"iasKts\":"<<s.values[5]<<",\"headingDeg\":"<<s.values[11]<<"}\n";out.flush();}}while(wait==WAIT_TIMEOUT);
                    if(wait!=WAIT_OBJECT_0+1){CancelIoEx(pipe.get(),&o);DWORD ignored;GetOverlappedResult(pipe.get(),&o,&ignored,TRUE);break;}DWORD ignored;if(!GetOverlappedResult(pipe.get(),&o,&ignored,FALSE))continue;}
                else if(!connected&&error!=ERROR_PIPE_CONNECTED)continue;
                try{session(pipe.get(),out);}catch(const std::exception&){log(out,"invalid_ipc_session");}
                shared_.connected=false;shared_.releaseRequested=true;CancelIoEx(pipe.get(),nullptr);DisconnectNamedPipe(pipe.get());log(out,"ipc_disconnected");
            }
            shared_.connected=false;shared_.releaseRequested=true;log(out,"plugin_worker_stopped");
        }catch(...){shared_.connected=false;shared_.releaseRequested=true;OutputDebugStringW(L"P3D AIPilot PDK worker faulted\n");}
    }
public:
    PipeWorker(Shared& shared,std::wstring pipe,std::wstring logPath):shared_(shared),pipe_(L"\\\\.\\pipe\\"+pipe),logPath_(std::move(logPath)){if(!stop_)throw std::runtime_error("stop event unavailable");thread_=std::thread([this]{run();});}
    ~PipeWorker(){SetEvent(stop_.get());if(thread_.joinable())thread_.join();}
};
}
