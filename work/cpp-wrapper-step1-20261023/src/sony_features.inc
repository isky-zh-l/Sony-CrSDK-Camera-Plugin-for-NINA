// Compiled inside sony_wrapper.cpp; shares the single serialized SDK owner and exception boundary.
namespace {
struct PropertySpec { uint32_t code; bool write; };
PropertySpec property_spec(uint32_t id) {
    switch(id) {
        case 1: return {sdk::CrDeviceProperty_FNumber,true};
        case 2: return {sdk::CrDeviceProperty_FocusMode,true};
        case 3: return {sdk::CrDeviceProperty_ExposureProgramMode,true};
        case 4: return {sdk::CrDeviceProperty_DriveMode,true};
        case 5: return {sdk::CrDeviceProperty_FileType,true};
        case 6: return {sdk::CrDeviceProperty_RAW_FileCompressionType,true};
        case 7: return {sdk::CrDeviceProperty_ImageSize,true};
        case 8: return {sdk::CrDeviceProperty_AspectRatio,true};
        case 9: return {sdk::CrDeviceProperty_LongExposureNR,true};
        case 10: return {sdk::CrDeviceProperty_HighIsoNR,true};
        case 11: return {sdk::CrDeviceProperty_SilentMode,true};
        case 12: return {sdk::CrDeviceProperty_ShutterType,true};
        case 13: return {sdk::CrDeviceProperty_LiveView_Image_Quality,true};
        case 14: return {sdk::CrDeviceProperty_LiveViewDisplayEffect,true};
        case 15: return {sdk::CrDeviceProperty_LiveViewStatus,false};
        case 16: return {sdk::CrDeviceProperty_BatteryRemain,false};
        case 17: return {sdk::CrDeviceProperty_BatteryLevel,false};
        case 18: return {sdk::CrDeviceProperty_RecordingState,false};
        case 19: return {sdk::CrDeviceProperty_MediaSLOT1_Status,false};
        case 20: return {sdk::CrDeviceProperty_MediaSLOT2_Status,false};
        case 21: return {sdk::CrDeviceProperty_MediaSLOT1_RemainingNumber,false};
        case 22: return {sdk::CrDeviceProperty_MediaSLOT2_RemainingNumber,false};
        case 23: return {sdk::CrDeviceProperty_MediaSLOT1_RemainingTime,false};
        case 24: return {sdk::CrDeviceProperty_MediaSLOT2_RemainingTime,false};
        case 25: return {sdk::CrDeviceProperty_FocusPositionSetting,true};
        case 26: return {sdk::CrDeviceProperty_FocusPositionCurrentValue,false};
        case 27: return {sdk::CrDeviceProperty_Focus_Magnifier_Setting,true};
        case 28: return {sdk::CrDeviceProperty_FocusMagnificationTime,true};
        case 29: return {sdk::CrDeviceProperty_InitialFocusMagnifier,true};
        case 30: return {sdk::CrDeviceProperty_Movie_File_Format,true};
        case 31: return {sdk::CrDeviceProperty_Movie_Recording_Setting,true};
        case 32: return {sdk::CrDeviceProperty_Movie_Recording_FrameRateSetting,true};
        case 33: return {sdk::CrDeviceProperty_WhiteBalance,true};
        case 34: return {sdk::CrDeviceProperty_Colortemp,true};
        case 35: return {sdk::CrDeviceProperty_ExposureBiasCompensation,true};
        case 36: return {sdk::CrDeviceProperty_MeteringMode,true};
        case 37: return {sdk::CrDeviceProperty_FocusArea,true};
        case 38: return {sdk::CrDeviceProperty_ImageStabilizationSteadyShot,true};
        case 39: return {sdk::CrDeviceProperty_AutoPowerOffTemperature,true};
        case 40: return {sdk::CrDeviceProperty_ContentsTransferStatus,false};
        case 41: return {sdk::CrDeviceProperty_ContentsTransferProgress,false};
        case 42: return {sdk::CrDeviceProperty_RemoteSaveImageSize,true};
        case 43: return {sdk::CrDeviceProperty_FocusIndication,false};
        case 44: return {sdk::CrDeviceProperty_APS_C_S35,true};
        case 45: return {sdk::CrDeviceProperty_Movie_RecordingMedia,true};

    default:return {0,false};
    }
}
struct NumericValues {
    bool range=false; std::vector<int64_t> choices;
    int64_t min=0,max=0,step=0;
    bool accepts(int64_t v) const {
        if(!range)return std::find(choices.begin(),choices.end(),v)!=choices.end();
        return step>0 && v>=min && v<=max && (uint64_t(v)-uint64_t(min))%uint64_t(step)==0;
    }
};
bool numeric_value(uint64_t raw,uint32_t type,int64_t& value) {
    const uint32_t bits=type&0xFF; if(bits<1||bits>4)return false;
    const size_t width=size_t(1)<<(bits-1);
    const bool sign=(type&sdk::CrDataType_SignBit)!=0;
    if(sign && width<8 && (raw&(uint64_t(1)<<(width*8-1))))raw|=(~uint64_t(0))<<(width*8);
    if(!sign && raw>uint64_t(INT64_MAX))return false;
    std::memcpy(&value,&raw,8);return true;
}
int32_t decode_choices(const sdk::CrDeviceProperty& p,NumericValues& v,SwError* e) {
    const auto type=uint32_t(p.GetValueType()); const uint32_t bits=type&0xFF;
    if(bits<1||bits>4 || (type&sdk::CrDataType_CustomBit))return result(e,SW_UNSUPPORTED,SW_SUPPORT,0,"Property is not a supported integer option/range");
    const size_t width=size_t(1)<<(bits-1); auto bytes=p.GetValueSize();auto data=p.GetValues();
    if(bytes>1024*1024 || bytes%width || (bytes&&!data))return result(e,SW_INTERNAL,SW_SUPPORT,0,"Malformed numeric candidate buffer");
    // SDK provides range as [minimum, maximum, step], not a large enumerated list.
    v.range=(type&sdk::CrDataType_RangeBit)!=0;
    if(v.range && bytes!=3*width)return result(e,SW_UNSUPPORTED,SW_SUPPORT,0,"Range must contain minimum/maximum/step");
    if(!(type&(sdk::CrDataType_ArrayBit|sdk::CrDataType_RangeBit)))return SW_OK;
    for(size_t offset=0;offset<bytes;offset+=width) { uint64_t raw=0;int64_t n=0;std::memcpy(&raw,data+offset,width);
        if(!numeric_value(raw,type,n))return result(e,SW_UNSUPPORTED,SW_SUPPORT,0,"Unsigned value exceeds signed option contract");v.choices.push_back(n); }
    if(v.range) {v.min=v.choices[0];v.max=v.choices[1];v.step=v.choices[2];v.choices.clear();if(v.min>v.max||v.step<=0)return result(e,SW_UNSUPPORTED,SW_SUPPORT,0,"Invalid range ordering/step");}
    return SW_OK;
}
int32_t feature_write_ready(SwError* e,bool allow_preview=true) {
    auto rc=ready_check(e,SW_SET);if(rc)return rc;
    if(connection_mode!=SW_MODE_REMOTE || capture_is_waiting() || unsafe_capture_id || movie_owned || transfer_active || feature_uncertain || (!allow_preview&&preview_active))
        return result(e,SW_STATE,SW_SET,0,"Feature mutation requires idle remote session; finish exposure/movie/transfer or disconnect uncertain session");
    if(cb->feature_warning.load())return result(e,SW_SDK,SW_SET,cb->feature_warning.load(),"Prior asynchronous feature command failed; disconnect required");
    return SW_OK;
}
int32_t feature_idle(SwError* e) {
    if(cb&&cb->feature_warning.load())return result(e,SW_SDK,SW_CAPTURE_CHECK,cb->feature_warning.load(),"Asynchronous feature operation failed; disconnect required");
    if(connection_mode!=SW_MODE_REMOTE||preview_active||movie_owned||transfer_active||feature_uncertain)
        return result(e,SW_STATE,SW_CAPTURE_CHECK,0,"Still capture requires remote mode and stopped preview/movie/transfer");
    return SW_OK;
}
int32_t read_numeric(uint32_t code,uint64_t& value,SwError* e) {
    PropertyList p;auto rc=p.load(code,e,SW_READ);if(rc)return rc;value=p.list[0].GetCurrentValue();return p.close(e,SW_READ);
}
// Movie Stop is also a cleanup action: allowed even after a failed Start/readback.
int32_t movie_set_internal(bool enabled,uint32_t ms,SwError* e) {
    if(!valid_timeout(ms))return result(e,SW_ARGUMENT,SW_SET,0,"Movie timeout 1..60000 ms required");
    if(enabled){auto rc=feature_write_ready(e);if(rc)return rc;}
    else if(!initialized||!device||!cb||cb->disconnected.load())return result(e,SW_STATE,SW_SET,0,"Movie stop requires a connected device");
    if(connection_mode!=SW_MODE_REMOTE || transfer_active || capture_is_waiting())return result(e,SW_STATE,SW_SET,0,"Movie command unavailable in current activity");
    uint64_t current=0;auto rc=read_numeric(sdk::CrDeviceProperty_RecordingState,current,e);if(rc)return rc;
    const uint64_t wanted=enabled?sdk::CrMovie_Recording_State_Recording:sdk::CrMovie_Recording_State_Not_Recording;
    if(current==wanted) {if(enabled&&!movie_owned)return result(e,SW_STATE,SW_SET,0,"Camera already recording outside this wrapper; query state/explicit Stop");if(!enabled)movie_owned=false;return SW_OK;}
    if(enabled && current!=sdk::CrMovie_Recording_State_Not_Recording)
        return result(e,SW_STATE,SW_SET,0,"Recording status failed/interval/unknown; no blind toggle");
    // Do not use Toggle: A7 V supports the explicit MovieRecord Down/Up pair.
    if(enabled)movie_owned=true; // retain ownership even if SDK accepts then reports an error
    const auto sdk_rc=sdk::SendCommand(device,sdk::CrCommandId_MovieRecord,enabled?sdk::CrCommandParam_Down:sdk::CrCommandParam_Up);
    if(sdk_rc){feature_uncertain=true;return result(e,SW_SDK,SW_SET,sdk_rc,"Movie command failed; stop/reconnect required");}
    const auto end=Clock::now()+std::chrono::milliseconds(ms);
    do {
        rc=connected_check(e,SW_VERIFY);if(rc){feature_uncertain=true;return rc;}
        rc=read_numeric(sdk::CrDeviceProperty_RecordingState,current,e);
        if(rc){feature_uncertain=true;return rc;}
        if(cb->feature_warning.load()){feature_uncertain=true;return result(e,SW_SDK,SW_VERIFY,cb->feature_warning.load(),"Asynchronous movie operation failed");}
        if(current==wanted){if(!enabled)movie_owned=false;return result(e,SW_OK,SW_VERIFY,0,"Recording state confirmed; movie finalization/download are separate");}
        std::this_thread::sleep_for(20ms);
    }while(Clock::now()<end);
    feature_uncertain=true;return result(e,SW_TIMEOUT,SW_VERIFY,0,"Movie state readback timed out; Stop remains available");
}
struct Transfer {
    SwTransferInfo info{}; Callback* session=nullptr; uint32_t handle=0; fs::path directory,file;
    Clock::time_point deadline{}; bool completed=false; HANDLE read=INVALID_HANDLE_VALUE;
} transfer;
uint64_t next_transfer=0;
void close_transfer_file(){if(transfer.read!=INVALID_HANDLE_VALUE){CloseHandle(transfer.read);transfer.read=INVALID_HANDLE_VALUE;}}
void reset_features(){close_transfer_file();transfer=Transfer{};preview_active=false;movie_owned=false;transfer_active=false;feature_uncertain=false;}
int32_t transfer_fail(SwError* e,int32_t code,uint32_t sony,const char* message) {
    close_transfer_file();transfer.info.state=3;transfer.info.path[0]=0;transfer_active=false;feature_uncertain=true;
    result(e,code,SW_CAPTURE_DOWNLOAD,sony,message);transfer.info.failure=*e;return code;
}
int32_t cleanup_features(SwError* e,uint32_t ms) {
    if(cb&&cb->disconnected.load()) {
        // A physical disconnect already stopped SDK threads. Do not block ReleaseDevice trying
        // to issue movie commands to a disconnected handle; this is not proof the body stopped recording.
        movie_owned=false;preview_active=false;
        if(transfer_active)transfer_fail(e,SW_SDK,cb->disconnect_error.load(),"Connection lost; original transfer incomplete");
        return SW_OK;
    }
    if(movie_owned){auto rc=movie_set_internal(false,std::min(ms,5000u),e);if(rc)return rc;}
    if(transfer_active) {
        sdk::SendCommand(device,sdk::CrCommandId_CancelContentsTransfer,sdk::CrCommandParam_Down);
        transfer_fail(e,SW_CANCELLED,0,"Transfer interrupted by disconnect; partial file retained");
    }
    // The SDK's disconnect terminates background preview and transfer threads. No unload before callback.
    preview_active=false;return SW_OK;
}
int32_t contents_ready(SwError* e) {
    auto rc=connected_check(e,SW_READ);if(rc)return rc;
    if(connection_mode!=SW_MODE_CONTENTS || transfer_active || feature_uncertain)return result(e,SW_STATE,SW_READ,0,"Requires idle Contents connection; disconnect remote first");
    uint64_t status=0;rc=read_numeric(sdk::CrDeviceProperty_ContentsTransferStatus,status,e);if(rc)return rc;
    return status==sdk::CrContentsTransfer_ON?SW_OK:result(e,SW_STATE,SW_READ,0,"Camera contents transfer not ready");
}
bool safe_name(const wchar_t* name,size_t capacity) {
    if(!name)return false;const size_t n=wcsnlen_s(name,capacity);if(!n||n==capacity)return false;
    if(name[n-1]==L'.'||name[n-1]==L' ')return false;
    return !wcspbrk(name,L"\\/:*?\"<>|") && std::wstring(name)!=L"." && std::wstring(name)!=L"..";
}
int32_t content_info(uint32_t handle,SwContentInfo& out,SwError* e) {
    sdk::CrMtpContentsInfo p;const auto rc=sdk::GetContentsDetailInfo(device,handle,&p);
    if(rc)return result(e,SW_SDK,SW_READ,rc,"Content metadata query failed");
    if(p.handle!=handle || !safe_name(p.fileName,512))return result(e,SW_ASSOCIATION,SW_READ,0,"Invalid content identity or unsafe filename");
    out={};out.size=sizeof(out);out.handle=p.handle;out.folder=p.parentFolderHandle;out.session=cb->generation;
    out.bytes=p.contentSize;out.width=p.width;out.height=p.height;
    if(!utf8(p.fileName,out.name,sizeof(out.name)) || wcsnlen_s(p.dateChar,16)==16 || !utf8(p.dateChar,out.date,sizeof(out.date)))
        return result(e,SW_BUFFER,SW_READ,0,"Content name/date exceeds UTF-8 contract");
    return SW_OK;
}
}
int32_t SW_CALL sw_get_feature_abi(uint32_t* version,uint32_t* ps,uint32_t* fs_,uint32_t* folders,uint32_t* contents,uint32_t* ts,SwError* e) {
    return boundary(e,SW_NONE,[&]()->int32_t {if(!version||!ps||!fs_||!folders||!contents||!ts)return result(e,SW_ARGUMENT,SW_NONE,0,"All feature ABI size outputs required");
        *version=1;*ps=sizeof(SwPropertyInfo);*fs_=sizeof(SwFrameInfo);*folders=sizeof(SwFolderInfo);*contents=sizeof(SwContentInfo);*ts=sizeof(SwTransferInfo);return SW_OK;});
}
int32_t SW_CALL sw_get_property(uint32_t id,SwPropertyInfo* out,int64_t* values,uint32_t capacity,uint32_t* count,SwError* e) {
    return boundary(e,SW_SUPPORT,[&]()->int32_t {
        if(!out||out->size!=sizeof(*out)||!count||(!values&&capacity))return result(e,SW_ARGUMENT,SW_SUPPORT,0,"Invalid property output/buffer");
        *out={};out->size=sizeof(*out);*count=0;auto spec=property_spec(id);if(!spec.code)return result(e,SW_UNSUPPORTED,SW_SUPPORT,0,"Unknown project property");
        auto rc=ready_check(e,SW_SUPPORT);if(rc)return rc;PropertyList p;rc=p.load(spec.code,e,SW_SUPPORT);if(rc){if(e->sony_code==sdk::CrError_Generic_NotSupported||e->sony_code==sdk::CrError_Api_InvalidCalled)return result(e,SW_UNSUPPORTED,SW_SUPPORT,e->sony_code,"Property not supported by this device");return rc;}
        NumericValues n;rc=decode_choices(p.list[0],n,e);if(rc)return rc;
        SwPropertyInfo info{};info.size=sizeof(info);info.id=id;info.readable=p.list[0].IsGetEnableCurrentValue();
        if(!numeric_value(p.list[0].GetCurrentValue(),p.list[0].GetValueType(),info.current))return result(e,SW_UNSUPPORTED,SW_SUPPORT,0,"Unsupported property current value");
        info.writable=spec.write&&p.list[0].IsSetEnableCurrentValue()&&(n.range||!n.choices.empty());
        info.candidate_count=static_cast<uint32_t>(n.choices.size());info.is_range=n.range;info.minimum=n.min;info.maximum=n.max;info.step=n.step;
        rc=p.close(e,SW_SUPPORT);if(rc)return rc;*count=info.candidate_count;
        if(values&&capacity<*count)return result(e,SW_BUFFER,SW_SUPPORT,0,"Candidate capacity changed; retry query");
        if(values)std::copy(n.choices.begin(),n.choices.end(),values);*out=info;return SW_OK;
    });
}
int32_t SW_CALL sw_set_property(uint32_t id,int64_t requested,uint32_t ms,int64_t* actual,SwError* e) {
    return boundary(e,SW_SET,[&]()->int32_t {
        if(!actual||!valid_timeout(ms))return result(e,SW_ARGUMENT,SW_SET,0,"Actual output and timeout 1..60000 required");*actual=0;
        auto rc=feature_write_ready(e);if(rc)return rc;auto spec=property_spec(id);if(!spec.code||!spec.write)return result(e,SW_UNSUPPORTED,SW_SET,0,"Read-only/unknown project property");
        PropertyList p;rc=p.load(spec.code,e,SW_SET);if(rc)return rc;NumericValues n;rc=decode_choices(p.list[0],n,e);if(rc)return rc;
        if(!p.list[0].IsSetEnableCurrentValue()||!n.accepts(requested))return result(e,SW_UNSUPPORTED,SW_SET,0,"Value not a current writable candidate/range step; no write submitted");
        sdk::CrDeviceProperty copy=p.list[0];rc=p.close(e,SW_SET);if(rc)return rc;copy.SetCurrentValue(static_cast<uint64_t>(requested));
        auto sdk_rc=sdk::SetDeviceProperty(device,&copy);if(sdk_rc){feature_uncertain=true;return result(e,SW_SDK,SW_SET,sdk_rc,"Property write unconfirmed; disconnect required");}
        auto deadline=Clock::now()+std::chrono::milliseconds(ms);
        const auto read_code=id==SW_PROP_FOCUSPOSITION?sdk::CrDeviceProperty_FocusPositionCurrentValue:spec.code;
        do {uint64_t current=0;rc=connected_check(e,SW_VERIFY);if(rc){feature_uncertain=true;return rc;}rc=read_numeric(read_code,current,e);if(rc){feature_uncertain=true;return rc;}
            int64_t signed_current=0;if(!numeric_value(current,copy.GetValueType(),signed_current)){feature_uncertain=true;return result(e,SW_INTERNAL,SW_VERIFY,0,"Invalid readback value");}
            if(cb->feature_warning.load()){feature_uncertain=true;return result(e,SW_SDK,SW_VERIFY,cb->feature_warning.load(),"Asynchronous property command failed");}
            if(signed_current==requested){*actual=signed_current;return SW_OK;}std::this_thread::sleep_for(20ms);
        }while(Clock::now()<deadline);
        feature_uncertain=true;return result(e,SW_TIMEOUT,SW_VERIFY,0,"Property readback timed out; disconnect required");
    });
}
int32_t SW_CALL sw_live_view_set(uint32_t enabled,SwError* e) {
    return boundary(e,SW_SET,[&]()->int32_t {
        if(enabled>1)return result(e,SW_ARGUMENT,SW_SET,0,"Enable must be zero/one");
        auto rc=feature_write_ready(e);if(rc)return rc;const uint32_t target=enabled?sdk::CrDeviceSetting_Enable:sdk::CrDeviceSetting_Disable;
        auto sdk_rc=sdk::SetDeviceSetting(device,sdk::Setting_Key_EnableLiveView,target);
        if(sdk_rc){feature_uncertain=true;return result(e,SW_SDK,SW_SET,sdk_rc,"SDK preview enable/disable failed");}
        uint32_t actual=0;sdk_rc=sdk::GetDeviceSetting(device,sdk::Setting_Key_EnableLiveView,&actual);
        if(sdk_rc||actual!=target){feature_uncertain=true;return result(e,sdk_rc?SW_SDK:SW_STATE,SW_VERIFY,sdk_rc,"Preview setting readback failed");}
        preview_active=enabled!=0;return SW_OK;
    });
}
int32_t SW_CALL sw_live_view_read(uint8_t* buffer,uint32_t capacity,SwFrameInfo* out,SwError* e) {
    return boundary(e,SW_READ,[&]()->int32_t {
        if(!out||out->size!=sizeof(*out)||(!buffer&&capacity))return result(e,SW_ARGUMENT,SW_READ,0,"Invalid frame output/buffer");*out={};out->size=sizeof(*out);
        auto rc=ready_check(e,SW_READ);if(rc)return rc;if(!preview_active||connection_mode!=SW_MODE_REMOTE||transfer_active||feature_uncertain)return result(e,SW_STATE,SW_READ,0,"Preview not actively enabled in usable remote session");
        uint64_t status=0;rc=read_numeric(sdk::CrDeviceProperty_LiveViewStatus,status,e);if(rc)return rc;
        if(status!=sdk::CrLiveView_Enable)return result(e,status==sdk::CrLiveView_NotSupport?SW_UNSUPPORTED:SW_PENDING,SW_READ,0,"Live image currently unavailable; no cached image returned");
        sdk::CrImageInfo info;auto sdk_rc=sdk::GetLiveViewImageInfo(device,&info);if(sdk_rc)return result(e,SW_SDK,SW_READ,sdk_rc,"Live image info failed");
        auto needed=info.GetBufferSize();if(!needed)return result(e,SW_PENDING,SW_READ,0,"No preview frame yet");
        if(needed>32u*1024*1024)return result(e,SW_INTERNAL,SW_READ,0,"Preview buffer exceeds 32 MiB bound");
        out->bytes=needed;out->width=info.GetWidth();out->height=info.GetHeight();if(!buffer)return SW_OK;
        if(capacity<needed)return result(e,SW_BUFFER,SW_READ,0,"Preview size changed; resize and retry");
        // Use local buffer: even on SDK error or malformed result the caller's array remains untouched.
        std::vector<uint8_t> data(needed);sdk::CrImageDataBlock frame;frame.SetSize(needed);frame.SetData(data.data());
        sdk_rc=sdk::GetLiveViewImage(device,&frame);if(sdk_rc)return result(e,SW_SDK,SW_READ,sdk_rc,"Live frame fetch failed");
        const auto bytes=frame.GetImageSize();if(!bytes){out->bytes=0;return result(e,SW_PENDING,SW_READ,0,"Empty preview frame");}
        const auto* source=frame.GetImageData();
        if(!source||bytes<4||source[0]!=0xFF||source[1]!=0xD8)return result(e,SW_INTERNAL,SW_READ,0,"Malformed JPEG preview");
        if(bytes>needed){out->bytes=bytes;return result(e,SW_BUFFER,SW_READ,0,"Preview frame larger than the queried buffer; resize and retry");}
        std::memcpy(buffer,source,bytes);out->bytes=bytes;out->frame_number=frame.GetFrameNo();out->time_code=frame.GetTimeCode();return SW_OK;
    });
}
int32_t SW_CALL sw_movie_set(uint32_t recording,uint32_t ms,SwError* e) {
    return boundary(e,SW_SET,[&]()->int32_t {if(recording>1)return result(e,SW_ARGUMENT,SW_SET,0,"Recording must be zero/one");return movie_set_internal(recording!=0,ms,e);});
}
int32_t SW_CALL sw_focus_pulse(int32_t speed,uint32_t hold,SwError* e) {
    return boundary(e,SW_SET,[&]()->int32_t {
        if(!speed||speed<-127||speed>127||hold<1||hold>2000)return result(e,SW_ARGUMENT,SW_SET,0,"Nonzero signed speed -127..127, pulse 1..2000 ms required");
        auto rc=feature_write_ready(e);if(rc)return rc;
        sdk::CrControlCodeInfo* info=nullptr;auto sdk_rc=sdk::GetSelectControlCode(device,sdk::CrControlCode_FocusOperation,&info);
        if(sdk_rc)return result(e,SW_UNSUPPORTED,SW_SUPPORT,sdk_rc,"Lens does not report relative focus control");
        bool accepted=false;if(info&&info->GetValueType()==sdk::CrDataType_Int8&&info->GetValueSize()==3&&info->GetValues()) {
            const auto p=reinterpret_cast<const int8_t*>(info->GetValues());accepted=p[2]>0&&speed>=p[0]&&speed<=p[1]&&(speed-p[0])%p[2]==0;
        }
        if(info){sdk_rc=sdk::ReleaseControlCodes(device,info);if(sdk_rc){fault=true;return result(e,SW_SDK,SW_SUPPORT,sdk_rc,"Focus control release failed");}}
        if(!accepted)return result(e,SW_UNSUPPORTED,SW_SUPPORT,0,"Focus speed is not a supported lens range step");
        sdk::CrError start=0;
        try {start=sdk::ExecuteControlCodeValue(device,sdk::CrControlCode_FocusOperation,static_cast<uint64_t>(static_cast<int64_t>(speed)));
            if(!start)std::this_thread::sleep_for(std::chrono::milliseconds(hold));
        }catch(...) {
            feature_uncertain=true;
            try {if(sdk::ExecuteControlCodeValue(device,sdk::CrControlCode_FocusOperation,0))sdk::ExecuteControlCodeValue(device,sdk::CrControlCode_FocusOperation,0);}catch(...){}
            throw;
        }
        auto stop=sdk::ExecuteControlCodeValue(device,sdk::CrControlCode_FocusOperation,0);
        if(stop)stop=sdk::ExecuteControlCodeValue(device,sdk::CrControlCode_FocusOperation,0);
        if(start||stop||cb->feature_warning.load()){feature_uncertain=true;return result(e,SW_SDK,SW_SET,start?start:(stop?stop:cb->feature_warning.load()),"Focus pulse start/stop failed; lens stop not confirmed");}
        return result(e,SW_OK,SW_VERIFY,0,"Focus stop command accepted; actual focus position must be read separately");
    });
}
int32_t SW_CALL sw_get_folders(SwFolderInfo* items,uint32_t capacity,uint32_t* count,SwError* e) {
    return boundary(e,SW_READ,[&]()->int32_t {
        if(!count||(!items&&capacity))return result(e,SW_ARGUMENT,SW_READ,0,"Invalid folder buffer");*count=0;auto rc=contents_ready(e);if(rc)return rc;
        sdk::CrMtpFolderInfo* p=nullptr;uint32_t n=0;auto sdk_rc=sdk::GetDateFolderList(device,&p,&n);
        if(sdk_rc)return result(e,SW_SDK,SW_READ,sdk_rc,"Date folder list failed");
        struct Guard{sdk::CrMtpFolderInfo* p;~Guard(){if(p && sdk::ReleaseDateFolderList(device,p))fault=true;}} guard{p};
        if(n>100000||(n&&!p))return result(e,SW_INTERNAL,SW_READ,0,"Malformed folder list");
        *count=n;if(!items||capacity<n){const auto release=p?sdk::ReleaseDateFolderList(device,p):0;guard.p=nullptr;if(release){fault=true;return result(e,SW_SDK,SW_READ,release,"Folder list release failed");}return !items?SW_OK:result(e,SW_BUFFER,SW_READ,0,"Folder capacity changed");}
        std::vector<SwFolderInfo> copy(n);for(uint32_t i=0;i<n;++i){copy[i].size=sizeof(SwFolderInfo);copy[i].handle=p[i].handle;copy[i].session=cb->generation;if(!utf8(p[i].folderName,copy[i].name,sizeof(copy[i].name)))return result(e,SW_BUFFER,SW_READ,0,"Folder name too long");}
        sdk_rc=p?sdk::ReleaseDateFolderList(device,p):0;guard.p=nullptr;if(sdk_rc){fault=true;return result(e,SW_SDK,SW_READ,sdk_rc,"Folder list release failed");}
        std::copy(copy.begin(),copy.end(),items);return SW_OK;
    });
}
int32_t SW_CALL sw_get_contents(uint64_t session,uint32_t folder,SwContentInfo* items,uint32_t capacity,uint32_t* count,SwError* e) {
    return boundary(e,SW_READ,[&]()->int32_t {
        if(!count||(!items&&capacity))return result(e,SW_ARGUMENT,SW_READ,0,"Invalid content buffer");*count=0;auto rc=contents_ready(e);if(rc)return rc;
        if(session!=cb->generation)return result(e,SW_ASSOCIATION,SW_READ,0,"Folder belongs to another session");
        sdk::CrContentHandle* p=nullptr;uint32_t n=0;auto sdk_rc=sdk::GetContentsHandleList(device,folder,&p,&n);
        if(sdk_rc)return result(e,SW_SDK,SW_READ,sdk_rc,"Content list failed");
        struct Guard{sdk::CrContentHandle* p;~Guard(){if(p&&sdk::ReleaseContentsHandleList(device,p))fault=true;}} guard{p};
        if(n>1000000||(n&&!p))return result(e,SW_INTERNAL,SW_READ,0,"Malformed contents list");*count=n;
        if(!items||capacity<n){const auto release=p?sdk::ReleaseContentsHandleList(device,p):0;guard.p=nullptr;if(release){fault=true;return result(e,SW_SDK,SW_READ,release,"Content list release failed");}return !items?SW_OK:result(e,SW_BUFFER,SW_READ,0,"Content capacity changed");}
        std::vector<SwContentInfo> copy(n);for(uint32_t i=0;i<n;++i){rc=content_info(p[i],copy[i],e);if(rc)return rc;if(copy[i].folder!=folder)return result(e,SW_ASSOCIATION,SW_READ,0,"Content parent folder mismatch");}
        sdk_rc=p?sdk::ReleaseContentsHandleList(device,p):0;guard.p=nullptr;if(sdk_rc){fault=true;return result(e,SW_SDK,SW_READ,sdk_rc,"Content list release failed");}
        std::copy(copy.begin(),copy.end(),items);return SW_OK;
    });
}
int32_t SW_CALL sw_content_start(uint64_t session,uint32_t handle,const char* root,uint32_t ms,SwTransferInfo* out,SwError* e) {
    return boundary(e,SW_CAPTURE_SAVE,[&]()->int32_t {
        if(!out||out->size!=sizeof(*out))return result(e,SW_ARGUMENT,SW_CAPTURE_SAVE,0,"Invalid transfer output");*out={};out->size=sizeof(*out);
        if(!root||!ms||ms>3600000)return result(e,SW_ARGUMENT,SW_CAPTURE_SAVE,0,"Existing root and timeout 1..3600000 required");auto rc=contents_ready(e);if(rc)return rc;
        if(session!=cb->generation)return result(e,SW_ASSOCIATION,SW_CAPTURE_SAVE,0,"Content belongs to another session");
        SwContentInfo source{};rc=content_info(handle,source,e);if(rc)return rc;if(!source.bytes)return result(e,SW_UNSUPPORTED,SW_CAPTURE_SAVE,0,"Camera did not report original byte size; cannot verify transfer");
        wchar_t directory[1024]{};if(strnlen_s(root,1024)==1024||!MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,root,-1,directory,1024))return result(e,SW_ARGUMENT,SW_CAPTURE_SAVE,0,"Invalid root UTF-8");
        fs::path parent(directory);if(!parent.is_absolute()||!fs::is_directory(parent)||parent.native().size()<3||parent.native()[1]!=L':')return result(e,SW_ARGUMENT,SW_CAPTURE_SAVE,0,"Existing absolute local root required");
        wchar_t filename[512]{};if(!MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,source.name,-1,filename,512)||!safe_name(filename,512))return result(e,SW_ARGUMENT,SW_CAPTURE_SAVE,0,"Unsafe content filename");
        close_transfer_file();transfer=Transfer{};transfer.info.size=sizeof(transfer.info);transfer.info.id=++next_transfer;transfer.info.state=1;transfer.info.expected_bytes=source.bytes;transfer.info.failure.size=sizeof(SwError);
        transfer.session=cb;transfer.handle=handle;transfer.deadline=Clock::now()+std::chrono::milliseconds(ms);
        wchar_t child[80]{};std::swprintf(child,80,L"content-p%lu-%llu-%llu",GetCurrentProcessId(),cb->generation,next_transfer);
        transfer.directory=fs::canonical(parent)/child;transfer.file=transfer.directory/filename;
        if(transfer.file.native().size()>240)return result(e,SW_BUFFER,SW_CAPTURE_SAVE,0,"Download path too long");
        if(!fs::create_directory(transfer.directory))return result(e,SW_IO,SW_CAPTURE_SAVE,0,"Exclusive download directory unavailable");
        bool stale=false;cb->content_events.drain([&](const auto&){stale=true;});if(stale||cb->content_events.overflow.load())return result(e,SW_ASSOCIATION,SW_CAPTURE_SAVE,0,"Unexpected contents notifications; disconnect required");
        transfer_active=true;auto folder=transfer.directory.native();auto file=transfer.file.filename().native();
        const auto sdk_rc=sdk::PullContentsFile(device,handle,sdk::CrPropertyStillImageTransSize_Original,folder.data(),file.data());
        if(sdk_rc){rc=transfer_fail(e,SW_SDK,sdk_rc,"Original content download request failed");*out=transfer.info;return rc;}
        *out=transfer.info;return SW_OK;
    });
}
int32_t SW_CALL sw_content_poll(uint64_t id,SwTransferInfo* out,SwError* e) {
    return boundary(e,SW_CAPTURE_DOWNLOAD,[&]()->int32_t {
        if(!out||out->size!=sizeof(*out))return result(e,SW_ARGUMENT,SW_CAPTURE_DOWNLOAD,0,"Invalid transfer output");*out={};out->size=sizeof(*out);
        if(!id||id!=transfer.info.id)return result(e,SW_STATE,SW_CAPTURE_DOWNLOAD,0,"Unknown transfer ID");
        auto publish=[&](int32_t rc){*out=transfer.info;if(transfer.info.state==3)*e=transfer.info.failure;return rc;};
        if(transfer.info.state!=1)return publish(transfer.info.state==2?SW_OK:transfer.info.failure.code);
        auto fail=[&](int32_t rc,uint32_t sony,const char* message){return publish(transfer_fail(e,rc,sony,message));};
        if(cb!=transfer.session||!device||cb->disconnected.load()||cb->error.load())return fail(SW_SDK,cb?cb->error.load():0,"Connection failed during transfer");
        if(Clock::now()>=transfer.deadline)return fail(SW_TIMEOUT,0,"Original content transfer timed out; partial file retained");
        bool bad=cb->content_events.overflow.load();uint32_t failure=0;
        cb->content_events.drain([&](const auto& event){
            if(event.code!=sdk::CrNotify_ContentsTransfer_Complete){bad=true;failure=event.code;return;}
            if(event.detail!=transfer.handle||event.invalid){bad=true;return;}
            fs::path p(event.path);if((p.is_absolute()&&!wide_equal(p.lexically_normal().native(),transfer.file.lexically_normal().native()))||(!p.is_absolute()&&!wide_equal(p.native(),transfer.file.filename().native()))){bad=true;return;}
            transfer.completed=true;
        });
        if(bad)return fail(SW_ASSOCIATION,failure,"Contents notification failed or mismatched identity/path");
        if(!transfer.completed)return publish(SW_PENDING);
        if(transfer.read==INVALID_HANDLE_VALUE) {
            transfer.read=CreateFileW(transfer.file.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL|FILE_FLAG_SEQUENTIAL_SCAN,nullptr);
            if(transfer.read==INVALID_HANDLE_VALUE)return publish(SW_PENDING);
            LARGE_INTEGER bytes{};if(!GetFileSizeEx(transfer.read,&bytes)||uint64_t(bytes.QuadPart)!=transfer.info.expected_bytes)return fail(SW_IO,GetLastError(),"Original download size differs from camera metadata");
        }
        std::array<uint8_t,64*1024> buffer{};DWORD got=0;
        if(!ReadFile(transfer.read,buffer.data(),static_cast<DWORD>(buffer.size()),&got,nullptr))return fail(SW_IO,GetLastError(),"Sequential downloaded file verification failed");
        transfer.info.verified_bytes+=got;
        if(transfer.info.verified_bytes>transfer.info.expected_bytes)return fail(SW_IO,0,"Downloaded file exceeds camera size");
        if(got)return publish(SW_PENDING);
        if(transfer.info.verified_bytes!=transfer.info.expected_bytes)return fail(SW_IO,0,"Premature end of original file");
        if(!utf8(transfer.file.c_str(),transfer.info.path,sizeof(transfer.info.path)))return fail(SW_BUFFER,0,"Downloaded path exceeds UTF-8 contract");
        close_transfer_file();transfer_active=false;transfer.info.state=2;return publish(SW_OK);
    });
}
int32_t SW_CALL sw_content_cancel(uint64_t id,SwTransferInfo* out,SwError* e) {
    return boundary(e,SW_CAPTURE_CANCEL,[&]()->int32_t {
        if(!out||out->size!=sizeof(*out))return result(e,SW_ARGUMENT,SW_CAPTURE_CANCEL,0,"Invalid transfer output");*out={};out->size=sizeof(*out);
        if(!id||id!=transfer.info.id||!transfer_active)return result(e,SW_STATE,SW_CAPTURE_CANCEL,0,"No matching active transfer");
        const auto sdk_rc=sdk::SendCommand(device,sdk::CrCommandId_CancelContentsTransfer,sdk::CrCommandParam_Down);
        const auto rc=transfer_fail(e,SW_CANCELLED,sdk_rc,"Download cancelled; SDK stop not implied; clean disconnect required");*out=transfer.info;return rc;
    });
}
