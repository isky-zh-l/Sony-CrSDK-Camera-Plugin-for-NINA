#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include "sony_wrapper.h"
#include "sony_features.h"
#include "CrImageDataBlock.h"
#include "CrControlCode.h"
#include "CameraRemote_SDK.h"
#include "IDeviceCallback.h"
#include <atomic>
#include <chrono>
#include <thread>
#include <mutex>
#include <cstring>
#include <cstdio>
#include <algorithm>
#include <limits>
#include <cfenv>
#include <vector>
#include <cmath>
#include <filesystem>
#include <array>
#include <cwchar>
#include <cstdint>
#include "download_events.h"
namespace sdk = SCRSDK;
using Clock = std::chrono::steady_clock;
using namespace std::chrono_literals;
static_assert(sizeof(void*)==8, "x64 only");
namespace {
// No allocation, I/O, locks or SDK calls in callbacks. Object retained for process lifetime.
struct Callback final : sdk::IDeviceCallback {
    std::atomic<bool> connected{false}, disconnected{false};
    std::atomic<uint32_t> error{0}, disconnect_error{0};
    sw_internal::Events events;
    sw_internal::Events content_events;
    std::atomic<uint32_t> feature_warning{0};
    void OnNotifyContentsTransfer(CrInt32u notify,sdk::CrContentHandle handle,CrChar* name) noexcept override {
        if(notify==sdk::CrNotify_ContentsTransfer_Start)return;
        content_events.content(notify,handle,name);
    }
    std::atomic<bool> untrusted{false};
    std::atomic<uint64_t> async_failure{0};
    const uint64_t generation;
    explicit Callback(uint64_t value):generation(value) {}
    void OnCompleteDownload(CrChar* name,CrInt32u type) noexcept override {
        if(type==0xFFFFFFFF) events.image(name);
    }
    void OnWarning(CrInt32u warning) noexcept override {
        if(warning==sdk::CrWarning_File_StorageFull || warning==sdk::CrWarning_SetFileName_Failed || warning==sdk::CrWarning_GetImage_Failed)
            events.failure(warning);
    }
    void OnWarningExt(CrInt32u warning,CrInt32 api,CrInt32 command,CrInt32 detail) noexcept override {
        if(command!=sdk::CrCommandId_Release && (warning==sdk::CrWarningExt_OperationInvalid || (warning==sdk::CrWarningExt_OperationResults && detail!=sdk::CrWarningExt_OperationResultsParam_OK))) feature_warning.store(warning);
        if(api==sdk::CrSdkApi_SendCommand && command==sdk::CrCommandId_Release &&
            (warning==sdk::CrWarningExt_OperationInvalid || (warning==sdk::CrWarningExt_OperationResults && detail!=sdk::CrWarningExt_OperationResultsParam_OK)))
            events.failure(warning,static_cast<uint32_t>(detail));
    }
    void OnConnected(sdk::DeviceConnectionVersioin) noexcept override { connected.store(true); }
    void OnDisconnected(CrInt32u e) noexcept override {
        disconnect_error.store(e); connected.store(false); disconnected.store(true);
    }
    void OnError(CrInt32u e) noexcept override { error.store(e); }
};
Callback* cb=nullptr;
std::mutex gate;
sdk::ICrEnumCameraObjectInfo* cameras=nullptr;
sdk::CrDeviceHandle device=0;
bool initialized=false, attempted=false, ready=false, fault=false;
uint64_t next_generation=0, unsafe_capture_id=0;
bool preview_active=false,movie_owned=false,feature_uncertain=false,transfer_active=false;
uint32_t connection_mode=SW_MODE_REMOTE;
int32_t feature_idle(SwError* e);
int32_t cleanup_features(SwError* e,uint32_t ms);
void reset_features();
void drain_capture_events();
void reset_connected_capture();
uint64_t disconnect_capture_id() noexcept;
bool capture_is_waiting() noexcept;
uint64_t capture_current_id() noexcept;
void capture_on_disconnect() noexcept;
void start_exposure_worker();
void stop_exposure_worker();
void exposure_tick() noexcept;
int32_t boundary_exception(SwError* e,uint32_t stage) noexcept;
int32_t result(SwError* e,int32_t code,uint32_t stage,uint32_t sony,const char* msg) noexcept {
    if(e) { *e={}; e->size=sizeof(*e); e->code=code; e->sony_code=sony; e->stage=stage;
        std::snprintf(e->message,sizeof(e->message),"%s",msg); }
    return code;
}
// Sony Init was observed changing this thread to FE_TOWARDZERO (768).
// Keep that implementation detail from leaking across the public C ABI.
struct FloatingEnvironment {
    std::fenv_t saved{};
    FloatingEnvironment() { std::fegetenv(&saved); }
    ~FloatingEnvironment() noexcept { std::fesetenv(&saved); }
};
template<class F> int32_t boundary(SwError* e,uint32_t stage,F body) noexcept {
    if(!e || e->size!=sizeof(*e)) return SW_ARGUMENT;
    try { FloatingEnvironment floating_environment; std::lock_guard<std::mutex> lock(gate); result(e,SW_OK,stage,0,"OK"); return body(); }
    catch(...) { return boundary_exception(e,stage); }
}
bool valid_timeout(uint32_t ms) { return ms>0 && ms<=60000; }
int32_t connected_check(SwError* e,uint32_t stage) {
    if(!initialized || !device || !cb || fault) return result(e,SW_STATE,stage,0,"No usable connected session");
    if(cb->error.load() || cb->disconnected.load()) {
        fault=true; ready=false;
        return result(e,SW_SDK,stage,cb->error.load()?cb->error.load():cb->disconnect_error.load(),"SDK callback reported error/disconnection");
    }
    if(!cb->connected.load()) return result(e,SW_STATE,stage,0,"Connection has not completed");
    return SW_OK;
}
// Copy numeric data only, never retain SDK property objects or their internal pointers.
struct Values { uint64_t iso=0, shutter=0, aperture=0; };
int32_t read_values(Values& v,SwError* e,uint32_t stage) {
    CrInt32u codes[]={sdk::CrDeviceProperty_IsoSensitivity,sdk::CrDeviceProperty_ShutterSpeed,sdk::CrDeviceProperty_FNumber};
    sdk::CrDeviceProperty* list=nullptr; CrInt32 count=0;
    auto rc=sdk::GetSelectDeviceProperties(device,3,codes,&list,&count);
    struct Guard {
        sdk::CrDeviceProperty*& p;
        ~Guard() noexcept { if(p) { try { sdk::ReleaseDeviceProperties(device,p); } catch(...) { fault=true; } } }
    } guard{list};
    if(rc) return result(e,SW_SDK,stage,rc,"GetSelectDeviceProperties failed");
    uint32_t found=0;
    for(CrInt32 i=0; list && i<count; ++i) {
        if(!list[i].IsGetEnableCurrentValue()) continue;
        switch(list[i].GetCode()) {
        case sdk::CrDeviceProperty_IsoSensitivity: v.iso=list[i].GetCurrentValue(); found|=1; break;
        case sdk::CrDeviceProperty_ShutterSpeed: v.shutter=list[i].GetCurrentValue(); found|=2; break;
        case sdk::CrDeviceProperty_FNumber: v.aperture=list[i].GetCurrentValue(); found|=4; break;
        }
    }
    if((found&3)!=3) return result(e,SW_UNSUPPORTED,stage,0,"Required properties absent or unreadable");
    auto release_rc=sdk::ReleaseDeviceProperties(device,list); list=nullptr;
    if(release_rc) { fault=true; return result(e,SW_SDK,stage,release_rc,"ReleaseDeviceProperties failed; session quarantined"); }
    return SW_OK;
}

// Own only the SDK list, never copy a property containing SDK-managed pointers.
struct PropertyList {
    sdk::CrDeviceProperty* list=nullptr;
    CrInt32 count=0;
    ~PropertyList() noexcept {
        if(list) { try { if(sdk::ReleaseDeviceProperties(device,list)) fault=true; } catch(...) { fault=true; } }
    }
    int32_t close(SwError* e,uint32_t stage) {
        if(!list) return SW_OK;
        auto rc=sdk::ReleaseDeviceProperties(device,list); list=nullptr;
        if(rc) { fault=true; return result(e,SW_SDK,stage,rc,"ReleaseDeviceProperties failed; session quarantined"); }
        return SW_OK;
    }
    int32_t load(CrInt32u code,SwError* e,uint32_t stage) {
        auto rc=sdk::GetSelectDeviceProperties(device,1,&code,&list,&count);
        if(rc) return result(e,SW_SDK,stage,rc,"GetSelectDeviceProperties failed");
        if(!list || count!=1 || list[0].GetCode()!=code || !list[0].IsGetEnableCurrentValue())
            return result(e,SW_UNSUPPORTED,stage,0,"Required property absent or unreadable");
        return SW_OK;
    }
};
int32_t ready_check(SwError* e,uint32_t stage) {
    auto status=connected_check(e,stage); if(status) return status;
    return ready?SW_OK:result(e,SW_STATE,stage,0,"Call sw_wait_ready first");
}
bool fixed_iso(uint32_t value) { return sw_internal::fixed_iso(value); }
bool ordinary_shutter(uint32_t value) {
    return value!=sdk::CrShutterSpeed_Bulb && value!=sdk::CrShutterSpeed_Nothing &&
        (value>>16)>0 && (value&0xFFFF)>0;
}
double seconds(uint32_t value) {
    std::fesetround(FE_TONEAREST);
    return double(value>>16)/double(value&0xFFFF);
}
int32_t candidates(sdk::CrDeviceProperty& prop,bool iso,std::vector<uint32_t>& raw,SwError* e,uint32_t stage) {
    if(!prop.IsSetEnableCurrentValue()) return result(e,SW_UNSUPPORTED,stage,0,"Property is not writable in current camera state");
    if(prop.GetValueType()!=sdk::CrDataType_UInt32Array)
        return result(e,SW_UNSUPPORTED,stage,0,"Expected UInt32Array candidate list; no guessed conversion");
    const auto bytes=prop.GetValueSize(); const auto data=prop.GetValues();
    if((bytes%sizeof(uint32_t)) || (bytes && !data))
        return result(e,SW_INTERNAL,stage,0,"Malformed SDK candidate list");
    for(uint32_t offset=0;offset<bytes;offset+=sizeof(uint32_t)) {
        uint32_t value=0; std::memcpy(&value,data+offset,sizeof(value));
        if(!(iso?fixed_iso(value):ordinary_shutter(value))) continue;
        bool duplicate=false;
        for(auto prior:raw) if(iso?(prior&0xFFFFFF)==(value&0xFFFFFF):seconds(prior)==seconds(value)) { duplicate=true; break; }
        if(!duplicate) raw.push_back(value);
    }
    return SW_OK;
}
template<class T> int32_t query_candidates(bool iso,T* values,uint32_t capacity,uint32_t* count,SwError* e) {
    if(!count || (!values && capacity)) return result(e,SW_ARGUMENT,SW_SUPPORT,0,"Invalid candidate output buffer/count");
    *count=0;
    auto status=ready_check(e,SW_SUPPORT); if(status) return status;
    PropertyList props;
    status=props.load(iso?sdk::CrDeviceProperty_IsoSensitivity:sdk::CrDeviceProperty_ShutterSpeed,e,SW_SUPPORT);
    if(status) return status;
    std::vector<uint32_t> raw;
    status=candidates(props.list[0],iso,raw,e,SW_SUPPORT); if(status) return status;
    status=props.close(e,SW_SUPPORT); if(status) return status;
    *count=static_cast<uint32_t>(raw.size());
    if(!values && !capacity) return SW_OK;
    if(capacity<raw.size()) return result(e,SW_BUFFER,SW_SUPPORT,0,"Candidate array too small; required count returned; array untouched");
    for(size_t i=0;i<raw.size();++i) values[i]=static_cast<T>(iso?double(raw[i]&0xFFFFFF):seconds(raw[i]));
    return SW_OK;
}
// Set request is sent once. A matching readback, not enqueue success, completes it.
int32_t set_candidate(bool iso,uint32_t requested_iso,double requested_seconds,uint32_t ms,
    uint32_t* actual_iso,double* actual_seconds,SwError* e) {
    if(!valid_timeout(ms) || (iso?!actual_iso:!actual_seconds))
        return result(e,SW_ARGUMENT,SW_SET,0,"Invalid actual output or timeout_ms (1..60000)");
    if(!iso && (!std::isfinite(requested_seconds) || requested_seconds<=0))
        return result(e,SW_ARGUMENT,SW_SET,0,"Ordinary exposure must be finite and positive seconds");
    auto status=ready_check(e,SW_SET); if(status) return status;
    if(movie_owned||transfer_active||feature_uncertain||connection_mode!=SW_MODE_REMOTE)return result(e,SW_STATE,SW_SET,0,"Movie/transfer/uncertain/non-remote session blocks setters");
    if(unsafe_capture_id) { auto rc=result(e,SW_STATE,SW_SET,0,"Capture state unconfirmed; reconcile before setting"); e->capture_id=unsafe_capture_id; return rc; }
    if(capture_is_waiting()) {
        auto rc=result(e,SW_STATE,SW_SET,0,"Cannot set parameters during capture"); e->capture_id=capture_current_id(); return rc;
    }
    const auto deadline=Clock::now()+std::chrono::milliseconds(ms);
    const CrInt32u code=iso?sdk::CrDeviceProperty_IsoSensitivity:sdk::CrDeviceProperty_ShutterSpeed;
    PropertyList props;
    status=props.load(code,e,SW_SET); if(status) return status;
    std::vector<uint32_t> raw;
    status=candidates(props.list[0],iso,raw,e,SW_SET); if(status) return status;
    uint32_t target=0; bool found=false;
    for(auto value:raw) if(iso?(value&0xFFFFFF)==requested_iso:seconds(value)==requested_seconds) { target=value; found=true; break; }
    if(!found) return result(e,SW_UNSUPPORTED,SW_SET,0,"Request not an exact current candidate; no write submitted");
    if(Clock::now()>=deadline) return result(e,SW_TIMEOUT,SW_SET,0,"Deadline expired before write; no write submitted");
    sdk::CrDeviceProperty property=props.list[0];
    status=props.close(e,SW_SET); if(status) return status;
    property.SetCurrentValue(target);
    auto rc=sdk::SetDeviceProperty(device,&property);
    if(rc) { fault=true; return result(e,SW_SDK,SW_SET,rc,"SetDeviceProperty failed; state unconfirmed; session quarantined"); }
    status=props.close(e,SW_SET); if(status) return status;
    for(;;) {
        status=connected_check(e,SW_VERIFY); if(status) { fault=true; return status; }
        PropertyList readback;
        status=readback.load(code,e,SW_VERIFY);
        if(status) { fault=true; return status; } // no blanket startup retry after writes
        const auto current=readback.list[0].GetCurrentValue();
        status=readback.close(e,SW_VERIFY); if(status) return status;
        if(current==target) {
            if(iso) *actual_iso=static_cast<uint32_t>(current)&0xFFFFFF;
            else *actual_seconds=seconds(static_cast<uint32_t>(current));
            return result(e,SW_OK,SW_VERIFY,0,"Requested value confirmed by camera property readback");
        }
        if(Clock::now()>=deadline) {
            fault=true; ready=false;
            return result(e,SW_TIMEOUT,SW_VERIFY,0,"Readback did not confirm exact request; state unconfirmed; disconnect required");
        }
        std::this_thread::sleep_until(std::min(deadline,Clock::now()+100ms));
    }
}

bool utf8(const wchar_t* src,char* dst,int capacity) {
    if(!src) { dst[0]=0; return true; }
    return WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,src,-1,dst,capacity,nullptr,nullptr)>0;
}
}
int32_t SW_CALL sw_initialize(SwError* e) {
    return boundary(e,SW_INIT,[&]()->int32_t {
        if(initialized || device) return result(e,SW_STATE,SW_INIT,0,"Already initialized or active device");
        // Pin this DLL: late SDK callbacks cannot target unloaded code, even on failure.
        HMODULE module=nullptr;
        if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(&sw_initialize),&module)) return result(e,SW_INTERNAL,SW_INIT,0,"Cannot pin wrapper module");
        if(!sdk::Init()) return result(e,SW_SDK,SW_INIT,0,"SDK Init returned false (no numeric SDK error)");
        initialized=true; fault=false; attempted=false; start_exposure_worker(); return SW_OK;
    });
}
int32_t SW_CALL sw_enumerate(uint32_t* count,SwError* e) {
    return boundary(e,SW_ENUM,[&]()->int32_t {
        if(!count) return result(e,SW_ARGUMENT,SW_ENUM,0,"count is required");
        *count=0;
        if(!initialized || attempted || fault) return result(e,SW_STATE,SW_ENUM,0,"Enumeration requires initialized unused session");
        if(cameras) { cameras->Release(); cameras=nullptr; }
        auto rc=sdk::EnumCameraObjects(&cameras,3);
        if(rc) return result(e,SW_SDK,SW_ENUM,rc,"EnumCameraObjects failed");
        *count=cameras?cameras->GetCount():0; return SW_OK;
    });
}
int32_t SW_CALL sw_camera_info(uint32_t index,SwCameraInfo* info,SwError* e) {
    return boundary(e,SW_ENUM,[&]()->int32_t {
        if(!info || info->size!=sizeof(*info)) return result(e,SW_ARGUMENT,SW_ENUM,0,"Invalid camera info size");
        if(!initialized || !cameras || fault) return result(e,SW_STATE,SW_ENUM,0,"No enumeration");
        if(index>=cameras->GetCount()) return result(e,SW_ARGUMENT,SW_ENUM,0,"Camera index out of range");
        auto camera=cameras->GetCameraObjectInfo(index);
        SwCameraInfo out{}; out.size=sizeof(out); out.index=index;
        if(!camera) return result(e,SW_INTERNAL,SW_ENUM,0,"Null camera info");
        if(!utf8(camera->GetModel(),out.model,sizeof(out.model)) || !utf8(camera->GetConnectionTypeName(),out.connection,sizeof(out.connection)))
            return result(e,SW_BUFFER,SW_ENUM,0,"UTF-8 camera text exceeds ABI capacity or is invalid");
        *info=out; return SW_OK;
    });
}
static void release_failed_attempt() noexcept {
    // A failed attempt leaves no device handle. Keeping "attempted" would poison enum/connect/shutdown
    // until the process restarts, so reset it and let the caller retry or shut down cleanly.
    if(device) return;
    if(cameras) { cameras->Release(); cameras=nullptr; }
    attempted=false; fault=false; ready=false;
}
static int32_t connect_impl(uint32_t index,uint32_t ms,SwError* e,uint32_t mode) {
    return boundary(e,SW_CONNECT,[&]()->int32_t {
        if(mode>1) return result(e,SW_ARGUMENT,SW_CONNECT,0,"Unsupported project connection mode");
        if(!valid_timeout(ms)) return result(e,SW_ARGUMENT,SW_CONNECT,0,"timeout_ms must be 1..60000");
        if(!initialized || !cameras || attempted || fault) return result(e,SW_STATE,SW_CONNECT,0,"Connect requires unused enumerated session");
        if(index>=cameras->GetCount()) return result(e,SW_ARGUMENT,SW_CONNECT,0,"Camera index out of range");
        if(unsafe_capture_id) { auto rc=result(e,SW_STATE,SW_CONNECT,0,"Unconfirmed prior capture; reconnect cannot prove camera idle"); e->capture_id=unsafe_capture_id; return rc; }
        // unsafe_capture_id only survives while a device handle exists; see sw_disconnect.
        cb=new Callback(++next_generation); // never reuse an old connection's callback/queue
        attempted=true;
        auto deadline=Clock::now()+std::chrono::milliseconds(ms);
        auto rc=sdk::Connect(const_cast<sdk::ICrCameraObjectInfo*>(cameras->GetCameraObjectInfo(index)),cb,&device,
            mode==SW_MODE_REMOTE?sdk::CrSdkControlMode_Remote:sdk::CrSdkControlMode_ContentsTransfer,sdk::CrReconnecting_OFF);
        if(rc) { auto code=result(e,SW_SDK,SW_CONNECT,rc,"Connect failed; no automatic retry"); release_failed_attempt(); return code; }
        while(!cb->connected.load() && !cb->disconnected.load() && !cb->error.load() && Clock::now()<deadline) std::this_thread::sleep_for(20ms);
        if(!cb->connected.load() && !cb->disconnected.load() && !cb->error.load()) {
            auto code=result(e,SW_TIMEOUT,SW_CONNECT,0,"Stopped waiting; try again; if it repeats, unplug and replug the camera"); release_failed_attempt(); return code;
        }
        auto status=connected_check(e,SW_CONNECT);
        if(status){ release_failed_attempt(); return status; }
        if(!status) { reset_connected_capture(); reset_features(); connection_mode=mode; if(mode==SW_MODE_CONTENTS)ready=true; }
        return status;
    });
}
int32_t SW_CALL sw_connect(uint32_t index,uint32_t ms,SwError* e) { return connect_impl(index,ms,e,SW_MODE_REMOTE); }
int32_t SW_CALL sw_connect_mode(uint32_t index,uint32_t mode,uint32_t ms,SwError* e) { return connect_impl(index,ms,e,mode); }
int32_t SW_CALL sw_wait_ready(uint32_t ms,SwError* e) {
    return boundary(e,SW_READY,[&]()->int32_t {
        if(!valid_timeout(ms)) return result(e,SW_ARGUMENT,SW_READY,0,"timeout_ms must be 1..60000");
        auto status=connected_check(e,SW_READY); if(status) return status;
        auto deadline=Clock::now()+std::chrono::milliseconds(ms);
        for(;;) {
            status=connected_check(e,SW_READY); if(status) return status;
            Values v; status=read_values(v,e,SW_READY);
            if(!status) { ready=true; return result(e,SW_OK,SW_READY,0,"Properties ready"); }
            // Retry only this documented startup transient, only during readiness.
            if(e->sony_code!=sdk::CrError_Api_InvalidCalled) return status;
            if(Clock::now()>=deadline) { ready=false; fault=true;
                return result(e,SW_TIMEOUT,SW_READY,e->sony_code,"Property readiness timed out; disconnect required"); }
            std::this_thread::sleep_until(std::min(deadline,Clock::now()+200ms));
        }
    });
}
int32_t SW_CALL sw_read_parameters(SwParameters* p,SwError* e) {
    return boundary(e,SW_READ,[&]()->int32_t {
        if(!p || p->size!=sizeof(*p)) return result(e,SW_ARGUMENT,SW_READ,0,"Invalid parameters size");
        auto status=connected_check(e,SW_READ); if(status) return status;
        if(!ready) return result(e,SW_STATE,SW_READ,0,"Call sw_wait_ready first");
        Values v; status=read_values(v,e,SW_READ); if(status) return status;
        std::fesetround(FE_TONEAREST);
        SwParameters out{}; out.size=sizeof(out);
        auto iso=static_cast<uint32_t>(v.iso);
        out.iso=iso&0xFFFFFF;
        out.iso_kind=out.iso==0xFFFFFF?SW_ISO_AUTO:(fixed_iso(iso)?SW_ISO_FIXED:SW_ISO_SPECIAL);
        if(out.iso_kind!=SW_ISO_FIXED) out.iso=0;
        auto shutter=static_cast<uint32_t>(v.shutter);
        out.exposure_seconds=std::numeric_limits<double>::quiet_NaN();
        if(shutter==sdk::CrShutterSpeed_Bulb) out.shutter_kind=SW_SHUTTER_BULB;
        else if(shutter==sdk::CrShutterSpeed_Nothing || !(shutter&0xFFFF)) out.shutter_kind=SW_SHUTTER_UNAVAILABLE;
        else { out.shutter_kind=SW_SHUTTER_SECONDS; out.exposure_seconds=double(shutter>>16)/double(shutter&0xFFFF); }
        out.f_number=v.aperture?double(v.aperture)/100.0:std::numeric_limits<double>::quiet_NaN();
        *p=out; return SW_OK;
    });
}
int32_t SW_CALL sw_disconnect(uint32_t ms,SwError* e) {
    return boundary(e,SW_DISCONNECT,[&]()->int32_t {
        if(!valid_timeout(ms)) return result(e,SW_ARGUMENT,SW_DISCONNECT,0,"timeout_ms must be 1..60000");
        if(!initialized || !device) return result(e,SW_STATE,SW_DISCONNECT,0,"No device to disconnect");
        auto feature_cleanup=cleanup_features(e,ms); if(feature_cleanup)return feature_cleanup;
        const auto interrupted=disconnect_capture_id();
        capture_on_disconnect();
        auto failure=[&](int32_t code,uint32_t sony,const char* message) {
            auto rc=result(e,code,SW_DISCONNECT,sony,message); e->capture_id=interrupted; return rc;
        };
        ready=false; auto deadline=Clock::now()+std::chrono::milliseconds(ms);
        if(!cb->disconnected.load()) {
            auto rc=sdk::Disconnect(device);
            if(rc) { fault=true; return failure(SW_SDK,rc,"Disconnect failed; session retained; do not unload DLL"); }
        }
        while(!cb->disconnected.load() && Clock::now()<deadline) std::this_thread::sleep_for(20ms);
        if(!cb->disconnected.load()) { fault=true; return failure(SW_TIMEOUT,0,"Stopped waiting; disconnection unconfirmed; session retained"); }
        auto rc=sdk::ReleaseDevice(device);
        if(rc) { device=0; reset_features(); attempted=false; fault=false; unsafe_capture_id=0; return failure(SW_SDK,rc,"ReleaseDevice failed after the link was lost; handle dropped; camera idle not confirmed"); }
        device=0; reset_features();
        // The device handle is gone, so this session is over even if the SDK reported an error while
        // disconnecting (typically a pulled cable). Leaving attempted=true would block enumeration,
        // reconnection and shutdown until the process restarts; a new connection re-runs every
        // capture precondition, so the stale unconfirmed capture id is cleared as well.
        attempted=false; fault=false; unsafe_capture_id=0;
        if(cb->disconnect_error.load()) { if(interrupted) { auto status=result(e,SW_CANCELLED,SW_DISCONNECT,cb->disconnect_error.load(),"Disconnected with SDK error during capture; device released; camera idle not confirmed"); e->capture_id=interrupted; return status; } return result(e,SW_OK,SW_DISCONNECT,cb->disconnect_error.load(),"Disconnected with SDK error; device released"); }
        if(interrupted) { auto status=result(e,SW_CANCELLED,SW_DISCONNECT,0,"Device disconnected/released, but capture was interrupted; camera idle not confirmed"); e->capture_id=interrupted; return status; }
        return SW_OK;
    });
}
int32_t SW_CALL sw_shutdown(SwError* e) {
    return boundary(e,SW_SHUTDOWN,[&]()->int32_t {
        if(!initialized || device) return result(e,SW_STATE,SW_SHUTDOWN,0,"Shutdown requires initialized SDK and released device");
        // No active SDK handle remains. SDK shutdown is not a declaration of camera idle.
        if(attempted) return result(e,SW_STATE,SW_SHUTDOWN,0,"Connection attempt not safely released");
        if(cameras) { cameras->Release(); cameras=nullptr; }
        stop_exposure_worker();
        if(!sdk::Release()) { fault=true; return result(e,SW_SDK,SW_SHUTDOWN,0,"SDK Release returned false"); }
        initialized=false; fault=false; return SW_OK;
    });
}




