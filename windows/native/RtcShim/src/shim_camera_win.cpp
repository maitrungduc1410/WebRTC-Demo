// Camera capture through Media Foundation, as the Windows Camera app does. libwebrtc captures only
// through DirectShow, where some drivers deliver no frames at all (Boot Camp's FaceTime HD camera
// at 720p). The camera is opened on a dedicated MTA thread and read asynchronously; each format
// gets a few seconds to deliver a frame before the next one is tried.
#include "shim_mf_win.h"

#include <algorithm>
#include <cctype>
#include <chrono>
#include <condition_variable>
#include <cstdlib>
#include <future>
#include <thread>

using namespace libwebrtc;

namespace rtc_shim {
namespace {

constexpr DWORD kStream = static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM);
constexpr std::chrono::seconds kFirstFrameTimeout(4);
constexpr std::chrono::seconds kFlushTimeout(2);
// Formats tried. With the timeout, it bounds how long a camera that sends nothing blocks creation.
constexpr size_t kMaxModes = 4;
// A driver that sends nothing at one size tends to do so in every format, so smaller sizes get
// their turn.
constexpr size_t kMaxModesPerSize = 2;

struct Mode {
  GUID subtype = GUID_NULL;
  UINT32 width = 0;
  UINT32 height = 0;
  double fps = 0;
};

// The output the reader delivers: one of the subtypes Convert() takes.
struct FrameFormat {
  GUID subtype = GUID_NULL;
  int width = 0;
  int height = 0;
  LONG pitch = 0;  // bytes per row of the first plane in a contiguous buffer
};

bool ConvertsItself(const GUID& subtype) {
  return subtype == MFVideoFormat_NV12 || subtype == MFVideoFormat_YUY2 ||
         subtype == MFVideoFormat_I420 || subtype == MFVideoFormat_IYUV;
}

int SubtypeRank(const GUID& subtype) {
  if (subtype == MFVideoFormat_NV12) return 0;
  if (subtype == MFVideoFormat_YUY2) return 1;
  if (subtype == MFVideoFormat_I420 || subtype == MFVideoFormat_IYUV) return 2;
  if (subtype == MFVideoFormat_MJPG) return 3;
  return 4;
}

std::string SubtypeName(const GUID& subtype) {
  if (subtype == MFVideoFormat_RGB24) return "RGB24";
  if (subtype == MFVideoFormat_RGB32) return "RGB32";
  // The other video subtypes are FOURCCs in Data1.
  std::string fourcc;
  for (int i = 0; i < 4; ++i) {
    const char c = static_cast<char>((subtype.Data1 >> (8 * i)) & 0xFF);
    if (!std::isprint(static_cast<unsigned char>(c))) return "other";
    fourcc += c;
  }
  return fourcc;
}

std::string Describe(const Mode& mode) {
  char text[96];
  std::snprintf(text, sizeof(text), "%s %ux%u@%.4g", SubtypeName(mode.subtype).c_str(), mode.width,
                mode.height, mode.fps);
  return text;
}

bool ReadMode(IMFMediaType* type, Mode* mode) {
  GUID major = GUID_NULL;
  if (FAILED(type->GetGUID(MF_MT_MAJOR_TYPE, &major)) || major != MFMediaType_Video ||
      FAILED(type->GetGUID(MF_MT_SUBTYPE, &mode->subtype)) ||
      FAILED(MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &mode->width, &mode->height)) ||
      mode->width < 2 || mode->height < 2) {
    return false;
  }
  UINT32 numerator = 0;
  UINT32 denominator = 0;
  mode->fps = SUCCEEDED(MFGetAttributeRatio(type, MF_MT_FRAME_RATE, &numerator, &denominator)) &&
                      denominator != 0
                  ? static_cast<double>(numerator) / denominator
                  : 0;
  return true;
}

bool SameMode(const Mode& a, const Mode& b) {
  return a.subtype == b.subtype && a.width == b.width && a.height == b.height &&
         std::abs(a.fps - b.fps) < 0.01;
}

