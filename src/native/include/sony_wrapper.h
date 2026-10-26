#ifndef SONY_WRAPPER_H
#define SONY_WRAPPER_H
#include <stdint.h>
#ifdef SW_BUILD
#define SW_API __declspec(dllexport)
#else
#define SW_API __declspec(dllimport)
#endif
#define SW_CALL __cdecl
#ifdef __cplusplus
extern "C" {
#endif
/* ABI v2 (v1 structures and entry points retained): Windows x64, pack 8, UTF-8 NUL-terminated strings; caller-owned buffers.
   Single process-wide camera, serialized calls from one owner thread.
   Old outputs valid only on SW_OK except SwError; capture outputs also valid
   on SW_PENDING and accepted-task terminal failures (see capture API contract).
   See TECHNICAL.md for state, deadlines, ownership, and failure rules. */
enum SwStatus { SW_OK=0, SW_ARGUMENT=1, SW_STATE=2, SW_SDK=3,
    SW_TIMEOUT=4, SW_UNSUPPORTED=5, SW_INTERNAL=6, SW_BUFFER=7,
    SW_PENDING=8, SW_CANCELLED=9, SW_IO=10, SW_ASSOCIATION=11, SW_ENDED_EARLY=12 };
enum SwStage { SW_NONE=0, SW_INIT=1, SW_ENUM=2, SW_CONNECT=3,
    SW_READY=4, SW_READ=5, SW_DISCONNECT=6, SW_SHUTDOWN=7,
    SW_SUPPORT=8, SW_SET=9, SW_VERIFY=10, SW_CAPTURE_CHECK=11,
    SW_CAPTURE_SAVE=12, SW_CAPTURE_DOWN=13, SW_CAPTURE_UP=14,
    SW_CAPTURE_DOWNLOAD=15, SW_CAPTURE_FILE=16, SW_CAPTURE_CANCEL=17, SW_CAPTURE_RECONCILE=18, SW_EXPOSURE_SETUP=19, SW_EXPOSURE_TIMER=20, SW_EXPOSURE_RESTORE=21 };
enum SwIsoKind { SW_ISO_FIXED=0, SW_ISO_AUTO=1, SW_ISO_SPECIAL=2 };
enum SwShutterKind { SW_SHUTTER_SECONDS=0, SW_SHUTTER_BULB=1, SW_SHUTTER_UNAVAILABLE=2 };
enum SwCaptureState { SW_CAPTURE_NONE=0, SW_CAPTURE_WAITING=1,
    SW_CAPTURE_SUCCEEDED=2, SW_CAPTURE_FAILED=3,
    SW_CAPTURE_CANCELLED_UNCONFIRMED=4, SW_CAPTURE_TIMED_OUT_UNCONFIRMED=5, SW_CAPTURE_ENDED_EARLY_UNCONFIRMED=6 };
/* Existing numeric values are retained; UNKNOWN means body has no compression selector,
   not that a file is valid. The Sony Bayer decoder validates every completed RAW. */
enum SwRawFormat { SW_RAW_UNKNOWN=0, SW_RAW_COMPRESSED=1, SW_RAW_LOSSLESS=2, SW_RAW_COMPRESSED_HQ=3,
    SW_RAW_UNCOMPRESSED=4, SW_RAW_LOSSLESS_SMALL=5, SW_RAW_LOSSLESS_MEDIUM=6, SW_RAW_LOSSLESS_LARGE=7 };
enum SwSaveDestination { SW_SAVE_COMPUTER=1, SW_SAVE_COMPUTER_AND_CARD=2, SW_SAVE_CARD=3 };

enum SwExposureMode { SW_EXPOSURE_ORDINARY=0, SW_EXPOSURE_BULB=1 };
enum SwExposurePhase { SW_EXPOSURE_IDLE=0, SW_EXPOSURE_HOLDING=1,
    SW_EXPOSURE_DOWNLOADING=2, SW_EXPOSURE_RESTORING=3, SW_EXPOSURE_TERMINAL=4 };
enum SwExposureEndReason { SW_END_NONE=0, SW_END_NORMAL=1, SW_END_CANCEL=2,
    SW_END_EARLY=3, SW_END_TIMEOUT=4, SW_END_ERROR=5, SW_END_DISCONNECT=6 };
enum SwRestoreState { SW_RESTORE_NONE=0, SW_RESTORE_PENDING=1,
    SW_RESTORE_CONFIRMED=2, SW_RESTORE_FAILED=3 };

#pragma pack(push,8)
typedef struct SwError {
    uint32_t size; int32_t code; uint32_t sony_code; uint32_t stage;
    uint64_t capture_id; char message[160];
} SwError;
typedef struct SwCameraInfo {
    uint32_t size; uint32_t index; char model[128]; char connection[32];
} SwCameraInfo;
typedef struct SwParameters {
    uint32_t size; uint32_t iso; uint32_t iso_kind; uint32_t shutter_kind;
    double exposure_seconds; double f_number;
} SwParameters;
typedef struct SwCaptureResult {
    uint32_t size; uint32_t state; uint64_t capture_id;
    SwParameters actual_parameters;
    uint32_t actual_valid; uint32_t raw_format; uint32_t save_destination;
    uint32_t download_notifications; uint64_t file_bytes;
    SwError failure;
    char receive_directory[1024]; char arw_path[1024];
} SwCaptureResult;

/* v2 request: explicit mode; no nearest value or automatic Bulb selection.
   All double durations are seconds. Bulb supports 1..900 seconds in exact
   millisecond increments (project policy, not a claimed Sony hardware maximum).
   Exposure timeout 0.001..1800; download timeout 0.001..3600 seconds.
   A shorter exposure timeout deliberately terminates with SW_TIMEOUT.
   Ordinary mode requires requested seconds equal the current queried/read-back
   ordinary shutter, and exposure timeout >= requested seconds.
   setup_timeout_ms: 1..60000; reserved must be zero. */
typedef struct SwExposureRequest {
    uint32_t size; uint32_t mode; double exposure_seconds;
    double exposure_timeout_seconds; double download_timeout_seconds;
    uint32_t setup_timeout_ms; uint32_t reserved;
} SwExposureRequest;
typedef struct SwExposureCapabilities {
    uint32_t size; uint32_t bulb_candidate_present;
    double bulb_min_seconds; double bulb_max_seconds; double bulb_step_seconds;
    double max_exposure_timeout_seconds; double max_download_timeout_seconds;
    uint32_t timer_setting_readable; uint32_t timer_enabled;
} SwExposureCapabilities;
/* software_hold_seconds measures host Down enqueue return -> first Up call;
   it is NOT measured sensor integration or EXIF time. NaN before release.
   camera_elapsed_* are optional whole-second readback while holding, not final EXIF.
   restore_error and release_error keep secondary errors even for cancelled tasks.
   original_parameters is the pre-task snapshot; original_timer_enabled valid
   only when timer_original_valid=1. Query is read-only with respect to camera. */
typedef struct SwExposureInfo {
    uint32_t size; uint32_t mode; double requested_seconds;
    double software_hold_seconds;
    uint32_t camera_elapsed_seconds; uint32_t camera_elapsed_valid;
    uint32_t phase; uint32_t end_reason; uint32_t release_attempts; uint32_t reserved;
    uint32_t restore_state; uint32_t reserved2;
    SwError release_error; SwError restore_error;
    SwParameters original_parameters;
    uint32_t original_timer_enabled; uint32_t timer_original_valid;
} SwExposureInfo;

#pragma pack(pop)
/* Initialize each struct's size to sizeof(struct), including SwError.
   timeout_ms is 1..60000. Enumeration uses the SDK's 3-second scan. */
SW_API int32_t SW_CALL sw_initialize(SwError* error);
SW_API int32_t SW_CALL sw_enumerate(uint32_t* count, SwError* error);
SW_API int32_t SW_CALL sw_camera_info(uint32_t index, SwCameraInfo* info, SwError* error);
SW_API int32_t SW_CALL sw_connect(uint32_t index, uint32_t timeout_ms, SwError* error);
SW_API int32_t SW_CALL sw_wait_ready(uint32_t timeout_ms, SwError* error);
SW_API int32_t SW_CALL sw_read_parameters(SwParameters* parameters, SwError* error);
/* Current writable fixed ISO / ordinary shutter candidates, no Auto/Bulb/sentinels.
   Count query: values=NULL, capacity=0 -> SW_OK and required count.
   Otherwise caller owns array; insufficient capacity -> SW_BUFFER, required count,
   array untouched. Candidate lists may change: setters always re-query.
   Exposure matches canonical queried double EXACTLY; no tolerance/nearest value.
   Setters: actual is valid only on SW_OK after readback; timeout 1..60000 ms.
   Rejected unsupported request never submits a write. Post-write failure leaves
   state unconfirmed/quarantined: do not issue another setter; see TECHNICAL.md. */
SW_API int32_t SW_CALL sw_get_supported_iso(uint32_t* values, uint32_t capacity, uint32_t* count, SwError* error);
SW_API int32_t SW_CALL sw_get_supported_exposure_seconds(double* values, uint32_t capacity, uint32_t* count, SwError* error);
SW_API int32_t SW_CALL sw_set_iso(uint32_t requested, uint32_t timeout_ms, uint32_t* actual, SwError* error);
SW_API int32_t SW_CALL sw_set_exposure_seconds(double requested, uint32_t timeout_ms, double* actual, SwError* error);
/* Sequential captures and clean reconnects supported. One active task per camera.
   All three calls use the same owner thread; wait is sliced (0..1000 ms).
   Start requires an existing absolute UTF-8 root, creates an exclusive child dir.
   Total timeout 1..60000 ms. Start SW_OK means WAITING, not photo success.
   Wait returns SW_PENDING while active, SW_OK only for SUCCEEDED; terminal
   failure returns its project code and a populated result/failure/capture_id.
   Cancel stops the request; held Bulb also requests bounded Up. Camera stop is not implied.
   New tasks require explicit reconcile proof, not reset.
   Valid output is cleared before every call, including rejected calls; NONE means
   no task result. arw_path is nonempty ONLY on SUCCEEDED. Files retained, caller-owned.
   See TECHNICAL.md for RAW/Single/host-save preconditions and completion rules. */
SW_API int32_t SW_CALL sw_capture_start(const char* receive_root_utf8, uint32_t timeout_ms, SwCaptureResult* output, SwError* error);
SW_API int32_t SW_CALL sw_capture_wait(uint64_t capture_id, uint32_t wait_ms, SwCaptureResult* output, SwError* error);
SW_API int32_t SW_CALL sw_capture_cancel(uint64_t capture_id, SwCaptureResult* output, SwError* error);
/* Reconcile a failed/cancelled task only after exact download+readability and Up proof.
   Preserves its failed terminal result and empty path; never converts it to success. */
SW_API int32_t SW_CALL sw_capture_reconcile(uint64_t capture_id, uint32_t wait_ms, SwCaptureResult* output, SwError* error);
SW_API int32_t SW_CALL sw_capture_start_ex(const char* receive_root_utf8, const char* prefix_utf8, uint32_t timeout_ms, SwCaptureResult* output, SwError* error);
SW_API int32_t SW_CALL sw_check_capture_ready(SwError* error);
SW_API int32_t SW_CALL sw_get_save_destination(uint32_t* destination, SwError* error);
SW_API int32_t SW_CALL sw_set_save_destination(uint32_t requested, uint32_t timeout_ms, uint32_t* actual, SwError* error);
/* Runtime ABI query: version, Error/Camera/Parameters/Capture sizes, respectively. */
SW_API int32_t SW_CALL sw_get_abi_info(uint32_t* version, uint32_t* error_size, uint32_t* camera_size, uint32_t* parameters_size, uint32_t* capture_size, SwError* error);

/* New interfaces, same caller-owned UTF-8/pack8/Cdecl contract.
   Bulb tasks temporarily set Shutter=Bulb and Timer=Off with readback, then
   restore the recorded values after exact file+S2/queue proof. ISO/save/mode
   remain explicit caller choices. A DLL-owned timer releases without polling.
   cancel/end request bounded Up, but never claim confirmed camera stop.
   Both partial files stay failed and caller-owned; reconcile preserves failure.
   Query info after every failure to inspect release and restoration separately.
   Invalid capture output is cleared as in legacy calls. */
SW_API int32_t SW_CALL sw_get_exposure_capabilities(SwExposureCapabilities* output, SwError* error);
SW_API int32_t SW_CALL sw_get_exposure_abi_sizes(uint32_t* request_size, uint32_t* capabilities_size, uint32_t* info_size, SwError* error);
SW_API int32_t SW_CALL sw_capture_start_v2(const char* receive_root_utf8, const char* prefix_utf8, const SwExposureRequest* request, SwCaptureResult* output, SwError* error);
SW_API int32_t SW_CALL sw_capture_exposure_info(uint64_t capture_id, SwExposureInfo* output, SwError* error);
SW_API int32_t SW_CALL sw_capture_end_exposure(uint64_t capture_id, SwCaptureResult* output, SwError* error);

SW_API int32_t SW_CALL sw_disconnect(uint32_t timeout_ms, SwError* error);
SW_API int32_t SW_CALL sw_shutdown(SwError* error);
#ifdef __cplusplus
}
static_assert(sizeof(SwExposureRequest)==40, "exposure request ABI");
static_assert(sizeof(SwExposureCapabilities)==56, "exposure capabilities ABI");
static_assert(sizeof(SwExposureInfo)==464, "exposure info ABI");
static_assert(sizeof(SwError)==184, "SwError ABI");
static_assert(sizeof(SwCameraInfo)==168, "SwCameraInfo ABI");
static_assert(sizeof(SwParameters)==32, "SwParameters ABI");
static_assert(sizeof(SwCaptureResult)==2304, "SwCaptureResult ABI");
#endif
#endif

/* Additive verified capture API: expected_iso must be fixed/nonzero; flags bit 0
   requires explicit body full-frame mode. Existing pack8 structures are unchanged. */
#ifdef __cplusplus
extern "C" {
#endif
SW_API int32_t SW_CALL sw_capture_start_verified(const char* root, const char* prefix,
    const SwExposureRequest* request, uint32_t expected_iso, uint32_t flags, SwCaptureResult* output, SwError* error);
#ifdef __cplusplus
}
#endif