int32_t SW_CALL sw_get_supported_iso(uint32_t* values,uint32_t capacity,uint32_t* count,SwError* e) {
    return boundary(e,SW_SUPPORT,[&] { return query_candidates(true,values,capacity,count,e); });
}
int32_t SW_CALL sw_get_supported_exposure_seconds(double* values,uint32_t capacity,uint32_t* count,SwError* e) {
    return boundary(e,SW_SUPPORT,[&] { return query_candidates(false,values,capacity,count,e); });
}
int32_t SW_CALL sw_set_iso(uint32_t requested,uint32_t ms,uint32_t* actual,SwError* e) {
    return boundary(e,SW_SET,[&] { return set_candidate(true,requested,0,ms,actual,nullptr,e); });
}
int32_t SW_CALL sw_set_exposure_seconds(double requested,uint32_t ms,double* actual,SwError* e) {
    return boundary(e,SW_SET,[&] { return set_candidate(false,0,requested,ms,nullptr,actual,e); });
}

namespace {
namespace fs = std::filesystem;
struct CaptureTask {
    bool used=false, candidate_received=false, info_known=false, up_confirmed=false, verified=false, reconciled=false;
    Callback* session=nullptr;
    HANDLE reader=INVALID_HANDLE_VALUE;
    uint64_t read_total=0, expected_bytes=0, verified_bytes=0;
    std::wstring verified_path;
    uint32_t notification_count=0;
    uint32_t asynchronous_failure=0, asynchronous_detail=0;
    SwCaptureResult output{};
    SwExposureInfo exposure{};
    bool v2=false, bulb=false, button_down=false, triggered=false;
    bool shutter_dirty=false, timer_dirty=false, restore_write_pending=false;
    uint64_t original_shutter=0, original_timer=0;
    uint32_t restore_property=0; uint64_t restore_target=0;
    double exposure_limit=0, download_limit=0;
    Clock::time_point down_at{}, planned_end{}, exposure_deadline{}, restore_deadline{};
    Clock::time_point deadline{}, stable_since{};
    std::wstring directory,prefix,candidate;
    BY_HANDLE_FILE_INFORMATION last_info{};
} capture;
struct RetiredCapture { SwCaptureResult output; SwExposureInfo exposure; std::wstring directory,prefix; Callback* session; };
std::vector<RetiredCapture> retired;
uint64_t last_capture_id=0;
void close_capture_reader() noexcept { if(capture.reader!=INVALID_HANDLE_VALUE) { CloseHandle(capture.reader); capture.reader=INVALID_HANDLE_VALUE; } }
void archive_capture() {
    if(capture.used) retired.push_back({capture.output,capture.exposure,capture.directory,capture.prefix,capture.session});
    close_capture_reader(); capture=CaptureTask{};
}
void reset_connected_capture() { archive_capture(); }
uint64_t disconnect_capture_id() noexcept { return capture.used && (capture.output.state==SW_CAPTURE_WAITING || unsafe_capture_id==capture.output.capture_id)?capture.output.capture_id:0; }
bool capture_is_waiting() noexcept { return capture.used && capture.output.state==SW_CAPTURE_WAITING; }
uint64_t capture_current_id() noexcept { return capture.output.capture_id; }
int32_t capture_error(SwError* e,int32_t code,uint32_t stage,uint32_t sony,const char* msg,uint64_t id) noexcept {
    auto rc=result(e,code,stage,sony,msg); e->capture_id=id; return rc;
}
void release_bulb(uint32_t reason) noexcept;
int32_t restore_step(SwError* e);
int32_t idle_proof(SwError* e,uint32_t stage);
int32_t finish_capture_failure(SwError* e,int32_t code,uint32_t stage,uint32_t sony,const char* msg,bool uncertain=true) noexcept {
    if(capture_is_waiting()) {
        if(capture.button_down) release_bulb(code==SW_CANCELLED?SW_END_CANCEL:(code==SW_TIMEOUT?SW_END_TIMEOUT:(code==SW_ENDED_EARLY?SW_END_EARLY:SW_END_ERROR)));
        uncertain=uncertain || capture.shutter_dirty || capture.timer_dirty;
        capture.exposure.phase=SW_EXPOSURE_TERMINAL;
        capture_error(e,code,stage,sony,msg,capture.output.capture_id);
        capture.output.state=code==SW_CANCELLED?SW_CAPTURE_CANCELLED_UNCONFIRMED:
            (code==SW_TIMEOUT && uncertain?SW_CAPTURE_TIMED_OUT_UNCONFIRMED:(code==SW_ENDED_EARLY?SW_CAPTURE_ENDED_EARLY_UNCONFIRMED:SW_CAPTURE_FAILED));
        capture.output.failure=*e; capture.output.arw_path[0]=0; capture.output.file_bytes=0;
        capture.output.download_notifications=capture.notification_count;
        close_capture_reader();
        if(uncertain) unsafe_capture_id=capture.output.capture_id;
    } else if(capture.used) *e=capture.output.failure;
    return code;
}
int32_t boundary_exception(SwError* e,uint32_t stage) noexcept {
    try {
        std::lock_guard<std::mutex> lock(gate); fault=true; ready=false;
        if(capture_is_waiting())return finish_capture_failure(e,SW_INTERNAL,stage,0,"C++ exception; bounded Bulb Up attempted; session quarantined");
        return result(e,SW_INTERNAL,stage,0,"C++ exception; session quarantined; inspect camera before recovery");
    }catch(...) {return result(e,SW_INTERNAL,stage,0,"Exception boundary failure; camera state unconfirmed");}
}
void capture_on_disconnect() noexcept {
    if(capture_is_waiting()) {
        SwError e{sizeof(SwError)};
        if(capture.button_down) release_bulb(SW_END_DISCONNECT);
        finish_capture_failure(&e,SW_CANCELLED,SW_CAPTURE_CANCEL,0,"Disconnect ended capture waiting; camera/transfer state not confirmed");
    }
}
int32_t publish_capture(SwCaptureResult* out,SwError* e) noexcept {
    if(capture_is_waiting()) {
        *out=capture.output;
        return capture_error(e,SW_PENDING,SW_CAPTURE_DOWNLOAD,0,"Capture pending; continue bounded wait calls",out->capture_id);
    }
    *out=capture.output;
    if(out->state==SW_CAPTURE_SUCCEEDED)
        return capture_error(e,SW_OK,SW_CAPTURE_FILE,0,"This capture's file download is complete and readable; RAW decoding is external",out->capture_id);
    *e=out->failure; return e->code;
}
template<class F> int32_t capture_boundary(uint64_t requested,SwCaptureResult* out,SwError* e,uint32_t stage,F body) noexcept {
    return boundary(e,stage,[&]()->int32_t {
        if(!out || out->size!=sizeof(*out))
            return capture_error(e,SW_ARGUMENT,stage,0,"Invalid capture result size",requested);
        // Even a rejected call must not leave the caller's old successful ARW path visible.
        *out={}; out->size=sizeof(*out);
        try { return body(); }
        catch(...) {
            fault=true;
            if(capture_is_waiting() && (!requested || requested==capture.output.capture_id)) {
                finish_capture_failure(e,SW_INTERNAL,stage,0,"C++ exception during capture; state unconfirmed; disconnect required");
                *out=capture.output; return SW_INTERNAL;
            }
            return capture_error(e,SW_INTERNAL,stage,0,"C++ exception; session quarantined; restart process",requested);
        }
    });
}
int32_t capture_read_property(CrInt32u code,uint64_t& value,SwError* e) {
    PropertyList p; auto rc=p.load(code,e,SW_CAPTURE_CHECK); if(rc) return rc;
    value=p.list[0].GetCurrentValue(); return p.close(e,SW_CAPTURE_CHECK);
}
int32_t capture_preconditions(SwError* e) {
    auto feature_status=feature_idle(e); if(feature_status)return feature_status;
    struct Required { CrInt32u code; uint64_t value; const char* message; };
    const Required required[]={
        {sdk::CrDeviceProperty_S2,sdk::CrLockIndicator_Unlocked,"S2 must actually read back Unlocked before trigger"},
        {sdk::CrDeviceProperty_ExposureProgramMode,sdk::CrExposure_M_Manual,"Requires M manual exposure mode; no automatic mode changes"},
        {sdk::CrDeviceProperty_FileType,sdk::CrFileType_Raw,"Requires RAW only, not RAW+JPEG/HEIF"},
        {sdk::CrDeviceProperty_DriveMode,sdk::CrDrive_Single,"Requires Single drive mode, no burst/bracket/timer"},
        {sdk::CrDeviceProperty_Still_Image_Trans_Size,sdk::CrPropertyStillImageTransSize_Original,"Requires original-size still image transfer"},
        {sdk::CrDeviceProperty_RemoteSaveImageSize,sdk::CrRemoteSaveImageSize_LargeSize,"Requires Large remote save image size"},
        {sdk::CrDeviceProperty_RecordingState,sdk::CrMovie_Recording_State_Not_Recording,"Requires no active movie recording"}
    };
    for(const auto& condition:required) {
        uint64_t value=0; auto rc=capture_read_property(condition.code,value,e); if(rc) return rc;
        if(value!=condition.value) return result(e,SW_UNSUPPORTED,SW_CAPTURE_CHECK,0,condition.message);
    }
    // Hardware finding 2026-10-24 (A7 V + electronic lens): with AF-C active the camera did not
    // release the shutter for a 120 ms Down/Up press and produced no file, no warning and no error.
    // Manual focus is the astronomy workflow anyway, so it is enforced before the trigger.
    // An electronic-lens-absent rig (telescope) cannot report FocusMode; that is not a violation.
    // Only real autofocus tokens (AF_S..PF) are refused; an unrecognised token is not treated as AF.
    {
        PropertyList focus;
        auto focus_rc=focus.load(sdk::CrDeviceProperty_FocusMode,e,SW_CAPTURE_CHECK);
        if(focus_rc==SW_OK) {
            const auto mode=focus.list[0].GetCurrentValue();
            focus_rc=focus.close(e,SW_CAPTURE_CHECK); if(focus_rc) return focus_rc;
            if(mode>=sdk::CrFocus_AF_S && mode<=sdk::CrFocus_PF) return result(e,SW_UNSUPPORTED,SW_CAPTURE_CHECK,0,
                "Requires manual lens focus (MF); autofocus can swallow the release and produce no file");
        } else if(focus_rc!=SW_UNSUPPORTED) return focus_rc;
    }
    uint64_t raw=0,store=0;
    auto rc=capture_read_property(sdk::CrDeviceProperty_RAW_FileCompressionType,raw,e); if(rc) return rc;
    if(raw==sdk::CrRAWFile_Compression) capture.output.raw_format=SW_RAW_COMPRESSED;
    else if(raw==sdk::CrRAWFile_LossLess) capture.output.raw_format=SW_RAW_LOSSLESS;
    else if(raw==sdk::CrRAWFile_Compressed_HQ) capture.output.raw_format=SW_RAW_COMPRESSED_HQ;
    else return result(e,SW_UNSUPPORTED,SW_CAPTURE_CHECK,0,"Only full-size Compressed, Lossless or Compressed HQ RAW supported in this step");
    rc=capture_read_property(sdk::CrDeviceProperty_StillImageStoreDestination,store,e); if(rc) return rc;
    if(store==sdk::CrStillImageStoreDestination_HostPC) capture.output.save_destination=SW_SAVE_COMPUTER;
    else if(store==sdk::CrStillImageStoreDestination_HostPCAndMemoryCard) capture.output.save_destination=SW_SAVE_COMPUTER_AND_CARD;
    else return result(e,SW_UNSUPPORTED,SW_CAPTURE_CHECK,0,"Requires Computer or Computer+Card save destination; card-only cannot return an ARW");
    return SW_OK;
}
int32_t capture_snapshot(SwError* e) {
    Values v; auto rc=read_values(v,e,SW_CAPTURE_CHECK); if(rc) return rc;
    if(!fixed_iso(static_cast<uint32_t>(v.iso)) || !(capture.bulb?v.shutter==sdk::CrShutterSpeed_Bulb:ordinary_shutter(static_cast<uint32_t>(v.shutter))))
        return result(e,SW_UNSUPPORTED,SW_CAPTURE_CHECK,0,"Requires fixed ISO, readable aperture and ordinary (non-Bulb) shutter");
    SwParameters p{}; p.size=sizeof(p); p.iso=static_cast<uint32_t>(v.iso)&0xFFFFFF; p.iso_kind=SW_ISO_FIXED;
    p.shutter_kind=capture.bulb?SW_SHUTTER_BULB:SW_SHUTTER_SECONDS; p.exposure_seconds=capture.bulb?std::numeric_limits<double>::quiet_NaN():seconds(static_cast<uint32_t>(v.shutter));
    std::fesetround(FE_TONEAREST); p.f_number=v.aperture?double(v.aperture)/100.0:std::numeric_limits<double>::quiet_NaN();
    if(p.exposure_seconds>30) return result(e,SW_UNSUPPORTED,SW_CAPTURE_CHECK,0,"Ordinary shutter longer than 30 seconds outside this minimal step");
    capture.output.actual_parameters=p; capture.output.actual_valid=1; return SW_OK;
}
struct NativeFile {
    HANDLE value=INVALID_HANDLE_VALUE;
    explicit NativeFile(HANDLE h):value(h) {}
    ~NativeFile() noexcept { if(value!=INVALID_HANDLE_VALUE) CloseHandle(value); }
};
int32_t capture_win_error(SwError* e,uint32_t stage,const char* operation,DWORD win32) noexcept {
    char text[160]; std::snprintf(text,sizeof(text),"%s: Win32=%lu (not a Sony code); state unconfirmed",operation,static_cast<unsigned long>(win32));
    return finish_capture_failure(e,SW_IO,stage,0,text);
}
bool same_file_info(const BY_HANDLE_FILE_INFORMATION& a,const BY_HANDLE_FILE_INFORMATION& b) noexcept {
    return a.dwVolumeSerialNumber==b.dwVolumeSerialNumber && a.nFileIndexHigh==b.nFileIndexHigh && a.nFileIndexLow==b.nFileIndexLow &&
        a.nFileSizeHigh==b.nFileSizeHigh && a.nFileSizeLow==b.nFileSizeLow &&
        a.ftLastWriteTime.dwHighDateTime==b.ftLastWriteTime.dwHighDateTime && a.ftLastWriteTime.dwLowDateTime==b.ftLastWriteTime.dwLowDateTime;
}
bool wide_equal(const std::wstring& a,const std::wstring& b) {
    return CompareStringOrdinal(a.c_str(),static_cast<int>(a.size()),b.c_str(),static_cast<int>(b.size()),TRUE)==CSTR_EQUAL;
}
// Drains events by immutable connection object and full task path. Never resets a mailbox for a new task.
void drain_events(Callback* source) {
    if(!source) return;
    source->events.drain([&](const sw_internal::Event& event) {
        if(event.kind==2) {
            uint64_t empty=0;
            source->async_failure.compare_exchange_strong(empty,(uint64_t(event.code)<<32)|event.detail);
            return;
        }
        if(event.invalid) { source->untrusted.store(true); return; }
        fs::path path(event.path);
        const auto extension=path.extension().native();
        // Remote mode never enables PostView. Known preview extensions cannot complete RAW tasks.
        if(wide_equal(extension,L".JPG") || wide_equal(extension,L".JPEG") || wide_equal(extension,L".HIF")) return;
        if(!path.is_absolute() || !wide_equal(extension,L".ARW")) { source->untrusted.store(true); return; }
        path=path.lexically_normal();
        auto matches=[&](const std::wstring& dir,const std::wstring& prefix) {
            return wide_equal(path.parent_path().native(),dir) && path.filename().native().compare(0,prefix.size(),prefix)==0;
        };
        if(capture.used && capture.session==source && matches(capture.directory,capture.prefix)) {
            if(capture_is_waiting() || unsafe_capture_id==capture.output.capture_id) {
                ++capture.notification_count;
                if(capture.candidate_received && !wide_equal(capture.candidate,path.native())) source->untrusted.store(true);
                else { capture.candidate=path.native(); capture.candidate_received=true; }
            } // completed/reconciled task: ignore duplicate, keep its terminal result immutable
            return;
        }
        for(const auto& old:retired) if(old.session==source && matches(old.directory,old.prefix)) return;
        source->untrusted.store(true); // unknown image, never guess which task generated it
    });
}
void drain_capture_events() { drain_events(cb); }
int32_t event_failure(Callback* source,SwError* e,uint32_t stage) {
    if(source->error.load()) return capture_error(e,SW_SDK,stage,source->error.load(),"SDK OnError; camera state unconfirmed",capture.output.capture_id);
    if(source->events.overflow.load() || source->untrusted.load())
        return capture_error(e,SW_ASSOCIATION,stage,0,"Unknown download or callback queue overflow; state cannot be reset safely",capture.output.capture_id);
    const auto detail=source->async_failure.load();
    if(detail) {
        char message[160]; std::snprintf(message,sizeof(message),"Unattributed SDK async failure warning=0x%08X param3=0x%08X; session blocked",
            static_cast<uint32_t>(detail>>32),static_cast<uint32_t>(detail));
        return capture_error(e,SW_SDK,stage,static_cast<uint32_t>(detail>>32),message,capture.output.capture_id);
    }
    return SW_OK;
}
int32_t probe_error(SwError* e,const char* operation,DWORD win32=0) {
    close_capture_reader();
    char message[160]; std::snprintf(message,sizeof(message),"%s; Win32=%lu (not Sony error)",operation,static_cast<unsigned long>(win32));
    return capture_error(e,SW_IO,SW_CAPTURE_FILE,0,message,capture.output.capture_id);
}
// One <=64KiB read per progress step; reader/offset survive across wait calls.
int32_t probe_capture_file(SwError* e) {
    if(capture.verified) return SW_OK;
    if(capture.reader==INVALID_HANDLE_VALUE) {
        NativeFile file(CreateFileW(capture.candidate.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr));
        if(file.value==INVALID_HANDLE_VALUE) {
            const auto error=GetLastError();
            if(error==ERROR_SHARING_VIOLATION || error==ERROR_LOCK_VIOLATION || error==ERROR_FILE_NOT_FOUND) return SW_PENDING;
            return probe_error(e,"Open downloaded file",error);
        }
        BY_HANDLE_FILE_INFORMATION info{};
        if(!GetFileInformationByHandle(file.value,&info)) return probe_error(e,"File information",GetLastError());
        if((info.dwFileAttributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT)) || GetFileType(file.value)!=FILE_TYPE_DISK)
            return probe_error(e,"Not a regular local file");
        if(!capture.info_known || !same_file_info(capture.last_info,info)) {
            capture.info_known=true; capture.last_info=info; capture.stable_since=Clock::now(); return SW_PENDING;
        }
        if(Clock::now()-capture.stable_since<500ms) return SW_PENDING;
        capture.expected_bytes=(uint64_t(info.nFileSizeHigh)<<32)|info.nFileSizeLow;
        if(capture.expected_bytes<8) return probe_error(e,"Missing TIFF header; no MB-size threshold");
        capture.reader=file.value; file.value=INVALID_HANDLE_VALUE; capture.read_total=0;
    }
    std::array<unsigned char,65536> data{};
    const auto wanted=static_cast<DWORD>(std::min<uint64_t>(data.size(),capture.expected_bytes-capture.read_total));
    DWORD got=0;
    if(!ReadFile(capture.reader,data.data(),wanted,&got,nullptr)) return probe_error(e,"Read downloaded bytes",GetLastError());
    if(!got && wanted) return probe_error(e,"Premature EOF");
    if(!capture.read_total && !(got>=8 && ((data[0]=='I' && data[1]=='I' && data[2]==42 && data[3]==0) ||
        (data[0]=='M' && data[1]=='M' && data[2]==0 && data[3]==42)))) return probe_error(e,"Missing TIFF/ARW header");
    capture.read_total+=got;
    if(capture.read_total<capture.expected_bytes) return SW_PENDING;
    DWORD extra=0; BY_HANDLE_FILE_INFORMATION after{};
    if(!ReadFile(capture.reader,data.data(),1,&extra,nullptr)) return probe_error(e,"Read EOF",GetLastError());
    if(extra || !GetFileInformationByHandle(capture.reader,&after) || !same_file_info(after,capture.last_info)) return probe_error(e,"File changed while reading");
    capture.verified=true; capture.verified_bytes=capture.read_total; capture.verified_path=capture.candidate;
    close_capture_reader(); return SW_OK;
}
int32_t capture_progress(SwError* e) {
    if(!capture_is_waiting()) return capture.output.failure.code;
    exposure_tick();
    if(!capture_is_waiting()) return capture.output.failure.code;
    drain_events(capture.session);
    auto rc=event_failure(capture.session,e,SW_CAPTURE_DOWNLOAD);
    if(rc || capture.session->disconnected.load()) {
        if(!rc) rc=capture_error(e,SW_SDK,SW_CAPTURE_DOWNLOAD,capture.session->disconnect_error.load(),"Disconnected during capture",capture.output.capture_id);
        const SwError failure=*e; return finish_capture_failure(e,rc,failure.stage,failure.sony_code,failure.message);
    }
    if(Clock::now()>=capture.deadline)
        return finish_capture_failure(e,SW_TIMEOUT,SW_CAPTURE_DOWNLOAD,0,"Total capture deadline expired; camera stop NOT confirmed");
    if(capture.button_down || !capture.candidate_received) return SW_PENDING;
    rc=probe_capture_file(e);
    if(rc==SW_PENDING) return rc;
    if(rc) { const SwError failure=*e; return finish_capture_failure(e,rc,failure.stage,failure.sony_code,failure.message); }
    drain_events(capture.session);
    rc=event_failure(capture.session,e,SW_CAPTURE_DOWNLOAD);
    if(rc) { const SwError failure=*e; return finish_capture_failure(e,rc,failure.stage,failure.sony_code,failure.message); }
    if(Clock::now()>=capture.deadline) return finish_capture_failure(e,SW_TIMEOUT,SW_CAPTURE_FILE,0,"Deadline reached before success commit");
    if(capture.v2) {
        rc=idle_proof(e,SW_EXPOSURE_RESTORE); if(rc==SW_PENDING) return rc;
        if(rc) { const SwError f=*e; return finish_capture_failure(e,rc,f.stage,f.sony_code,f.message); }
        rc=restore_step(e); if(rc==SW_PENDING) return rc;
        if(rc) { const SwError f=*e; return finish_capture_failure(e,rc,f.stage,f.sony_code,f.message); }
    }
    if(!utf8(capture.verified_path.c_str(),capture.output.arw_path,sizeof(capture.output.arw_path)))
        return finish_capture_failure(e,SW_BUFFER,SW_CAPTURE_FILE,0,"Output path too long");
    capture.output.file_bytes=capture.verified_bytes; capture.output.download_notifications=capture.notification_count;
    capture.exposure.phase=SW_EXPOSURE_TERMINAL;
    capture.output.state=SW_CAPTURE_SUCCEEDED; capture.output.failure={}; capture.output.failure.size=sizeof(SwError);
    capture.output.failure.capture_id=capture.output.capture_id; return SW_OK;
}

