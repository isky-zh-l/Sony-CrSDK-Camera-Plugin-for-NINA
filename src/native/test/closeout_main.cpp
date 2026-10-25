#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include "sony_wrapper.h"
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <chrono>
#include <thread>
#include <string>
#include <stdexcept>
#include <filesystem>
#include <vector>
#include <cfenv>
#include <algorithm>
static SwError error{sizeof(SwError)};
static bool connected=false, restore_needed=false;
static uint32_t original_destination=0;
static uint64_t last_task=0;
static void need(bool condition,const char* text) { std::printf("CHECK %s %s\n",text,condition?"PASS":"FAIL"); if(!condition) throw std::runtime_error(text); }
static int32_t log(const char* name,int32_t rc) {
 std::printf("%s rc=%d sony=0x%08X stage=%u id=%llu message=%s\n",name,rc,error.sony_code,error.stage,static_cast<unsigned long long>(error.capture_id),error.message);return rc;
}
static void okay(const char* name,int32_t rc) { need(log(name,rc)==SW_OK,name); }
static void connect_camera(bool fake) {
 uint32_t n=0; okay("enumerate",sw_enumerate(&n,&error));need(n==1,"one camera");
 SwCameraInfo info{sizeof(info)};okay("camera info",sw_camera_info(0,&info,&error));
 std::printf("CAMERA=%s connection=%s\n",info.model,info.connection);
 need((std::strcmp(info.model,"SIMULATED")==0)==fake,"expected real/fake backend; never mistake a fake RAW for a real photo");
 okay("connect",sw_connect(0,15000,&error));connected=true;okay("ready",sw_wait_ready(20000,&error));
}
static void disconnect_clean() {okay("disconnect clean",sw_disconnect(10000,&error));connected=false;}
static SwCaptureResult begin(const char* root,const char* prefix,uint32_t timeout=60000) {
 SwCaptureResult result{sizeof(result)};
 const auto rc=prefix?sw_capture_start_ex(root,prefix,timeout,&result,&error):sw_capture_start(root,timeout,&result,&error);
 if(result.capture_id)last_task=result.capture_id;
 okay("capture start",rc);need(result.state==SW_CAPTURE_WAITING && !result.arw_path[0],"start has no premature file");return result;
}
static SwCaptureResult finish(uint64_t id,uint32_t slice=5) {
 SwCaptureResult result{sizeof(result)};int32_t rc;double maximum=0;unsigned pending=0;
 do { const auto before=std::chrono::steady_clock::now();rc=sw_capture_wait(id,slice,&result,&error);
  maximum=std::max(maximum,std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-before).count());
  if(rc==SW_PENDING) {++pending; if(!slice)std::this_thread::sleep_for(std::chrono::milliseconds(1));}
 }while(rc==SW_PENDING);
 okay("capture finish",rc);need(result.capture_id==id && result.state==SW_CAPTURE_SUCCEEDED && result.arw_path[0],"exact successful task result");
 need(std::filesystem::file_size(std::filesystem::u8path(result.arw_path))==result.file_bytes,"returned file exists with exact size");
 std::printf("SAMPLE_ID=%llu\nSAMPLE_PATH=%s\nSAMPLE_BYTES=%llu\nWAIT_MAX_MS=%.3f slice=%u pending=%u\n",
  static_cast<unsigned long long>(id),result.arw_path,static_cast<unsigned long long>(result.file_bytes),maximum,slice,pending);
 return result;
}
static bool reconcile(uint64_t id,unsigned seconds=15) {
 SwCaptureResult result{sizeof(result)};
 const auto end=std::chrono::steady_clock::now()+std::chrono::seconds(seconds);int32_t rc;
 do {rc=sw_capture_reconcile(id,100,&result,&error);}while(rc==SW_PENDING && std::chrono::steady_clock::now()<end);
 log("explicit reconcile",rc);
 if(rc==SW_OK)need(result.state!=SW_CAPTURE_SUCCEEDED && !result.arw_path[0] && !result.file_bytes,"reconcile proves idle without rewriting failed result");
 return rc==SW_OK;
}
static void rejected_next(const char* root,uint64_t blocked) {
 SwCaptureResult reused{sizeof(reused)};reused.state=SW_CAPTURE_SUCCEEDED;strcpy_s(reused.arw_path,"old-image.ARW");
 need(log("next start blocked",sw_capture_start_ex(root,"HOST",60000,&reused,&error))==SW_STATE,"unconfirmed task blocks next");
 need(error.capture_id==blocked && reused.state==SW_CAPTURE_NONE && !reused.arw_path[0],"block has correct id and no old image");
}
static void normal_sequence(const char* root,bool fake) {
 uint32_t actual=0;okay("get original destination",sw_get_save_destination(&original_destination,&error));
 std::printf("ORIGINAL_DESTINATION=%u\n",original_destination);
 okay("set Computer only",sw_set_save_destination(SW_SAVE_COMPUTER,5000,&actual,&error));restore_needed=true;need(actual==SW_SAVE_COMPUTER,"Computer-only readback");
 auto first=begin(root,nullptr); // also exercise the legacy start entry and full generated prefix
 SwCaptureResult busy{sizeof(busy)};
 need(log("concurrent start rejected",sw_capture_start_ex(root,"HOST",60000,&busy,&error))==SW_STATE && error.capture_id==first.capture_id,"one active task");
 first=finish(first.capture_id);
 auto second=begin(root,"HOST");
 SwCaptureResult old{sizeof(old)};okay("query archived first while second active",sw_capture_wait(first.capture_id,0,&old,&error));
 need(old.capture_id==first.capture_id && std::strcmp(old.arw_path,first.arw_path)==0,"explicit old-id query returns only that old result");
 second=finish(second.capture_id);
 need(first.capture_id!=second.capture_id && std::strcmp(first.arw_path,second.arw_path)!=0,"same connection distinct ids and paths");
 uint32_t dummy=0;need(log("generic parameter error",sw_set_iso(400,0,&dummy,&error))==SW_ARGUMENT && error.capture_id==0,"generic errors do not inherit previous capture id");
 disconnect_clean();connect_camera(fake);
 okay("reassert Computer-only after reconnect",sw_set_save_destination(SW_SAVE_COMPUTER,5000,&actual,&error));need(actual==SW_SAVE_COMPUTER,"Computer-only on new connection");
 auto third=begin(root,"HOST");third=finish(third.capture_id);
 need(third.capture_id!=second.capture_id && std::strcmp(third.arw_path,second.arw_path)!=0,"same process reconnect creates new task/path");
 need(third.save_destination==SW_SAVE_COMPUTER && second.save_destination==SW_SAVE_COMPUTER,"Computer-only captures verified through wrapper");
 need(std::filesystem::u8path(second.arw_path).filename().u8string().rfind("HOST",0)==0 &&
      std::filesystem::u8path(third.arw_path).filename().u8string().rfind("HOST",0)==0,"custom prefix survives Computer-only routing");
 okay("restore original destination",sw_set_save_destination(original_destination,5000,&actual,&error));need(actual==original_destination,"original destination restored");restore_needed=false;
}
static void cancel_and_timeout(const char* root,bool fake) {
 auto cancelled=begin(root,"CANCEL");
 need(log("cancel active",sw_capture_cancel(cancelled.capture_id,&cancelled,&error))==SW_CANCELLED,"controlled active cancellation");
 need(cancelled.state==SW_CAPTURE_CANCELLED_UNCONFIRMED && !cancelled.arw_path[0],"cancel not a successful photo or a camera-stop claim");
 rejected_next(root,cancelled.capture_id);
 SwCaptureResult result{sizeof(result)};need(sw_capture_wait(cancelled.capture_id,0,&result,&error)==SW_CANCELLED && !result.arw_path[0],"cancelled result stays terminal");
 need(reconcile(cancelled.capture_id),"cancelled exact download drained before next task");
 const uint32_t timeout=fake?300:2500;
 auto timed=begin(root,"TIMEOUT",timeout);
 std::this_thread::sleep_for(std::chrono::milliseconds(timeout+150)); // total deadline, independent of when the app next waits
 need(log("total deadline",sw_capture_wait(timed.capture_id,0,&timed,&error))==SW_TIMEOUT,"controlled total-deadline timeout");
 need(timed.state==SW_CAPTURE_TIMED_OUT_UNCONFIRMED && !timed.arw_path[0] && error.capture_id==timed.capture_id,"timeout returns this id and no old file");
 rejected_next(root,timed.capture_id);need(reconcile(timed.capture_id),"timed-out exact download drained before next task");
}
static void interrupted_disconnect(const char* root,bool fake) {
 auto task=begin(root,"DISC");
 need(log("disconnect during capture",sw_disconnect(10000,&error))==SW_CANCELLED && error.capture_id==task.capture_id,"disconnect reports interrupted task, not misleading success");connected=false;
 SwCaptureResult result{sizeof(result)};
 need(sw_capture_wait(task.capture_id,0,&result,&error)==SW_CANCELLED && !result.arw_path[0],"interrupted task never returns old/successful image");
 need(log("reconnect still blocked",sw_connect(0,15000,&error))==SW_STATE && error.capture_id==task.capture_id,"transport close alone does not confirm camera idle");
 const bool proof=reconcile(task.capture_id,5);
 std::printf("POST_DISCONNECT_IDLE_PROVEN=%d (if 0: quarantine retained, no further exposure)\n",int(proof));
 if(proof) {connect_camera(fake);disconnect_clean();} // no photo after the interrupt
}
#include "bulb_cases.h"
int wmain(int argc,wchar_t** argv) {
 setvbuf(stdout,nullptr,_IONBF,0);if(argc!=3)return 2;
 const bool bulb_mode=std::wcsncmp(argv[1],L"--bulb-",7)==0;
 const bool fake=std::wcscmp(argv[1],L"--fake")==0 || std::wcscmp(argv[1],L"--bulb-fake")==0;
 char root[1024]{};if(!WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,argv[2],-1,root,sizeof(root),nullptr,nullptr))return 2;
 char scenario[64]{};GetEnvironmentVariableA("SW_FAKE_CASE",scenario,sizeof(scenario));
 if(!fake && scenario[0])return 2;
 std::printf("CLOSEOUT backend=%s scenario=%s; client only calls public wrapper ABI\n",fake?"FAKE":"REAL",scenario);
 bool success=false;const int rounding=std::fegetround();
 try {
  uint32_t v=0,es=0,cs=0,ps=0,rs=0;okay("ABI",sw_get_abi_info(&v,&es,&cs,&ps,&rs,&error));need(v==2&&es==184&&cs==168&&ps==32&&rs==2304,"ABI sizes");
  okay("initialize",sw_initialize(&error));connect_camera(fake);
  SwParameters p{sizeof(p)};okay("baseline parameters",sw_read_parameters(&p,&error));
  std::printf("BASELINE iso=%u seconds=%.17g f=%.17g\n",p.iso,p.exposure_seconds,p.f_number);
  need(p.iso_kind==SW_ISO_FIXED&&p.shutter_kind==SW_SHUTTER_SECONDS&&p.exposure_seconds<=1,"ordinary short exposure; no silent settings changes");
  okay("actual S2/command queue ready check",sw_check_capture_ready(&error));
  if(bulb_mode) {run_bulb_acceptance(root,fake,scenario);}
  else if(!fake && std::wcscmp(argv[1],L"--inspect")==0) { disconnect_clean(); success=true; }
  else if(fake&&(std::strcmp(scenario,"unknown")==0||std::strcmp(scenario,"overflow")==0||std::strncmp(scenario,"upfail",6)==0)) {
   SwCaptureResult t{sizeof(t)};auto rc=sw_capture_start_ex(root,"HOST",60000,&t,&error);last_task=t.capture_id;
   if(std::strncmp(scenario,"upfail",6)==0) need(log("first Up failed with bounded retry",rc)==SW_SDK&&error.sony_code==0x8402&&error.stage==SW_CAPTURE_UP&&error.capture_id==t.capture_id,"Up error raw code/id preserved even if retry succeeded");
   else {okay("start fault scenario",rc);do{rc=sw_capture_wait(t.capture_id,50,&t,&error);}while(rc==SW_PENDING);need(log("injected callback failure",rc)==SW_ASSOCIATION,"unknown path / overflow fails closed");}
   need(!t.arw_path[0]&&t.file_bytes==0,"failure no old file");rejected_next(root,t.capture_id);
   if(std::strcmp(scenario,"upfail")==0)need(reconcile(t.capture_id),"retry Up success plus exact file permits explicit recovery");
   if(std::strcmp(scenario,"upfail_both")==0)need(!reconcile(t.capture_id),"two failed Up attempts cannot prove release or resume");
  } else if(fake&&std::strcmp(scenario,"chunks")==0) {
   auto t=begin(root,"CHUNK");SwCaptureResult r{sizeof(r)};need(sw_capture_wait(t.capture_id,0,&r,&error)==SW_PENDING,"initial file stability pending");
   std::this_thread::sleep_for(std::chrono::milliseconds(600));unsigned calls=0;int32_t rc;
   do{rc=sw_capture_wait(t.capture_id,0,&r,&error);++calls;}while(rc==SW_PENDING);
   okay("chunked full read",rc);need(calls>=256,"16MiB never read in a single zero-duration slice");std::printf("ZERO_SLICE_READ_CALLS=%u\n",calls);
  } else if(fake&&std::strcmp(scenario,"recovery")==0) {cancel_and_timeout(root,true);interrupted_disconnect(root,true);}
  else {normal_sequence(root,fake);if(!fake){cancel_and_timeout(root,false);interrupted_disconnect(root,false);}}
  success=true;
 }catch(const std::exception& e){std::printf("FAILURE=%s\n",e.what());}
 // Best-effort restoration is never described as confirmed if quarantine/SDK refuses it.
 if(restore_needed&&connected) {
  if(last_task)reconcile(last_task,5);
  uint32_t actual=0;auto rc=sw_set_save_destination(original_destination,5000,&actual,&error);
  log("cleanup destination restore",rc);if(rc!=SW_OK||actual!=original_destination){success=false;std::printf("RESTORE_UNCONFIRMED original_destination=%u\n",original_destination);}
 }
 if(connected){auto rc=log("cleanup disconnect",sw_disconnect(10000,&error));if(rc!=SW_OK&&rc!=SW_CANCELLED)success=false;connected=false;}
 if(log("shutdown",sw_shutdown(&error))!=SW_OK)success=false;
 if(std::fegetround()!=rounding)success=false;
 std::printf("CLOSEOUT %s backend=%s\n",success?"PASS":"FAIL",fake?"FAKE":"REAL");return success?0:1;
}



