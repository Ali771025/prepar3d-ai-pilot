#pragma once
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <cstdlib>
#include <stdexcept>
#include <type_traits>
#include <vector>
#include "WireSchema.hpp"

namespace aipilot {
constexpr uint32_t Magic=0x50494150, MaxPayload=4096;
enum class Type:uint16_t {Hello=1,HelloAck,Heartbeat,Telemetry,Command,CommandAck,Status,Error,Capabilities,Reset,Acquire,Release};
struct Message {Type type{};uint64_t sequence{};int64_t timestamp{};std::vector<uint8_t> payload;};
template<class T> void put(std::vector<uint8_t>& out,T value) {
    static_assert(std::is_trivially_copyable<T>::value,"wire primitive");
    const auto* p=reinterpret_cast<const uint8_t*>(&value);out.insert(out.end(),p,p+sizeof(T));
}
struct Reader {
    const std::vector<uint8_t>& bytes;size_t position=0;
    template<class T>T get(){if(position+sizeof(T)>bytes.size())throw std::runtime_error("truncated payload");T value;std::memcpy(&value,bytes.data()+position,sizeof(T));position+=sizeof(T);return value;}
};
inline uint32_t crc(const std::vector<uint8_t>& bytes){uint32_t v=~0u;for(size_t i=0;i<bytes.size();++i){if(i>=28&&i<32)continue;v^=bytes[i];for(int j=0;j<8;++j)v=(v>>1)^((v&1)?0xEDB88320u:0u);}return ~v;}
inline std::vector<uint8_t> encode(const Message& m){
    if(m.payload.size()>MaxPayload)throw std::runtime_error("payload limit");
    std::vector<uint8_t> out;out.reserve(32+m.payload.size());put(out,Magic);put(out,uint16_t(1));put(out,uint16_t(m.type));put(out,m.sequence);put(out,m.timestamp);put(out,uint32_t(m.payload.size()));put(out,0u);out.insert(out.end(),m.payload.begin(),m.payload.end());uint32_t c=crc(out);std::memcpy(out.data()+28,&c,4);return out;
}
inline Message decode(const std::vector<uint8_t>& bytes){
    Reader r{bytes};if(r.get<uint32_t>()!=Magic||r.get<uint16_t>()!=1)throw std::runtime_error("protocol mismatch");
    Message m;m.type=Type(r.get<uint16_t>());m.sequence=r.get<uint64_t>();m.timestamp=r.get<int64_t>();auto n=r.get<uint32_t>();auto c=r.get<uint32_t>();
    if(uint16_t(m.type)<1||uint16_t(m.type)>12||n>MaxPayload||bytes.size()!=32+n||crc(bytes)!=c)throw std::runtime_error("invalid frame");
    m.payload.assign(bytes.begin()+32,bytes.end());return m;
}
struct Control {uint64_t lease=0;int64_t validUntil=0;uint32_t source=0;int32_t priority=0;std::array<double,6> axes{};uint32_t gear=0;};
inline Control readControl(const std::vector<uint8_t>& payload){
    if(payload.size()!=76)throw std::runtime_error("command length");
    Reader r{payload};Control c;c.lease=r.get<uint64_t>();c.validUntil=r.get<int64_t>();c.source=r.get<uint32_t>();c.priority=r.get<int32_t>();for(auto& axis:c.axes)axis=r.get<double>();c.gear=r.get<uint32_t>();return c;
}
inline int32_t signedAxis(double value)noexcept{return int32_t(std::round(value*16383.0));}
inline int32_t unsignedAxis(double value)noexcept{return int32_t(std::round(value*16383.0));}
inline int32_t brakeAxis(double value)noexcept{return int32_t(std::round(-16383.0+value*32766.0));}
class CommandGate {
    uint64_t lease_=0, generation_=0,lastSequence_=0;
    int64_t until_=0;uint64_t untilTick_=0;
public:
    uint64_t lease()const noexcept{return lease_;}
    bool release()noexcept{bool active=lease_!=0;lease_=0;until_=0;untilTick_=0;lastSequence_=0;return active;}
    uint32_t acquire(bool enabled,bool ready,bool paused)noexcept{
        if(!enabled)return 1;
        if(!ready)return 5;
        if(paused)return 9;
        if(lease_)return 6;
        lease_=++generation_;until_=0;return 0;
    }
    uint32_t accept(const Control& c,uint64_t sequence,int64_t timestamp,int64_t now,uint64_t tick=0)noexcept{
        if(!lease_||c.lease!=lease_)return 6;
        if(sequence<=lastSequence_)return 3;
        if(timestamp>now+1000||timestamp<now-1000||c.validUntil<=now||c.validUntil>now+500||c.validUntil<timestamp)return 2;
        for(size_t i=0;i<c.axes.size();++i)if(!std::isfinite(c.axes[i])||c.axes[i]<(i<3?-1.0:0.0)||c.axes[i]>1.0)return 4;
        if(c.gear>1)return 4;
        lastSequence_=sequence;until_=c.validUntil;untilTick_=tick+uint64_t(c.validUntil-now);return 0;
    }
    bool expired(int64_t now,uint64_t tick=0)const noexcept{return lease_&&until_&&(now>=until_||tick>=untilTick_);}
};
struct Snapshot {uint64_t supported=0;uint32_t flags=0,aircraftId=0,rate=25;std::array<double,FieldCount> values{};std::array<char,256> title{};uint16_t titleLength=0;uint64_t frame=0;int64_t timestamp=0;bool controls=false,ownedTarget=false;};
inline std::vector<uint8_t> telemetry(const Snapshot& s){std::vector<uint8_t> out;put(out,s.supported);put(out,s.flags);put(out,s.aircraftId);for(double v:s.values)put(out,v);put(out,s.titleLength);out.insert(out.end(),s.title.begin(),s.title.begin()+s.titleLength);return out;}
}