struct ReleaseButtonGuard {
    bool armed=true;
    ~ReleaseButtonGuard() noexcept {
        if(armed) { try { auto rc=sdk::SendCommand(device,sdk::CrCommandId_Release,sdk::CrCommandParam_Up);
            capture.up_confirmed=!rc; if(rc) unsafe_capture_id=capture.output.capture_id; }
            catch(...) { unsafe_capture_id=capture.output.capture_id; } }
    }
};
}
namespace {
int32_t publish_record(const SwCaptureResult& record,SwCaptureResult* out,SwError* e) {
    *out=record;
    if(record.state==SW_CAPTURE_SUCCEEDED) return capture_error(e,SW_OK,SW_CAPTURE_FILE,0,"Requested task download complete and readable",record.capture_id);
    *e=record.failure; return e->code;
}
Clock::duration duration(double seconds_value) {
    return std::chrono::duration_cast<Clock::duration>(std::chrono::duration<double>(seconds_value));
}
bool milliseconds_exact(double value,double minimum,double maximum) {
    std::fesetround(FE_TONEAREST);
    if(!std::isfinite(value) || value<minimum || value>maximum) return false;
    const auto ms=std::llround(value*1000.0);
    return double(ms)/1000.0==value;
}
int32_t validate_request(const SwExposureRequest& r,SwError* e) {
    if(r.size!=sizeof(r) || r.reserved || r.mode>SW_EXPOSURE_BULB || !valid_timeout(r.setup_timeout_ms) ||
       !std::isfinite(r.exposure_seconds) || r.exposure_seconds<=0 ||
       !milliseconds_exact(r.exposure_timeout_seconds,0.001,1800) ||
       !milliseconds_exact(r.download_timeout_seconds,0.001,3600))
        return capture_error(e,SW_ARGUMENT,SW_EXPOSURE_SETUP,0,"Invalid layout/mode/timeouts; finite exact millisecond timeouts required",0);
    if(r.mode==SW_EXPOSURE_BULB && !milliseconds_exact(r.exposure_seconds,1,900))
        return capture_error(e,SW_UNSUPPORTED,SW_EXPOSURE_SETUP,0,"Software Bulb policy: 1..900 seconds, exact millisecond values; no rounding",0);
    if(r.mode==SW_EXPOSURE_ORDINARY && (r.exposure_seconds>30 || r.exposure_timeout_seconds<r.exposure_seconds))
        return capture_error(e,SW_UNSUPPORTED,SW_EXPOSURE_SETUP,0,"Ordinary <=30 seconds; exposure timeout must cover requested exposure",0);
    return SW_OK;
}
bool has_candidate(sdk::CrDeviceProperty& p,uint64_t target) {
    size_t width=0;
    switch(p.GetValueType()) {
    case sdk::CrDataType_UInt8Array:width=1;break;
    case sdk::CrDataType_UInt16Array:width=2;break;
    case sdk::CrDataType_UInt32Array:width=4;break;
    default:return false;
    }
    if(!p.IsSetEnableCurrentValue() || p.GetValueSize()%width || (p.GetValueSize()&&!p.GetValues())) return false;
    for(size_t i=0;i<p.GetValueSize();i+=width) {
        uint64_t v=0;std::memcpy(&v,p.GetValues()+i,width);if(v==target)return true;
    }
    return false;
}
int32_t setup_write(uint32_t code,uint64_t target,SwError* e) {
    PropertyList p;auto rc=p.load(code,e,SW_EXPOSURE_SETUP);if(rc)return rc;
    if(!has_candidate(p.list[0],target)) return result(e,SW_UNSUPPORTED,SW_EXPOSURE_SETUP,0,"Required Bulb setting is not a writable camera candidate");
    sdk::CrDeviceProperty local=p.list[0];rc=p.close(e,SW_EXPOSURE_SETUP);if(rc)return rc;
    if(Clock::now()>=capture.deadline)return result(e,SW_TIMEOUT,SW_EXPOSURE_SETUP,0,"Setup deadline before property write");
    local.SetCurrentValue(target);auto sdk_rc=sdk::SetDeviceProperty(device,&local);
    if(sdk_rc)return result(e,SW_SDK,SW_EXPOSURE_SETUP,sdk_rc,"Temporary exposure property write failed; restoration required");
    for(;;) {
        PropertyList q;rc=q.load(code,e,SW_EXPOSURE_SETUP);
        if(!rc) {const auto current=q.list[0].GetCurrentValue();rc=q.close(e,SW_EXPOSURE_SETUP);if(rc)return rc;if(current==target)return SW_OK;}
        else if(e->sony_code!=sdk::CrError_Api_InvalidCalled)return rc;
        if(Clock::now()>=capture.deadline)return result(e,SW_TIMEOUT,SW_EXPOSURE_SETUP,e->sony_code,"Temporary exposure setting not confirmed by setup deadline");
        std::this_thread::sleep_until(std::min(capture.deadline,Clock::now()+50ms));
    }
}
int32_t prepare_exposure(const SwExposureRequest& request,SwError* e) {
    Values v;auto rc=read_values(v,e,SW_EXPOSURE_SETUP);if(rc)return rc;
    auto& original=capture.exposure.original_parameters;original.size=sizeof(original);
    original.iso_kind=fixed_iso(static_cast<uint32_t>(v.iso))?SW_ISO_FIXED:SW_ISO_SPECIAL;
    original.iso=original.iso_kind==SW_ISO_FIXED?static_cast<uint32_t>(v.iso)&0xFFFFFF:0;
    original.shutter_kind=v.shutter==sdk::CrShutterSpeed_Bulb?SW_SHUTTER_BULB:SW_SHUTTER_SECONDS;
    original.exposure_seconds=ordinary_shutter(static_cast<uint32_t>(v.shutter))?seconds(static_cast<uint32_t>(v.shutter)):std::numeric_limits<double>::quiet_NaN();
    original.f_number=double(v.aperture)/100.0;
    if(!capture.bulb) {
        if(!ordinary_shutter(static_cast<uint32_t>(v.shutter)) || original.exposure_seconds!=request.exposure_seconds)
            return result(e,SW_UNSUPPORTED,SW_EXPOSURE_SETUP,0,"Ordinary request must exactly match current read-back shutter; explicitly use setter first");
        return SW_OK;
    }
    capture.original_shutter=v.shutter;
    PropertyList shutter;rc=shutter.load(sdk::CrDeviceProperty_ShutterSpeed,e,SW_EXPOSURE_SETUP);if(rc)return rc;
    if(!has_candidate(shutter.list[0],sdk::CrShutterSpeed_Bulb))return result(e,SW_UNSUPPORTED,SW_EXPOSURE_SETUP,0,"Camera does not currently offer writable Bulb");
    rc=shutter.close(e,SW_EXPOSURE_SETUP);if(rc)return rc;
    if(v.shutter!=sdk::CrShutterSpeed_Bulb) {
        capture.shutter_dirty=true;capture.exposure.restore_state=SW_RESTORE_PENDING;
        rc=setup_write(sdk::CrDeviceProperty_ShutterSpeed,sdk::CrShutterSpeed_Bulb,e);if(rc)return rc;
    }
    PropertyList timer;rc=timer.load(sdk::CrDeviceProperty_BulbTimerSetting,e,SW_EXPOSURE_SETUP);if(rc)return rc;
    capture.original_timer=timer.list[0].GetCurrentValue();
    if(capture.original_timer>1 || !has_candidate(timer.list[0],sdk::CrBulbTimerSetting_Off))
        return result(e,SW_UNSUPPORTED,SW_EXPOSURE_SETUP,0,"Requires queryable Bulb Timer with writable Off; software timing cannot compete with camera timer");
    capture.exposure.original_timer_enabled=static_cast<uint32_t>(capture.original_timer);capture.exposure.timer_original_valid=1;
    rc=timer.close(e,SW_EXPOSURE_SETUP);if(rc)return rc;
    if(capture.original_timer!=sdk::CrBulbTimerSetting_Off) {
        capture.timer_dirty=true;capture.exposure.restore_state=SW_RESTORE_PENDING;
        rc=setup_write(sdk::CrDeviceProperty_BulbTimerSetting,sdk::CrBulbTimerSetting_Off,e);if(rc)return rc;
    }
    return SW_OK;
}
int32_t idle_proof(SwError* e,uint32_t stage) {
    if(!device || cb!=capture.session || !cb->connected.load() || cb->disconnected.load())
        return capture_error(e,SW_STATE,stage,0,"No matching live connection for idle proof",capture.output.capture_id);
    CrInt32u queued=0;auto sdk_rc=sdk::GetCommandQueueCount(device,&queued);
    if(sdk_rc)return capture_error(e,SW_SDK,stage,sdk_rc,"Cannot read command queue for idle proof",capture.output.capture_id);
    PropertyList button;auto rc=button.load(sdk::CrDeviceProperty_S2,e,stage);if(rc){e->capture_id=capture.output.capture_id;return rc;}
    auto value=button.list[0].GetCurrentValue();rc=button.close(e,stage);if(rc){e->capture_id=capture.output.capture_id;return rc;}
    if(queued || value!=sdk::CrLockIndicator_Unlocked)return capture_error(e,SW_PENDING,stage,0,"Waiting for actual command drain and S2 Unlocked",capture.output.capture_id);
    return SW_OK;
}
int32_t restore_failure(SwError* e,int32_t code,uint32_t sony,const char* message) {
    capture.exposure.restore_state=SW_RESTORE_FAILED;
    capture_error(e,code,SW_EXPOSURE_RESTORE,sony,message,capture.output.capture_id);
    capture.exposure.restore_error=*e;unsafe_capture_id=capture.output.capture_id;return code;
}
// One state-machine step, no readback sleep/loop. Caller drives it between bounded slices.
int32_t restore_step(SwError* e) {
    if(capture.exposure.restore_state==SW_RESTORE_FAILED){*e=capture.exposure.restore_error;return e->code;}
    if(!capture.shutter_dirty&&!capture.timer_dirty) {
        capture.exposure.restore_state=SW_RESTORE_CONFIRMED;
        if(capture_is_waiting())capture.exposure.phase=SW_EXPOSURE_DOWNLOADING;
        return SW_OK;
    }
    capture.exposure.phase=capture_is_waiting()?SW_EXPOSURE_RESTORING:SW_EXPOSURE_TERMINAL;
    if(capture.restore_deadline==Clock::time_point{})capture.restore_deadline=Clock::now()+10s;
    if(Clock::now()>=capture.restore_deadline)return restore_failure(e,SW_TIMEOUT,0,"Temporary settings restore exceeded 10 seconds; session quarantined");
    if(!device || cb!=capture.session || cb->disconnected.load())return restore_failure(e,SW_STATE,0,"Cannot restore without original live connection");
    if(capture.restore_write_pending) {
        PropertyList q;auto rc=q.load(capture.restore_property,e,SW_EXPOSURE_RESTORE);
        if(rc)return restore_failure(e,rc,e->sony_code,"Restore readback failed; no blind repeated writes");
        const auto value=q.list[0].GetCurrentValue();rc=q.close(e,SW_EXPOSURE_RESTORE);
        if(rc)return restore_failure(e,rc,e->sony_code,"Restore readback release failed");
        if(value!=capture.restore_target)return SW_PENDING;
        if(capture.restore_property==sdk::CrDeviceProperty_BulbTimerSetting)capture.timer_dirty=false;
        else capture.shutter_dirty=false;
        capture.restore_write_pending=false;return SW_PENDING;
    }
    auto rc=idle_proof(e,SW_EXPOSURE_RESTORE);if(rc)return rc;
    // Restore timer while Shutter is still Bulb, then restore shutter itself.
    capture.restore_property=capture.timer_dirty?sdk::CrDeviceProperty_BulbTimerSetting:sdk::CrDeviceProperty_ShutterSpeed;
    capture.restore_target=capture.timer_dirty?capture.original_timer:capture.original_shutter;
    PropertyList p;rc=p.load(capture.restore_property,e,SW_EXPOSURE_RESTORE);
    if(rc)return restore_failure(e,rc,e->sony_code,"Restore property unavailable");
    if(!has_candidate(p.list[0],capture.restore_target))return restore_failure(e,SW_UNSUPPORTED,0,"Original value no longer writable; restoration refused");
    sdk::CrDeviceProperty local=p.list[0];rc=p.close(e,SW_EXPOSURE_RESTORE);if(rc)return restore_failure(e,rc,e->sony_code,"Restore list release failed");
    local.SetCurrentValue(capture.restore_target);auto sdk_rc=sdk::SetDeviceProperty(device,&local);
    if(sdk_rc)return restore_failure(e,SW_SDK,sdk_rc,"Restore write failed; original value unconfirmed");
    capture.restore_write_pending=true;return SW_PENDING;
}
void release_bulb(uint32_t reason) noexcept {
    if(!capture.button_down)return;
    capture.button_down=false; // at most two explicit attempts, never a repeated timer loop
    auto& info=capture.exposure;
    info.end_reason=reason;info.phase=SW_EXPOSURE_DOWNLOADING;
    info.software_hold_seconds=std::chrono::duration<double>(Clock::now()-capture.down_at).count();
    try {
        info.release_attempts=1;const auto first=sdk::SendCommand(device,sdk::CrCommandId_Release,sdk::CrCommandParam_Up);
        auto last=first;
        if(first){info.release_attempts=2;last=sdk::SendCommand(device,sdk::CrCommandId_Release,sdk::CrCommandParam_Up);}
        capture.up_confirmed=last==0;
        if(first)capture_error(&info.release_error,SW_SDK,SW_CAPTURE_UP,first,"Bulb Up first attempt failed; bounded retry attempted, idle still requires proof",capture.output.capture_id);
    }catch(...) {
        capture.up_confirmed=false;capture_error(&info.release_error,SW_INTERNAL,SW_CAPTURE_UP,0,"Exception during Bulb Up; cannot confirm release",capture.output.capture_id);
    }
    capture.deadline=Clock::now()+duration(capture.download_limit);
    if(info.release_error.code)unsafe_capture_id=capture.output.capture_id;
}
void exposure_tick() noexcept {
    if(!capture.v2 || !capture.triggered || !capture_is_waiting())return;
    SwError e{sizeof(SwError)};
    if(capture.button_down) {
        if(fault || capture.session->disconnected.load() || capture.session->error.load()) {
            release_bulb(capture.session->disconnected.load()?SW_END_DISCONNECT:SW_END_ERROR);
            finish_capture_failure(&e,SW_SDK,SW_EXPOSURE_TIMER,capture.session->error.load()?capture.session->error.load():capture.session->disconnect_error.load(),"Connection failed while Bulb held; bounded Up attempted; state unconfirmed");return;
        }
        if(Clock::now()>=std::min(capture.planned_end,capture.exposure_deadline)) {
            const bool expired=capture.exposure_deadline<capture.planned_end;
            release_bulb(expired?SW_END_TIMEOUT:SW_END_NORMAL);
            if(capture.exposure.release_error.code) {
                const auto f=capture.exposure.release_error;finish_capture_failure(&e,f.code,f.stage,f.sony_code,f.message);return;
            }
            if(expired){finish_capture_failure(&e,SW_TIMEOUT,SW_EXPOSURE_TIMER,0,"Exposure hold limit expired; Up requested, partial file is not requested exposure success");return;}
        }
    }
    if(!capture.button_down && Clock::now()>=capture.deadline)
        finish_capture_failure(&e,SW_TIMEOUT,SW_CAPTURE_DOWNLOAD,0,"Download/readability deadline expired after release; camera/transfer state unconfirmed");
}
std::atomic<bool> exposure_worker_stop{false};
std::thread* exposure_worker=nullptr;
void start_exposure_worker() {
    if(exposure_worker)return;
    exposure_worker_stop.store(false);
    exposure_worker=new std::thread([]() noexcept {
        while(!exposure_worker_stop.load()) {
            try {
                // Never wait for the public gate during shutdown: shutdown holds gate while joining.
                std::unique_lock<std::mutex> lock(gate,std::try_to_lock);
                if(lock.owns_lock() && initialized && device) { FloatingEnvironment f;exposure_tick(); }
            }catch(...) {} // tick itself has no exception path; no exception may leave the thread
            std::this_thread::sleep_for(10ms);
        }
    });
}
void stop_exposure_worker() {
    exposure_worker_stop.store(true);
    if(exposure_worker){exposure_worker->join();delete exposure_worker;exposure_worker=nullptr;}
}

int32_t start_capture(const char* root_utf8,const char* prefix_utf8,uint32_t ms,SwCaptureResult* out,SwError* e,const SwExposureRequest* request=nullptr) {
    return capture_boundary(0,out,e,SW_CAPTURE_CHECK,[&]()->int32_t {
        if(request) { auto validation=validate_request(*request,e); if(validation) return validation; ms=request->setup_timeout_ms; }
        if(!root_utf8 || !valid_timeout(ms)) return capture_error(e,SW_ARGUMENT,SW_CAPTURE_CHECK,0,"Root required; timeout_ms 1..60000",0);
        if(movie_owned || transfer_active || feature_uncertain || connection_mode!=SW_MODE_REMOTE || capture_is_waiting() || unsafe_capture_id) return capture_error(e,SW_STATE,SW_CAPTURE_CHECK,0,"One active/unconfirmed task: finish or explicitly reconcile it first",unsafe_capture_id?unsafe_capture_id:capture.output.capture_id);
        auto rc=ready_check(e,SW_CAPTURE_CHECK); if(rc) return rc;
        drain_capture_events();
        if(cb->untrusted.load() || cb->events.overflow.load()) return capture_error(e,SW_ASSOCIATION,SW_CAPTURE_CHECK,0,"Untrusted session notifications; clean disconnect required",0);
        if(cb->async_failure.load()) return capture_error(e,SW_SDK,SW_CAPTURE_CHECK,static_cast<uint32_t>(cb->async_failure.load()>>32),"SDK async failure in session; clean disconnect required",0);
        wchar_t wide_root[1024]{};
        if(strnlen_s(root_utf8,1024)==1024 || !MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,root_utf8,-1,wide_root,1024))
            return capture_error(e,SW_ARGUMENT,SW_CAPTURE_SAVE,0,"Invalid UTF-8 root",0);
        fs::path root(wide_root); std::error_code error;
        if(!root.is_absolute() || root.native().size()<3 || root.native()[1]!=L':' || !fs::is_directory(root,error))
            return capture_error(e,SW_ARGUMENT,SW_CAPTURE_SAVE,0,"Root must be existing absolute local directory",0);
        root=fs::canonical(root);
        std::wstring custom;
        if(prefix_utf8) {
            const auto n=strnlen_s(prefix_utf8,17);
            if(!n || n>16) return capture_error(e,SW_ARGUMENT,SW_CAPTURE_SAVE,0,"Prefix must contain 1..16 ASCII letters/digits/underscore",0);
            for(size_t i=0;i<n;++i) {
                const char c=prefix_utf8[i];
                if(!((c>='A'&&c<='Z')||(c>='a'&&c<='z')||(c>='0'&&c<='9')||c=='_'))
                    return capture_error(e,SW_ARGUMENT,SW_CAPTURE_SAVE,0,"Invalid filename prefix",0);
                custom.push_back(static_cast<wchar_t>(c));
            }
        }
        archive_capture();
        FILETIME time{}; GetSystemTimePreciseAsFileTime(&time);
        last_capture_id=std::max(last_capture_id+1,(uint64_t(time.dwHighDateTime)<<32)|time.dwLowDateTime);
        capture.used=true; capture.session=cb; capture.output={}; capture.output.size=sizeof(capture.output);
        capture.output.capture_id=last_capture_id; capture.output.state=SW_CAPTURE_WAITING;
        capture.output.failure.size=sizeof(SwError); capture.output.failure.capture_id=last_capture_id;
        capture.deadline=Clock::now()+std::chrono::milliseconds(ms);
        capture.exposure.size=sizeof(SwExposureInfo); capture.exposure.software_hold_seconds=std::numeric_limits<double>::quiet_NaN();
        capture.exposure.release_error.size=sizeof(SwError); capture.exposure.restore_error.size=sizeof(SwError);
        capture.v2=request!=nullptr; capture.bulb=request && request->mode==SW_EXPOSURE_BULB;
        if(request) { capture.exposure.mode=request->mode; capture.exposure.requested_seconds=request->exposure_seconds;
            capture.exposure_limit=request->exposure_timeout_seconds; capture.download_limit=request->download_timeout_seconds; }

        auto fail=[&](int32_t status,bool uncertain=false)->int32_t {
            const SwError failure=*e; finish_capture_failure(e,status,failure.stage,failure.sony_code,failure.message,uncertain); *out=capture.output; return status;
        };
        // A property setter may already read back its value while its command is still draining.
        // Observe the real queue until empty, bounded by this task's total deadline; never reset it.
        for(;;) {
            CrInt32u queued=0; const auto queued_rc=sdk::GetCommandQueueCount(device,&queued);
            if(queued_rc) { result(e,SW_SDK,SW_CAPTURE_CHECK,queued_rc,"Cannot read command queue before trigger"); return fail(SW_SDK); }
            if(!queued) break;
            if(Clock::now()>=capture.deadline) { result(e,SW_TIMEOUT,SW_CAPTURE_CHECK,0,"Command queue did not drain before trigger; no shutter submitted"); return fail(SW_TIMEOUT); }
            std::this_thread::sleep_until(std::min(capture.deadline,Clock::now()+50ms));
        }
        rc=capture_preconditions(e); if(rc) return fail(rc);
        if(capture.v2) { rc=prepare_exposure(*request,e); if(rc) return fail(rc); }
        rc=capture_snapshot(e); if(rc) return fail(rc);
        if(!capture.v2) capture.exposure.requested_seconds=capture.output.actual_parameters.exposure_seconds;
        wchar_t child[80]{}; std::swprintf(child,80,L"capture-%016llX-p%lu",static_cast<unsigned long long>(last_capture_id),static_cast<unsigned long>(GetCurrentProcessId()));
        capture.directory=(root/child).lexically_normal().native();
        wchar_t automatic[32]{}; std::swprintf(automatic,32,L"S%016llX_",static_cast<unsigned long long>(last_capture_id));
        capture.prefix=prefix_utf8?custom:automatic;
        char directory[1024]{};
        if(capture.directory.size()>220 || !utf8(capture.directory.c_str(),directory,sizeof(directory))) {
            result(e,SW_BUFFER,SW_CAPTURE_SAVE,0,"Receive path exceeds conservative limits"); return fail(SW_BUFFER);
        }
        if(!fs::create_directory(capture.directory,error)) { result(e,SW_IO,SW_CAPTURE_SAVE,0,"Exclusive task directory creation failed"); return fail(SW_IO); }
        std::memcpy(capture.output.receive_directory,directory,sizeof(directory));
        auto sdk_rc=sdk::SetSaveInfo(device,capture.directory.data(),capture.prefix.data(),1);
        if(sdk_rc) { result(e,SW_SDK,SW_CAPTURE_SAVE,sdk_rc,"SetSaveInfo failed; no shutter submitted"); return fail(SW_SDK); }
        for(;;) {
            rc=capture_snapshot(e); if(!rc) break;
            if(e->sony_code!=sdk::CrError_Api_InvalidCalled) return fail(rc);
            if(Clock::now()>=capture.deadline) { result(e,SW_TIMEOUT,SW_CAPTURE_SAVE,e->sony_code,"Routing readiness timed out before trigger"); return fail(SW_TIMEOUT); }
            std::this_thread::sleep_until(std::min(capture.deadline,Clock::now()+200ms));
        }
        drain_capture_events();
        rc=event_failure(cb,e,SW_CAPTURE_SAVE); if(rc) return fail(rc,true);
        if(Clock::now()>=capture.deadline) { result(e,SW_TIMEOUT,SW_CAPTURE_SAVE,0,"Deadline before shutter submission"); return fail(SW_TIMEOUT); }
        ReleaseButtonGuard guard;
        capture.triggered=true;
        sdk_rc=sdk::SendCommand(device,sdk::CrCommandId_Release,sdk::CrCommandParam_Down);
        if(sdk_rc) {
            const auto up=sdk::SendCommand(device,sdk::CrCommandId_Release,sdk::CrCommandParam_Up);
            capture.up_confirmed=!up; guard.armed=!!up;
            capture_error(e,SW_SDK,SW_CAPTURE_DOWN,sdk_rc,"Down failed; bounded Up cleanup attempted; camera state unconfirmed",last_capture_id);
            return fail(SW_SDK,true);
        }
        if(capture.bulb) {
            guard.armed=false; capture.button_down=true; capture.down_at=Clock::now();
            capture.planned_end=capture.down_at+duration(capture.exposure.requested_seconds);
            capture.exposure_deadline=capture.down_at+duration(capture.exposure_limit);
            capture.deadline=capture.exposure_deadline+duration(capture.download_limit);
            capture.exposure.phase=SW_EXPOSURE_HOLDING;
            *out=capture.output;
            return capture_error(e,SW_OK,SW_CAPTURE_DOWN,0,"Bulb started; DLL timer owns release even when caller does not poll",last_capture_id);
        }
        capture.down_at=Clock::now();
        if(capture.v2) capture.deadline=capture.down_at+duration(request->exposure_seconds+request->download_timeout_seconds);
        std::this_thread::sleep_for(120ms);
        capture.exposure.software_hold_seconds=std::chrono::duration<double>(Clock::now()-capture.down_at).count();
        capture.exposure.end_reason=SW_END_NORMAL; capture.exposure.phase=SW_EXPOSURE_DOWNLOADING;
        const auto first_up=sdk::SendCommand(device,sdk::CrCommandId_Release,sdk::CrCommandParam_Up);
        auto final_up=first_up;
        if(first_up) final_up=sdk::SendCommand(device,sdk::CrCommandId_Release,sdk::CrCommandParam_Up);
        capture.up_confirmed=!final_up; capture.exposure.release_attempts=first_up?2:1; guard.armed=false;
        if(first_up) {
            char message[160]; std::snprintf(message,sizeof(message),"Up failed 0x%X; one retry returned 0x%X; task failed even if retry succeeded",first_up,final_up);
            capture_error(e,SW_SDK,SW_CAPTURE_UP,first_up,message,last_capture_id); return fail(SW_SDK,true);
        }
        *out=capture.output;
        return capture_error(e,SW_OK,SW_CAPTURE_UP,0,"Triggered; WAITING for this task's download",last_capture_id);
    });
}
}
int32_t SW_CALL sw_capture_start(const char* root,uint32_t ms,SwCaptureResult* out,SwError* e) { return start_capture(root,nullptr,ms,out,e); }
int32_t SW_CALL sw_capture_start_ex(const char* root,const char* prefix,uint32_t ms,SwCaptureResult* out,SwError* e) {
    if(!prefix) return capture_boundary(0,out,e,SW_CAPTURE_SAVE,[&] { return capture_error(e,SW_ARGUMENT,SW_CAPTURE_SAVE,0,"Custom prefix is required",0); });
    return start_capture(root,prefix,ms,out,e);
}
int32_t SW_CALL sw_capture_wait(uint64_t id,uint32_t wait_ms,SwCaptureResult* out,SwError* e) {
    return capture_boundary(id,out,e,SW_CAPTURE_DOWNLOAD,[&]()->int32_t {
        if(!id || wait_ms>1000) return capture_error(e,SW_ARGUMENT,SW_CAPTURE_DOWNLOAD,0,"Nonzero task id, wait_ms 0..1000",id);
        if(!capture.used || id!=capture.output.capture_id) {
            for(const auto& old:retired) if(old.output.capture_id==id) return publish_record(old.output,out,e);
            return capture_error(e,SW_STATE,SW_CAPTURE_DOWNLOAD,0,"Unknown task id; no stale result returned",id);
        }
        const auto end=Clock::now()+std::chrono::milliseconds(wait_ms);
        do {
            if(!capture_is_waiting()) return publish_capture(out,e);
            const auto before=capture.read_total;
            capture_progress(e);
            if(!capture_is_waiting() || Clock::now()>=end) return publish_capture(out,e);
            if(before==capture.read_total) std::this_thread::sleep_until(std::min({end,capture.deadline,Clock::now()+50ms}));
        } while(true);
    });
}
int32_t SW_CALL sw_capture_cancel(uint64_t id,SwCaptureResult* out,SwError* e) {
    return capture_boundary(id,out,e,SW_CAPTURE_CANCEL,[&]()->int32_t {
        if(!id) return capture_error(e,SW_ARGUMENT,SW_CAPTURE_CANCEL,0,"Nonzero task id required",id);
        if(!capture.used || id!=capture.output.capture_id || !capture_is_waiting())
            return capture_error(e,SW_STATE,SW_CAPTURE_CANCEL,0,"Cancel only applies to the active WAITING task; use wait for terminal query",id);
        finish_capture_failure(e,SW_CANCELLED,SW_CAPTURE_CANCEL,0,"Waiting cancelled; camera exposure/transfer NOT stopped or confirmed");
        return publish_capture(out,e);
    });
}
int32_t SW_CALL sw_capture_reconcile(uint64_t id,uint32_t wait_ms,SwCaptureResult* out,SwError* e) {
    return capture_boundary(id,out,e,SW_CAPTURE_RECONCILE,[&]()->int32_t {
        if(!id || wait_ms>1000) return capture_error(e,SW_ARGUMENT,SW_CAPTURE_RECONCILE,0,"Nonzero task id and wait_ms 0..1000 required",id);
        if(!capture.used || id!=capture.output.capture_id || capture_is_waiting()) return capture_error(e,SW_STATE,SW_CAPTURE_RECONCILE,0,"No matching failed terminal task to reconcile",id);
        *out=capture.output;
        if(capture.reconciled) return capture_error(e,SW_OK,SW_CAPTURE_RECONCILE,0,"Already reconciled; terminal result remains failed",id);
        if(unsafe_capture_id!=id || (capture.triggered && !capture.up_confirmed)) return capture_error(e,SW_STATE,SW_CAPTURE_RECONCILE,0,"No proven Up or no matching quarantined task; cannot declare idle",id);
        const auto end=Clock::now()+std::chrono::milliseconds(wait_ms);
        do {
            drain_events(capture.session);
            auto rc=event_failure(capture.session,e,SW_CAPTURE_RECONCILE); if(rc) return rc;
            const auto before=capture.read_total;
            if(capture.candidate_received || !capture.triggered) {
                rc=capture.triggered?probe_capture_file(e):SW_OK;
                if(rc==SW_OK) {
                    drain_events(capture.session); rc=event_failure(capture.session,e,SW_CAPTURE_RECONCILE); if(rc) return rc;
                    if(!device || cb!=capture.session || !cb->connected.load() || cb->disconnected.load())
                        return capture_error(e,SW_STATE,SW_CAPTURE_RECONCILE,0,"No live matching connection for S2 readback; camera idle remains unconfirmed",id);
                    CrInt32u queued=0; auto sdk_rc=sdk::GetCommandQueueCount(device,&queued);
                    if(sdk_rc) return capture_error(e,SW_SDK,SW_CAPTURE_RECONCILE,sdk_rc,"Cannot prove SDK command queue drained",id);
                    PropertyList button; auto button_rc=button.load(sdk::CrDeviceProperty_S2,e,SW_CAPTURE_RECONCILE);
                    if(button_rc) { e->capture_id=id; return button_rc; }
                    const auto button_value=button.list[0].GetCurrentValue(); button_rc=button.close(e,SW_CAPTURE_RECONCILE);
                    if(button_rc) { e->capture_id=id; return button_rc; }
                    if(queued || button_value!=sdk::CrLockIndicator_Unlocked) {
                        std::this_thread::sleep_until(std::min(end,Clock::now()+50ms));
                        return capture_error(e,SW_PENDING,SW_CAPTURE_RECONCILE,0,"File complete, but command queue/S2 release not yet confirmed; still quarantined",id);
                    }
                    if(capture.v2) {
                        const auto restore_rc=restore_step(e);
                        if(restore_rc==SW_PENDING) {
                            if(Clock::now()>=end) return capture_error(e,SW_PENDING,SW_EXPOSURE_RESTORE,0,"Restore pending; session still quarantined",id);
                            std::this_thread::sleep_until(std::min(end,Clock::now()+20ms)); continue;
                        }
                        if(restore_rc) return restore_rc;
                    }
                    capture.reconciled=true; unsafe_capture_id=0;
                    return capture_error(e,SW_OK,SW_CAPTURE_RECONCILE,0,"Queue empty, S2 readback Unlocked, exact download complete/readable; original failed result preserved",id);
                }
                if(rc!=SW_PENDING) return rc;
            }
            if(Clock::now()>=end) return capture_error(e,SW_PENDING,SW_CAPTURE_RECONCILE,0,"Reconciliation pending; next task still forbidden",id);
            if(before==capture.read_total) std::this_thread::sleep_until(std::min(end,Clock::now()+50ms));
        } while(true);
    });
}

