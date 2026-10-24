#include "sony_wrapper.h"
#include <cstdio>
#include <cstring>
#include <cfenv>
#include <vector>
#include <algorithm>
#include <cmath>
#include <limits>
static SwError err{sizeof(SwError)};
static bool call(const char* label,int32_t rc,int32_t expected=SW_OK) {
    std::printf("%s rc=%d expected=%d sony=0x%08X stage=%u message=%s\n",label,rc,expected,err.sony_code,err.stage,err.message);
    return rc==expected;
}
static bool check(const char* label,bool ok) { std::printf("CHECK %s %s\n",label,ok?"PASS":"FAIL"); return ok; }
static void print(const char* label,const SwParameters& p) {
    std::printf("%s iso=%u iso_kind=%u seconds=%.17g shutter_kind=%u f=%.17g\n",label,p.iso,p.iso_kind,p.exposure_seconds,p.shutter_kind,p.f_number);
}
static bool same(const SwParameters& a,const SwParameters& b) {
    return a.iso==b.iso && a.iso_kind==b.iso_kind && a.shutter_kind==b.shutter_kind &&
        a.exposure_seconds==b.exposure_seconds && a.f_number==b.f_number;
}
static bool contract() {
    uint32_t n=0, actual_iso=0x12345678; double actual_seconds=-123.0;
    bool ok=call("ISO query before connection",sw_get_supported_iso(nullptr,0,&n,&err),SW_STATE);
    ok=call("shutter query before connection",sw_get_supported_exposure_seconds(nullptr,0,&n,&err),SW_STATE) && ok;
    ok=call("ISO set before connection",sw_set_iso(400,1000,&actual_iso,&err),SW_STATE) && ok;
    ok=call("seconds set before connection",sw_set_exposure_seconds(0.01,1000,&actual_seconds,&err),SW_STATE) && ok;
    ok=call("null count",sw_get_supported_iso(nullptr,0,nullptr,&err),SW_ARGUMENT) && ok;
    ok=call("null array nonzero capacity",sw_get_supported_exposure_seconds(nullptr,1,&n,&err),SW_ARGUMENT) && ok;
    ok=call("null actual ISO",sw_set_iso(400,1000,nullptr,&err),SW_ARGUMENT) && ok;
    ok=call("zero timeout",sw_set_iso(400,0,&actual_iso,&err),SW_ARGUMENT) && ok;
    for(double invalid:{0.0,-1.0,std::numeric_limits<double>::infinity(),std::numeric_limits<double>::quiet_NaN()})
        ok=call("invalid seconds",sw_set_exposure_seconds(invalid,1000,&actual_seconds,&err),SW_ARGUMENT) && ok;
    ok=check("actual outputs untouched on rejection",actual_iso==0x12345678 && actual_seconds==-123.0) && ok;
    return ok;
}
static bool parameter_test() {
    SwParameters original{sizeof(SwParameters)};
    if(!call("baseline read",sw_read_parameters(&original,&err))) return false;
    print("BASELINE (recorded before all writes)",original);
    if(!check("baseline restorable fixed ISO and ordinary shutter",original.iso_kind==SW_ISO_FIXED && original.shutter_kind==SW_SHUTTER_SECONDS)) return false;
    uint32_t ni=0,ns=0;
    if(!call("ISO count",sw_get_supported_iso(nullptr,0,&ni,&err)) ||
       !call("seconds count",sw_get_supported_exposure_seconds(nullptr,0,&ns,&err))) return false;
    if(!check("nonempty bounded candidate counts",ni>0 && ns>0 && ni<=65536 && ns<=65536)) return false;
    std::vector<uint32_t> isos(ni); std::vector<double> shutters(ns);
    if(!call("ISO candidates",sw_get_supported_iso(isos.data(),ni,&ni,&err)) ||
       !call("seconds candidates",sw_get_supported_exposure_seconds(shutters.data(),ns,&ns,&err))) return false;
    isos.resize(ni); shutters.resize(ns);
    std::printf("SUPPORTED_ISO count=%u:",ni); for(auto v:isos) std::printf(" %u",v); std::printf("\n");
    std::printf("SUPPORTED_SECONDS count=%u:",ns); for(auto v:shutters) std::printf(" %.17g",v); std::printf("\n");
    bool ok=true;
    uint32_t iso_sentinel=0x12345678, required=0; double shutter_sentinel=-123.0;
    ok=call("ISO insufficient capacity",sw_get_supported_iso(&iso_sentinel,0,&required,&err),SW_BUFFER) && ok;
    ok=check("ISO buffer untouched and required count",iso_sentinel==0x12345678 && required==ni) && ok;
    ok=call("seconds insufficient capacity",sw_get_supported_exposure_seconds(&shutter_sentinel,0,&required,&err),SW_BUFFER) && ok;
    ok=check("seconds buffer untouched and required count",shutter_sentinel==-123.0 && required==ns) && ok;
    auto original_iso=std::find(isos.begin(),isos.end(),original.iso);
    auto original_shutter=std::find(shutters.begin(),shutters.end(),original.exposure_seconds);
    ok=check("original values present in current candidates",original_iso!=isos.end() && original_shutter!=shutters.end()) && ok;
    auto iso_choice=std::find(isos.begin(),isos.end(),400u);
    if(iso_choice==isos.end() || *iso_choice==original.iso) iso_choice=std::find_if(isos.begin(),isos.end(),[&](uint32_t v){return v!=original.iso;});
    auto shutter_choice=std::find(shutters.begin(),shutters.end(),1.0/60.0);
    if(shutter_choice==shutters.end() || *shutter_choice==original.exposure_seconds)
        shutter_choice=std::find_if(shutters.begin(),shutters.end(),[&](double v){return v!=original.exposure_seconds;});
    ok=check("different supported values available",iso_choice!=isos.end() && shutter_choice!=shutters.end()) && ok;
    if(!ok) return false; // Nothing has been written.
    const auto target_iso=*iso_choice; const auto target_seconds=*shutter_choice;
    std::printf("TARGET iso=%u seconds=%.17g\n",target_iso,target_seconds);
    bool touched_iso=false,touched_shutter=false;
    uint32_t actual_iso=0; double actual_seconds=-1;
    touched_iso=true;
    ok=call("set ISO",sw_set_iso(target_iso,5000,&actual_iso,&err));
    ok=check("returned actual ISO",ok && actual_iso==target_iso) && ok;
    SwParameters after_iso{sizeof(SwParameters)};
    if(ok) {
        ok=call("independent read after ISO",sw_read_parameters(&after_iso,&err));
        if(ok) { print("AFTER_ISO",after_iso); ok=check("ISO changed; shutter and aperture unchanged",after_iso.iso==target_iso &&
            after_iso.iso_kind==SW_ISO_FIXED && after_iso.exposure_seconds==original.exposure_seconds && after_iso.f_number==original.f_number); }
    }
    if(ok) {
        touched_shutter=true;
        ok=call("set seconds",sw_set_exposure_seconds(target_seconds,5000,&actual_seconds,&err));
        ok=check("returned actual seconds exact",ok && actual_seconds==target_seconds) && ok;
    }
    SwParameters before_reject{sizeof(SwParameters)};
    if(ok) {
        ok=call("independent read after seconds",sw_read_parameters(&before_reject,&err));
        if(ok) { print("BEFORE_REJECTION",before_reject); ok=check("both requested values active",before_reject.iso==target_iso &&
            before_reject.iso_kind==SW_ISO_FIXED && before_reject.shutter_kind==SW_SHUTTER_SECONDS &&
            before_reject.exposure_seconds==target_seconds && before_reject.f_number==original.f_number); }
    }
    // Positive finite non-candidates: one ULP from a supported shutter proves there is no approximate matching.
    if(ok) {
        uint32_t unsupported_iso=1;
        while(std::find(isos.begin(),isos.end(),unsupported_iso)!=isos.end()) ++unsupported_iso;
        double unsupported_seconds=std::nextafter(target_seconds,std::numeric_limits<double>::infinity());
        while(std::find(shutters.begin(),shutters.end(),unsupported_seconds)!=shutters.end())
            unsupported_seconds=std::nextafter(unsupported_seconds,std::numeric_limits<double>::infinity());
        std::printf("UNSUPPORTED iso=%u seconds=%.17g (one ULP above selected shutter unless occupied)\n",unsupported_iso,unsupported_seconds);
        actual_iso=0x12345678;
        ok=call("reject unsupported ISO",sw_set_iso(unsupported_iso,5000,&actual_iso,&err),SW_UNSUPPORTED);
        ok=check("rejected ISO actual untouched",actual_iso==0x12345678) && ok;
        SwParameters after_reject_iso{sizeof(SwParameters)};
        bool read_ok=call("read after rejected ISO",sw_read_parameters(&after_reject_iso,&err));
        if(read_ok) print("AFTER_REJECT_ISO",after_reject_iso);
        ok=check("rejected ISO changed no parameters",read_ok && same(before_reject,after_reject_iso)) && ok;
        if(ok) {
            actual_seconds=-123.0;
            ok=call("reject unsupported seconds",sw_set_exposure_seconds(unsupported_seconds,5000,&actual_seconds,&err),SW_UNSUPPORTED);
            ok=check("rejected seconds actual untouched",actual_seconds==-123.0) && ok;
            SwParameters after_reject_seconds{sizeof(SwParameters)};
            read_ok=call("read after rejected seconds",sw_read_parameters(&after_reject_seconds,&err));
            if(read_ok) print("AFTER_REJECT_SECONDS",after_reject_seconds);
            ok=check("rejected seconds changed no parameters",read_ok && same(before_reject,after_reject_seconds)) && ok;
        }
    }
    // Always attempt restoration after any attempted write, even if a verification above failed.
    // Wrapper quarantine may reject it: log that honestly instead of claiming camera state is known.
    bool restored=true;
    if(touched_shutter) {
        const bool rc=call("restore seconds",sw_set_exposure_seconds(original.exposure_seconds,5000,&actual_seconds,&err));
        restored=check("restored actual seconds",rc && actual_seconds==original.exposure_seconds) && restored;
    }
    if(touched_iso) {
        const bool rc=call("restore ISO",sw_set_iso(original.iso,5000,&actual_iso,&err));
        restored=check("restored actual ISO",rc && actual_iso==original.iso) && restored;
    }
    SwParameters final_values{sizeof(SwParameters)};
    const bool final_read=call("final independent read",sw_read_parameters(&final_values,&err));
    if(final_read) print("FINAL",final_values);
    restored=check("FINAL EXACT BASELINE RESTORED",final_read && same(original,final_values)) && restored;
    if(!restored) std::printf("RESTORATION UNCONFIRMED; stop testing and inspect camera; baseline logged above\n");
    return restored && ok;
}
int main(int argc,char** argv) {
    setvbuf(stdout,nullptr,_IONBF,0);
    const bool contract_mode=argc==2 && std::strcmp(argv[1],"--contract")==0;
    const int rounding=std::fegetround();
    std::printf("STEP2 wrapper parameter test; no SDK includes/calls; no shooting\n");
    if(!call("initialize",sw_initialize(&err))) return 1;
    bool ok=false;
    if(contract_mode) ok=contract();
    else {
        uint32_t count=0;
        ok=call("enumerate",sw_enumerate(&count,&err));
        ok=check("exactly one camera",ok && count==1) && ok;
        if(ok) {
            SwCameraInfo info{sizeof(SwCameraInfo)};
            ok=call("camera info",sw_camera_info(0,&info,&err));
            if(ok) std::printf("camera model=%s connection=%s\n",info.model,info.connection);
        }
        if(ok) {
            ok=call("connect",sw_connect(0,15000,&err));
            if(ok) ok=call("wait ready",sw_wait_ready(20000,&err));
            if(ok) ok=parameter_test();
            const bool clean=call("disconnect",sw_disconnect(10000,&err)); ok=clean && ok;
        }
    }
    const bool released=call("shutdown",sw_shutdown(&err)); ok=released && ok;
    ok=check("caller rounding preserved",std::fegetround()==rounding) && ok;
    std::printf("%s %s\n",contract_mode?"STEP2 CONTRACT":"STEP2",ok?"PASS":"FAIL");
    return ok?0:1;
}