// Closest size first, then frame rate (lower than asked costs more), then the cheapest format to
// convert; one frame rate per size and format, and at most kMaxModesPerSize formats per size.
std::vector<Mode> PickModes(std::vector<Mode> modes, int width, int height, int fps) {
  auto size_cost = [&](const Mode& m) {
    return std::abs(static_cast<int>(m.width) - width) +
           std::abs(static_cast<int>(m.height) - height);
  };
  auto fps_cost = [&](const Mode& m) {
    if (m.fps <= 0) return 1000.0;
    return m.fps >= fps ? m.fps - fps : (fps - m.fps) * 2;
  };
  std::stable_sort(modes.begin(), modes.end(), [&](const Mode& a, const Mode& b) {
    if (size_cost(a) != size_cost(b)) return size_cost(a) < size_cost(b);
    if (fps_cost(a) != fps_cost(b)) return fps_cost(a) < fps_cost(b);
    return SubtypeRank(a.subtype) < SubtypeRank(b.subtype);
  });
  std::vector<Mode> picked;
  for (const Mode& mode : modes) {
    const auto same_size = [&](const Mode& p) {
      return p.width == mode.width && p.height == mode.height;
    };
    const bool seen = std::any_of(picked.begin(), picked.end(), [&](const Mode& p) {
      return same_size(p) && p.subtype == mode.subtype;
    });
    const auto size_count =
        static_cast<size_t>(std::count_if(picked.begin(), picked.end(), same_size));
    if (!seen && size_count < kMaxModesPerSize) picked.push_back(mode);
    if (picked.size() == kMaxModes) break;
  }
  return picked;
}

std::string Lower(std::string text) {
  for (char& c : text) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
  return text;
}

// DirectShow and Media Foundation list a camera under different interface classes:
// "\\?\usb#vid_05ac&pid_8514&mi_00#7&4978da4&0&0000#{65e8773d-...}\global" is the same device as
// "...&0&0000#{e5323777-...}\global". The part before "#{" names the device.
std::string DeviceInstance(const std::string& path) {
  const size_t brace = path.rfind("#{");
  return brace == std::string::npos ? std::string() : Lower(path.substr(0, brace));
}

std::string AllocatedString(IMFActivate* device, REFGUID key) {
  WCHAR* value = nullptr;
  UINT32 length = 0;
  if (FAILED(device->GetAllocatedString(key, &value, &length))) return std::string();
  std::string utf8 = Narrow(value);
  CoTaskMemFree(value);
  return utf8;
}

class ReadCallback;

}  // namespace

class CameraReader {
 public:
  CameraReader(scoped_refptr<RTCVideoSource> source, std::string device_path, std::string name,
               int width, int height, int fps)
      : source_(std::move(source)),
        device_path_(std::move(device_path)),
        name_(std::move(name)),
        width_(width > 0 ? width : 1280),
        height_(height > 0 ? height : 720),
        fps_(fps > 0 ? fps : 30) {}

  bool Start(std::string* error) {
    std::promise<std::string> started;
    std::future<std::string> result = started.get_future();
    thread_ = std::thread([this, &started] { Run(&started); });
    *error = result.get();
    if (error->empty()) return true;
    thread_.join();
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

  bool Alive() {
    std::lock_guard<std::mutex> lock(mutex_);
    return SUCCEEDED(failure_);
  }

  // ReadCallback calls these on Media Foundation's threads, one at a time.
  void OnReadSample(HRESULT status, DWORD flags, IMFSample* sample);
  void OnReadFailed(HRESULT hr) { Fail(hr); }
  void OnFlush() {
    {
      std::lock_guard<std::mutex> lock(mutex_);
      flushed_ = true;
    }
    wake_.notify_all();
  }

 private:
  void Run(std::promise<std::string>* started);
  std::string Open();
  std::string FindCamera();
  std::string OpenReader();
  std::string ListModes(std::vector<Mode>* modes);
  std::string TryMode(const Mode& mode);
  bool ReadFormat();
  bool Convert(IMFSample* sample);
  void Fail(HRESULT hr);
  void CloseReader();

  const scoped_refptr<RTCVideoSource> source_;
  const std::string device_path_;
  const std::string name_;
  const int width_;
  const int height_;
  const int fps_;

  std::thread thread_;
  std::mutex mutex_;
  std::condition_variable wake_;
  bool stop_ = false;
  bool flushed_ = false;
  int64_t frames_ = 0;
  HRESULT failure_ = S_OK;

  std::wstring link_;
  Com<IMFMediaSource> media_;
  Com<IMFSourceReader> reader_;
  ReadCallback* callback_ = nullptr;
  std::atomic<bool> reading_{false};
  // Only touched by the reading callback (and by the thread before the first read).
  FrameFormat format_;
  std::vector<uint8_t> y_, u_, v_;
};

namespace {

// Forwards the source reader's callbacks to the CameraReader until Detach().
class ReadCallback final : public IMFSourceReaderCallback {
 public:
  explicit ReadCallback(CameraReader* owner) : owner_(owner) {}

