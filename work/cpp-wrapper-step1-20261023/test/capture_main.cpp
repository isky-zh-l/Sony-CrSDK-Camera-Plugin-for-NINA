#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include "sony_wrapper.h"
#include <cwchar>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <cfenv>
static SwError err{sizeof(SwError)};
static bool call(const char* label,int32_t rc,int32_t expected=SW_OK) {
    std::printf("%s rc=%d expected=%d sony=0x%08X stage=%u capture_id=%llu message=%s\n",label,rc,expected,
        err.sony_code,err.stage,static_cast<unsigned long long>(err.capture_id),err.message);
    return rc==expected;
}
static bool check(const char* text,bool ok) { std::printf("CHECK %s %s\n",text,ok?"PASS":"FAIL"); return ok; }
static bool equal_parameters(const SwParameters& a,const SwParameters& b) {
    return a.iso==b.iso && a.iso_kind==b.iso_kind && a.shutter_kind==b.shutter_kind &&
        a.exposure_seconds==b.exposure_seconds && a.f_number==b.f_number;
}
static void print_result(const SwCaptureResult& r) {
    std::printf("CAPTURE_RESULT id=%llu state=%u actual_valid=%u iso=%u seconds=%.17g f=%.17g raw_format=%u save_destination=%u notifications=%u bytes=%llu\n",
        static_cast<unsigned long long>(r.capture_id),r.state,r.actual_valid,r.actual_parameters.iso,r.actual_parameters.exposure_seconds,
        r.actual_parameters.f_number,r.raw_format,r.save_destination,r.download_notifications,static_cast<unsigned long long>(r.file_bytes));
    std::printf("RECEIVE_DIRECTORY=%s\nRESULT_PATH=%s\n",r.receive_directory,r.arw_path);
}
static bool contract() {
    SwCaptureResult r{sizeof(SwCaptureResult)};
    bool ok=call("start before connection",sw_capture_start("C:\\",60000,&r,&err),SW_STATE);
    ok=call("null root",sw_capture_start(nullptr,60000,&r,&err),SW_ARGUMENT) && ok;
    ok=call("zero total timeout",sw_capture_start("C:\\",0,&r,&err),SW_ARGUMENT) && ok;
    r.state=SW_CAPTURE_SUCCEEDED; r.capture_id=999; r.file_bytes=123;
    std::snprintf(r.arw_path,sizeof(r.arw_path),"C:/old-successful-image.ARW");
    ok=call("unknown task wait",sw_capture_wait(42,0,&r,&err),SW_STATE) && ok;
    ok=check("rejected call clears reused successful output",r.state==SW_CAPTURE_NONE && r.capture_id==0 && r.file_bytes==0 && r.arw_path[0]==0) && ok;
    ok=check("unknown task error retains requested id",err.capture_id==42) && ok;
    ok=call("zero task id",sw_capture_wait(0,0,&r,&err),SW_ARGUMENT) && ok;
    ok=call("oversize wait slice",sw_capture_wait(42,1001,&r,&err),SW_ARGUMENT) && ok;
    ok=call("cancel unknown task (no camera operation)",sw_capture_cancel(42,&r,&err),SW_STATE) && ok;
    SwCaptureResult invalid{};
    ok=call("invalid result layout",sw_capture_start("C:\\",60000,&invalid,&err),SW_ARGUMENT) && ok;
    ok=check("no task/path returned by invalid calls",r.capture_id==0 && r.arw_path[0]==0 && r.receive_directory[0]==0) && ok;
    return ok;
}
static bool shoot(const char* root) {
    SwParameters baseline{sizeof(SwParameters)};
    if(!call("baseline parameters",sw_read_parameters(&baseline,&err))) return false;
    std::printf("BASELINE iso=%u iso_kind=%u seconds=%.17g shutter_kind=%u f=%.17g\n",
        baseline.iso,baseline.iso_kind,baseline.exposure_seconds,baseline.shutter_kind,baseline.f_number);
    if(!check("short ordinary exposure prerequisite (no automatic settings change)",baseline.shutter_kind==SW_SHUTTER_SECONDS &&
        baseline.exposure_seconds>0 && baseline.exposure_seconds<=1.0 && baseline.iso_kind==SW_ISO_FIXED)) return false;
    SwCaptureResult r{sizeof(SwCaptureResult)};
    int32_t rc=sw_capture_start(root,60000,&r,&err);
    bool ok=call("capture start",rc);
    if(!ok) { print_result(r); return false; }
    const auto id=r.capture_id;
    ok=check("start is WAITING, no premature path",id!=0 && r.state==SW_CAPTURE_WAITING && r.arw_path[0]==0) && ok;
    // All operations, including cancellation, are on the owner thread between bounded wait slices.
    // This acceptance run intentionally does not cancel or trigger timeout/fault scenarios.
    do {
        rc=sw_capture_wait(id,500,&r,&err);
        if(rc==SW_PENDING) std::printf("PENDING id=%llu state=%u\n",static_cast<unsigned long long>(id),r.state);
    } while(rc==SW_PENDING);
    ok=call("capture terminal",rc) && ok;
    print_result(r);
    ok=check("same task, successful state and actual snapshot",rc==SW_OK && r.capture_id==id && r.state==SW_CAPTURE_SUCCEEDED &&
        r.actual_valid==1 && equal_parameters(baseline,r.actual_parameters)) && ok;
    if(rc!=SW_OK) return false; // Failure result must never be decoded as a successful ARW.
    const auto file=std::filesystem::u8path(r.arw_path);
    std::error_code error;
    const auto bytes=std::filesystem::file_size(file,error);
    std::ifstream stream(file,std::ios::binary); char header[4]{}; stream.read(header,sizeof(header));
    ok=check("returned exact file exists, size matches, test application can read",!error && bytes==r.file_bytes && stream.gcount()==4) && ok;
    SwCaptureResult again{sizeof(SwCaptureResult)};
    ok=call("repeat terminal wait",sw_capture_wait(id,0,&again,&err)) && ok;
    ok=check("terminal result is absorbing/idempotent",again.capture_id==r.capture_id && again.state==r.state &&
        again.file_bytes==r.file_bytes && std::strcmp(again.arw_path,r.arw_path)==0) && ok;
    SwCaptureResult refused{sizeof(SwCaptureResult)};
    ok=call("unknown capture rejected without triggering",sw_capture_wait(id+12345,0,&refused,&err),SW_STATE) && ok;
    ok=check("unknown task returns no task/path",refused.capture_id==0 && refused.arw_path[0]==0) && ok;
    SwParameters final_values{sizeof(SwParameters)};
    const bool read_ok=call("final parameters",sw_read_parameters(&final_values,&err));
    ok=check("camera parameters unchanged by shooting wrapper",read_ok && equal_parameters(baseline,final_values)) && ok;
    return ok;
}
int wmain(int argc,wchar_t** argv) {
    setvbuf(stdout,nullptr,_IONBF,0);
    const int rounding=std::fegetround();
    const bool contract_mode=argc==2 && std::wcscmp(argv[1],L"--contract")==0;
    if(!contract_mode && argc!=2) { std::printf("Usage: wrapper_capture_test.exe --contract OR <existing absolute UTF-8 receive root>\n"); return 2; }
    std::printf("STEP3 C ABI capture test; no Sony SDK includes/calls\n");
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
            if(ok) {
                char root_utf8[1024]{};
                if(!WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,argv[1],-1,root_utf8,sizeof(root_utf8),nullptr,nullptr)) ok=false;
                else ok=shoot(root_utf8);
            }
            const bool clean=call("disconnect",sw_disconnect(10000,&err)); ok=clean && ok;
        }
    }
    const bool released=call("shutdown",sw_shutdown(&err)); ok=released && ok;
    ok=check("caller rounding preserved",std::fegetround()==rounding) && ok;
    std::printf("%s %s\n",contract_mode?"STEP3 CONTRACT":"STEP3",ok?"PASS":"FAIL");
    return ok?0:1;
}
