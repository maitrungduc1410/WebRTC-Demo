#pragma once

#include <algorithm>
#include <atomic>
#include <cstring>
#include <exception>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

#include "rtc_shim.h"

#include "base/refcountedobject.h"
#include "libwebrtc.h"
#include "rtc_audio_device.h"
#include "rtc_data_channel.h"
#include "rtc_desktop_capturer.h"
#include "rtc_desktop_device.h"
#include "rtc_desktop_media_list.h"
#include "rtc_frame_cryptor.h"
#include "rtc_ice_candidate.h"
#include "rtc_logging.h"
#include "rtc_media_stream.h"
#include "rtc_mediaconstraints.h"
#include "rtc_peerconnection.h"
#include "rtc_peerconnection_factory.h"
#include "rtc_rtp_capabilities.h"
#include "rtc_rtp_receiver.h"
#include "rtc_rtp_sender.h"
#include "rtc_rtp_transceiver.h"
#include "rtc_video_device.h"
#include "rtc_video_frame.h"
#include "rtc_video_renderer.h"
#include "rtc_video_source.h"
#include "rtc_video_track.h"

#ifndef RTC_SHIM_LIBWEBRTC_VERSION
#define RTC_SHIM_LIBWEBRTC_VERSION "unknown"
#endif

namespace rtc_shim {

using libwebrtc::scoped_refptr;

void SetLastError(std::string message);
void ClearLastError();

// Every exported function runs its body through Guard so that no C++ exception crosses the C ABI.
template <typename R, typename F>
R Guard(R fallback, const char* function, F&& body) noexcept {
  try {
    return body();
  } catch (const std::exception& e) {
    SetLastError(std::string(function) + ": " + e.what());
  } catch (...) {
    SetLastError(std::string(function) + ": unknown error");
  }
  return fallback;
}

template <typename F>
void GuardVoid(const char* function, F&& body) noexcept {
  try {
    body();
  } catch (const std::exception& e) {
    SetLastError(std::string(function) + ": " + e.what());
  } catch (...) {
    SetLastError(std::string(function) + ": unknown error");
  }
}

inline libwebrtc::string Str(const char* s) { return libwebrtc::string(s ? s : ""); }

inline std::string StdStr(const libwebrtc::string& s) { return s.std_string(); }

int32_t CopyOut(const std::string& value, char* buf, int32_t cap);

}  // namespace rtc_shim

// ---- Handle definitions (opaque to callers) -------------------------------------------------

struct rtc_factory {
  libwebrtc::scoped_refptr<libwebrtc::RTCPeerConnectionFactory> factory;
};

struct rtc_track {
  libwebrtc::scoped_refptr<libwebrtc::RTCMediaTrack> track;
  // Non-null for video tracks; the same object as `track`.
  libwebrtc::scoped_refptr<libwebrtc::RTCVideoTrack> video;
};

struct rtc_sender {
  libwebrtc::scoped_refptr<libwebrtc::RTCRtpSender> sender;
};

struct rtc_key_provider {
  libwebrtc::scoped_refptr<libwebrtc::KeyProvider> provider;
};

struct rtc_media_source {
  libwebrtc::scoped_refptr<libwebrtc::MediaSource> source;
};

namespace rtc_shim {

class DesktopCaptureObserver;
class FileVideoReader;

// Implemented in shim_file_source_win.cpp (Windows) or shim_file_source_stub.cpp.
FileVideoReader* StartFileVideoReader(scoped_refptr<libwebrtc::RTCVideoSource> source,
                                      const std::string& utf8_path, bool loop,
                                      rtc_capture_state_cb cb, void* user);
// Joins the reader thread; no state callback runs after it returns.
void StopFileVideoReader(FileVideoReader* reader);

struct FileVideoReaderDeleter {
  void operator()(FileVideoReader* reader) const { StopFileVideoReader(reader); }
};

}  // namespace rtc_shim

struct rtc_video_source {
  libwebrtc::scoped_refptr<libwebrtc::RTCVideoSource> source;
  libwebrtc::scoped_refptr<libwebrtc::RTCVideoCapturer> camera;
  // libwebrtc's camera capturer frees its capture module when it stops or fails to start, and
  // then crashes on any further StartCapture/StopCapture. False from then on.
  bool camera_open = false;
  libwebrtc::scoped_refptr<libwebrtc::RTCDesktopCapturer> desktop;
  uint32_t desktop_fps = 0;
  rtc_shim::DesktopCaptureObserver* desktop_observer = nullptr;
  std::unique_ptr<rtc_shim::FileVideoReader, rtc_shim::FileVideoReaderDeleter> file;
};