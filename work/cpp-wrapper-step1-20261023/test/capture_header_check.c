#include "sony_wrapper.h"
#include <stddef.h>
_Static_assert(sizeof(SwCaptureResult)==2304,"capture result ABI");
_Static_assert(offsetof(SwCaptureResult,capture_id)==8,"capture id offset");
_Static_assert(offsetof(SwCaptureResult,actual_parameters)==16,"actual offset");
_Static_assert(offsetof(SwCaptureResult,file_bytes)==64,"bytes offset");
_Static_assert(offsetof(SwCaptureResult,failure)==72,"failure offset");
_Static_assert(offsetof(SwCaptureResult,receive_directory)==256,"directory offset");
_Static_assert(offsetof(SwCaptureResult,arw_path)==1280,"path offset");
int capture_header_check(void) { return sizeof(SwCaptureResult)==2304; }

_Static_assert(sizeof(SwExposureRequest)==40,"v2 request ABI");
_Static_assert(sizeof(SwExposureCapabilities)==56,"v2 capabilities ABI");
_Static_assert(sizeof(SwExposureInfo)==464,"v2 exposure info ABI");
_Static_assert(offsetof(SwExposureRequest,exposure_seconds)==8,"requested seconds offset");
_Static_assert(offsetof(SwExposureRequest,setup_timeout_ms)==32,"setup timeout offset");
_Static_assert(offsetof(SwExposureInfo,release_error)==56,"release error offset");
_Static_assert(offsetof(SwExposureInfo,restore_error)==240,"restore error offset");
_Static_assert(offsetof(SwExposureInfo,original_parameters)==424,"original parameters offset");
