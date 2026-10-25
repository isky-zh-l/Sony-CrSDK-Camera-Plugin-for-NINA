#include "sony_features.h"
#include <Windows.h>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <thread>
#include <chrono>
#include <cmath>
#include <stdexcept>
SwError e{sizeof(e)};
void need(bool yes,const char* name){printf("CHECK %s %s\n",yes?"PASS":"FAIL",name);if(!yes){printf("error=%d sony=%X message=%s\n",e.code,e.sony_code,e.message);throw std::runtime_error(name);}}
void ok(int rc,const char* name){need(rc==0,name);}
void connect(uint32_t mode){uint32_t count=0;ok(sw_enumerate(&count,&e),"enum");need(count==1,"one fake device");SwCameraInfo c{sizeof(c)};ok(sw_camera_info(0,&c,&e),"model");need(!strcmp(c.model,"SIMULATED"),"refuse real camera");ok(sw_connect_mode(0,mode,1000,&e),"connect mode");if(!mode)ok(sw_wait_ready(1000,&e),"ready");}
int wmain(int argc,wchar_t** argv){
 if(argc!=2)return 2;char root[1024]{};WideCharToMultiByte(CP_UTF8,0,argv[1],-1,root,1024,nullptr,nullptr);char scenario[100]{};GetEnvironmentVariableA("SW_FAKE_CASE",scenario,100);std::string sc=scenario;
 try {
  uint32_t v,ps,fs,ds,cs,ts;ok(sw_get_feature_abi(&v,&ps,&fs,&ds,&cs,&ts,&e),"feature ABI query");need(v==1&&ps==56&&fs==24&&ds==272&&cs==584&&ts==1240,"feature ABI sizes");
  ok(sw_initialize(&e),"init");connect(0);
  SwPropertyInfo p{sizeof(p)};uint32_t n=0;
  if(sc=="feature-unsupported") {need(sw_get_property(SW_PROP_METERINGMODE,&p,nullptr,0,&n,&e)==SW_UNSUPPORTED,"absent property explicit unsupported");ok(sw_check_capture_ready(&e),"unsupported query preserves session");}
  else if(sc=="feature-set-fail"||sc=="feature-set-timeout"){
   int64_t actual=99;need(sw_set_property(SW_PROP_FOCUSMODE,2,10,&actual,&e)==(sc=="feature-set-fail"?SW_SDK:SW_TIMEOUT),"set error/timeout");need(actual==0,"no stale actual");need(sw_check_capture_ready(&e)==SW_STATE,"unconfirmed write blocks capture");
  } else if(sc=="feature-focus-stop-fail") {need(sw_focus_pulse(1,1,&e)==SW_SDK,"failed stop fails pulse");need(sw_check_capture_ready(&e)==SW_STATE,"failed stop quarantines");}
  else if(sc=="feature-movie-fail") {need(sw_movie_set(1,10,&e)==SW_SDK,"failed movie start");ok(sw_movie_set(0,10,&e),"explicit movie stop cleanup");need(sw_check_capture_ready(&e)==SW_STATE,"movie failure remains uncertain");}
  else if(sc=="feature-no-lens") {
   SwParameters b{sizeof(b)};ok(sw_read_parameters(&b,&e),"lensless params");need(std::isnan(b.f_number),"unknown aperture NaN");
   SwCaptureResult r{sizeof(r)};ok(sw_capture_start(root,5000,&r,&e),"lensless capture start");int rc;do{rc=sw_capture_wait(r.capture_id,10,&r,&e);}while(rc==SW_PENDING);ok(rc,"lensless capture finish");need(std::isnan(r.actual_parameters.f_number),"lensless snapshot unknown aperture");
  } else if(sc=="feature-af-blocks") {
   // Hardware 2026-10-24: AF-C swallowed the 120 ms release and produced no file, warning or error.
   need(sw_check_capture_ready(&e)==SW_UNSUPPORTED,"autofocus blocks capture readiness");
   SwCaptureResult blocked{sizeof(blocked)};need(sw_capture_start(root,5000,&blocked,&e)==SW_UNSUPPORTED,"autofocus blocks capture start");
   need(blocked.arw_path[0]==0,"refused capture exposes no file path");
  } else if(sc=="feature-normal"||sc=="feature-live-error") {
   ok(sw_get_property(SW_PROP_APERTURE,&p,nullptr,0,&n,&e),"query aperture count");need(n==3&&p.current==280&&p.writable,"aperture capabilities");
   int64_t a[3]={99,99,99};need(sw_get_property(SW_PROP_APERTURE,&p,a,2,&n,&e)==SW_BUFFER&&a[0]==99,"short option buffer untouched");
   ok(sw_get_property(SW_PROP_APERTURE,&p,a,3,&n,&e),"query aperture list");int64_t actual=0;need(sw_set_property(SW_PROP_APERTURE,350,10,&actual,&e)==SW_UNSUPPORTED,"invalid aperture rejected");ok(sw_set_property(SW_PROP_APERTURE,400,100,&actual,&e),"aperture set and readback");need(actual==400,"actual aperture");
   ok(sw_get_property(SW_PROP_FOCUSMAGNIFIER,&p,nullptr,0,&n,&e),"magnifier status readable");need(!p.writable,"magnifier requires dedicated position protocol");
   need(sw_set_property(SW_PROP_FOCUSMAGNIFIER,0x00000028FFFFFFFFll,100,&actual,&e)==SW_UNSUPPORTED&&actual==0,"magnifier generic write rejected before submission");ok(sw_check_capture_ready(&e),"rejected magnifier preserves idle capture session");
   need(sw_set_property(SW_PROP_BATTERYPERCENT,2,100,&actual,&e)==SW_UNSUPPORTED,"readonly cannot write");
   ok(sw_set_property(SW_PROP_FOCUSPOSITION,12345,100,&actual,&e),"absolute focus range and actual position");
   need(sw_set_property(SW_PROP_FOCUSPOSITION,65536,10,&actual,&e)==SW_UNSUPPORTED,"out of focus range");
   ok(sw_focus_pulse(-1,1,&e),"signed relative focus with stop");need(sw_focus_pulse(5,1,&e)==SW_UNSUPPORTED,"speed outside lens range rejected");
   ok(sw_live_view_set(1,&e),"enable preview");SwFrameInfo frame{sizeof(frame)};ok(sw_live_view_read(nullptr,0,&frame,&e),"preview info");need(frame.bytes==1024,"preview required buffer");uint8_t tiny[4]={1,2,3,4};need(sw_live_view_read(tiny,4,&frame,&e)==SW_BUFFER&&tiny[0]==1,"short frame buffer untouched");
   uint8_t data[1024]{};const auto rc=sw_live_view_read(data,1024,&frame,&e);
   if(sc=="feature-live-error")need(rc==SW_SDK&&data[0]==0,"frame fetch error leaves buffer untouched");
   else {ok(rc,"JPEG preview copied");need(frame.bytes==4&&data[0]==255&&data[1]==216,"exact copied JPEG preview");}
   need(sw_check_capture_ready(&e)==SW_STATE,"stop preview before still capture");ok(sw_live_view_set(0,&e),"stop preview");need(sw_live_view_read(data,1024,&frame,&e)==SW_STATE,"never return stale preview after stop");
   ok(sw_movie_set(1,100,&e),"start movie readback");need(sw_set_iso(400,100,reinterpret_cast<uint32_t*>(&actual),&e)==SW_STATE,"movie blocks legacy setter");need(sw_check_capture_ready(&e)==SW_STATE,"movie blocks capture");ok(sw_movie_set(0,100,&e),"stop movie readback");ok(sw_check_capture_ready(&e),"idle after movie");
  }
  ok(sw_disconnect(1000,&e),"disconnect remote");connect(1);
  SwFolderInfo folder{};ok(sw_get_folders(nullptr,0,&n,&e),"folder count");need(n==1,"one date folder");ok(sw_get_folders(&folder,1,&n,&e),"folders");
  SwContentInfo content{};need(sw_get_contents(folder.session+1,folder.handle,&content,1,&n,&e)==SW_ASSOCIATION,"stale folder session rejected");ok(sw_get_contents(folder.session,folder.handle,&content,1,&n,&e),"contents");need(content.bytes==(sc=="feature-zero-size"?0:4096),"camera original size");
  SwTransferInfo t{sizeof(t)};int rc=sw_content_start(content.session,content.handle,root,sc=="feature-transfer-timeout"?1:1000,&t,&e);
  if(sc=="feature-zero-size")need(rc==SW_UNSUPPORTED&&!t.id,"no unverifiable zero-size transfer");
  else {
   ok(rc,"start original file");need(!t.path[0]&&t.state==1,"no premature original path");
   need(sw_content_start(content.session,content.handle,root,1000,&t,&e)==SW_STATE,"overlapping transfer blocked");
   // A rejected start clears only the caller's output; poll original ID 1.
   if(sc=="feature-transfer-pending")rc=sw_content_cancel(1,&t,&e);
   else {if(sc=="feature-transfer-timeout")std::this_thread::sleep_for(std::chrono::milliseconds(3));do{rc=sw_content_poll(1,&t,&e);}while(rc==SW_PENDING);}
   if(sc=="feature-transfer-pending")need(rc==SW_CANCELLED&&t.state==3&&!t.path[0],"cancel retains failed empty path");
   else if(sc=="feature-transfer-timeout")need(rc==SW_TIMEOUT&&t.state==3&&!t.path[0],"timeout retains failed empty path");
   else if(sc=="feature-wrong-size")need(rc==SW_IO&&t.state==3&&!t.path[0],"truncated download rejected");
   else if(sc=="feature-wrong-handle"||sc=="feature-transfer-error")need(rc==SW_ASSOCIATION&&t.state==3&&!t.path[0],"bad callback fails association");
   else {ok(rc,"original file completed");need(t.state==2&&t.verified_bytes==4096&&t.path[0],"callback plus exact size plus full read verified");}
  }
  ok(sw_disconnect(1000,&e),"disconnect contents");ok(sw_shutdown(&e),"shutdown");printf("FEATURE PASS %s\n",scenario);return 0;
 }catch(const std::exception& ex){printf("FEATURE FAIL %s: %s\n",scenario,ex.what());return 1;}
}
