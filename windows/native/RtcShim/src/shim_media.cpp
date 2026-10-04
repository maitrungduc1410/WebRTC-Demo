#include "shim_internal.h"

using namespace libwebrtc;
using namespace rtc_shim;

namespace rtc_shim {

// Reports desktop capture state. libwebrtc calls it on its signaling thread (blocking the capture
// thread), so Deactivate() waits for a callback in progress.
class DesktopCaptureObserver : public libwebrtc::DesktopCapturerObserver {
 public:
  DesktopCaptureObserver(rtc_capture_state_cb cb, void* user) : cb_(cb), user_(user) {}

  void OnStart(scoped_refptr<RTCDesktopCapturer>) override { Report(RTC_CAPTURE_RUNNING); }
  void OnPaused(scoped_refptr<RTCDesktopCapturer>) override { Report(RTC_CAPTURE_PAUSED); }
  void OnStop(scoped_refptr<RTCDesktopCapturer>) override { Report(RTC_CAPTURE_STOPPED); }
  void OnError(scoped_refptr<RTCDesktopCapturer>) override { Report(RTC_CAPTURE_FAILED); }

  void Deactivate() {
    std::lock_guard<std::mutex> lock(mutex_);
    active_ = false;
  }

 private:
  void Report(int32_t state) {
    std::lock_guard<std::mutex> lock(mutex_);
    if (active_ && cb_ && state != last_) {
      last_ = state;
      cb_(user_, state);
    }
  }

  rtc_capture_state_cb cb_;
  void* user_;
  std::mutex mutex_;
  bool active_ = true;
  int32_t last_ = -1;
};

namespace {

class VideoSink : public RTCVideoRenderer<scoped_refptr<RTCVideoFrame>> {
 public:
  VideoSink(scoped_refptr<RTCVideoTrack> track, int32_t max_width, int32_t max_height,
            rtc_video_frame_cb cb, void* user)
      : track_(std::move(track)), cb_(cb), user_(user) {
    SetMaxSize(max_width, max_height);
  }

  void SetMaxSize(int32_t max_width, int32_t max_height) {
    max_width_.store(std::max(0, max_width));
    max_height_.store(std::max(0, max_height));
  }

  void Attach() { track_->AddRenderer(this); }

  // RemoveRenderer takes the lock libwebrtc holds while delivering a frame, so no OnFrame runs
  // after it returns.
  void Detach() { track_->RemoveRenderer(this); }

  void OnFrame(scoped_refptr<RTCVideoFrame> frame) override {
    if (!frame || !cb_) return;
    const int rotation = static_cast<int>(frame->rotation());
    const bool swap = rotation == 90 || rotation == 270;
    int width = swap ? frame->height() : frame->width();
    int height = swap ? frame->width() : frame->height();
    if (width <= 0 || height <= 0) return;

    const int max_w = max_width_.load();
    const int max_h = max_height_.load();
    double scale = 1.0;
    if (max_w > 0 && width > max_w) scale = std::min(scale, static_cast<double>(max_w) / width);
    if (max_h > 0 && height > max_h) scale = std::min(scale, static_cast<double>(max_h) / height);
    if (scale < 1.0) {
      width = std::max(2, static_cast<int>(width * scale) & ~1);
      height = std::max(2, static_cast<int>(height * scale) & ~1);
    }

    // ConvertToARGB rotates and scales into a tightly packed buffer (it ignores the stride
    // argument). libyuv "ARGB" is B, G, R, A in memory, which is DXGI B8G8R8A8.
    const int stride = width * 4;
    buffer_.resize(static_cast<size_t>(stride) * height);
    frame->ConvertToARGB(RTCVideoFrame::Type::kARGB, buffer_.data(), stride, width, height);
    cb_(user_, buffer_.data(), width, height, stride, rotation);
  }

 private:
  scoped_refptr<RTCVideoTrack> track_;
  rtc_video_frame_cb cb_;
  void* user_;
  std::atomic<int> max_width_{0};
  std::atomic<int> max_height_{0};
  std::vector<uint8_t> buffer_;
};

class DesktopListObserver : public MediaListObserver {
 public:
  DesktopListObserver(rtc_desktop_list_cb cb, void* user) : cb_(cb), user_(user) {}

  void OnMediaSourceAdded(scoped_refptr<MediaSource> source) override {
    Report(RTC_DESKTOP_SOURCE_ADDED, source);
  }
  void OnMediaSourceRemoved(scoped_refptr<MediaSource> source) override {
    Report(RTC_DESKTOP_SOURCE_REMOVED, source);
  }
  void OnMediaSourceNameChanged(scoped_refptr<MediaSource> source) override {
    Report(RTC_DESKTOP_SOURCE_NAME_CHANGED, source);
  }
  void OnMediaSourceThumbnailChanged(scoped_refptr<MediaSource> source) override {
    Report(RTC_DESKTOP_SOURCE_THUMBNAIL_CHANGED, source);
  }

