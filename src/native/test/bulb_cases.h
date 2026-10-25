using namespace std::chrono_literals;
// Included by the existing standalone wrapper client. No Sony headers or calls.
#include <cmath>
#include <limits>
static SwExposureRequest request_for(double seconds,double exposure_limit=0,double download_limit=120) {
 SwExposureRequest r{};r.size=sizeof(r);r.mode=SW_EXPOSURE_BULB;r.exposure_seconds=seconds;
 r.exposure_timeout_seconds=exposure_limit?exposure_limit:seconds+5;r.download_timeout_seconds=download_limit;r.setup_timeout_ms=15000;return r;
}
static SwExposureInfo exposure_info(uint64_t id) {
 SwExposureInfo info{sizeof(info)};okay("exposure info",sw_capture_exposure_info(id,&info,&error));
 std::printf("EXPOSURE id=%llu mode=%u requested=%.6f host_hold=%.6f camera_elapsed=%u valid=%u phase=%u reason=%u up_attempts=%u restore=%u release_sony=0x%X restore_sony=0x%X original_timer=%u timer_valid=%u\n",
  static_cast<unsigned long long>(id),info.mode,info.requested_seconds,info.software_hold_seconds,info.camera_elapsed_seconds,info.camera_elapsed_valid,
  info.phase,info.end_reason,info.release_attempts,info.restore_state,info.release_error.sony_code,info.restore_error.sony_code,info.original_timer_enabled,info.timer_original_valid);
 return info;
}
static SwCaptureResult begin_bulb(const char* root,double seconds,double limit=0,double download=120) {
 auto r=request_for(seconds,limit,download);SwCaptureResult out{sizeof(out)};
 const auto before=std::chrono::steady_clock::now();const auto rc=sw_capture_start_v2(root,"BULB",&r,&out,&error);
 if(out.capture_id)last_task=out.capture_id;
 okay("Bulb start",rc);need(out.capture_id&&out.state==SW_CAPTURE_WAITING&&!out.arw_path[0],"Bulb start returns task without premature file");
 std::printf("BULB_START_MS=%.3f REQUEST_SECONDS=%.3f\n",std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-before).count(),seconds);
 SwCaptureResult reused{sizeof(reused)};strcpy_s(reused.arw_path,"previous.ARW");
 need(sw_capture_start_v2(root,"BUSY",&r,&reused,&error)==SW_STATE&&error.capture_id==out.capture_id&&!reused.arw_path[0]&&!reused.capture_id,"duplicate submission rejected without stale result");
 return out;
}
static SwCaptureResult finish_bulb(uint64_t id) {
 SwCaptureResult result{sizeof(result)};int32_t rc;auto next=std::chrono::steady_clock::now();
 do {
  rc=sw_capture_wait(id,100,&result,&error);
  if(rc==SW_PENDING&&std::chrono::steady_clock::now()>=next){exposure_info(id);next=std::chrono::steady_clock::now()+5s;}
 }while(rc==SW_PENDING);
 okay("Bulb download/readability and restoration",rc);result=finish(id,0);auto info=exposure_info(id);
 need(info.end_reason==SW_END_NORMAL&&info.restore_state==SW_RESTORE_CONFIRMED,"normal Bulb end and original settings restored");
 need(result.actual_valid&&result.actual_parameters.shutter_kind==SW_SHUTTER_BULB&&std::isnan(result.actual_parameters.exposure_seconds),"Bulb request duration not mislabelled actual sensor time");
 std::printf("BULB_SAMPLE_ID=%llu\nBULB_SAMPLE_SECONDS=%.3f\nBULB_SAMPLE_PATH=%s\n",static_cast<unsigned long long>(id),info.requested_seconds,result.arw_path);
 return result;
}
static void recovered_bulb(uint64_t id) {
 need(reconcile(id,25),"exact failed download, S2/queue and restoration permit recovery");
 auto info=exposure_info(id);need(info.restore_state==SW_RESTORE_CONFIRMED,"failed task temporary settings restored");
 SwCaptureResult result{sizeof(result)};need(sw_capture_wait(id,0,&result,&error)!=SW_OK&&!result.arw_path[0]&&!result.file_bytes,"recovered task still failed with empty result path");
 okay("capture-ready after recovery",sw_check_capture_ready(&error));
}
static void invalid_exposure_tests(const char* root) {
 SwParameters before{sizeof(before)},after{sizeof(after)};okay("record parameters before invalid requests",sw_read_parameters(&before,&error));
 for(double seconds: {0.0,-1.0,std::numeric_limits<double>::quiet_NaN(),std::numeric_limits<double>::infinity(),1e300,900.001,1.0000001}) {
  auto r=request_for(seconds);if(!std::isfinite(r.exposure_timeout_seconds)||r.exposure_timeout_seconds>1800)r.exposure_timeout_seconds=10;
  SwCaptureResult out{sizeof(out)};strcpy_s(out.arw_path,"old.ARW");
  auto rc=sw_capture_start_v2(root,"INVALID",&r,&out,&error);log("invalid exposure rejected",rc);
  need(rc!=SW_OK&&rc!=SW_PENDING&&!out.arw_path[0]&&!out.capture_id&&error.capture_id==0,"invalid duration allocates no task and returns no previous file");
 }
 auto r=request_for(2);r.download_timeout_seconds=0;SwCaptureResult out{sizeof(out)};
 need(sw_capture_start_v2(root,"INVALID",&r,&out,&error)==SW_ARGUMENT&&!out.capture_id,"zero download timeout rejected");
 r=request_for(2);r.mode=99;need(sw_capture_start_v2(root,"INVALID",&r,&out,&error)==SW_ARGUMENT,"unknown mode rejected");
 r=request_for(2);r.size=1;need(sw_capture_start_v2(root,"INVALID",&r,&out,&error)==SW_ARGUMENT,"wrong request size rejected");
 okay("read after invalid requests",sw_read_parameters(&after,&error));
 need(before.iso==after.iso&&before.exposure_seconds==after.exposure_seconds&&before.shutter_kind==after.shutter_kind,"invalid requests change no camera parameters");
}
static void bulb_fault_case(const char* root,const char* scenario) {
 const std::string kind=scenario;
 auto task=begin_bulb(root,kind=="bulb-disconnect"?10:(kind=="bulb-early"?5:1),kind=="bulb-exposure-timeout"?0.05:15,kind=="bulb-download-timeout"?0.05:10);
 if(kind=="bulb-disconnect") {
  need(log("disconnect active Bulb",sw_disconnect(10000,&error))==SW_CANCELLED&&error.capture_id==task.capture_id,"disconnect reports interrupted Bulb");connected=false;
  auto info=exposure_info(task.capture_id);need(info.end_reason==SW_END_DISCONNECT&&info.release_attempts==1,"disconnect attempts Bulb Up before closing transport");
  need(sw_connect(0,5000,&error)==SW_STATE&&error.capture_id==task.capture_id,"cannot bypass Bulb uncertainty by reconnecting");return;
 }
 if(kind=="bulb-early") {
  std::this_thread::sleep_for(150ms);need(log("explicit early end",sw_capture_end_exposure(task.capture_id,&task,&error))==SW_ENDED_EARLY&&task.state==SW_CAPTURE_ENDED_EARLY_UNCONFIRMED&&!task.arw_path[0],"early end distinct from cancel and requested success");
  auto info=exposure_info(task.capture_id);need(info.end_reason==SW_END_EARLY&&info.release_attempts==1,"early end requests Up");rejected_next(root,task.capture_id);recovered_bulb(task.capture_id);return;
 }
 // No polling. The DLL timer must release/expire independently.
 std::this_thread::sleep_for(1400ms);int32_t rc;
 do{rc=sw_capture_wait(task.capture_id,100,&task,&error);}while(rc==SW_PENDING);
 log("fault task terminal",rc);need(!task.arw_path[0]&&!task.file_bytes,"fault never hands out old or partial file");
 auto info=exposure_info(task.capture_id);rejected_next(root,task.capture_id);
 if(kind=="bulb-exposure-timeout"||kind=="bulb-download-timeout") {
  need(rc==SW_TIMEOUT,"independent exposure/download timeout");
  need(info.end_reason==static_cast<uint32_t>(kind=="bulb-exposure-timeout"?SW_END_TIMEOUT:SW_END_NORMAL),"timeout stage separated from normal release");recovered_bulb(task.capture_id);
 }else if(kind=="bulb-restorefail") {
  need(rc==SW_SDK&&task.failure.stage==SW_EXPOSURE_RESTORE&&info.restore_state==SW_RESTORE_FAILED&&info.restore_error.sony_code==0x8402,"restore failure separately reported with raw Sony error");
  need(!reconcile(task.capture_id,2),"failed restoration cannot be silently reset");
 }else {
  need(rc==SW_SDK&&task.failure.stage==SW_CAPTURE_UP&&task.failure.sony_code==0x8402&&info.release_attempts==2&&info.release_error.sony_code==0x8402,"Up failure preserves raw code, id and bounded retry");
  if(kind=="bulb-upfail")recovered_bulb(task.capture_id);
  else need(!reconcile(task.capture_id,2),"two failed Bulb Up commands keep session quarantined");
 }
}
static void run_bulb_acceptance(const char* root,bool fake,const char* scenario) {
 uint32_t rs=0,cs=0,is=0;okay("v2 ABI sizes",sw_get_exposure_abi_sizes(&rs,&cs,&is,&error));need(rs==40&&cs==56&&is==464,"v2 request/capabilities/info layout");
 SwExposureCapabilities cap{sizeof(cap)};okay("query exposure capabilities",sw_get_exposure_capabilities(&cap,&error));
 need(cap.bulb_candidate_present&&cap.bulb_min_seconds==1&&cap.bulb_max_seconds==900&&cap.bulb_step_seconds==0.001,"actual Bulb candidate plus explicit software timing policy");
 invalid_exposure_tests(root);
 if(fake && std::strcmp(scenario,"bulb-timer")!=0 && std::strcmp(scenario,"bulb-late")!=0){bulb_fault_case(root,scenario);return;}
 SwParameters original{sizeof(original)};okay("record original exposure",sw_read_parameters(&original,&error));
 uint32_t saved=0,actual=0;okay("record original save destination",sw_get_save_destination(&saved,&error));
 auto restore=[&]() {
  uint32_t iso=0,store=0;okay("restore original ISO",sw_set_iso(original.iso,10000,&iso,&error));need(iso==original.iso,"ISO restored by readback");
  okay("restore original destination",sw_set_save_destination(saved,10000,&store,&error));need(store==saved,"save destination restored by readback");
  SwParameters final{sizeof(final)};okay("final parameters",sw_read_parameters(&final,&error));
  need(final.iso==original.iso&&final.shutter_kind==original.shutter_kind&&final.exposure_seconds==original.exposure_seconds&&final.f_number==original.f_number,"FINAL EXACT ORIGINAL CAMERA PARAMETERS");
  okay("final actual S2/queue check",sw_check_capture_ready(&error));
 };
 try {
  okay("temporary test ISO100",sw_set_iso(100,10000,&actual,&error));need(actual==100,"test ISO confirmed");
  okay("temporary test Computer-only",sw_set_save_destination(SW_SAVE_COMPUTER,10000,&actual,&error));need(actual==SW_SAVE_COMPUTER,"test save destination confirmed");
  auto first=begin(root,"ORDINARY");first=finish(first.capture_id);
  auto short_bulb=begin_bulb(root,fake?1:2);
  std::this_thread::sleep_for(fake?1600ms:3000ms); // deliberately no wrapper calls across release deadline
  auto info=exposure_info(short_bulb.capture_id);
  need(info.end_reason==SW_END_NORMAL&&std::isfinite(info.software_hold_seconds)&&info.software_hold_seconds<info.requested_seconds+0.75,"DLL released on schedule while caller did not poll");
  short_bulb=finish_bulb(short_bulb.capture_id);
  auto second=begin(root,"ORDINARY");second=finish(second.capture_id);
  need(first.capture_id!=second.capture_id&&std::strcmp(first.arw_path,second.arw_path)!=0,"same connection ordinary/Bulb/ordinary distinct tasks");
  auto long_bulb=begin_bulb(root,fake?2:60);long_bulb=finish_bulb(long_bulb.capture_id);
  need(long_bulb.save_destination==SW_SAVE_COMPUTER,"long Bulb delivered via Computer-only wrapper path");
  disconnect_clean();connect_camera(fake);
  okay("Computer-only after normal reconnect",sw_set_save_destination(SW_SAVE_COMPUTER,10000,&actual,&error));
  auto ordinary=request_for(original.exposure_seconds,5,120);ordinary.mode=SW_EXPOSURE_ORDINARY;
  SwCaptureResult third{sizeof(third)};okay("v2 ordinary after reconnect",sw_capture_start_v2(root,"RECONNECT",&ordinary,&third,&error));last_task=third.capture_id;third=finish(third.capture_id);need(third.capture_id!=long_bulb.capture_id,"normal same-process reconnect and ordinary capture");
  auto cancelled=begin_bulb(root,10);std::this_thread::sleep_for(1100ms);
  need(log("cancel active Bulb",sw_capture_cancel(cancelled.capture_id,&cancelled,&error))==SW_CANCELLED&&!cancelled.arw_path[0],"Bulb cancellation stops request and exposes no partial file");
  info=exposure_info(cancelled.capture_id);need(info.end_reason==SW_END_CANCEL&&info.release_attempts==1&&info.software_hold_seconds<5,"cancel actively submits Up instead of waiting ten seconds");
  rejected_next(root,cancelled.capture_id);recovered_bulb(cancelled.capture_id);
  auto timed=begin_bulb(root,10,1);std::this_thread::sleep_for(1800ms);
  need(log("Bulb exposure limit",sw_capture_wait(timed.capture_id,0,&timed,&error))==SW_TIMEOUT&&!timed.arw_path[0]&&error.capture_id==timed.capture_id,"independent exposure timeout has own id and no previous image");
  info=exposure_info(timed.capture_id);need(info.end_reason==SW_END_TIMEOUT&&info.release_attempts==1&&info.software_hold_seconds<3,"timeout timer requests Up without caller polling");
  rejected_next(root,timed.capture_id);recovered_bulb(timed.capture_id);
  restore();
 }catch(...) {
  if(last_task)reconcile(last_task,25);
  try{restore();}catch(...){std::printf("RESTORE_UNCONFIRMED originalISO=%u destination=%u; inspect camera, do not force reset\n",original.iso,saved);}
  throw;
 }
}
