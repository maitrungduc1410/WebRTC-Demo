// Non-Windows builds (the Linux test build) have no Media Foundation.
#include "shim_internal.h"

namespace rtc_shim {

class FileVideoReader {};

FileVideoReader* StartFileVideoReader(scoped_refptr<libwebrtc::RTCVideoSource>,
                                      const std::string&, bool, rtc_capture_state_cb, void*) {
  SetLastError("rtc_file_source_create: video files are only supported on Windows");
  return nullptr;
}

void StopFileVideoReader(FileVideoReader* reader) { delete reader; }

}  // namespace rtc_shim
