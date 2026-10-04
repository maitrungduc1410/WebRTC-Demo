#include "shim_internal.h"

using namespace libwebrtc;
using namespace rtc_shim;

namespace rtc_shim {

namespace {
thread_local std::string g_last_error;

std::mutex g_log_mutex;
rtc_log_cb g_log_cb = nullptr;
void* g_log_user = nullptr;
int32_t g_log_min = RTC_LOG_NONE;

void ForwardLog(const libwebrtc::string& message) {
  std::lock_guard<std::mutex> lock(g_log_mutex);
  if (g_log_cb) g_log_cb(g_log_user, message.c_string());
}

RTCLoggingSeverity ToSeverity(int32_t severity) {
  switch (severity) {
    case RTC_LOG_VERBOSE:
      return Verbose;
    case RTC_LOG_INFO:
      return Info;
    case RTC_LOG_WARNING:
      return Warning;
    case RTC_LOG_ERROR:
      return Error;
    default:
      return None;
  }
}
}  // namespace

void ShimLog(int32_t severity, const std::string& message) {
  std::lock_guard<std::mutex> lock(g_log_mutex);
  if (g_log_cb && severity >= g_log_min && g_log_min < RTC_LOG_NONE) {
    g_log_cb(g_log_user, message.c_str());
  }
}

void SetLastError(std::string message) { g_last_error = std::move(message); }

void ClearLastError() { g_last_error.clear(); }

int32_t CopyOut(const std::string& value, char* buf, int32_t cap) {
  if (buf && cap > 0) {
    const size_t n = std::min(value.size(), static_cast<size_t>(cap - 1));
    std::memcpy(buf, value.data(), n);
    buf[n] = '\0';
  }
  return static_cast<int32_t>(value.size());
}

}  // namespace rtc_shim