  void Deactivate() {
    std::lock_guard<std::mutex> lock(mutex_);
    active_ = false;
  }

 private:
  void Report(int32_t event, const scoped_refptr<MediaSource>& source) {
    if (!source) return;
    const std::string id = source->id().std_string();
    std::lock_guard<std::mutex> lock(mutex_);
    if (active_ && cb_) cb_(user_, event, id.c_str());
  }

  rtc_desktop_list_cb cb_;
  void* user_;
  std::mutex mutex_;
  bool active_ = true;
};

}  // namespace
}  // namespace rtc_shim

struct rtc_video_sink {
  std::unique_ptr<VideoSink> sink;
};

struct rtc_desktop_list {
  scoped_refptr<RTCDesktopMediaList> list;
  std::unique_ptr<DesktopListObserver> observer;
};

extern "C" {

// ---- Video sources --------------------------------------------------------------------------

rtc_video_source* RTC_CALL rtc_camera_source_create(rtc_factory* factory,
                                                    uint32_t device_index, uint32_t width,
                                                    uint32_t height, uint32_t fps) {
  return Guard<rtc_video_source*>(nullptr, __func__, [&]() -> rtc_video_source* {
    if (!factory) return nullptr;
    scoped_refptr<RTCVideoDevice> device = factory->factory->GetVideoDevice();
    if (!device || device_index >= device->NumberOfDevices()) {
      SetLastError("rtc_camera_source_create: no such camera");
      return nullptr;
    }
    char name[256] = {0};
    char unique_id[512] = {0};
    device->GetDeviceName(device_index, name, sizeof(name), unique_id, sizeof(unique_id));

    // Media Foundation first (as the Camera app); DirectShow for what it doesn't list or can't read.
    scoped_refptr<RTCVideoSource> mf_source =
        factory->factory->CreateCustomVideoSource(Str("camera"), RTCMediaConstraints::Create());
    if (mf_source) {
      std::string mf_error;
      CameraReader* reader = StartCameraReader(mf_source, unique_id, name, static_cast<int>(width),
                                               static_cast<int>(height), static_cast<int>(fps),
                                               &mf_error);
      if (reader) {
        auto* handle = new rtc_video_source();
        handle->source = mf_source;
        handle->mf_camera.reset(reader);
        handle->media_foundation = true;
        return handle;
      }
      if (!mf_error.empty()) {
        ShimLog(RTC_LOG_WARNING, std::string("(shim_media.cpp): ") + name + ": " + mf_error +
                                     "; capturing through DirectShow");
      }
    }

    scoped_refptr<RTCVideoCapturer> capturer =
        device->Create(name, device_index, width, height, fps);
    if (!capturer) {
      SetLastError("rtc_camera_source_create: the camera could not be opened");
      return nullptr;
    }
    scoped_refptr<RTCVideoSource> source = factory->factory->CreateVideoSource(
        capturer, Str("camera"), RTCMediaConstraints::Create());
    if (!source) return nullptr;
    if (!capturer->CaptureStarted() && !capturer->StartCapture()) {
      SetLastError("rtc_camera_source_create: StartCapture failed");
      return nullptr;
    }
    auto* handle = new rtc_video_source();
    handle->source = source;
    handle->camera = capturer;
    handle->camera_open = true;
    return handle;
  });
}

int32_t RTC_CALL rtc_video_source_set_capturing(rtc_video_source* source, int32_t capturing) {
  return Guard<int32_t>(0, __func__, [&]() -> int32_t {
    if (!source) return 0;
    if (source->media_foundation) {
      // Stopping closes it; like libwebrtc's, it is then not reopened in place.
      if (!capturing) {
        source->mf_camera.reset();
        return 1;
      }
      if (source->mf_camera && !rtc_shim::CameraReaderAlive(source->mf_camera.get())) {
        source->mf_camera.reset();  // the caller opens the camera again
      }
      return source->mf_camera ? 1 : 0;
    }
    if (source->camera) {
      if (!capturing) {
        if (source->camera_open) source->camera->StopCapture();
        source->camera_open = false;
        return 1;
      }
      if (!source->camera_open) return 0;
      if (source->camera->CaptureStarted()) return 1;
      source->camera_open = source->camera->StartCapture();
      return source->camera_open ? 1 : 0;
    }
    if (source->desktop) {
      if (!capturing) {
        source->desktop->Stop();
        return 1;
      }
      if (source->desktop->IsRunning()) return 1;
      return source->desktop->Start(source->desktop_fps) == RTCDesktopCapturer::CS_RUNNING ? 1 : 0;
    }
    return 0;
  });
}

rtc_video_source* RTC_CALL rtc_custom_source_create(rtc_factory* factory) {
  return Guard<rtc_video_source*>(nullptr, __func__, [&]() -> rtc_video_source* {
    if (!factory) return nullptr;
    scoped_refptr<RTCVideoSource> source =
        factory->factory->CreateCustomVideoSource(Str("custom"), RTCMediaConstraints::Create());
    if (!source) return nullptr;
    auto* handle = new rtc_video_source();
    handle->source = source;
    return handle;
  });
}

int32_t RTC_CALL rtc_custom_source_push_i420(rtc_video_source* source, int32_t width,
                                             int32_t height, const uint8_t* y, int32_t stride_y,
                                             const uint8_t* u, int32_t stride_u,
                                             const uint8_t* v, int32_t stride_v) {
  return Guard<int32_t>(0, __func__, [&]() -> int32_t {
    if (!source || !source->source || !y || !u || !v || width <= 0 || height <= 0) return 0;
    scoped_refptr<RTCVideoFrame> frame =
        RTCVideoFrame::Create(width, height, y, stride_y, u, stride_u, v, stride_v);
    if (!frame) return 0;
    source->source->OnCapturedFrame(frame);
    return 1;
  });
}

rtc_video_source* RTC_CALL rtc_desktop_source_create(rtc_factory* factory,
                                                     rtc_media_source* media_source,
                                                     uint32_t fps, int32_t show_cursor,
                                                     rtc_capture_state_cb cb, void* user) {
  return Guard<rtc_video_source*>(nullptr, __func__, [&]() -> rtc_video_source* {
    if (!factory || !media_source) return nullptr;
    scoped_refptr<RTCDesktopDevice> device = factory->factory->GetDesktopDevice();
    if (!device) {
      SetLastError("rtc_desktop_source_create: desktop capture is not available");
      return nullptr;
    }
    scoped_refptr<RTCDesktopCapturer> capturer =
        device->CreateDesktopCapturer(media_source->source, show_cursor != 0);
    if (!capturer) {
      SetLastError("rtc_desktop_source_create: CreateDesktopCapturer failed");
      return nullptr;
    }
    auto handle = std::make_unique<rtc_video_source>();
    handle->desktop_observer = new DesktopCaptureObserver(cb, user);
    capturer->RegisterDesktopCapturerObserver(handle->desktop_observer);
    handle->source = factory->factory->CreateDesktopSource(capturer, Str("screen"),
                                                           RTCMediaConstraints::Create());
    handle->desktop = capturer;
    handle->desktop_fps = fps == 0 ? 15 : fps;
    if (!handle->source ||
        capturer->Start(handle->desktop_fps) == RTCDesktopCapturer::CS_FAILED) {
      SetLastError("rtc_desktop_source_create: capture failed to start");
      rtc_video_source_release(handle.release());
      return nullptr;
    }
    return handle.release();
  });
}

rtc_video_source* RTC_CALL rtc_file_source_create(rtc_factory* factory, const char* utf8_path,
                                                  int32_t loop, rtc_capture_state_cb cb,
                                                  void* user) {
  return Guard<rtc_video_source*>(nullptr, __func__, [&]() -> rtc_video_source* {
    if (!factory || !utf8_path) return nullptr;
    scoped_refptr<RTCVideoSource> source =
        factory->factory->CreateCustomVideoSource(Str("file"), RTCMediaConstraints::Create());
    if (!source) return nullptr;
    FileVideoReader* reader = StartFileVideoReader(source, utf8_path, loop != 0, cb, user);
    if (!reader) return nullptr;
    auto* handle = new rtc_video_source();
    handle->source = source;
    handle->file.reset(reader);
    return handle;
  });
}

void RTC_CALL rtc_video_source_release(rtc_video_source* source) {
  GuardVoid(__func__, [&] {
    if (!source) return;
    source->file.reset();
    source->mf_camera.reset();
    if (source->desktop) {
      if (source->desktop_observer) {
        source->desktop_observer->Deactivate();
        source->desktop->DeRegisterDesktopCapturerObserver();
      }
      source->desktop->Stop();
    }
    if (source->camera && source->camera_open) source->camera->StopCapture();
    // The observer is not deleted: the capturer may still be inside a BlockingCall that read the
    // observer pointer before DeRegister. Deactivated, it is inert.
    delete source;
  });
}

// ---- Video sinks ----------------------------------------------------------------------------

rtc_video_sink* RTC_CALL rtc_video_sink_create(rtc_track* video_track, int32_t max_width,
                                               int32_t max_height, rtc_video_frame_cb cb,
                                               void* user) {
  return Guard<rtc_video_sink*>(nullptr, __func__, [&]() -> rtc_video_sink* {
    if (!video_track || !video_track->video || !cb) return nullptr;
    auto handle = std::make_unique<rtc_video_sink>();
    handle->sink =
        std::make_unique<VideoSink>(video_track->video, max_width, max_height, cb, user);
    handle->sink->Attach();
    return handle.release();
  });
}

void RTC_CALL rtc_video_sink_set_max_size(rtc_video_sink* sink, int32_t max_width,
                                          int32_t max_height) {
  if (sink) sink->sink->SetMaxSize(max_width, max_height);
}

void RTC_CALL rtc_video_sink_release(rtc_video_sink* sink) {
  GuardVoid(__func__, [&] {
    if (!sink) return;
    sink->sink->Detach();
    delete sink;
  });
}

// ---- Screen / window sources ----------------------------------------------------------------

rtc_desktop_list* RTC_CALL rtc_desktop_list_create(rtc_factory* factory, int32_t type,
                                                   rtc_desktop_list_cb cb, void* user) {
  return Guard<rtc_desktop_list*>(nullptr, __func__, [&]() -> rtc_desktop_list* {
    if (!factory) return nullptr;
    scoped_refptr<RTCDesktopDevice> device = factory->factory->GetDesktopDevice();
    if (!device) {
      SetLastError("rtc_desktop_list_create: desktop capture is not available");
      return nullptr;
    }
    scoped_refptr<RTCDesktopMediaList> list =
        device->GetDesktopMediaList(type == RTC_DESKTOP_WINDOW ? kWindow : kScreen);
    if (!list) return nullptr;
    auto handle = std::make_unique<rtc_desktop_list>();
    handle->list = list;
    if (cb) {
      handle->observer = std::make_unique<DesktopListObserver>(cb, user);
      list->RegisterMediaListObserver(handle->observer.get());
    }
    return handle.release();
  });
}

int32_t RTC_CALL rtc_desktop_list_update(rtc_desktop_list* list, int32_t force_reload,
                                         int32_t thumbnails) {
  return Guard<int32_t>(-1, __func__, [&]() -> int32_t {
    if (!list) return -1;
    return list->list->UpdateSourceList(force_reload != 0, thumbnails != 0);
  });
}

rtc_media_source* RTC_CALL rtc_desktop_list_source(rtc_desktop_list* list, int32_t index) {
  return Guard<rtc_media_source*>(nullptr, __func__, [&]() -> rtc_media_source* {
    if (!list || index < 0 || index >= list->list->GetSourceCount()) return nullptr;
    scoped_refptr<MediaSource> source = list->list->GetSource(index);
    return source ? new rtc_media_source{source} : nullptr;
  });
}

void RTC_CALL rtc_desktop_list_release(rtc_desktop_list* list) {
  GuardVoid(__func__, [&] {
    if (!list) return;
    if (list->observer) {
      list->observer->Deactivate();
      list->list->DeRegisterMediaListObserver();
      // Thumbnail tasks may still be queued on the list's thread and read the observer pointer
      // they captured; keep the inert observer alive rather than risk a dangling call.
      list->observer.release();
    }
    delete list;
  });
}

int32_t RTC_CALL rtc_media_source_id(rtc_media_source* source, char* buf, int32_t cap) {
  return Guard<int32_t>(0, __func__, [&]() -> int32_t {
    return CopyOut(source ? source->source->id().std_string() : std::string(), buf, cap);
  });
}

int32_t RTC_CALL rtc_media_source_name(rtc_media_source* source, char* buf, int32_t cap) {
  return Guard<int32_t>(0, __func__, [&]() -> int32_t {
    return CopyOut(source ? source->source->name().std_string() : std::string(), buf, cap);
  });
}

int32_t RTC_CALL rtc_media_source_type(rtc_media_source* source) {
  return Guard<int32_t>(RTC_DESKTOP_SCREEN, __func__, [&]() -> int32_t {
    return source && source->source->type() == kWindow ? RTC_DESKTOP_WINDOW : RTC_DESKTOP_SCREEN;
  });
}

int32_t RTC_CALL rtc_media_source_thumbnail(rtc_media_source* source, uint8_t* buf,
                                            int32_t cap) {
  return Guard<int32_t>(0, __func__, [&]() -> int32_t {
    if (!source) return 0;
    const auto thumbnail = source->source->thumbnail();
    const int32_t size = static_cast<int32_t>(thumbnail.size());
    if (buf && cap > 0 && size > 0) {
      std::memcpy(buf, thumbnail.data(), static_cast<size_t>(std::min(size, cap)));
    }
    return size;
  });
}

void RTC_CALL rtc_media_source_release(rtc_media_source* source) {
  GuardVoid(__func__, [&] { delete source; });
}

}  // extern "C"