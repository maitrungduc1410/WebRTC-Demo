// Non-Windows builds (the Linux test build) capture cameras with libwebrtc only.
#include "shim_internal.h"

namespace rtc_shim {

class CameraReader {};

CameraReader* StartCameraReader(scoped_refptr<libwebrtc::RTCVideoSource>, const std::string&,
                                const std::string&, int, int, int, std::string* error) {
  error->clear();
  return nullptr;
}

bool CameraReaderAlive(CameraReader*) { return false; }

void StopCameraReader(CameraReader* reader) { delete reader; }

}  // namespace rtc_shim