  STDMETHODIMP QueryInterface(REFIID iid, void** out) override {
    if (!out) return E_POINTER;
    if (iid == IID_IUnknown || iid == IID_IMFSourceReaderCallback) {
      *out = static_cast<IMFSourceReaderCallback*>(this);
      AddRef();
      return S_OK;
    }
    *out = nullptr;
    return E_NOINTERFACE;
  }
  STDMETHODIMP_(ULONG) AddRef() override { return ++refs_; }
  STDMETHODIMP_(ULONG) Release() override {
    const ULONG refs = --refs_;
    if (refs == 0) delete this;
    return refs;
  }

  STDMETHODIMP OnReadSample(HRESULT status, DWORD, DWORD flags, LONGLONG,
                            IMFSample* sample) override {
    std::lock_guard<std::mutex> lock(mutex_);
    try {
      if (owner_) owner_->OnReadSample(status, flags, sample);
    } catch (...) {  // nothing may unwind into Media Foundation
      if (owner_) owner_->OnReadFailed(E_OUTOFMEMORY);
    }
    return S_OK;
  }
  STDMETHODIMP OnFlush(DWORD) override {
    std::lock_guard<std::mutex> lock(mutex_);
    if (owner_) owner_->OnFlush();
    return S_OK;
  }
  STDMETHODIMP OnEvent(DWORD, IMFMediaEvent*) override { return S_OK; }

  // Waits for a callback in progress; none reaches the owner afterwards.
  void Detach() {
    std::lock_guard<std::mutex> lock(mutex_);
    owner_ = nullptr;
  }

 private:
  ~ReadCallback() = default;