namespace {
uint32_t project_destination(uint64_t raw) {
    return raw==sdk::CrStillImageStoreDestination_HostPC?SW_SAVE_COMPUTER:
        (raw==sdk::CrStillImageStoreDestination_HostPCAndMemoryCard?SW_SAVE_COMPUTER_AND_CARD:SW_SAVE_CARD);
}
}
int32_t SW_CALL sw_get_save_destination(uint32_t* value,SwError* e) {
    return boundary(e,SW_READ,[&]()->int32_t {
        if(!value) return result(e,SW_ARGUMENT,SW_READ,0,"Destination output required");
        auto status=ready_check(e,SW_READ); if(status) return status;
        PropertyList props; status=props.load(sdk::CrDeviceProperty_StillImageStoreDestination,e,SW_READ); if(status) return status;
        const auto current=props.list[0].GetCurrentValue(); status=props.close(e,SW_READ); if(status) return status;
        if(current<1 || current>3) return result(e,SW_UNSUPPORTED,SW_READ,0,"Unrecognized save destination");
        *value=project_destination(current); return SW_OK;
    });
}
int32_t SW_CALL sw_set_save_destination(uint32_t requested,uint32_t ms,uint32_t* actual,SwError* e) {
    return boundary(e,SW_SET,[&]()->int32_t {
        if(!actual || !valid_timeout(ms) || requested<1 || requested>3) return result(e,SW_ARGUMENT,SW_SET,0,"Invalid destination/output/timeout");
        if(movie_owned || transfer_active || feature_uncertain || connection_mode!=SW_MODE_REMOTE || capture_is_waiting() || unsafe_capture_id) { auto rc=result(e,SW_STATE,SW_SET,0,"Cannot change destination during active/unconfirmed capture"); e->capture_id=unsafe_capture_id?unsafe_capture_id:capture_current_id(); return rc; }
        auto status=ready_check(e,SW_SET); if(status) return status;
        const uint32_t target=requested==SW_SAVE_COMPUTER?sdk::CrStillImageStoreDestination_HostPC:
            (requested==SW_SAVE_COMPUTER_AND_CARD?sdk::CrStillImageStoreDestination_HostPCAndMemoryCard:sdk::CrStillImageStoreDestination_MemoryCard);
        const auto deadline=Clock::now()+std::chrono::milliseconds(ms);
        PropertyList props; status=props.load(sdk::CrDeviceProperty_StillImageStoreDestination,e,SW_SET); if(status) return status;
        auto& prop=props.list[0];
        if(!prop.IsSetEnableCurrentValue()) return result(e,SW_UNSUPPORTED,SW_SET,0,"Destination not writable");
        size_t width=0;
        switch(prop.GetValueType()) { case sdk::CrDataType_UInt8Array: width=1; break; case sdk::CrDataType_UInt16Array: width=2; break; case sdk::CrDataType_UInt32Array: width=4; break; default: return result(e,SW_UNSUPPORTED,SW_SET,0,"Unknown destination candidate type"); }
        bool supported=false; const auto size=prop.GetValueSize(); const auto data=prop.GetValues();
        if(size%width || (size && !data)) return result(e,SW_INTERNAL,SW_SET,0,"Malformed destination candidates");
        for(size_t offset=0;offset<size;offset+=width) { uint32_t candidate=0; std::memcpy(&candidate,data+offset,width); if(candidate==target) supported=true; }
        if(!supported) return result(e,SW_UNSUPPORTED,SW_SET,0,"Destination is not a real camera candidate");
        sdk::CrDeviceProperty local=prop; status=props.close(e,SW_SET); if(status) return status;
        local.SetCurrentValue(target); auto sdk_rc=sdk::SetDeviceProperty(device,&local);
        if(sdk_rc) { fault=true; return result(e,SW_SDK,SW_SET,sdk_rc,"Set destination failed; readback unconfirmed"); }
        for(;;) {
            PropertyList readback; status=readback.load(sdk::CrDeviceProperty_StillImageStoreDestination,e,SW_VERIFY); if(status) { fault=true; return status; }
            auto current=readback.list[0].GetCurrentValue(); status=readback.close(e,SW_VERIFY); if(status) return status;
            if(current==target) { *actual=project_destination(current); return result(e,SW_OK,SW_VERIFY,0,"Destination confirmed by readback"); }
            if(Clock::now()>=deadline) { fault=true; return result(e,SW_TIMEOUT,SW_VERIFY,0,"Destination readback timed out; state unconfirmed"); }
            std::this_thread::sleep_until(std::min(deadline,Clock::now()+100ms));
        }
    });
}
int32_t SW_CALL sw_get_abi_info(uint32_t* version,uint32_t* es,uint32_t* cs,uint32_t* ps,uint32_t* rs,SwError* e) {
    return boundary(e,SW_NONE,[&]()->int32_t {
        if(!version || !es || !cs || !ps || !rs) return result(e,SW_ARGUMENT,SW_NONE,0,"All ABI outputs required");
        *version=2; *es=sizeof(SwError); *cs=sizeof(SwCameraInfo); *ps=sizeof(SwParameters); *rs=sizeof(SwCaptureResult); return SW_OK;
    });
}

