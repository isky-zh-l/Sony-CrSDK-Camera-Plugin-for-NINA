#pragma once
#include <atomic>
#include <cstdint>
#include <cstddef>
namespace sw_internal {
// Bounded MPSC mailbox. Slots are reclaimed only after consumer copy; no callback locks/allocation.
// Ordering is intentionally irrelevant: paths identify tasks, any unattributed failure fails closed.
struct Event { uint32_t kind=0,code=0,detail=0; bool invalid=false; wchar_t path[1024]{}; };
class Events {
    struct Slot { std::atomic<uint32_t> state{0}; Event value{}; } slots[64];
    std::atomic<uint64_t> ticket{0};
    bool push(uint32_t kind,uint32_t code,uint32_t detail,const wchar_t* path) noexcept {
        const auto start=ticket.fetch_add(1);
        for(size_t n=0;n<64;++n) {
            auto& slot=slots[(start+n)%64]; uint32_t empty=0;
            if(!slot.state.compare_exchange_strong(empty,1)) continue;
            slot.value.kind=kind; slot.value.code=code; slot.value.detail=detail; slot.value.invalid=false;
            size_t i=0;
            if(kind==1) {
                if(path) for(;i<1023 && path[i];++i) slot.value.path[i]=path[i];
                slot.value.invalid=!path || !i || (i==1023 && path[i]!=0);
            }
            slot.value.path[i]=0; slot.state.store(2,std::memory_order_release); return true;
        }
        overflow.store(true); return false;
    }
public:
    std::atomic<bool> overflow{false};
    void content(uint32_t notify,uint32_t handle,const wchar_t* path) noexcept { push(1,notify,handle,path); }
    void image(const wchar_t* path) noexcept { push(1,0,0,path); }
    void failure(uint32_t code,uint32_t detail=0) noexcept { push(2,code,detail,nullptr); }
    template<class F> void drain(F consume) {
        for(auto& slot:slots) if(slot.state.load(std::memory_order_acquire)==2) {
            const Event value=slot.value; slot.state.store(0,std::memory_order_release); consume(value);
        }
    }
};
inline bool fixed_iso(uint32_t value) noexcept {
    const uint32_t iso=value&0xFFFFFF, flags=value&0xFF000000;
    return iso>0 && iso<0xFFFFFF && (flags==0 || flags==0x10000000);
}
}
