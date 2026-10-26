#include "sony_features.h"
#include <Windows.h>
#include <filesystem>
#include <cstdio>
#include <stdexcept>
#include <cstring>
SwError e{sizeof(e)};
void check(bool value,const char* name){printf("%s %s\n",value?"PASS":"FAIL",name);if(!value)throw std::runtime_error(e.message);}
void enumerate(){uint32_t count=0;check(sw_enumerate(&count,&e)==0&&count==1,"enumeration usable");SwCameraInfo info{sizeof(info)};check(sw_camera_info(0,&info,&e)==0&&!strcmp(info.connection,"FAKE"),"isolated fake only");}
void connect(){enumerate();check(sw_connect(0,100,&e)==0,"same-process connect");check(sw_wait_ready(100,&e)==0,"ready");}
int main(int argc,char** argv){if(argc!=2)return 2;try {
 check(sw_initialize(&e)==0,"initialize");
 for(auto scenario:{"connect-timeout","connect-error-handle"}){
  SetEnvironmentVariableA("SW_FAKE_CASE",scenario);enumerate();check(sw_connect(0,40,&e)!=0,"partial connection rejected");
  check(sw_disconnect(100,&e)==0,"partial cleanup is idempotent");
  SetEnvironmentVariableA("SW_FAKE_CASE","feature-normal");connect();check(sw_disconnect(100,&e)==0,"disconnect after recovery");
 }
 connect();SetEnvironmentVariableA("SW_FAKE_CASE","release-fail");check(sw_disconnect(100,&e)!=0,"release failure reported");
 uint32_t count=0;check(sw_enumerate(&count,&e)!=0,"retained handle blocks enumeration");
 SetEnvironmentVariableA("SW_FAKE_CASE","feature-normal");check(sw_disconnect(100,&e)==0,"release retry succeeds");connect();
 uint32_t actual=0;check(sw_set_iso(800,100,&actual,&e)==0,"ISO 800 confirmed");
 SwExposureRequest request{sizeof(request),SW_EXPOSURE_ORDINARY,0.01,5,5,1000,0};double seconds=0;
 check(sw_set_exposure_seconds(0.01,100,&seconds,&e)==0,"shutter confirmed");
 SetEnvironmentVariableA("SW_FAKE_BODY_ISO","1600");SwCaptureResult result{sizeof(result)};
 check(sw_capture_start_verified(argv[1],"TEST",&request,800,0,&result,&e)==SW_UNSUPPORTED,"body ISO change rejected before trigger");
 check(!result.arw_path[0],"no stale image on rejection");
 check(sw_set_iso(800,100,&actual,&e)==0,"correct body-side ISO");
 SetEnvironmentVariableA("SW_FAKE_CASE","iso-race");result={sizeof(result)};
 check(sw_capture_start_verified(argv[1],"TEST",&request,800,0,&result,&e)==SW_UNSUPPORTED,"ISO race during routing rejected");
 SetEnvironmentVariableA("SW_FAKE_CASE","feature-normal");check(sw_set_iso(800,100,&actual,&e)==0,"ISO restored");
 SetEnvironmentVariableA("SW_FAKE_CROP","2");result={sizeof(result)};
 check(sw_capture_start_verified(argv[1],"TEST",&request,800,1,&result,&e)==SW_UNSUPPORTED,"strict full-frame rejects crop");
 SetEnvironmentVariableA("SW_FAKE_CROP","3");result={sizeof(result)};
 check(sw_capture_start_verified(argv[1],"TEST",&request,800,1,&result,&e)==SW_UNSUPPORTED,"strict full-frame rejects unconfirmed Auto");
 SetEnvironmentVariableA("SW_FAKE_CROP","2");result={sizeof(result)};
 check(sw_capture_start_verified(argv[1],"TEST",&request,800,0,&result,&e)==0,"default permits APS-C");
 int rc;do{rc=sw_capture_wait(result.capture_id,50,&result,&e);}while(rc==SW_PENDING);
 check(rc==0&&result.actual_valid&&result.actual_parameters.iso==800,"capture returns actual verified ISO");
 check(sw_disconnect(100,&e)==0,"disconnect");check(sw_shutdown(&e)==0,"shutdown");return 0;
 }catch(const std::exception& ex){printf("ERROR %s\n",ex.what());return 1;}}
