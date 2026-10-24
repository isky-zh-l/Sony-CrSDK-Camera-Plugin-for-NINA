// Test double only. It models SDK sequencing/errors, not camera firmware or image quality.
namespace SCRSDK {
std::map<uint32_t,uint64_t> feature_properties;
uint32_t live_enabled=CrDeviceSetting_Enable;
uint32_t recording=CrMovie_Recording_State_Not_Recording;
static const uint8_t preview_jpeg[]={0xff,0xd8,0xff,0xd9};
CrImageInfo::CrImageInfo():width(640),height(480),bufferSize(1024){}
CrImageInfo::~CrImageInfo(){}
CrInt32u CrImageInfo::GetBufferSize()const{return bufferSize;}
CrInt32u CrImageInfo::GetWidth()const{return width;}
CrInt32u CrImageInfo::GetHeight()const{return height;}
CrImageDataBlock::CrImageDataBlock():frameNo(1),size(0),pData(nullptr),imageSize(sizeof(preview_jpeg)),timeCode(0){}
CrImageDataBlock::~CrImageDataBlock(){}
CrInt32u CrImageDataBlock::GetFrameNo()const{return frameNo;}
void CrImageDataBlock::SetSize(CrInt32u n){size=n;}
CrInt32u CrImageDataBlock::GetSize()const{return size;}
void CrImageDataBlock::SetData(CrInt8u* p){pData=p;}
CrInt32u CrImageDataBlock::GetImageSize()const{return imageSize;}
CrInt8u* CrImageDataBlock::GetImageData()const{return pData;}
CrInt32u CrImageDataBlock::GetTimeCode()const{return timeCode;}
extern "C" CrError GetLiveViewImageInfo(CrDeviceHandle,CrImageInfo*){return 0;}
extern "C" CrError GetLiveViewImage(CrDeviceHandle,CrImageDataBlock* p){
 if(scenario()=="feature-live-error")return CrError_Api_InvalidCalled;
 if(!live_enabled||p->GetSize()<sizeof(preview_jpeg))return CrError_Generic_InvalidParameter;
 std::memcpy(p->GetImageData(),preview_jpeg,sizeof(preview_jpeg));return 0;
}
extern "C" CrError GetDeviceSetting(CrDeviceHandle,CrInt32u key,CrInt32u* value){*value=key==Setting_Key_EnableLiveView?live_enabled:0;return 0;}
extern "C" CrError SetDeviceSetting(CrDeviceHandle,CrInt32u key,CrInt32u value){if(key==Setting_Key_EnableLiveView)live_enabled=value;return 0;}
CrControlCodeInfo::CrControlCodeInfo():code(CrControlCode_FocusOperation),reserved(0),valueType(CrDataType_Int8),valuesSize(3),values(new CrInt8u[3]{253,3,1}){}
CrControlCodeInfo::~CrControlCodeInfo(){delete[] values;}
CrControlCode CrControlCodeInfo::GetCode()const{return code;}
CrDataType CrControlCodeInfo::GetValueType()const{return valueType;}
CrInt32u CrControlCodeInfo::GetValueSize()const{return valuesSize;}
CrInt8u* CrControlCodeInfo::GetValues()const{return values;}
extern "C" CrError GetSelectControlCode(CrDeviceHandle,CrControlCode,CrControlCodeInfo** out){*out=new CrControlCodeInfo;return 0;}
extern "C" CrError ReleaseControlCodes(CrDeviceHandle,CrControlCodeInfo* p){delete p;return 0;}
extern "C" CrError ExecuteControlCodeValue(CrDeviceHandle,CrControlCode,CrInt64u value){
 if(scenario()=="feature-focus-stop-fail"&&value==0)return CrError_Api_InvalidCalled;
 if(value!=0)feature_properties[CrDeviceProperty_FocusPositionCurrentValue]+=value;return 0;
}
CrMtpFolderInfo::CrMtpFolderInfo():handle(10),folderNameSize(9),folderName(new CrChar[9]{L'2',L'0',L'2',L'6',L'1',L'0',L'2',L'4',0}){}
CrMtpFolderInfo::~CrMtpFolderInfo(){delete[] folderName;}
CrMtpContentsInfo::CrMtpContentsInfo():handle(20),parentFolderHandle(10),contentSize(4096),dateChar{},width(3840),height(2160),fileNameSize(10),fileName(new CrChar[10]{L'T',L'E',L'S',L'T',L'1',L'.',L'M',L'P',L'4',0}){}
CrMtpContentsInfo::~CrMtpContentsInfo(){delete[] fileName;}
extern "C" CrError GetDateFolderList(CrDeviceHandle,CrMtpFolderInfo** out,CrInt32u* n){*n=1;*out=new CrMtpFolderInfo[1];return 0;}
extern "C" CrError ReleaseDateFolderList(CrDeviceHandle,CrMtpFolderInfo* p){delete[] p;return 0;}
extern "C" CrError GetContentsHandleList(CrDeviceHandle,CrFolderHandle folder,CrContentHandle** out,CrInt32u* n){if(folder!=10)return CrError_Generic_InvalidParameter;*n=1;*out=new CrContentHandle[1]{20};return 0;}
extern "C" CrError ReleaseContentsHandleList(CrDeviceHandle,CrContentHandle* p){delete[] p;return 0;}
extern "C" CrError GetContentsDetailInfo(CrDeviceHandle,CrContentHandle handle,CrMtpContentsInfo* p){if(handle!=20)return CrError_Generic_InvalidParameter;p->handle=handle;if(scenario()=="feature-zero-size")p->contentSize=0;return 0;}
extern "C" CrError PullContentsFile(CrDeviceHandle h,CrContentHandle handle,CrPropertyStillImageTransSize type,CrChar* directory,CrChar* name){
 if(type!=CrPropertyStillImageTransSize_Original)return CrError_Generic_InvalidParameter;
 auto path=(std::filesystem::path(directory)/name).native();std::ofstream f(std::filesystem::path(path),std::ios::binary);f.seekp(scenario()=="feature-wrong-size"?2047:4095);f.put('X');f.close();
 if(scenario()=="feature-transfer-pending"||scenario()=="feature-transfer-timeout")return 0;
 if(scenario()=="feature-wrong-handle")handle++;
 auto& s=sessions.at(static_cast<size_t>(h)-1);
 if(scenario()=="feature-transfer-error")s.cb->OnNotifyContentsTransfer(CrError_Api_InvalidCalled,handle,path.data());
 else s.cb->OnNotifyContentsTransfer(CrNotify_ContentsTransfer_Complete,handle,path.data());
 return 0;
}
}