  std::atomic<ULONG> refs_{1};
  std::mutex mutex_;
  CameraReader* owner_;
};

}  // namespace

void CameraReader::Run(std::promise<std::string>* started) {
  const HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
  const HRESULT mf = MFStartup(MF_VERSION, MFSTARTUP_LITE);
  std::string error = SUCCEEDED(mf) ? Open() : HrMessage("MFStartup", mf);
  const bool ok = error.empty();
  started->set_value(std::move(error));  // `started` is gone after this line.
  if (ok) {
    HRESULT failure = S_OK;
    {
      std::unique_lock<std::mutex> lock(mutex_);
      wake_.wait(lock, [this] { return stop_ || FAILED(failure_); });
      if (!stop_) failure = failure_;
    }
    if (FAILED(failure)) {
      ShimLog(RTC_LOG_WARNING, "(shim_camera_win.cpp): " + name_ + " stopped: " +
                                   HrMessage("reading frames", failure));
    }
  }
  CloseReader();
  if (SUCCEEDED(mf)) MFShutdown();
  if (SUCCEEDED(com)) CoUninitialize();
}

std::string CameraReader::Open() {
  std::string error = FindCamera();
  if (!error.empty()) return error;
  std::vector<Mode> modes;
  error = ListModes(&modes);
  if (!error.empty()) return error;

  std::string tried;
  for (const Mode& mode : modes) {
    error = TryMode(mode);
    if (error.empty()) {
      ShimLog(RTC_LOG_INFO, "(shim_camera_win.cpp): " + name_ + " through Media Foundation, " +
                                Describe(mode));
      return std::string();
    }
    CloseReader();
    ShimLog(RTC_LOG_INFO,
            "(shim_camera_win.cpp): " + name_ + ", " + Describe(mode) + ": " + error);
    tried += (tried.empty() ? "" : ", ") + Describe(mode) + ": " + error;
  }
  return "no format delivered frames (" + tried + ")";
}

std::string CameraReader::FindCamera() {
  Com<IMFAttributes> attributes;
  HRESULT hr = MFCreateAttributes(attributes.Put(), 1);
  if (FAILED(hr)) return HrMessage("MFCreateAttributes", hr);
  attributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
                      MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
  IMFActivate** devices = nullptr;
  UINT32 count = 0;
  hr = MFEnumDeviceSources(attributes.Get(), &devices, &count);
  if (FAILED(hr)) return HrMessage("listing cameras", hr);

  const std::string instance = DeviceInstance(device_path_);
  std::string by_path;
  std::string by_name;
  int named = 0;
  for (UINT32 i = 0; i < count; ++i) {
    const std::string link =
        AllocatedString(devices[i], MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK);
    if (!instance.empty() && DeviceInstance(link) == instance) by_path = link;
    if (AllocatedString(devices[i], MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME) == name_) {
      by_name = link;
      ++named;
    }
    devices[i]->Release();
  }
  CoTaskMemFree(devices);

  // A name is only trusted when no other camera has it.
  const std::string link = !by_path.empty() ? by_path : named == 1 ? by_name : std::string();
  if (link.empty()) return "Media Foundation doesn't list this camera";
  link_ = Widen(link);
  return std::string();
}

std::string CameraReader::OpenReader() {
  Com<IMFAttributes> device;
  HRESULT hr = MFCreateAttributes(device.Put(), 2);
  if (FAILED(hr)) return HrMessage("MFCreateAttributes", hr);
  device->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
                  MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
  device->SetString(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, link_.c_str());
  hr = MFCreateDeviceSource(device.Get(), media_.Put());
  if (FAILED(hr)) return HrMessage("opening the camera", hr);

  Com<IMFAttributes> options;
  hr = MFCreateAttributes(options.Put(), 2);
  if (FAILED(hr)) return HrMessage("MFCreateAttributes", hr);
  callback_ = new ReadCallback(this);
  options->SetUnknown(MF_SOURCE_READER_ASYNC_CALLBACK, callback_);
  // Decodes MJPG and converts the formats Convert() doesn't take to NV12.
  options->SetUINT32(MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, TRUE);
  hr = MFCreateSourceReaderFromMediaSource(media_.Get(), options.Get(), reader_.Put());
  if (FAILED(hr)) return HrMessage("MFCreateSourceReaderFromMediaSource", hr);
  return std::string();
}

std::string CameraReader::ListModes(std::vector<Mode>* modes) {
  std::string error = OpenReader();
  if (!error.empty()) return error;
  std::vector<Mode> all;
  for (DWORD i = 0;; ++i) {
    Com<IMFMediaType> type;
    if (FAILED(reader_->GetNativeMediaType(kStream, i, type.Put()))) break;
    Mode mode;
    if (ReadMode(type.Get(), &mode)) all.push_back(mode);
  }
  if (all.empty()) return "the camera lists no video format";
  *modes = PickModes(std::move(all), width_, height_, fps_);
  return std::string();
}

std::string CameraReader::TryMode(const Mode& mode) {
  if (!reader_) {
    std::string error = OpenReader();
    if (!error.empty()) return error;
  }
  Com<IMFMediaType> native;
  for (DWORD i = 0;; ++i) {
    Com<IMFMediaType> type;
    if (FAILED(reader_->GetNativeMediaType(kStream, i, type.Put()))) break;
    Mode candidate;
    if (ReadMode(type.Get(), &candidate) && SameMode(candidate, mode)) {
      *native.Put() = type.Get();
      native->AddRef();
      break;
    }
  }
  if (!native) return "no longer offered";
  HRESULT hr = reader_->SetCurrentMediaType(kStream, nullptr, native.Get());
  if (FAILED(hr)) return HrMessage("selecting the format", hr);
  if (!ConvertsItself(mode.subtype)) {
    Com<IMFMediaType> nv12;
    hr = MFCreateMediaType(nv12.Put());
    if (FAILED(hr)) return HrMessage("MFCreateMediaType", hr);
    nv12->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    nv12->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    MFSetAttributeSize(nv12.Get(), MF_MT_FRAME_SIZE, mode.width, mode.height);
    hr = reader_->SetCurrentMediaType(kStream, nullptr, nv12.Get());
    if (FAILED(hr)) return HrMessage("converting to NV12", hr);
  }
  if (!ReadFormat()) return "unsupported output format";

  {
    std::lock_guard<std::mutex> lock(mutex_);
    frames_ = 0;
    failure_ = S_OK;
  }
  reading_ = true;
  hr = reader_->ReadSample(kStream, 0, nullptr, nullptr, nullptr, nullptr);
  if (FAILED(hr)) return HrMessage("ReadSample", hr);

  std::unique_lock<std::mutex> lock(mutex_);
  wake_.wait_for(lock, kFirstFrameTimeout, [this] { return frames_ > 0 || FAILED(failure_); });
  if (frames_ > 0) return std::string();
  if (FAILED(failure_)) return HrMessage("reading frames", failure_);
  return "no frame in " + std::to_string(kFirstFrameTimeout.count()) + " s";
}

bool CameraReader::ReadFormat() {
  Com<IMFMediaType> type;
  if (FAILED(reader_->GetCurrentMediaType(kStream, type.Put()))) return false;
  Mode mode;
  if (!ReadMode(type.Get(), &mode) || !ConvertsItself(mode.subtype)) return false;
  FrameFormat format;
  format.subtype = mode.subtype;
  format.width = static_cast<int>(mode.width);
  format.height = static_cast<int>(mode.height);
  UINT32 stride = 0;
  if (SUCCEEDED(type->GetUINT32(MF_MT_DEFAULT_STRIDE, &stride))) {
    format.pitch = static_cast<LONG>(static_cast<INT32>(stride));
  } else {
    format.pitch = mode.subtype == MFVideoFormat_YUY2 ? format.width * 2 : format.width;
  }
  format_ = format;
  return true;
}

void CameraReader::OnReadSample(HRESULT status, DWORD flags, IMFSample* sample) {
  if (FAILED(status) || (flags & (MF_SOURCE_READERF_ERROR | MF_SOURCE_READERF_ENDOFSTREAM))) {
    Fail(FAILED(status) ? status : MF_E_END_OF_STREAM);
    return;
  }
  if ((flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) && !ReadFormat()) {
    Fail(MF_E_INVALIDMEDIATYPE);
    return;
  }
  if (sample && Convert(sample)) {
    bool first = false;
    {
      std::lock_guard<std::mutex> lock(mutex_);
      first = frames_++ == 0;
    }
    if (first) wake_.notify_all();
  }
  if (!reading_) return;
  const HRESULT hr = reader_->ReadSample(kStream, 0, nullptr, nullptr, nullptr, nullptr);
  if (FAILED(hr)) Fail(hr);
}

void CameraReader::Fail(HRESULT hr) {
  {
    std::lock_guard<std::mutex> lock(mutex_);
    if (SUCCEEDED(failure_)) failure_ = hr;
  }
  wake_.notify_all();
}

bool CameraReader::Convert(IMFSample* sample) {
  Com<IMFMediaBuffer> buffer;
  if (FAILED(sample->ConvertToContiguousBuffer(buffer.Put()))) return false;
  const FrameFormat& f = format_;
  const bool yuy2 = f.subtype == MFVideoFormat_YUY2;
  const int w = f.width & ~1;
  const int h = f.height & ~1;
  const int cw = w / 2;
  const int ch = h / 2;

  BYTE* data = nullptr;
  LONG pitch = 0;
  DWORD length = 0;  // bytes readable from `data`
  Com<IMF2DBuffer2> buffer2d;
  BYTE* start = nullptr;
  if (SUCCEEDED(buffer->QueryInterface(IID_PPV_ARGS(buffer2d.Put()))) &&
      SUCCEEDED(buffer2d->Lock2DSize(MF2DBuffer_LockFlags_Read, &data, &pitch, &start, &length))) {
    length -= static_cast<DWORD>(data - start);
  } else {
    buffer2d.Reset();
    if (FAILED(buffer->Lock(&data, nullptr, &length))) return false;
    pitch = f.pitch;
  }
  auto unlock = [&] {
    if (buffer2d) buffer2d->Unlock2D(); else buffer->Unlock();
  };
  // Offset just past the last byte read below. Bottom-up rows (a negative pitch) only come with
  // RGB, which the reader converts.
  int64_t needed = 0;
  const int64_t p = pitch;
  if (yuy2) {
    needed = (h - 1) * p + w * 2;
  } else if (f.subtype == MFVideoFormat_NV12) {
    needed = (f.height + ch - 1) * p + w;
  } else {
    needed = f.height * p + (f.height / 2 + ch - 1) * (p / 2) + cw;
  }
  if (pitch <= 0 || static_cast<int64_t>(length) < needed) {
    unlock();
    return false;
  }

  if (f.subtype == MFVideoFormat_NV12) {
    CopyNv12ToI420(data, data + static_cast<size_t>(f.height) * pitch, pitch, 0, 0, w, h, &y_,
                   &u_, &v_);
  } else {
    y_.resize(static_cast<size_t>(w) * h);
    u_.resize(static_cast<size_t>(cw) * ch);
    v_.resize(static_cast<size_t>(cw) * ch);
    if (yuy2) {
      // Y0 U Y1 V per pixel pair; chroma of each row pair averaged.
      for (int row = 0; row < h; ++row) {
        const BYTE* line = data + static_cast<size_t>(row) * pitch;
        uint8_t* y = &y_[static_cast<size_t>(row) * w];
        for (int col = 0; col < w; ++col) y[col] = line[col * 2];
      }
      for (int row = 0; row < ch; ++row) {
        const BYTE* top = data + static_cast<size_t>(row * 2) * pitch;
        const BYTE* bottom = top + pitch;
        uint8_t* u = &u_[static_cast<size_t>(row) * cw];
        uint8_t* v = &v_[static_cast<size_t>(row) * cw];
        for (int col = 0; col < cw; ++col) {
          u[col] = static_cast<uint8_t>((top[col * 4 + 1] + bottom[col * 4 + 1] + 1) / 2);
          v[col] = static_cast<uint8_t>((top[col * 4 + 3] + bottom[col * 4 + 3] + 1) / 2);
        }
      }
    } else {
      // I420: the U then V planes follow Y at half the pitch.
      const LONG chroma_pitch = pitch / 2;
      const BYTE* src_u = data + static_cast<size_t>(f.height) * pitch;
      const BYTE* src_v = src_u + static_cast<size_t>(f.height / 2) * chroma_pitch;
      for (int row = 0; row < h; ++row) {
        std::memcpy(&y_[static_cast<size_t>(row) * w], data + static_cast<size_t>(row) * pitch,
                    static_cast<size_t>(w));
      }
      for (int row = 0; row < ch; ++row) {
        std::memcpy(&u_[static_cast<size_t>(row) * cw],
                    src_u + static_cast<size_t>(row) * chroma_pitch, static_cast<size_t>(cw));
        std::memcpy(&v_[static_cast<size_t>(row) * cw],
                    src_v + static_cast<size_t>(row) * chroma_pitch, static_cast<size_t>(cw));
      }
    }
  }
  unlock();

  scoped_refptr<RTCVideoFrame> frame =
      RTCVideoFrame::Create(w, h, y_.data(), w, u_.data(), cw, v_.data(), cw);
  if (!frame) return false;
  source_->OnCapturedFrame(frame);
  return true;
}

void CameraReader::CloseReader() {
  reading_ = false;
  if (reader_) {
    {
      std::lock_guard<std::mutex> lock(mutex_);
      flushed_ = false;
    }
    if (SUCCEEDED(reader_->Flush(kStream))) {
      std::unique_lock<std::mutex> lock(mutex_);
      wake_.wait_for(lock, kFlushTimeout, [this] { return flushed_; });
    }
  }
  if (callback_) callback_->Detach();
  reader_.Reset();
  if (media_) media_->Shutdown();
  media_.Reset();
  if (callback_) callback_->Release();
  callback_ = nullptr;
}

CameraReader* StartCameraReader(scoped_refptr<RTCVideoSource> source,
                                const std::string& device_path, const std::string& name,
                                int width, int height, int fps, std::string* error) {
  auto reader =
      std::make_unique<CameraReader>(std::move(source), device_path, name, width, height, fps);
  return reader->Start(error) ? reader.release() : nullptr;
}

bool CameraReaderAlive(CameraReader* reader) { return reader && reader->Alive(); }

void StopCameraReader(CameraReader* reader) {
  if (!reader) return;
  reader->Stop();
  delete reader;
}

}  // namespace rtc_shim