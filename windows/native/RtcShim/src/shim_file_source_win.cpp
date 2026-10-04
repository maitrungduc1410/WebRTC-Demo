// Video file share: Media Foundation decodes the file to NV12 on a dedicated MTA thread, the
// frames are converted to I420 and pushed into a custom video source at their own timestamps.
#include "shim_mf_win.h"

#include <chrono>
#include <condition_variable>
#include <future>
#include <thread>

#include <propvarutil.h>

using namespace libwebrtc;

namespace rtc_shim {
namespace {

struct Format {
  int width = 0;   // decoded buffer size
  int height = 0;
  int crop_x = 0;  // visible area inside it
  int crop_y = 0;
  int crop_w = 0;
  int crop_h = 0;
};

bool ReadFormat(IMFSourceReader* reader, Format* format) {
  Com<IMFMediaType> type;
  if (FAILED(reader->GetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, type.Put()))) {
    return false;
  }
  UINT32 width = 0;
  UINT32 height = 0;
  if (FAILED(MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &width, &height)) || width == 0 ||
      height == 0) {
    return false;
  }
  format->width = static_cast<int>(width);
  format->height = static_cast<int>(height);
  format->crop_x = 0;
  format->crop_y = 0;
  format->crop_w = format->width;
  format->crop_h = format->height;

  // Decoders align the buffer (1080 -> 1088); the aperture is the picture without the padding.
  MFVideoArea area{};
  UINT32 size = 0;
  if (SUCCEEDED(type->GetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE, reinterpret_cast<UINT8*>(&area),
                              sizeof(area), &size)) &&
      size == sizeof(area)) {
    const int x = area.OffsetX.value;
    const int y = area.OffsetY.value;
    const int w = static_cast<int>(area.Area.cx);
    const int h = static_cast<int>(area.Area.cy);
    if (x >= 0 && y >= 0 && w > 0 && h > 0 && x + w <= format->width &&
        y + h <= format->height) {
      format->crop_x = x & ~1;
      format->crop_y = y & ~1;
      format->crop_w = w & ~1;
      format->crop_h = h & ~1;
    }
  }
  return format->crop_w >= 2 && format->crop_h >= 2;
}

}  // namespace

class FileVideoReader {
 public:
  FileVideoReader(scoped_refptr<RTCVideoSource> source, std::string path, bool loop,
                  rtc_capture_state_cb cb, void* user)
      : source_(std::move(source)), path_(std::move(path)), loop_(loop), cb_(cb), user_(user) {}

  // Opens the file on the reader thread and waits for the result, so a bad file fails creation.
  bool Start() {
    std::promise<std::string> opened;
    std::future<std::string> result = opened.get_future();
    thread_ = std::thread([this, &opened] { Run(&opened); });
    const std::string error = result.get();
    if (error.empty()) return true;
    thread_.join();
    SetLastError("rtc_file_source_create: " + error);
    return false;
  }

  void Stop() {
    {
      std::lock_guard<std::mutex> lock(mutex_);
      stop_ = true;
    }
    wake_.notify_all();
    if (thread_.joinable()) thread_.join();
  }

 private:
  void Run(std::promise<std::string>* opened) {
    const HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const HRESULT mf = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    std::string error = SUCCEEDED(mf) ? Open() : HrMessage("MFStartup", mf);
    const bool ok = error.empty();
    opened->set_value(std::move(error));  // `opened` is gone after this line.
    if (ok) {
      Report(ReadFrames() ? RTC_CAPTURE_STOPPED : RTC_CAPTURE_FAILED);
    }
    reader_.Reset();
    if (SUCCEEDED(mf)) MFShutdown();
    if (SUCCEEDED(com)) CoUninitialize();
  }

  std::string Open() {
    Com<IMFAttributes> attributes;
    HRESULT hr = MFCreateAttributes(attributes.Put(), 1);
    if (FAILED(hr)) return HrMessage("MFCreateAttributes", hr);
    // Lets the reader convert whatever the decoder produces (and deinterlace) to NV12.
    attributes->SetUINT32(MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, TRUE);

    const std::wstring path = Widen(path_);
    hr = MFCreateSourceReaderFromURL(path.c_str(), attributes.Get(), reader_.Put());
    if (FAILED(hr)) return HrMessage("opening the file", hr);

    reader_->SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS, FALSE);
    hr = reader_->SetStreamSelection(MF_SOURCE_READER_FIRST_VIDEO_STREAM, TRUE);
    if (FAILED(hr)) return "the file has no video stream";

