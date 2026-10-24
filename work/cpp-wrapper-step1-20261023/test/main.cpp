#include "sony_wrapper.h"
#include <cstdio>
#include <cstring>
#include <cfenv>
static SwError err{sizeof(SwError)};
static bool call(const char* name,int32_t rc) {
    std::printf("%s rc=%d sony=0x%08X stage=%u capture=%llu message=%s\n",name,rc,err.sony_code,err.stage,
        static_cast<unsigned long long>(err.capture_id),err.message);
    std::fflush(stdout); return rc==SW_OK;
}
int main(int argc,char** argv) {
    setvbuf(stdout,nullptr,_IONBF,0);
    std::printf("Sony wrapper C ABI test; x64=%d; NO direct SDK calls\n",int(sizeof(void*)==8));
    const int caller_rounding=std::fegetround();
    std::printf("rounding_before_init=%d (FE_TONEAREST=%d)\n",caller_rounding,FE_TONEAREST);
    if(!call("initialize",sw_initialize(&err))) return 1;
    std::printf("rounding_after_init=%d\n",std::fegetround());
    bool ok=std::fegetround()==caller_rounding, connected=false;
    if(argc==2 && std::strcmp(argv[1],"--contract")==0) {
        SwParameters p{sizeof(SwParameters)};
        bool rejected=ok && sw_read_parameters(&p,&err)==SW_STATE;
        call("read before connection (expect SW_STATE)",err.code);
        rejected= (sw_connect(0,0,&err)==SW_ARGUMENT) && rejected;
        call("zero timeout (expect SW_ARGUMENT)",err.code);
        rejected= (sw_enumerate(nullptr,&err)==SW_ARGUMENT) && rejected;
        call("null count (expect SW_ARGUMENT)",err.code);
        SwCameraInfo info{};
        rejected= (sw_camera_info(0,&info,&err)==SW_ARGUMENT) && rejected;
        call("wrong structure size (expect SW_ARGUMENT)",err.code);
        ok=call("shutdown",sw_shutdown(&err)) && rejected;
        std::printf("CONTRACT %s\n",ok?"PASS":"FAIL"); return ok?0:1;
    }
    uint32_t count=0;
    ok=call("enumerate",sw_enumerate(&count,&err));
    std::printf("camera_count=%u\n",count);
    for(uint32_t i=0;ok && i<count;++i) {
        SwCameraInfo info{sizeof(SwCameraInfo)};
        ok=call("camera_info",sw_camera_info(i,&info,&err));
        if(ok) std::printf("camera[%u] model=%s connection=%s\n",i,info.model,info.connection);
    }
    // Refuse ambiguous selection; this step targets a single attached A7 V.
    if(ok && count!=1) { std::printf("Expected exactly one camera; no connection attempted\n"); ok=false; }
    if(ok) {
        connected=call("connect",sw_connect(0,15000,&err)); ok=connected;
        if(ok) ok=call("wait_ready",sw_wait_ready(20000,&err));
        if(ok) {
            SwParameters p{sizeof(SwParameters)};
            ok=call("read_parameters",sw_read_parameters(&p,&err));
            if(ok) std::printf("iso=%u iso_kind=%u exposure_seconds=%.12g shutter_kind=%u f_number=%.4g\n",
                p.iso,p.iso_kind,p.exposure_seconds,p.shutter_kind,p.f_number);
        }
        // Cleanup also runs after failed/late connection; the wrapper decides whether a handle exists.
        bool clean=call("disconnect",sw_disconnect(10000,&err)); ok=clean && ok;
    }
    bool released=call("shutdown",sw_shutdown(&err)); ok=released && ok;
    const bool rounding_preserved=std::fegetround()==caller_rounding;
    std::printf("rounding_after_test=%d preserved=%d\n",std::fegetround(),int(rounding_preserved));
    ok=rounding_preserved && ok;
    std::printf("STEP1 %s\n",ok?"PASS":"FAIL"); return ok?0:1;
}