int32_t SW_CALL sw_check_capture_ready(SwError* e) {
    return boundary(e,SW_CAPTURE_CHECK,[&]()->int32_t {
        auto status=ready_check(e,SW_CAPTURE_CHECK); if(status) return status;
        if(movie_owned || transfer_active || feature_uncertain || connection_mode!=SW_MODE_REMOTE || capture_is_waiting() || unsafe_capture_id) { auto rc=result(e,SW_STATE,SW_CAPTURE_CHECK,0,"Active or unconfirmed capture"); e->capture_id=unsafe_capture_id?unsafe_capture_id:capture_current_id(); return rc; }
        CrInt32u queued=0; auto rc=sdk::GetCommandQueueCount(device,&queued);
        if(rc) return result(e,SW_SDK,SW_CAPTURE_CHECK,rc,"Cannot read command queue");
        if(queued) return result(e,SW_STATE,SW_CAPTURE_CHECK,0,"Command queue not empty");
        const auto old_raw=capture.output.raw_format, old_save=capture.output.save_destination;
        const auto status_check=capture_preconditions(e);
        capture.output.raw_format=old_raw; capture.output.save_destination=old_save;
        return status_check;
    });
}

int32_t SW_CALL sw_get_exposure_abi_sizes(uint32_t* request_size,uint32_t* capabilities_size,uint32_t* info_size,SwError* e) {
    return boundary(e,SW_NONE,[&]()->int32_t {
        if(!request_size||!capabilities_size||!info_size)return result(e,SW_ARGUMENT,SW_NONE,0,"All exposure ABI size outputs required");
        *request_size=sizeof(SwExposureRequest);*capabilities_size=sizeof(SwExposureCapabilities);*info_size=sizeof(SwExposureInfo);return SW_OK;
    });
}
int32_t SW_CALL sw_get_exposure_capabilities(SwExposureCapabilities* out,SwError* e) {
    return boundary(e,SW_SUPPORT,[&]()->int32_t {
        if(!out||out->size!=sizeof(*out))return result(e,SW_ARGUMENT,SW_SUPPORT,0,"Invalid exposure capabilities size");
        *out={};out->size=sizeof(*out);auto rc=ready_check(e,SW_SUPPORT);if(rc)return rc;
        PropertyList p;rc=p.load(sdk::CrDeviceProperty_ShutterSpeed,e,SW_SUPPORT);if(rc)return rc;
        out->bulb_candidate_present=has_candidate(p.list[0],sdk::CrShutterSpeed_Bulb)?1:0;rc=p.close(e,SW_SUPPORT);if(rc)return rc;
        out->bulb_min_seconds=1;out->bulb_max_seconds=900;out->bulb_step_seconds=0.001;
        out->max_exposure_timeout_seconds=1800;out->max_download_timeout_seconds=3600;
        // Timer validity depends on current Shutter=Bulb. Absence here is not silently treated as Off.
        PropertyList timer;SwError optional{sizeof(SwError)};
        if(!timer.load(sdk::CrDeviceProperty_BulbTimerSetting,&optional,SW_SUPPORT)) {
            out->timer_setting_readable=1;out->timer_enabled=static_cast<uint32_t>(timer.list[0].GetCurrentValue());
            rc=timer.close(e,SW_SUPPORT);if(rc)return rc;
        }
        return result(e,SW_OK,SW_SUPPORT,0,"Bulb candidate queried; setup must additionally confirm writable Timer Off; time range is project policy");
    });
}
int32_t SW_CALL sw_capture_start_v2(const char* root,const char* prefix,const SwExposureRequest* request,SwCaptureResult* out,SwError* e) {
    if(!request)return capture_boundary(0,out,e,SW_EXPOSURE_SETUP,[&] {return capture_error(e,SW_ARGUMENT,SW_EXPOSURE_SETUP,0,"Exposure request required",0);});
    return start_capture(root,prefix,60000,out,e,request);
}
int32_t SW_CALL sw_capture_exposure_info(uint64_t id,SwExposureInfo* out,SwError* e) {
    return boundary(e,SW_EXPOSURE_TIMER,[&]()->int32_t {
        if(!out||out->size!=sizeof(*out))return capture_error(e,SW_ARGUMENT,SW_EXPOSURE_TIMER,0,"Invalid exposure info size",id);
        *out={};out->size=sizeof(*out);
        if(!id)return capture_error(e,SW_ARGUMENT,SW_EXPOSURE_TIMER,0,"Nonzero capture id required",id);
        if(capture.used&&id==capture.output.capture_id) {
            exposure_tick();
            if(capture.button_down && device && cb==capture.session && !cb->disconnected.load()) {
                PropertyList elapsed;SwError optional{sizeof(SwError)};
                if(!elapsed.load(sdk::CrDeviceProperty_ElapsedBulbExposureTime,&optional,SW_EXPOSURE_TIMER)) {
                    capture.exposure.camera_elapsed_seconds=static_cast<uint32_t>(elapsed.list[0].GetCurrentValue());capture.exposure.camera_elapsed_valid=1;
                    const auto rc=elapsed.close(e,SW_EXPOSURE_TIMER);if(rc){e->capture_id=id;return rc;}
                }
            }
            *out=capture.exposure;return capture_error(e,SW_OK,SW_EXPOSURE_TIMER,0,"Info only; software hold is not sensor/EXIF exposure time",id);
        }
        for(const auto& old:retired)if(old.output.capture_id==id){*out=old.exposure;return capture_error(e,SW_OK,SW_EXPOSURE_TIMER,0,"Archived exposure info",id);}
        return capture_error(e,SW_STATE,SW_EXPOSURE_TIMER,0,"Unknown capture id; no previous exposure info fallback",id);
    });
}
int32_t SW_CALL sw_capture_end_exposure(uint64_t id,SwCaptureResult* out,SwError* e) {
    return capture_boundary(id,out,e,SW_CAPTURE_UP,[&]()->int32_t {
        if(!id||!capture.used||id!=capture.output.capture_id||!capture_is_waiting()||!capture.button_down)
            return capture_error(e,SW_STATE,SW_CAPTURE_UP,0,"Early end requires current actively held Bulb task",id);
        release_bulb(SW_END_EARLY);
        finish_capture_failure(e,SW_ENDED_EARLY,SW_CAPTURE_UP,capture.exposure.release_error.sony_code,"Exposure ended early by caller; partial file never fulfils original request");
        return publish_capture(out,e);
    });
}

#include "sony_features.inc"
