// Non-Windows builds (the Linux test build) have no voice-capture DMO to keep out.
#include "shim_internal.h"

namespace rtc_shim {

bool InitializeFactory(libwebrtc::RTCPeerConnectionFactory* factory) {
  return factory->Initialize();
}

}  // namespace rtc_shim
