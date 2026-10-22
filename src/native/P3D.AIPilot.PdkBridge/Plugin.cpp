#include <string>
#include <atlbase.h>
#include <initguid.h>
#include <Pdk.h>
#include <ISimObject.h>
#include <ISimObjectAI.h>
#include <IEventService.h>
#include <IPanelSystem.h>
#include <chrono>
#include <memory>
#include <iterator>
#include <cwchar>
#include "PipeWorker.hpp"

namespace {
using namespace aipilot;
class Plugin;
class Callback:public P3D::ICallbackV400 {
    std::atomic<ULONG> references_{1};
    std::atomic<Plugin*> owner_;int kind_;
public:Callback(Plugin* owner,int kind):owner_(owner),kind_(kind){}
    virtual ~Callback()=default;
    ULONG STDMETHODCALLTYPE AddRef() override{return references_.fetch_add(1)+1;}
    ULONG STDMETHODCALLTYPE Release() override{ULONG count=references_.fetch_sub(1)-1;if(count==0)delete this;return count;}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** value) override{
        if(!value)return E_POINTER;*value=nullptr;
        if(IsEqualIID(id,IID_IUnknown)||IsEqualIID(id,P3D::IID_ICallbackV400)){*value=static_cast<P3D::ICallbackV400*>(this);AddRef();return S_OK;}
        return E_NOINTERFACE;
    }
    void detach()noexcept{owner_=nullptr;}
    void Invoke(P3D::IParameterListV400* parameters) override;
};
class Plugin {
    CComPtr<P3D::IEventServiceV400> events_;
    CComPtr<P3D::ISimObjectManagerV400> objects_;
    CComPtr<P3D::ISimObjectManagerV410> creator_;
    CComPtr<P3D::IPanelSystemV400> panel_;
    CComPtr<P3D::ISimObjectAIV02> ownedPilot_;
    CComPtr<P3D::IAirplaneAIServiceV02> ownedDynamics_;
    Shared humanShared_;std::unique_ptr<PipeWorker> humanWorker_;
    bool ownedMode_=false,spawnEnabled_=false,spawnAttempted_=false;
    double pitchSign_=1,bankSign_=1;bool orientationVerified_=false;
    UINT ownedId_=0;std::wstring ownedTitle_;P3D::DXYZ spawnPosition_{},spawnAttitude_{};
    std::array<int,3> scopedEvents_{};std::array<int,3> scopedIndices_{};
    uint64_t lastHumanCapture_=0;
    bool getTarget(CComPtr<P3D::IBaseObjectV400>& target) {
        if(!ownedMode_)return SUCCEEDED(objects_->GetUserObject(&target))&&target;
        if(!ownedId_||FAILED(objects_->GetObject(ownedId_,&target))||!target||target->IsUser())return false;
        return true;
    }
    void discoverOwned(P3D::IBaseObjectV400* object) {
        ownedPilot_.Release();ownedDynamics_.Release();eventsReady_=false;
        if(!object||object->IsUser())return;
        if(FAILED(object->QueryService(P3D::SID_SimObjectAI,P3D::IID_ISimObjectAIV02,reinterpret_cast<void**>(&ownedPilot_)))||
           FAILED(object->QueryService(P3D::SID_AirplaneAIService,P3D::IID_IAirplaneAIServiceV02,reinterpret_cast<void**>(&ownedDynamics_)))) {
            OutputDebugStringW(L"AI target lacks required object-local pilot/dynamics services; controls blocked\n");return;
        }
        const wchar_t* names[]={L"AXIS_LEFT_BRAKE_SET",L"AXIS_RIGHT_BRAKE_SET",L"GEAR_SET"};
        for(size_t i=0;i<3;++i){scopedIndices_[i]=0;if(FAILED(object->GetPropertyCodeAndIndex(P3D::PROPERTY_TYPE_EVENT,names[i],scopedEvents_[i],scopedIndices_[i]))) {
            OutputDebugStringW(L"AI target lacks scoped gear/brake event; controls blocked\n");return;
        }}
        eventsReady_=true;
    }
    void spawnOwned() {
        if(!ownedMode_||!spawnEnabled_||spawnAttempted_)return;
        CComPtr<P3D::IBaseObjectV400> user;if(FAILED(objects_->GetUserObject(&user))||!user)return;
        spawnAttempted_=true;
        UINT id=0;if(FAILED(creator_->CreateObject(ownedTitle_.c_str(),id))||!id){OutputDebugStringW(L"Owned F22 creation failed; exact aircraft title required\n");return;}
        ownedId_=id;CComPtr<P3D::IBaseObjectV400> target;
        CComPtr<P3D::IBaseObjectV450> positionObject;
        if(!getTarget(target)||FAILED(target->QueryInterface(P3D::IID_IBaseObjectV450,reinterpret_cast<void**>(&positionObject)))||FAILED(positionObject->InitPosition(&spawnPosition_,&spawnAttitude_,nullptr,nullptr,TRUE))) {
            if(target&&!target->IsUser())objects_->RemoveObject(ownedId_);ownedId_=0;return;
        }
        discoverOwned(target);
        if(ownedPilot_)ownedPilot_->Deactivate();
        if(ownedDynamics_)ownedDynamics_->SetThrottlePercent(0);
        if(eventsReady_){trigger(target,4,brakeAxis(1));trigger(target,5,brakeAxis(1));}
        // Object-scoped only. Unsupported events cannot fall through to Panel.SendKeyEvent.
        if(FAILED(target->TriggerProperty(L"ENGINE_AUTO_START",L"number",0.0,0)))
            OutputDebugStringW(L"Owned F22 engine start not supported; inspect engine readiness before arming\n");
        OutputDebugStringW(L"Owned F22 created and positioned; telemetry only until explicit acquire\n");
    }
    CComPtr<Callback> frame_,messages_,oneHz_;
    Shared shared_;std::unique_ptr<PipeWorker> worker_;
    CommandGate gate_;uint64_t aircraft_=0,lastCapture_=0,acquiredTick_=0,frameNumber_=0;
    bool enabled_=false,eventsReady_=false,ready_=false;unsigned rate_=25;
    std::array<UINT32,8> eventCodes_{};
    std::wstring ini_;
    Snapshot snapshot_;
    static constexpr const wchar_t* eventNames_[8]={L"AXIS_AILERONS_SET",L"AXIS_ELEVATOR_SET",L"AXIS_RUDDER_SET",L"THROTTLE_SET",L"AXIS_LEFT_BRAKE_SET",L"AXIS_RIGHT_BRAKE_SET",L"FLAPS_SET",L"GEAR_SET"};
    void resolveEvents(){
        eventsReady_=false;if(!panel_)return;
        auto count=panel_->GetEventCount();if(count==0||count>65536)return;
        for(UINT32 i=0;i<count;++i){auto id=panel_->GetEventIdByIndex(i);if(!id)continue;
            auto token=panel_->GetEventTokenString(id);if(!token)continue;
            for(size_t j=0;j<eventCodes_.size();++j)if(_wcsicmp(token,eventNames_[j])==0)eventCodes_[j]=id;}
        eventsReady_=true;for(auto id:eventCodes_)if(!id)eventsReady_=false;
    }
    bool trigger(P3D::IBaseObjectV400* aircraft,size_t index,double value)noexcept{
        if(!eventsReady_||!aircraft)return false;
        if(ownedMode_){
            if(aircraft->IsUser()||aircraft->GetId()!=ownedId_||!ownedDynamics_)return false;
            if(index==0)ownedDynamics_->SetAileronPercent(value/16383.0);
            else if(index==1)ownedDynamics_->SetElevatorPercent(value/16383.0);
            else if(index==2)ownedDynamics_->SetRudderPercent(value/16383.0);
            else if(index==3)ownedDynamics_->SetThrottlePercent(value/16383.0);
            else if(index==6)ownedDynamics_->SetFlapsPercent(value/16383.0);
            else {size_t slot=index==7?2:index-4;return SUCCEEDED(aircraft->TriggerProperty(slot==2?L"GEAR_SET":slot==0?L"AXIS_LEFT_BRAKE_SET":L"AXIS_RIGHT_BRAKE_SET",L"number",value,scopedIndices_[slot]));}
            return true;
        }
        if(!panel_)return false;
        // Signed axis payloads are encoded in the standard 32-bit key-event value.
        panel_->SendKeyEvent(eventCodes_[index],static_cast<UINT32>(static_cast<int32_t>(value)));
        return true; // Dispatch only; aircraft response must be validated with live telemetry.
    }
    void release(P3D::IBaseObjectV400* aircraft)noexcept{
        bool active=gate_.release();
        if(active&&aircraft){for(size_t i=0;i<3;++i)trigger(aircraft,i,0);trigger(aircraft,4,brakeAxis(0));trigger(aircraft,5,brakeAxis(0));}
        if(active&&ownedMode_&&aircraft&&!aircraft->IsUser()){
            trigger(aircraft,3,unsignedAxis(0)); // Never leave the last AI throttle latched.
            if(ownedPilot_)ownedPilot_->Activate();
        }
        shared_.lease=0;acquiredTick_=0;
    }
    void capture(P3D::IBaseObjectV400* aircraft,uint64_t tick,bool publish=true){
        snapshot_=Snapshot{};snapshot_.rate=rate_;snapshot_.frame=frameNumber_;snapshot_.aircraftId=aircraft->GetId();snapshot_.timestamp=nowMs();
        for(size_t i=0;i<FieldCount;++i){double value=0;
            if(SUCCEEDED(aircraft->GetProperty(Properties[i].name,Properties[i].unit,value))&&std::isfinite(value)){snapshot_.values[i]=value;snapshot_.supported|=1ULL<<i;}}
        if(ownedMode_){snapshot_.values[9]*=pitchSign_;snapshot_.values[10]*=bankSign_;}
        // Fuel is normalized only when both independently queried values are available.
        double capacity=0;if((snapshot_.supported&(1ULL<<23))&&SUCCEEDED(aircraft->GetProperty(L"FUEL TOTAL CAPACITY",L"gallons",capacity))&&std::isfinite(capacity)&&capacity>0)snapshot_.values[23]/=capacity;
        else {snapshot_.values[23]=0;snapshot_.supported&=~(1ULL<<23);}
        for(size_t i=0;i<std::size(Flags);++i){double value=0;if(SUCCEEDED(aircraft->GetProperty(Flags[i].name,Flags[i].unit,value))&&std::isfinite(value)){
            snapshot_.supported|=1ULL<<(FieldCount+i);if(value!=0)snapshot_.flags|=1u<<i;}}
        int mode=aircraft->GetMode();snapshot_.supported|=(1ULL<<(FieldCount+4))|(1ULL<<(FieldCount+5));
        if(mode&SIMOBJECT_MODE_SLEW)snapshot_.flags|=1u<<4;else snapshot_.flags&=~(1u<<4);
        if(mode&SIMOBJECT_MODE_PAUSE)snapshot_.flags|=1u<<5;else snapshot_.flags&=~(1u<<5);
        wchar_t title[256]{};if(SUCCEEDED(aircraft->GetTitle(title,256))){int count=WideCharToMultiByte(CP_UTF8,0,title,-1,nullptr,0,nullptr,nullptr);if(count>0&&count<=256){WideCharToMultiByte(CP_UTF8,0,title,-1,snapshot_.title.data(),256,nullptr,nullptr);snapshot_.titleLength=uint16_t(count-1);}}
        uint64_t required=0;for(unsigned i:{1u,2u,3u,4u,5u,7u,8u,9u,10u,11u,12u,13u,27u,28u,29u,30u,31u,35u,36u,37u,38u,39u,40u})required|=1ULL<<i;
        ready_=(snapshot_.supported&required)==required&&snapshot_.titleLength>0;
        if(ownedMode_&&aircraft->GetId()==ownedId_){
            double first=0,second=0;
            ready_=ready_&&orientationVerified_&&SUCCEEDED(aircraft->GetProperty(L"GENERAL ENG COMBUSTION:1",L"bool",first))&&first!=0
                &&SUCCEEDED(aircraft->GetProperty(L"GENERAL ENG COMBUSTION:2",L"bool",second))&&second!=0;
        }
        snapshot_.controls=enabled_&&eventsReady_&&ready_;snapshot_.ownedTarget=ownedMode_&&!aircraft->IsUser();if(publish)shared_.publish(snapshot_);lastCapture_=tick;
    }
public:
    explicit Plugin(P3D::IPdk* pdk){
        if(!pdk||FAILED(pdk->QueryService(P3D::SID_EventService,P3D::IID_IEventServiceV400,reinterpret_cast<void**>(&events_)))||
           FAILED(pdk->QueryService(P3D::SID_SimObjectManager,P3D::IID_ISimObjectManagerV400,reinterpret_cast<void**>(&objects_))))throw std::runtime_error("PDK services unavailable");
        pdk->QueryService(P3D::SID_PanelSystem,P3D::IID_IPanelSystemV400,reinterpret_cast<void**>(&panel_));
        resolveEvents(); // Startup discovery, never a per-frame event enumeration.
        HMODULE module=nullptr;GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,reinterpret_cast<LPCWSTR>(&eventNames_),&module);
        wchar_t path[32768]{};if(!GetModuleFileNameW(module,path,DWORD(std::size(path))))throw std::runtime_error("module path unavailable");
        auto directory=std::filesystem::path(path).parent_path();ini_=(directory/L"PdkBridge.ini").wstring();
        enabled_=GetPrivateProfileIntW(L"Bridge",L"ControlsEnabled",0,ini_.c_str())==1;
        wchar_t mode[32]{};GetPrivateProfileStringW(L"Mission",L"TargetMode",L"User",mode,32,ini_.c_str());
        ownedMode_=_wcsicmp(mode,L"OwnedAI")==0;if(!ownedMode_&&_wcsicmp(mode,L"User")!=0)throw std::runtime_error("invalid target mode");
        if(ownedMode_){
            if(FAILED(pdk->QueryService(P3D::SID_SimObjectManager,P3D::IID_ISimObjectManagerV410,reinterpret_cast<void**>(&creator_))))throw std::runtime_error("PDK 4.1+ object creation service missing");
            spawnEnabled_=GetPrivateProfileIntW(L"Mission",L"SpawnEnabled",0,ini_.c_str())==1;
            wchar_t title[256]{};GetPrivateProfileStringW(L"Mission",L"AircraftTitle",L"",title,256,ini_.c_str());ownedTitle_=title;
            auto number=[&](const wchar_t* key){wchar_t text[64]{};GetPrivateProfileStringW(L"Mission",key,L"",text,64,ini_.c_str());wchar_t* end=nullptr;double value=std::wcstod(text,&end);if(end==text||*end||!std::isfinite(value))throw std::runtime_error("invalid spawn coordinate");return value;};
            pitchSign_=number(L"PitchSign");bankSign_=number(L"BankSign");
            if(std::abs(pitchSign_)!=1||std::abs(bankSign_)!=1)throw std::runtime_error("invalid orientation sign");
            orientationVerified_=GetPrivateProfileIntW(L"Mission",L"OrientationVerified",0,ini_.c_str())==1;
            double lat=number(L"LatitudeDeg"),lon=number(L"LongitudeDeg"),alt=number(L"AltitudeMslFt"),heading=number(L"HeadingDeg");
            if(lat<-90||lat>90||lon<-180||lon>180||alt<-1500||alt>50000||heading<0||heading>=360||ownedTitle_.empty())throw std::runtime_error("invalid mission spawn");
            constexpr double rad=3.14159265358979323846/180.0;
            spawnPosition_.dX=lon*rad;spawnPosition_.dY=alt;spawnPosition_.dZ=lat*rad;spawnAttitude_.dY=heading*rad;
        }
        rate_=GetPrivateProfileIntW(L"Bridge",L"TelemetryHz",25,ini_.c_str());if(rate_<1||rate_>60)throw std::runtime_error("invalid telemetry rate");
        wchar_t pipe[129]{};GetPrivateProfileStringW(L"Bridge",L"PipeName",L"P3D-AIPilot-Telemetry",pipe,129,ini_.c_str());if(!pipe[0]||std::wcschr(pipe,L'\\'))throw std::runtime_error("invalid pipe name");
        std::filesystem::create_directories(directory/L"logs"/L"pdk");
        worker_=std::make_unique<PipeWorker>(shared_,pipe,(directory/L"logs"/L"pdk"/L"bridge.jsonl").wstring());
        if(ownedMode_)humanWorker_=std::make_unique<PipeWorker>(humanShared_,L"P3D-AIPilot-Human",(directory/L"logs"/L"pdk"/L"human.jsonl").wstring());
        frame_.Attach(new Callback(this,0));messages_.Attach(new Callback(this,1));oneHz_.Attach(new Callback(this,2));
        if(FAILED(events_->RegisterCallback(P3D::EVENTID_Frame,frame_))||FAILED(events_->RegisterCallback(P3D::EVENTID_Message,messages_))||FAILED(events_->RegisterCallback(P3D::EVENTID_1Hz,oneHz_))){stop();throw std::runtime_error("PDK callback registration failed");}
        OutputDebugStringW(L"P3D AIPilot PDK bridge loaded; controls default OFF\n");
    }
    ~Plugin(){stop();}
    void stop()noexcept{
        if(frame_)frame_->detach();if(messages_)messages_->detach();if(oneHz_)oneHz_->detach();
        if(events_){if(frame_)events_->UnregisterCallback(P3D::EVENTID_Frame,frame_);if(messages_)events_->UnregisterCallback(P3D::EVENTID_Message,messages_);if(oneHz_)events_->UnregisterCallback(P3D::EVENTID_1Hz,oneHz_);}
        CComPtr<P3D::IBaseObjectV400> aircraft;if(objects_)getTarget(aircraft);release(aircraft);
        if(ownedMode_&&ownedId_&&aircraft&&!aircraft->IsUser())objects_->RemoveObject(ownedId_);
        humanWorker_.reset();ownedPilot_.Release();ownedDynamics_.Release();
        worker_.reset();frame_.Release();messages_.Release();oneHz_.Release();events_.Release();objects_.Release();creator_.Release();panel_.Release();
    }
    void invoke(int kind,P3D::IParameterListV400* parameters)noexcept{
        try {
            if(kind==1){if(parameters&&parameters->GetCount()&&parameters->GetParameter(0).Value==EVENT_MESSAGE_APPLICATION_SHUTDOWN)shared_.releaseRequested=true;return;}
            if(kind==2){spawnOwned();return;}
            auto started=std::chrono::steady_clock::now();++frameNumber_;auto tick=GetTickCount64();auto now=nowMs();
            CComPtr<P3D::IBaseObjectV400> aircraft;getTarget(aircraft);
            if(ownedMode_&&tick-lastHumanCapture_>=1000/rate_){
                CComPtr<P3D::IBaseObjectV400> user;
                if(SUCCEEDED(objects_->GetUserObject(&user))&&user){Snapshot previous=snapshot_;bool previousReady=ready_;uint64_t previousCapture=lastCapture_;
                    capture(user,tick,false);Snapshot human=snapshot_;human.controls=false;human.ownedTarget=false;humanShared_.publish(human);
                    snapshot_=previous;ready_=previousReady;lastCapture_=previousCapture;lastHumanCapture_=tick;}
            }
            if(!aircraft){release(nullptr);ready_=false;return;}
            if(aircraft_!=aircraft->GetId()){release(nullptr);aircraft_=aircraft->GetId();ready_=false;lastCapture_=0;}
            bool paused=(aircraft->GetMode()&(SIMOBJECT_MODE_PAUSE|SIMOBJECT_MODE_SLEW))!=0;
            if(shared_.releaseRequested.exchange(false)||!shared_.connected.load()||tick-shared_.heartbeatTick.load()>1000||paused||gate_.expired(now,tick)||(gate_.lease()&&tick-acquiredTick_>500&&acquiredTick_!=0))release(aircraft);
            if(tick-lastCapture_>=1000/rate_)capture(aircraft,tick);
            bool emergency=(GetAsyncKeyState(VK_F12)&0x8000)!=0;
            if(emergency){release(aircraft);shared_.releaseRequested=true;}
            Request request;unsigned processed=0;
            while(processed++<8&&shared_.requests.pop(request)){
                if(request.epoch!=shared_.epoch.load())continue;
                uint32_t result=0;
                if(request.type==Type::Release)release(aircraft);
                else if(request.type==Type::Acquire){result=gate_.acquire(enabled_&&eventsReady_,ready_,paused||emergency);if(result==0){acquiredTick_=tick;if(ownedMode_&&ownedPilot_)ownedPilot_->Deactivate();}}
                else if(request.type==Type::Command){result=gate_.accept(request.control,request.sequence,request.timestamp,now,tick);
                    if(result==0){acquiredTick_=0;bool ok=true;for(size_t i=0;i<3;++i)ok=trigger(aircraft,i,signedAxis(request.control.axes[i]))&&ok;
                        ok=trigger(aircraft,3,unsignedAxis(request.control.axes[3]))&&ok;
                        ok=trigger(aircraft,4,brakeAxis(request.control.axes[4]))&&ok;
                        ok=trigger(aircraft,5,brakeAxis(request.control.axes[4]))&&ok;
                        ok=trigger(aircraft,6,unsignedAxis(request.control.axes[5]))&&ok;ok=trigger(aircraft,7,request.control.gear)&&ok;
                        if(!ok){result=7;release(aircraft);}}
                }
                shared_.lease=gate_.lease();if(!shared_.acks.push(Ack{request.sequence,result,gate_.lease(),request.epoch}))release(aircraft);
            }
            auto elapsed=std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now()-started).count();shared_.callbackNanoseconds=uint64_t(elapsed);
        }catch(...){shared_.releaseRequested=true;OutputDebugStringW(L"P3D AIPilot PDK callback error; release requested\n");}
    }
};
void Callback::Invoke(P3D::IParameterListV400* parameters){if(auto* owner=owner_.load())owner->invoke(kind_,parameters);}
std::unique_ptr<Plugin> plugin;
}
extern "C" __declspec(dllexport) void __stdcall DLLStart(P3D::IPdk* pdk)noexcept{try{if(!plugin)plugin=std::make_unique<Plugin>(pdk);}catch(...){OutputDebugStringW(L"P3D AIPilot PDK initialization failed\n");}}
extern "C" __declspec(dllexport) void __stdcall DLLStop()noexcept{try{plugin.reset();}catch(...){OutputDebugStringW(L"P3D AIPilot PDK shutdown failed\n");}}