extern "C" {

// ---- Library --------------------------------------------------------------------------------

int32_t RTC_CALL rtc_shim_abi_version(void) { return RTC_SHIM_ABI_VERSION; }

const char* RTC_CALL rtc_shim_libwebrtc_version(void) { return RTC_SHIM_LIBWEBRTC_VERSION; }

const char* RTC_CALL rtc_last_error(void) { return g_last_error.c_str(); }

int32_t RTC_CALL rtc_initialize(void) {
  return Guard<int32_t>(0, __func__, [] { return LibWebRTC::Initialize() ? 1 : 0; });
}

void RTC_CALL rtc_terminate(void) {
  GuardVoid(__func__, [] { LibWebRTC::Terminate(); });
}

void RTC_CALL rtc_set_log_callback(int32_t min_severity, rtc_log_cb cb, void* user) {
  GuardVoid(__func__, [&] {
    {
      std::lock_guard<std::mutex> lock(g_log_mutex);
      g_log_cb = cb;
      g_log_user = user;
      g_log_min = min_severity;
    }
    if (cb) {
      LibWebRTCLogging::setLogSink(ToSeverity(min_severity), &ForwardLog);
    } else {
      LibWebRTCLogging::removeLogSink();
    }
  });
}

// ---- Factory and devices --------------------------------------------------------------------

rtc_factory* RTC_CALL rtc_factory_create(void) {
  return Guard<rtc_factory*>(nullptr, __func__, []() -> rtc_factory* {
    scoped_refptr<RTCPeerConnectionFactory> factory = LibWebRTC::CreateRTCPeerConnectionFactory();
    if (!factory || !factory->Initialize()) {
      SetLastError("rtc_factory_create: factory initialization failed");
      return nullptr;
    }
    return new rtc_factory{factory};
  });
}

void RTC_CALL rtc_factory_release(rtc_factory* factory) {
  GuardVoid(__func__, [&] {
    if (!factory) return;
    if (factory->factory) factory->factory->Terminate();
    delete factory;
  });
}

int32_t RTC_CALL rtc_video_device_count(rtc_factory* factory) {
  return Guard<int32_t>(0, __func__, [&]() -> int32_t {
    if (!factory) return 0;
    scoped_refptr<RTCVideoDevice> device = factory->factory->GetVideoDevice();
    return device ? static_cast<int32_t>(device->NumberOfDevices()) : 0;
  });
}

int32_t RTC_CALL rtc_video_device_info(rtc_factory* factory, uint32_t index, char* name,
                                       int32_t name_cap, char* unique_id,
                                       int32_t unique_id_cap) {
  return Guard<int32_t>(-1, __func__, [&]() -> int32_t {
    if (!factory) return -1;
    scoped_refptr<RTCVideoDevice> device = factory->factory->GetVideoDevice();
    if (!device || index >= device->NumberOfDevices()) return -1;
    char device_name[256] = {0};
    char device_id[256] = {0};
    if (device->GetDeviceName(index, device_name, sizeof(device_name), device_id,
                              sizeof(device_id)) != 0) {
      return -1;
    }
    CopyOut(device_name, name, name_cap);
    CopyOut(device_id, unique_id, unique_id_cap);
    return 0;
  });
}

namespace {

enum class AudioDirection { kRecording, kPlayout };

int32_t AudioDeviceCount(rtc_factory* factory, AudioDirection direction) {
  if (!factory) return 0;
  scoped_refptr<RTCAudioDevice> device = factory->factory->GetAudioDevice();
  if (!device) return 0;
  return direction == AudioDirection::kRecording ? device->RecordingDevices()
                                                 : device->PlayoutDevices();
}

int32_t AudioDeviceInfo(rtc_factory* factory, AudioDirection direction, uint32_t index,
                        char* name, int32_t name_cap, char* guid, int32_t guid_cap) {
  if (!factory) return -1;
  scoped_refptr<RTCAudioDevice> device = factory->factory->GetAudioDevice();
  if (!device) return -1;
  char device_name[RTCAudioDevice::kAdmMaxDeviceNameSize] = {0};
  char device_guid[RTCAudioDevice::kAdmMaxGuidSize] = {0};
  const uint16_t i = static_cast<uint16_t>(index);
  const int32_t result = direction == AudioDirection::kRecording
                             ? device->RecordingDeviceName(i, device_name, device_guid)
                             : device->PlayoutDeviceName(i, device_name, device_guid);
  if (result != 0) return result;
  CopyOut(device_name, name, name_cap);
  CopyOut(device_guid, guid, guid_cap);
  return 0;
}

}  // namespace

int32_t RTC_CALL rtc_audio_recording_device_count(rtc_factory* factory) {
  return Guard<int32_t>(0, __func__,
                        [&] { return AudioDeviceCount(factory, AudioDirection::kRecording); });
}

int32_t RTC_CALL rtc_audio_recording_device_info(rtc_factory* factory, uint32_t index,
                                                 char* name, int32_t name_cap, char* guid,
                                                 int32_t guid_cap) {
  return Guard<int32_t>(-1, __func__, [&] {
    return AudioDeviceInfo(factory, AudioDirection::kRecording, index, name, name_cap, guid,
                           guid_cap);
  });
}

int32_t RTC_CALL rtc_audio_set_recording_device(rtc_factory* factory, uint32_t index) {
  return Guard<int32_t>(-1, __func__, [&]() -> int32_t {
    if (!factory) return -1;
    scoped_refptr<RTCAudioDevice> device = factory->factory->GetAudioDevice();
    return device ? device->SetRecordingDevice(static_cast<uint16_t>(index)) : -1;
  });
}

int32_t RTC_CALL rtc_audio_playout_device_count(rtc_factory* factory) {
  return Guard<int32_t>(0, __func__,
                        [&] { return AudioDeviceCount(factory, AudioDirection::kPlayout); });
}

int32_t RTC_CALL rtc_audio_playout_device_info(rtc_factory* factory, uint32_t index, char* name,
                                               int32_t name_cap, char* guid, int32_t guid_cap) {
  return Guard<int32_t>(-1, __func__, [&] {
    return AudioDeviceInfo(factory, AudioDirection::kPlayout, index, name, name_cap, guid,
                           guid_cap);
  });
}

int32_t RTC_CALL rtc_audio_set_playout_device(rtc_factory* factory, uint32_t index) {
  return Guard<int32_t>(-1, __func__, [&]() -> int32_t {
    if (!factory) return -1;
    scoped_refptr<RTCAudioDevice> device = factory->factory->GetAudioDevice();
    return device ? device->SetPlayoutDevice(static_cast<uint16_t>(index)) : -1;
  });
}

// ---- Tracks ---------------------------------------------------------------------------------

rtc_track* RTC_CALL rtc_audio_track_create(rtc_factory* factory, const char* track_id) {
  return Guard<rtc_track*>(nullptr, __func__, [&]() -> rtc_track* {
    if (!factory) return nullptr;
    RTCAudioOptions options;
    options.echo_cancellation = true;
    options.auto_gain_control = true;
    options.noise_suppression = true;
    scoped_refptr<RTCAudioSource> source = factory->factory->CreateAudioSource(
        Str("microphone"), RTCAudioSource::SourceType::kMicrophone, options);
    if (!source) {
      SetLastError("rtc_audio_track_create: no audio source");
      return nullptr;
    }
    scoped_refptr<RTCAudioTrack> track = factory->factory->CreateAudioTrack(source, Str(track_id));
    if (!track) return nullptr;
    return new rtc_track{track, nullptr};
  });
}

rtc_track* RTC_CALL rtc_video_track_create(rtc_factory* factory, rtc_video_source* source,
                                           const char* track_id) {
  return Guard<rtc_track*>(nullptr, __func__, [&]() -> rtc_track* {
    if (!factory || !source || !source->source) return nullptr;
    scoped_refptr<RTCVideoTrack> track =
        factory->factory->CreateVideoTrack(source->source, Str(track_id));
    if (!track) return nullptr;
    return new rtc_track{track, track};
  });
}

int32_t RTC_CALL rtc_track_kind(rtc_track* track) {
  return track && track->video ? RTC_KIND_VIDEO : RTC_KIND_AUDIO;
}

int32_t RTC_CALL rtc_track_id(rtc_track* track, char* buf, int32_t cap) {
  return Guard<int32_t>(0, __func__, [&]() -> int32_t {
    if (!track) return CopyOut("", buf, cap);
    return CopyOut(StdStr(track->track->id()), buf, cap);
  });
}

int32_t RTC_CALL rtc_track_set_enabled(rtc_track* track, int32_t enabled) {
  return Guard<int32_t>(0, __func__, [&]() -> int32_t {
    if (!track) return 0;
    return track->track->set_enabled(enabled != 0) ? 1 : 0;
  });
}

int32_t RTC_CALL rtc_track_is_enabled(rtc_track* track) {
  return Guard<int32_t>(0, __func__,
                        [&]() -> int32_t { return track && track->track->enabled() ? 1 : 0; });
}

void RTC_CALL rtc_track_release(rtc_track* track) {
  GuardVoid(__func__, [&] { delete track; });
}

// ---- End-to-end encryption ------------------------------------------------------------------

rtc_key_provider* RTC_CALL rtc_key_provider_create(const rtc_key_provider_options* options) {
  return Guard<rtc_key_provider*>(nullptr, __func__, [&]() -> rtc_key_provider* {
    if (!options) return nullptr;
    KeyProviderOptions native;
    native.shared_key = options->shared_key != 0;
    if (options->ratchet_salt && options->ratchet_salt_length > 0) {
      std::vector<uint8_t> salt(options->ratchet_salt,
                                options->ratchet_salt + options->ratchet_salt_length);
      native.ratchet_salt = libwebrtc::vector<uint8_t>(salt);
    }
    if (options->uncrypted_magic_bytes && options->uncrypted_magic_bytes_length > 0) {
      std::vector<uint8_t> magic(
          options->uncrypted_magic_bytes,
          options->uncrypted_magic_bytes + options->uncrypted_magic_bytes_length);
      native.uncrypted_magic_bytes = libwebrtc::vector<uint8_t>(magic);
    }
    native.ratchet_window_size = options->ratchet_window_size;
    native.failure_tolerance = options->failure_tolerance;
    native.key_ring_size = options->key_ring_size;
    native.discard_frame_when_cryptor_not_ready =
        options->discard_frame_when_cryptor_not_ready != 0;
    native.key_derivation_algorithm = options->key_derivation == RTC_KEY_DERIVATION_HKDF
                                          ? KeyDerivationAlgorithm::kHKDF
                                          : KeyDerivationAlgorithm::kPBKDF2;
    scoped_refptr<KeyProvider> provider = KeyProvider::Create(&native);
    if (!provider) return nullptr;
    return new rtc_key_provider{provider};
  });
}

int32_t RTC_CALL rtc_key_provider_set_shared_key(rtc_key_provider* provider, int32_t index,
                                                 const uint8_t* key, int32_t length) {
  return Guard<int32_t>(0, __func__, [&]() -> int32_t {
    if (!provider || !key || length <= 0) return 0;
    std::vector<uint8_t> bytes(key, key + length);
    return provider->provider->SetSharedKey(index, libwebrtc::vector<uint8_t>(bytes)) ? 1 : 0;
  });
}

void RTC_CALL rtc_key_provider_release(rtc_key_provider* provider) {
  GuardVoid(__func__, [&] { delete provider; });
}

}  // extern "C"