#include "sony_wrapper.h"
_Static_assert(sizeof(SwError)==184,"error ABI");
_Static_assert(sizeof(SwCameraInfo)==168,"camera ABI");
_Static_assert(sizeof(SwParameters)==32,"parameter ABI");
int c_header_check(void) { return sizeof(SwParameters)==32; }

#include "sony_features.h"
