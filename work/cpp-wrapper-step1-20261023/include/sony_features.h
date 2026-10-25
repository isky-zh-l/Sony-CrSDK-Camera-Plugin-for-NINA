#ifndef SONY_FEATURES_H
#define SONY_FEATURES_H
#include "sony_wrapper.h"
#ifdef __cplusplus
extern "C" {
#endif
/* Additive feature ABI 1. Core ABI 2 is unchanged. Project IDs below are NOT Sony property IDs.
   Numeric choices are opaque camera option tokens, not physical units, except documented convenience APIs.
   Property metadata/actual values are valid only on SW_OK. Counts and frame capacity may be returned on SW_BUFFER. Pack 8, Cdecl, UTF-8.
   All functions use the same owner thread as core. No callbacks cross the ABI.
   Runtime readable/writable/candidates are authoritative; a camera mode/lens can disable any property. */
enum SwPropertyId {
    SW_PROP_APERTURE=1,
    SW_PROP_FOCUSMODE=2,
    SW_PROP_EXPOSUREMODE=3,
    SW_PROP_DRIVEMODE=4,
    SW_PROP_FILEFORMAT=5,
    SW_PROP_RAWCOMPRESSION=6,
    SW_PROP_IMAGESIZE=7,
    SW_PROP_ASPECTRATIO=8,
    SW_PROP_LONGEXPOSURENOISEREDUCTION=9,
    SW_PROP_HIGHISONOISEREDUCTION=10,
    SW_PROP_SILENTMODE=11,
    SW_PROP_SHUTTERTYPE=12,
    SW_PROP_LIVEVIEWQUALITY=13,
    SW_PROP_LIVEVIEWDISPLAYEFFECT=14,
    SW_PROP_LIVEVIEWSTATUS=15,
    SW_PROP_BATTERYPERCENT=16,
    SW_PROP_BATTERYLEVEL=17,
    SW_PROP_RECORDINGSTATE=18,
    SW_PROP_SLOT1STATUS=19,
    SW_PROP_SLOT2STATUS=20,
    SW_PROP_SLOT1REMAININGIMAGES=21,
    SW_PROP_SLOT2REMAININGIMAGES=22,
    SW_PROP_SLOT1REMAININGSECONDS=23,
    SW_PROP_SLOT2REMAININGSECONDS=24,
    SW_PROP_FOCUSPOSITION=25,
    SW_PROP_FOCUSPOSITIONACTUAL=26,
    SW_PROP_FOCUSMAGNIFIER=27,
    SW_PROP_FOCUSMAGNIFICATIONTIME=28,
    SW_PROP_INITIALFOCUSMAGNIFIER=29,
    SW_PROP_MOVIEFORMAT=30,
    SW_PROP_MOVIERECORDINGSETTING=31,
    SW_PROP_MOVIEFRAMERATE=32,
    SW_PROP_WHITEBALANCE=33,
    SW_PROP_COLORTEMPERATURE=34,
    SW_PROP_EXPOSURECOMPENSATION=35,
    SW_PROP_METERINGMODE=36,
    SW_PROP_FOCUSAREA=37,
    SW_PROP_STEADYSHOT=38,
    SW_PROP_AUTOPOWEROFFTEMPERATURE=39,
    SW_PROP_CONTENTSTRANSFERSTATUS=40,
    SW_PROP_CONTENTSTRANSFERPROGRESS=41,
    SW_PROP_REMOTESAVEIMAGESIZE=42,
    SW_PROP_FOCUSINDICATOR=43,
    SW_PROP_APSCMODE=44,
    SW_PROP_MOVIERECORDINGMEDIA=45,
    SW_PROP_FOCUSDRIVINGSTATUS=46,
    SW_PROP_STILLSTORAGEDESTINATION=47,
};
enum SwConnectionMode { SW_MODE_REMOTE=0, SW_MODE_CONTENTS=1 };
#pragma pack(push,8)
typedef struct SwPropertyInfo {
    uint32_t size,id,readable,writable; int64_t current;
    uint32_t candidate_count,is_range; int64_t minimum,maximum,step;
} SwPropertyInfo;
typedef struct SwFrameInfo { uint32_t size,bytes,width,height,frame_number,time_code; } SwFrameInfo;
typedef struct SwFolderInfo { uint32_t size,handle; uint64_t session; char name[256]; } SwFolderInfo;
typedef struct SwContentInfo {
    uint32_t size,handle; uint64_t session,bytes; uint32_t folder,width,height,reserved;
    char name[512]; char date[32];
} SwContentInfo;
/* transfer state 1 waiting, 2 succeeded, 3 failed. Path ONLY returned on success.
   expected_bytes is camera-reported original size; verified_bytes counts bytes actually read locally.
   Cancellation/timeout retains a quarantined transfer until clean disconnect; never resets to success. */
typedef struct SwTransferInfo {
    uint32_t size,state; uint64_t id,expected_bytes,verified_bytes;
    SwError failure; char path[1024];
} SwTransferInfo;
#pragma pack(pop)
SW_API int32_t SW_CALL sw_get_feature_abi(uint32_t* version,uint32_t* property_size,uint32_t* frame_size,uint32_t* folder_size,uint32_t* content_size,uint32_t* transfer_size,SwError* e);
SW_API int32_t SW_CALL sw_connect_mode(uint32_t index,uint32_t mode,uint32_t timeout_ms,SwError* e);
SW_API int32_t SW_CALL sw_get_property(uint32_t id,SwPropertyInfo* info,int64_t* candidates,uint32_t capacity,uint32_t* count,SwError* e);
SW_API int32_t SW_CALL sw_set_property(uint32_t id,int64_t requested,uint32_t timeout_ms,int64_t* actual,SwError* e);
/* Enables/disables SDK live image acquisition with readback. Stop before still exposure/content transfer.
   Read does NOT return the SDK's stale cached frame after stop. Query buffer with NULL/0.
   Frames are JPEG preview only, not sensor RAW or original movie. No guaranteed fps. */
SW_API int32_t SW_CALL sw_live_view_set(uint32_t enabled,SwError* e);
SW_API int32_t SW_CALL sw_live_view_read(uint8_t* buffer,uint32_t capacity,SwFrameInfo* info,SwError* e);
/* A7 V Down=start/Up=stop, checks recording state. No toggle, no silent mode switch.
   Timeout/mutation error quarantines; Stop remains allowed as cleanup. */
SW_API int32_t SW_CALL sw_movie_set(uint32_t recording,uint32_t timeout_ms,SwError* e);
/* Bounded relative focus pulse; speed is exact signed lens candidate. ALWAYS sends stop, even on failed start.
   1..2000 ms; this blocks owner until stopped. Lens position is not calibrated physical distance. */
/* Semantic astronomy choices: 1 long-exposure NR off, 2 high-ISO NR off,
   3 SteadyShot off, 4 manual lens focus, 5 full-size lossless RAW (exact available token).
   Returns project property ID and SDK candidate token, never writes. Requires remote connection. */
SW_API int32_t SW_CALL sw_get_astro_target(uint32_t setting,uint32_t* property,int64_t* value,SwError* e);
SW_API int32_t SW_CALL sw_focus_pulse(int32_t speed,uint32_t hold_ms,SwError* e);
SW_API int32_t SW_CALL sw_get_folders(SwFolderInfo* items,uint32_t capacity,uint32_t* count,SwError* e);
SW_API int32_t SW_CALL sw_get_contents(uint64_t session,uint32_t folder,SwContentInfo* items,uint32_t capacity,uint32_t* count,SwError* e);
/* Only Contents connection mode: disconnect remote, re-enumerate, then connect contents.
   Source requires session+handle from list; SDK Original transfer, exclusive child dir, no overwrites.
   No arbitrary SDK IDs or SDK binaries/headers exposed to callers. 1..3600000 ms deadline.
   Poll is nonblocking, verifies complete callback, exact source size and sequential local readability.
   No completion without expected size. Buffers/files caller owned; files retained on failure. */
SW_API int32_t SW_CALL sw_content_start(uint64_t session,uint32_t handle,const char* root_utf8,uint32_t timeout_ms,SwTransferInfo* info,SwError* e);
SW_API int32_t SW_CALL sw_content_poll(uint64_t id,SwTransferInfo* info,SwError* e);
SW_API int32_t SW_CALL sw_content_cancel(uint64_t id,SwTransferInfo* info,SwError* e);
#ifdef __cplusplus
}
static_assert(sizeof(SwPropertyInfo)==56,"feature property ABI");
static_assert(sizeof(SwFrameInfo)==24,"feature frame ABI");
static_assert(sizeof(SwFolderInfo)==272,"feature folder ABI");
static_assert(sizeof(SwContentInfo)==584,"feature content ABI");
static_assert(sizeof(SwTransferInfo)==1240,"feature transfer ABI");
#endif
#endif