    Com<IMFMediaType> type;
    hr = MFCreateMediaType(type.Put());
    if (FAILED(hr)) return HrMessage("MFCreateMediaType", hr);
    type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    hr = reader_->SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, nullptr, type.Get());
    if (FAILED(hr)) return HrMessage("selecting NV12 output", hr);
    if (!ReadFormat(reader_.Get(), &format_)) return "unsupported video format";
    return std::string();
  }

  // Returns false on a decoding error, true when stopped or at the end of a non-looping file.
  bool ReadFrames() {
    using Clock = std::chrono::steady_clock;
    bool started = false;
    Clock::time_point base_time;
    LONGLONG base_timestamp = 0;
    LONGLONG last_timestamp = 0;
    bool have_base = false;

    while (!Stopping()) {
      DWORD flags = 0;
      LONGLONG timestamp = 0;
      Com<IMFSample> sample;
      HRESULT hr = reader_->ReadSample(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, nullptr, &flags,
                                       &timestamp, sample.Put());
      if (FAILED(hr) || (flags & MF_SOURCE_READERF_ERROR)) return false;
      if (flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) {
        if (!ReadFormat(reader_.Get(), &format_)) return false;
      }
      if (flags & MF_SOURCE_READERF_ENDOFSTREAM) {
        if (!loop_) return true;
        PROPVARIANT position;
        PropVariantInit(&position);
        position.vt = VT_I8;
        position.hVal.QuadPart = 0;
        hr = reader_->SetCurrentPosition(GUID_NULL, position);
        PropVariantClear(&position);
        if (FAILED(hr)) return false;
        // The next loop starts one frame after the last frame of this one.
        base_time += std::chrono::microseconds((last_timestamp - base_timestamp) / 10 + 33000);
        have_base = false;
        continue;
      }
      if (!sample) continue;

      if (!have_base) {
        if (!started) base_time = Clock::now();
        base_timestamp = timestamp;
        have_base = true;
      }
      last_timestamp = timestamp;
      const auto due = base_time + std::chrono::microseconds((timestamp - base_timestamp) / 10);
      {
        std::unique_lock<std::mutex> lock(mutex_);
        if (wake_.wait_until(lock, due, [this] { return stop_; })) return true;
      }
      if (!Push(sample.Get())) return false;
      if (!started) {
        started = true;
        Report(RTC_CAPTURE_RUNNING);
      }
    }
    return true;
  }

  bool Push(IMFSample* sample) {
    Com<IMFMediaBuffer> buffer;
    if (FAILED(sample->ConvertToContiguousBuffer(buffer.Put()))) return false;

    BYTE* data = nullptr;
    LONG pitch = 0;
    Com<IMF2DBuffer> buffer2d;
    bool locked2d = false;
    DWORD length = 0;
    if (SUCCEEDED(buffer->QueryInterface(IID_PPV_ARGS(buffer2d.Put()))) &&
        SUCCEEDED(buffer2d->Lock2D(&data, &pitch))) {
      locked2d = true;
    } else if (SUCCEEDED(buffer->Lock(&data, nullptr, &length))) {
      pitch = format_.width;
      if (length < static_cast<DWORD>(format_.width * format_.height * 3 / 2)) {
        buffer->Unlock();
        return false;
      }
    } else {
      return false;
    }
    if (pitch < 0) {  // bottom-up buffers are not produced for NV12; refuse rather than misread
      if (locked2d) buffer2d->Unlock2D(); else buffer->Unlock();
      return false;
    }

    const Format& f = format_;
    const int w = f.crop_w;
    const int h = f.crop_h;
    const int cw = w / 2;
    CopyNv12ToI420(data, data + static_cast<size_t>(f.height) * pitch, pitch, f.crop_x, f.crop_y,
                   w, h, &y_, &u_, &v_);
    if (locked2d) buffer2d->Unlock2D(); else buffer->Unlock();

    scoped_refptr<RTCVideoFrame> frame =
        RTCVideoFrame::Create(w, h, y_.data(), w, u_.data(), cw, v_.data(), cw);
    if (!frame) return false;
    source_->OnCapturedFrame(frame);
    return true;
  }

  bool Stopping() {
    std::lock_guard<std::mutex> lock(mutex_);
    return stop_;
  }

  void Report(int32_t state) {
    if (cb_ && !(state == RTC_CAPTURE_STOPPED && Stopping())) cb_(user_, state);
  }

  scoped_refptr<RTCVideoSource> source_;
  const std::string path_;
  const bool loop_;
  const rtc_capture_state_cb cb_;
  void* const user_;

  std::thread thread_;
  std::mutex mutex_;
  std::condition_variable wake_;
  bool stop_ = false;

  Com<IMFSourceReader> reader_;
  Format format_;
  std::vector<uint8_t> y_, u_, v_;
};

FileVideoReader* StartFileVideoReader(scoped_refptr<RTCVideoSource> source,
                                      const std::string& utf8_path, bool loop,
                                      rtc_capture_state_cb cb, void* user) {
  auto reader = std::make_unique<FileVideoReader>(std::move(source), utf8_path, loop, cb, user);
  return reader->Start() ? reader.release() : nullptr;
}

void StopFileVideoReader(FileVideoReader* reader) {
  if (!reader) return;
  reader->Stop();
  delete reader;
}

}  // namespace rtc_shim