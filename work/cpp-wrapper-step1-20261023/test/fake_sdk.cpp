// Local deterministic SDK test double. No camera I/O. Official headers remain external build inputs.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include "CameraRemote_SDK.h"
#include "IDeviceCallback.h"
#include "CrImageDataBlock.h"
#include "CrControlCode.h"
#include <map>
#include <vector>
#include <string>
#include <fstream>
#include <filesystem>
#include <cstring>
#include <cstdlib>
#include <chrono>
namespace SCRSDK {
extern std::map<uint32_t,uint64_t> feature_properties;
extern uint32_t live_enabled,recording;
extern "C" CrError Set2byte_CharBuffer(CrInt8u**,const char*) {return CrError_Api_InvalidCalled;}
CrDeviceProperty::CrDeviceProperty():code(0),valueType(CrDataType_UInt32Array),enableFlag{},variableFlag{},currentValue(0),currentStr(nullptr),valuesSize(0),values(nullptr),getSetValuesSize(0),getSetValues(nullptr) {}
CrDeviceProperty::~CrDeviceProperty() { delete[] values; delete[] getSetValues; delete[] currentStr; }
CrDeviceProperty::CrDeviceProperty(const CrDeviceProperty& p):CrDeviceProperty() { *this=p; }
CrDeviceProperty& CrDeviceProperty::operator=(const CrDeviceProperty& p) {
    if(this!=&p) { code=p.code; valueType=p.valueType; currentValue=p.currentValue; enableFlag=p.enableFlag; variableFlag=p.variableFlag;
        SetValueSize(p.valuesSize); SetValues(p.values); }
    return *this;
}
std::string scenario();
bool CrDeviceProperty::IsGetEnableCurrentValue()const{
 return !((scenario()=="timer-unreadable"&&code==CrDeviceProperty_BulbTimerSetting)||
          (scenario()=="optional-unreadable"&&code==CrDeviceProperty_RemoteSaveImageSize));
}
bool CrDeviceProperty::IsSetEnableCurrentValue()const{
 if(scenario()=="astro-readonly"&&code==CrDeviceProperty_ImageStabilizationSteadyShot)return false;
 return !(code==CrDeviceProperty_BulbTimerSetting && (scenario()=="timer-readonly-off"||scenario()=="timer-readonly-on"));
}
void CrDeviceProperty::SetCode(CrInt32u x){code=x;} CrInt32u CrDeviceProperty::GetCode()const{return code;}
void CrDeviceProperty::SetValueType(CrDataType x){valueType=x;} CrDataType CrDeviceProperty::GetValueType()const{return valueType;}
void CrDeviceProperty::SetCurrentValue(CrInt64u x){currentValue=x;} CrInt64u CrDeviceProperty::GetCurrentValue()const{return currentValue;}
void CrDeviceProperty::SetValueSize(CrInt32u x){valuesSize=x;} CrInt32u CrDeviceProperty::GetValueSize()const{return valuesSize;}
void CrDeviceProperty::SetValues(CrInt8u* p){delete[] values;values=nullptr;if(p&&valuesSize){values=new CrInt8u[valuesSize];std::memcpy(values,p,valuesSize);}}
CrInt8u* CrDeviceProperty::GetValues()const{return values;}
void CrDeviceProperty::SetPropertyEnableFlag(CrPropertyEnableFlag x){enableFlag=x;} CrPropertyEnableFlag CrDeviceProperty::GetPropertyEnableFlag()const{return enableFlag;}
void CrDeviceProperty::SetPropertyVariableFlag(CrPropertyVariableFlag x){variableFlag=x;} CrPropertyVariableFlag CrDeviceProperty::GetPropertyVariableFlag()const{return variableFlag;}
void CrDeviceProperty::Alloc(CrInt32u,CrInt32u,CrInt16u){}
void CrDeviceProperty::SetCurrentStr(CrInt16u*){} CrInt16u* CrDeviceProperty::GetCurrentStr()const{return nullptr;}
void CrDeviceProperty::SetSetValueSize(CrInt32u x){getSetValuesSize=x;} CrInt32u CrDeviceProperty::GetSetValueSize()const{return getSetValuesSize;}
void CrDeviceProperty::SetSetValues(CrInt8u*){} CrInt8u* CrDeviceProperty::GetSetValues()const{return nullptr;}
CrInt32u CrDeviceProperty::GetDisplayValueSize()const{return 0;} CrInt8u* CrDeviceProperty::GetDisplayValues()const{return nullptr;}
class Camera final:public ICrCameraObjectInfo {
public:
 void Release()override{}
 CrChar* GetName()const override{return const_cast<CrChar*>(L"TEST DOUBLE");} CrInt32u GetNameSize()const override{return 12;}
 // Explicit test-only identity for exercising the real NINA adapter's model gate.
 // Serial and connection identity remain simulated; never used in a production SDK package.
 CrChar* GetModel()const override{
  static wchar_t model[128]{};
  if(GetEnvironmentVariableW(L"SW_FAKE_MODEL",model,128))return model;
  char flag[2]{};GetEnvironmentVariableA("SW_FAKE_MODEL_A7V",flag,sizeof(flag));
  return const_cast<CrChar*>(flag[0]=='1'?L"ILCE-7M5":L"SIMULATED");
 }
 CrInt32u GetModelSize()const override{return static_cast<CrInt32u>(wcslen(GetModel())+1);}
 CrInt16 GetUsbPid()const override{return 0;} CrInt8u* GetId()const override{return nullptr;} CrInt32u GetIdSize()const override{return 0;} CrInt32u GetIdType()const override{return 0;}
 CrInt32u GetConnectionStatus()const override{return 0;} CrChar* GetConnectionTypeName()const override{return const_cast<CrChar*>(L"FAKE");}
 CrChar* GetAdaptorName()const override{return const_cast<CrChar*>(L"NO HARDWARE");} CrChar* GetGuid()const override{return const_cast<CrChar*>(L"test");}
 CrChar* GetPairingNecessity()const override{return nullptr;} CrInt16u GetAuthenticationState()const override{return 0;} CrInt32u GetSSHsupport()const override{return 0;}
 CrInt32u GetIPAddress()const override{return 0;} CrChar* GetIPAddressChar()const override{return nullptr;} CrInt32u GetIPAddressCharSize()const override{return 0;}
 CrInt8u* GetMACAddress()const override{return nullptr;} CrInt32u GetMACAddressSize()const override{return 0;} CrChar* GetMACAddressChar()const override{return nullptr;} CrInt32u GetMACAddressCharSize()const override{return 0;}
} camera;
class Enumeration final:public ICrEnumCameraObjectInfo { public: CrInt32u GetCount()const override{return 1;} const ICrCameraObjectInfo* GetCameraObjectInfo(CrInt32u)const override{return &camera;} void Release()override{delete this;} };
struct Session { IDeviceCallback* cb; std::wstring directory,prefix,last_file; unsigned up=0; bool held=false; std::chrono::steady_clock::time_point down_at{}; };
std::vector<Session> sessions;
uint64_t iso=800,shutter=0x10064,destination=3,bulb_timer=1;
unsigned queued_remaining=0;
std::string scenario(){char value[64]{};GetEnvironmentVariableA("SW_FAKE_CASE",value,sizeof(value));return value[0]?value:"late";}
extern "C" bool Init(CrInt32u){return true;}
extern "C" bool Release(){return true;}
extern "C" CrError EnumCameraObjects(ICrEnumCameraObjectInfo** p,CrInt8u){*p=new Enumeration;return 0;}
extern "C" CrError Connect(ICrCameraObjectInfo*,IDeviceCallback* cb,CrDeviceHandle* handle,CrSdkControlMode,CrReconnectingSet,const char*,const char*,const char*,CrInt32u,const CrInt16u*) {
 if(!sessions.empty()&&!sessions.back().last_file.empty()) sessions.back().cb->OnCompleteDownload(sessions.back().last_file.data(),0xFFFFFFFF); // old connection callback object
 feature_properties.clear();queued_remaining=0; sessions.push_back({cb});*handle=static_cast<CrDeviceHandle>(sessions.size());cb->OnConnected(static_cast<DeviceConnectionVersioin>(0));return 0;
}
extern "C" CrError Disconnect(CrDeviceHandle h){sessions.at(static_cast<size_t>(h)-1).cb->OnDisconnected(0);return 0;}
extern "C" CrError ReleaseDevice(CrDeviceHandle){return 0;}
extern "C" CrError GetSelectDeviceProperties(CrDeviceHandle h,CrInt32u count,CrInt32u* codes,CrDeviceProperty** out,CrInt32* n) {
 *out=nullptr;*n=0;
 for(CrInt32u i=0;i<count;++i) {
  const auto code=codes[i];const auto mode=scenario();
  if(mode=="legacy" && (code==CrDeviceProperty_BulbTimerSetting || code==CrDeviceProperty_RAW_FileCompressionType ||
     code==CrDeviceProperty_Still_Image_Trans_Size || code==CrDeviceProperty_RemoteSaveImageSize || code==CrDeviceProperty_RecordingState || code==CrDeviceProperty_LiveViewStatus))return CrError_Generic_NotSupported;
  if(mode=="legacy-absent" && code==CrDeviceProperty_BulbTimerSetting)return 0;
  if(mode=="timer-error" && code==CrDeviceProperty_BulbTimerSetting)return CrError_Api_InvalidCalled;
  if(mode=="required-missing" && code==CrDeviceProperty_FileType)return CrError_Generic_NotSupported;
  if(mode=="optional-error" && code==CrDeviceProperty_RemoteSaveImageSize)return CrError_Api_InvalidCalled;
  if(mode=="optional-absent" && code==CrDeviceProperty_RemoteSaveImageSize)return 0;
  if(mode=="astro-query-error" && code==CrDeviceProperty_HighIsoNR)return CrError_Api_InvalidCalled;
  if(mode=="astro-unsupported" && code==CrDeviceProperty_ImageStabilizationSteadyShot)return CrError_Generic_NotSupported;
  if(mode=="feature-no-lens" && (code==CrDeviceProperty_FocusPositionSetting || code==CrDeviceProperty_FocusPositionCurrentValue || code==CrDeviceProperty_FocusDrivingStatus))return CrError_Generic_NotSupported;
  if(mode=="battery-absent" && code==CrDeviceProperty_BatteryRemain)return CrError_Generic_NotSupported;
  if(code==CrDeviceProperty_BulbTimerSetting && shutter!=0)return CrError_Api_InvalidCalled;
 }
 *out=new CrDeviceProperty[count];*n=static_cast<CrInt32>(count);
 for(CrInt32u i=0;i<count;++i) {
  auto& p=(*out)[i];p.SetCode(codes[i]);p.SetValueType(CrDataType_UInt32Array);uint64_t v=0;std::vector<uint32_t> options;
  if(scenario()=="feature-unsupported"&&codes[i]==CrDeviceProperty_MeteringMode){delete[] *out;*out=nullptr;*n=0;return CrError_Generic_NotSupported;}
  if(codes[i]==CrDeviceProperty_Focus_Magnifier_Setting){
   uint64_t choices[]={0x00000000FFFFFFFFull,0x0000000AFFFFFFFFull,0x00000028FFFFFFFFull,0x00000050FFFFFFFFull};
   p.SetValueType(CrDataType_UInt64Array);p.SetCurrentValue(feature_properties.count(codes[i])?feature_properties[codes[i]]:0xFFFFFFFFull);
   p.SetValueSize(sizeof(choices));p.SetValues(reinterpret_cast<CrInt8u*>(choices));continue;
  }
  switch(codes[i]) {
  case CrDeviceProperty_IsoSensitivity:v=iso;options={0x10000032,100,400,800,0xFFFFFF};break;
  case CrDeviceProperty_ShutterSpeed:v=shutter;options=scenario()=="no-bulb"?std::vector<uint32_t>{0x10064,0x1003C,0x3C0001}:std::vector<uint32_t>{0,0x10064,0x1003C,0x3C0001};break;
  case CrDeviceProperty_FNumber:v=scenario()=="feature-no-lens"?0:(feature_properties.count(codes[i])?feature_properties[codes[i]]:280);options={280,400,560};break;
  case CrDeviceProperty_LiveViewStatus:v=CrLiveView_Enable;break;
  case CrDeviceProperty_RecordingState:v=recording;break;
  case CrDeviceProperty_BatteryRemain:v=scenario()=="battery-unknown"?255:78;break;
  case CrDeviceProperty_FocusDrivingStatus:v=scenario()=="focus-busy"?2:1;break;
  case CrDeviceProperty_LongExposureNR:v=feature_properties.count(codes[i])?feature_properties[codes[i]]:2;options={1,2};break;
  case CrDeviceProperty_HighIsoNR:v=feature_properties.count(codes[i])?feature_properties[codes[i]]:3;options={1,2,3,4};break;
  case CrDeviceProperty_ImageStabilizationSteadyShot:v=feature_properties.count(codes[i])?feature_properties[codes[i]]:2;options={1,2};break;
  case CrDeviceProperty_ContentsTransferStatus:v=CrContentsTransfer_ON;break;
  case CrDeviceProperty_FocusPositionSetting:v=feature_properties[CrDeviceProperty_FocusPositionCurrentValue];options={0,65535,1};p.SetValueType(CrDataType_UInt32Range);break;
  case CrDeviceProperty_FocusMode:v=feature_properties.count(codes[i])?feature_properties[codes[i]]:(scenario()=="feature-af-blocks"?CrFocus_AF_C:CrFocus_MF);options={CrFocus_MF,CrFocus_AF_S,CrFocus_AF_C};break;
  case CrDeviceProperty_ExposureProgramMode:v=scenario()=="feature-not-manual"?2:1;break;
  case CrDeviceProperty_FileType:v=scenario()=="video-only"?1:2;break;
  case CrDeviceProperty_DriveMode:v=1;break;
  case CrDeviceProperty_S2:v=sessions.at(static_cast<size_t>(h)-1).held?CrLockIndicator_Locked:CrLockIndicator_Unlocked;break;
  case CrDeviceProperty_BulbTimerSetting:v=scenario()=="timer-readonly-off"?0:bulb_timer;options={0,1};break;
  case CrDeviceProperty_ElapsedBulbExposureTime:v=static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::seconds>(std::chrono::steady_clock::now()-sessions.at(static_cast<size_t>(h)-1).down_at).count());break;
  case CrDeviceProperty_RemoteSaveImageSize:v=scenario()=="optional-wrong"?0:1;break;
  case CrDeviceProperty_RAW_FileCompressionType:{
   char token[16]{};GetEnvironmentVariableA("SW_FAKE_RAW_FORMAT",token,sizeof(token));
   v=token[0]?static_cast<uint64_t>(std::strtoul(token,nullptr,10)):(feature_properties.count(codes[i])?feature_properties[codes[i]]:1);options=scenario()=="astro-lossless-large"?std::vector<uint32_t>{1,3,4,5,6}:std::vector<uint32_t>{1,2,6};break;
  }
  case CrDeviceProperty_StillImageStoreDestination:v=destination;options={1,3,2};break;
  default:v=feature_properties[codes[i]];options={0,1,2};break;
  }
  p.SetCurrentValue(v);p.SetValueSize(static_cast<CrInt32u>(options.size()*4));p.SetValues(reinterpret_cast<CrInt8u*>(options.data()));
 }
 return 0;
}
extern "C" CrError ReleaseDeviceProperties(CrDeviceHandle,CrDeviceProperty* p){delete[] p;return 0;}
extern "C" CrError SetDeviceProperty(CrDeviceHandle,CrDeviceProperty* p){
 if(scenario()=="feature-set-fail"||(scenario()=="astro-partial"&&p->GetCode()==CrDeviceProperty_HighIsoNR))return CrError_Api_InvalidCalled;
 if(scenario()!="feature-set-timeout"){feature_properties[p->GetCode()]=(p->GetCode()==CrDeviceProperty_Focus_Magnifier_Setting&&static_cast<uint32_t>(p->GetCurrentValue())==0xFFFFFFFFu)?((p->GetCurrentValue()&0xFFFFFFFF00000000ull)|0x00960064ull):p->GetCurrentValue();if(p->GetCode()==CrDeviceProperty_FocusPositionSetting)feature_properties[CrDeviceProperty_FocusPositionCurrentValue]=p->GetCurrentValue();}
if(scenario()=="queue")queued_remaining=2;
 if(scenario()=="bulb-restorefail" && p->GetCode()==CrDeviceProperty_ShutterSpeed && p->GetCurrentValue()!=0)return CrError_Api_InvalidCalled;switch(p->GetCode()){case CrDeviceProperty_IsoSensitivity:iso=p->GetCurrentValue();break;case CrDeviceProperty_ShutterSpeed:shutter=p->GetCurrentValue();break;case CrDeviceProperty_BulbTimerSetting:bulb_timer=p->GetCurrentValue();break;case CrDeviceProperty_StillImageStoreDestination:destination=p->GetCurrentValue();break;}return 0;}
extern "C" CrError SetSaveInfo(CrDeviceHandle h,CrChar* dir,CrChar* prefix,CrInt32){auto& s=sessions.at(static_cast<size_t>(h)-1);s.directory=dir;s.prefix=prefix;return 0;}
extern "C" CrError SendCommand(CrDeviceHandle h,CrInt32u command,CrCommandParam param) {
 if(command==CrCommandId_MovieRecord){if(scenario()=="feature-movie-fail")return CrError_Api_InvalidCalled;recording=param==CrCommandParam_Down?1:0;return 0;}
 if(command==CrCommandId_CancelContentsTransfer)return 0;
 auto& s=sessions.at(static_cast<size_t>(h)-1);
 if(param==CrCommandParam_Down){s.up=0;s.held=true;s.down_at=std::chrono::steady_clock::now();return 0;}
 ++s.up;
 if(((scenario()=="upfail"||scenario()=="bulb-upfail")&&s.up==1)||scenario()=="upfail_both"||scenario()=="bulb-upfail-both")return CrError_Api_InvalidCalled;
 s.held=false;
 auto previous=s.last_file;
 s.last_file=(std::filesystem::path(s.directory)/(s.prefix+L"00001.ARW")).native();
 wchar_t fixture[1024]{};
 if(GetEnvironmentVariableW(L"SW_FAKE_RAW_FIXTURE",fixture,1024)) {
  std::filesystem::copy_file(std::filesystem::path(fixture),std::filesystem::path(s.last_file),std::filesystem::copy_options::overwrite_existing);
 } else {
 std::ofstream file(std::filesystem::path(s.last_file),std::ios::binary);
 const char header[8]={'I','I',42,0,8,0,0,0};file.write(header,8);
 const size_t length=scenario()=="chunks"?16*1024*1024:4096;
 file.seekp(static_cast<std::streamoff>(length-1));file.put('X');file.close();
 }
 if(scenario()=="unknown") { wchar_t unrelated[]=L"C:\\unrelated\\old.ARW";s.cb->OnCompleteDownload(unrelated,0xFFFFFFFF); }
 if((scenario()=="late"||scenario()=="bulb-late")&&!previous.empty()) {s.cb->OnCompleteDownload(previous.data(),0xFFFFFFFF);s.cb->OnCompleteDownload(previous.data(),0xFFFFFFFF);}
 if(scenario()=="preview") {auto jpg=(std::filesystem::path(s.directory)/L"PREVIEW.JPG").native();s.cb->OnCompleteDownload(jpg.data(),0xFFFFFFFF);}
 const unsigned repeats=scenario()=="overflow"?80:2;
 for(unsigned i=0;i<repeats;++i)s.cb->OnCompleteDownload(s.last_file.data(),0xFFFFFFFF);
 return 0;
}
}



namespace SCRSDK { extern "C" CrError GetCommandQueueCount(CrDeviceHandle,CrInt32u* count){*count=queued_remaining?1:0;if(queued_remaining)--queued_remaining;return 0;} }

#include "fake_features.inc"
