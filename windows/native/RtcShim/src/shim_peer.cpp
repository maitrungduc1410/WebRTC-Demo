#include <shared_mutex>

#include "shim_internal.h"

using namespace libwebrtc;
using namespace rtc_shim;

namespace rtc_shim {

// Marks the object whose callback the current thread is running, so close/release called from
// inside that callback can tell instead of waiting for itself.
// Callbacks can nest (a callback calling an API that completes synchronously), so the scopes form
// a per-thread stack.
class CallbackScope {
 public:
  explicit CallbackScope(const void* owner) : owner_(owner), outer_(innermost_) {
    innermost_ = this;
  }
  ~CallbackScope() { innermost_ = outer_; }
  CallbackScope(const CallbackScope&) = delete;
  CallbackScope& operator=(const CallbackScope&) = delete;

  static bool Inside(const void* owner) {
    for (const CallbackScope* scope = innermost_; scope; scope = scope->outer_) {
      if (scope->owner_ == owner) return true;
    }
    return false;
  }

 private:
  static thread_local const CallbackScope* innermost_;
  const void* const owner_;
  const CallbackScope* const outer_;
};

thread_local const CallbackScope* CallbackScope::innermost_ = nullptr;

// Forwards data channel events to the C callbacks; Deactivate() waits for a callback in progress
// on another thread, and from inside one of its own callbacks just turns later ones off.
class DataChannelForwarder : public RTCDataChannelObserver {
 public:
  void Activate(const rtc_data_channel_observer& callbacks, void* user) {
    std::lock_guard<std::mutex> lock(mutex_);
    cb_ = callbacks;
    user_ = user;
    active_ = true;
  }

  void Deactivate() {
    if (CallbackScope::Inside(this)) {
      active_ = false;  // This thread already holds mutex_.
      return;
    }
    std::lock_guard<std::mutex> lock(mutex_);
    active_ = false;
  }

  void OnStateChange(RTCDataChannelState state) override {
    std::lock_guard<std::mutex> lock(mutex_);
    CallbackScope scope(this);
    if (active_ && cb_.on_state) cb_.on_state(user_, state);
  }

  void OnMessage(const char* buffer, int length, bool binary) override {
    std::lock_guard<std::mutex> lock(mutex_);
    CallbackScope scope(this);
    if (active_ && cb_.on_message) {
      cb_.on_message(user_, reinterpret_cast<const uint8_t*>(buffer), length, binary ? 1 : 0);
    }
  }

 private:
  std::mutex mutex_;
  rtc_data_channel_observer cb_{};
  void* user_ = nullptr;
  bool active_ = false;
};

}  // namespace rtc_shim

struct rtc_data_channel {
  explicit rtc_data_channel(scoped_refptr<RTCDataChannel> c) : channel(std::move(c)) {}
  scoped_refptr<RTCDataChannel> channel;
  rtc_shim::DataChannelForwarder* forwarder = nullptr;
};

namespace rtc_shim {
namespace {

// The libwebrtc wrapper dereferences its inner connection without a null check once Close() has
// run, so every call goes through this state. Calls from application threads take the lock
// shared; close takes it exclusively. Completion handlers run on the signaling thread and only
// try-lock: blocking there could deadlock against an application thread that holds the lock while
// waiting for the signaling thread.
struct PcState {
  std::shared_mutex mutex;
  bool closed = false;
  scoped_refptr<RTCPeerConnection> pc;
};

class PcGuard {
 public:
  explicit PcGuard(PcState& state) : lock_(state.mutex) {
    pc_ = state.closed ? nullptr : state.pc.get();
  }
  PcGuard(PcState& state, std::try_to_lock_t) : lock_(state.mutex, std::try_to_lock) {
    pc_ = lock_.owns_lock() && !state.closed ? state.pc.get() : nullptr;
  }
  RTCPeerConnection* operator->() const { return pc_; }
  explicit operator bool() const { return pc_ != nullptr; }

 private:
  std::shared_lock<std::shared_mutex> lock_;
  RTCPeerConnection* pc_ = nullptr;
};

// Forwards observer events to the C callbacks. Close() waits for a callback in progress and
// turns every later one into a no-op, which is what lets rtc_pc_close promise silence.
class CallbackHub {
 public:
  CallbackHub(const void* owner, const rtc_pc_observer& callbacks, void* user)
      : owner_(owner), cb_(callbacks), user_(user) {}

  template <typename F>
  void Invoke(F&& f) {
    std::lock_guard<std::mutex> lock(mutex_);
    CallbackScope scope(owner_);
    if (!closed_) f(cb_, user_);
  }

  void Close() {
    std::lock_guard<std::mutex> lock(mutex_);
    closed_ = true;
  }

  bool closed() {
    std::lock_guard<std::mutex> lock(mutex_);
    return closed_;
  }

 private:
  const void* const owner_;
  const rtc_pc_observer cb_;
  void* const user_;
  std::mutex mutex_;
  bool closed_ = false;
};

class CryptorObserver : public RTCFrameCryptorObserver {
 public:
  explicit CryptorObserver(std::shared_ptr<CallbackHub> hub) : hub_(std::move(hub)) {}

  void OnFrameCryptionStateChanged(const libwebrtc::string participant_id,
                                   RTCFrameCryptionState state) override {
    const std::string participant = participant_id.std_string();
    hub_->Invoke([&](const rtc_pc_observer& cb, void* user) {
      if (cb.on_cryptor_state) cb.on_cryptor_state(user, participant.c_str(), state);
    });
  }

 private:
  std::shared_ptr<CallbackHub> hub_;
};

// Frame cryptors of one connection, keyed by "sender-<id>" / "receiver-<id>". Sender cryptors are
// created on application threads (blocking on the signaling thread) and receiver cryptors on the
// signaling thread, so the lock is never held while a cryptor is created.
class CryptorSet {
 public:
  CryptorSet(scoped_refptr<RTCPeerConnectionFactory> factory,
             scoped_refptr<KeyProvider> key_provider, std::shared_ptr<CallbackHub> hub)
      : factory_(std::move(factory)),
        key_provider_(std::move(key_provider)),
        observer_(new RefCountedObject<CryptorObserver>(std::move(hub))) {}

  bool enabled() const { return key_provider_.get() != nullptr; }

  bool AttachSender(scoped_refptr<RTCRtpSender> sender) {
    const std::string id = "sender-" + sender->id().std_string();
    if (Contains(id)) return false;
    scoped_refptr<RTCFrameCryptor> cryptor = FrameCryptorFactory::frameCryptorFromRtpSender(
        factory_, Str("local"), sender, FrameCryptorAlgorithm::kAesGcm, key_provider_);
    return Insert(id, cryptor);
  }

  bool AttachReceiver(scoped_refptr<RTCRtpReceiver> receiver) {
    const std::string id = "receiver-" + receiver->id().std_string();
    if (Contains(id)) return false;
    scoped_refptr<RTCFrameCryptor> cryptor = FrameCryptorFactory::frameCryptorFromRtpReceiver(
        factory_, Str("remote"), receiver, FrameCryptorAlgorithm::kAesGcm, key_provider_);
    return Insert(id, cryptor);
  }

  void DisposeAll() {
    std::map<std::string, scoped_refptr<RTCFrameCryptor>> cryptors;
    {
      std::lock_guard<std::mutex> lock(mutex_);
      disposed_ = true;
      cryptors.swap(cryptors_);
    }
    for (auto& entry : cryptors) entry.second->DeRegisterRTCFrameCryptorObserver();
  }

 private:
  bool Contains(const std::string& id) {
    std::lock_guard<std::mutex> lock(mutex_);
    return disposed_ || cryptors_.count(id) > 0;
  }

  bool Insert(const std::string& id, scoped_refptr<RTCFrameCryptor> cryptor) {
    if (!cryptor) {
      SetLastError("failed to create frame cryptor " + id);
      return false;
    }
    cryptor->SetKeyIndex(0);
    cryptor->RegisterRTCFrameCryptorObserver(observer_);
    // M150 forwards frames unencrypted while a cryptor is disabled, so it is enabled right away.
    cryptor->SetEnabled(true);
    std::lock_guard<std::mutex> lock(mutex_);
    if (disposed_ || cryptors_.count(id) > 0) {
      cryptor->DeRegisterRTCFrameCryptorObserver();
      return false;
    }
    cryptors_[id] = cryptor;
    return true;
  }

  scoped_refptr<RTCPeerConnectionFactory> factory_;
  scoped_refptr<KeyProvider> key_provider_;
  scoped_refptr<RTCFrameCryptorObserver> observer_;
  std::mutex mutex_;
  bool disposed_ = false;
  std::map<std::string, scoped_refptr<RTCFrameCryptor>> cryptors_;
};

class PeerObserver : public RTCPeerConnectionObserver {
 public:
  PeerObserver(std::shared_ptr<CallbackHub> hub, std::shared_ptr<CryptorSet> cryptors)
      : hub_(std::move(hub)), cryptors_(std::move(cryptors)) {}

  void OnSignalingState(RTCSignalingState state) override {
    hub_->Invoke([&](const rtc_pc_observer& cb, void* user) {
      if (cb.on_signaling_state) cb.on_signaling_state(user, state);
    });
  }

  void OnPeerConnectionState(RTCPeerConnectionState state) override {
    hub_->Invoke([&](const rtc_pc_observer& cb, void* user) {
      if (cb.on_connection_state) cb.on_connection_state(user, state);
    });
  }

  void OnIceGatheringState(RTCIceGatheringState state) override {
    hub_->Invoke([&](const rtc_pc_observer& cb, void* user) {
      if (cb.on_ice_gathering_state) cb.on_ice_gathering_state(user, state);
    });
  }

  void OnIceConnectionState(RTCIceConnectionState state) override {
    hub_->Invoke([&](const rtc_pc_observer& cb, void* user) {
      if (cb.on_ice_connection_state) cb.on_ice_connection_state(user, state);
    });
  }

  void OnIceCandidate(scoped_refptr<RTCIceCandidate> candidate) override {
    if (!candidate) return;
    const std::string mid = candidate->sdp_mid().std_string();
    const std::string sdp = candidate->candidate().std_string();
    const int index = candidate->sdp_mline_index();
    hub_->Invoke([&](const rtc_pc_observer& cb, void* user) {
      if (cb.on_ice_candidate) cb.on_ice_candidate(user, mid.c_str(), index, sdp.c_str());
    });
  }

  void OnAddStream(scoped_refptr<RTCMediaStream>) override {}
  void OnRemoveStream(scoped_refptr<RTCMediaStream>) override {}
  void OnTrack(scoped_refptr<RTCRtpTransceiver>) override {}
  void OnRemoveTrack(scoped_refptr<RTCRtpReceiver>) override {}

  void OnDataChannel(scoped_refptr<RTCDataChannel> data_channel) override {
    if (!data_channel) return;
    hub_->Invoke([&](const rtc_pc_observer& cb, void* user) {
      if (cb.on_data_channel) cb.on_data_channel(user, new rtc_data_channel(data_channel));
    });
  }

  void OnRenegotiationNeeded() override {
    hub_->Invoke([&](const rtc_pc_observer& cb, void* user) {
      if (cb.on_renegotiation_needed) cb.on_renegotiation_needed(user);
    });
  }

  // Unified Plan reports every new receiver here, before any frame is decoded, so the receiver's
  // cryptor is attached before its first encrypted frame.
  void OnAddTrack(libwebrtc::vector<scoped_refptr<RTCMediaStream>>,
                  scoped_refptr<RTCRtpReceiver> receiver) override {
    if (!receiver || hub_->closed()) return;
    scoped_refptr<RTCMediaTrack> track = receiver->track();
    if (!track) return;
    if (cryptors_->enabled()) cryptors_->AttachReceiver(receiver);

    const bool is_video = track->kind().std_string() == "video";
    const std::string receiver_id = receiver->id().std_string();
    const auto stream_ids = receiver->stream_ids();
    const std::string stream_id = stream_ids.size() > 0 ? stream_ids[0].std_string() : std::string();
    hub_->Invoke([&](const rtc_pc_observer& cb, void* user) {
      if (!cb.on_track) return;
      auto* handle = new rtc_track{track, nullptr};
      if (is_video) handle->video = static_cast<RTCVideoTrack*>(track.get());
      cb.on_track(user, handle, is_video ? RTC_KIND_VIDEO : RTC_KIND_AUDIO, receiver_id.c_str(),
                  stream_id.c_str());
    });
  }

 private:
  std::shared_ptr<CallbackHub> hub_;
  std::shared_ptr<CryptorSet> cryptors_;
};

// One asynchronous offer/answer/remote-description operation. Owned by the libwebrtc callbacks:
// exactly one of them deletes it. If libwebrtc drops both, it leaks a few bytes.
struct SdpOp {
  std::shared_ptr<PcState> state;
  rtc_sdp_cb cb;
  void* ctx;
  std::string sdp;
  std::string type;

  void Succeed() {
    CallbackScope scope(state.get());
    if (cb) cb(ctx, 1, type.c_str(), sdp.c_str(), nullptr);
  }

  void Fail(const char* error) {
    CallbackScope scope(state.get());
    if (cb) cb(ctx, 0, nullptr, nullptr, error && *error ? error : "failed");
  }
};

struct ResultOp {
  const void* owner;
  rtc_result_cb cb;
  void* ctx;

  void Complete(int32_t ok, const char* error) {
    CallbackScope scope(owner);
    if (cb) cb(ctx, ok, error);
  }
};

struct LevelOp {
  const void* owner;
  rtc_audio_level_cb cb;
  void* ctx;

  void Complete(double level) {
    CallbackScope scope(owner);
    cb(ctx, level);
  }
};

class OnceLevelOp {
 public:
  OnceLevelOp(const void* owner, rtc_audio_level_cb cb, void* ctx)
      : owner_(owner), cb_(cb), ctx_(ctx) {}

  void Complete(double level) {
    if (done_.exchange(true)) return;
    CallbackScope scope(owner_);
    cb_(ctx_, level);
  }

 private:
  const void* const owner_;
  const rtc_audio_level_cb cb_;
  void* const ctx_;
  std::atomic<bool> done_{false};
};

// The loudest inbound-rtp audioLevel in the reports, or -1.
double LoudestInboundAudio(const libwebrtc::vector<scoped_refptr<MediaRTCStats>>& reports) {
  double level = -1;
  for (size_t i = 0; i < reports.size(); ++i) {
    if (reports[i]->type().std_string() != "inbound-rtp") continue;
    bool audio = false;
    double value = -1;
    const auto members = reports[i]->Members();
    for (size_t m = 0; m < members.size(); ++m) {
      const auto& member = members[m];
      if (!member->IsDefined()) continue;
      const std::string name = member->GetName().std_string();
      if (name == "kind" && member->GetType() == RTCStatsMember::kString) {
        audio = member->ValueString().std_string() == "audio";
      } else if (name == "audioLevel" && member->GetType() == RTCStatsMember::kDouble) {
        value = member->ValueDouble();
      }
    }
    if (audio && value > level) level = value;
  }
  return level;
}

double LocalAudioLevel(const libwebrtc::vector<scoped_refptr<MediaRTCStats>>& reports) {
  double level = -1;
  for (size_t i = 0; i < reports.size(); ++i) {
    if (reports[i]->type().std_string() != "media-source") continue;
    bool audio = false;
    double value = -1;
    const auto members = reports[i]->Members();
    for (size_t m = 0; m < members.size(); ++m) {
      const auto& member = members[m];
      if (!member->IsDefined()) continue;
      const std::string name = member->GetName().std_string();
      if (name == "kind" && member->GetType() == RTCStatsMember::kString) {
        audio = member->ValueString().std_string() == "audio";
      } else if (name == "audioLevel" && member->GetType() == RTCStatsMember::kDouble) {
        value = member->ValueDouble();
      }
    }
    if (audio && value > level) level = value;
  }
  return level;
}

double InboundAudioPackets(const libwebrtc::vector<scoped_refptr<MediaRTCStats>>& reports) {
  double packets = -1;
  for (size_t i = 0; i < reports.size(); ++i) {
    if (reports[i]->type().std_string() != "inbound-rtp") continue;
    bool audio = false;
    double value = -1;
    const auto members = reports[i]->Members();
    for (size_t m = 0; m < members.size(); ++m) {
      const auto& member = members[m];
      if (!member->IsDefined()) continue;
      const std::string name = member->GetName().std_string();
      if (name == "kind" && member->GetType() == RTCStatsMember::kString) {
        audio = member->ValueString().std_string() == "audio";
      } else if (name == "packetsReceived") {
        switch (member->GetType()) {
          case RTCStatsMember::kUint32: value = member->ValueUint32(); break;
          case RTCStatsMember::kUint64: value = static_cast<double>(member->ValueUint64()); break;
          case RTCStatsMember::kInt32: value = member->ValueInt32(); break;
          case RTCStatsMember::kInt64: value = static_cast<double>(member->ValueInt64()); break;
          default: break;
        }
      }
    }
    if (audio && value >= 0) packets = (packets < 0 ? 0 : packets) + value;
  }
  return packets;
}

void ApplyLocalDescription(SdpOp* op) {
  PcGuard pc(*op->state, std::try_to_lock);
  if (!pc) {
    op->Fail("peer connection closed");
    delete op;
    return;
  }
  pc->SetLocalDescription(
      Str(op->sdp.c_str()), Str(op->type.c_str()),
      [op] {
        op->Succeed();
        delete op;
      },
      [op](const char* error) {
        op->Fail(error);
        delete op;
      });
}

}  // namespace
}  // namespace rtc_shim

struct rtc_peer_connection {
  scoped_refptr<RTCPeerConnectionFactory> factory;
  std::shared_ptr<PcState> state = std::make_shared<PcState>();
  std::shared_ptr<CallbackHub> hub;
  std::shared_ptr<CryptorSet> cryptors;
  std::unique_ptr<PeerObserver> observer;
};

namespace {

void CreateSessionDescription(rtc_peer_connection* pc, bool offer, rtc_sdp_cb cb, void* ctx) {
  auto* op = new SdpOp{pc ? pc->state : nullptr, cb, ctx, {}, {}};
  if (!pc) {
    op->Fail("null peer connection");
    delete op;
    return;
  }
  PcGuard guard(*pc->state);
  if (!guard) {
    op->Fail("peer connection closed");
    delete op;
    return;
  }
  auto on_success = [op](const libwebrtc::string sdp, const libwebrtc::string type) {
    op->sdp = sdp.std_string();
    op->type = type.std_string();
    ApplyLocalDescription(op);
  };
  auto on_failure = [op](const char* error) {
    op->Fail(error);
    delete op;
  };
  scoped_refptr<RTCMediaConstraints> constraints = RTCMediaConstraints::Create();
  if (offer) {
    guard->CreateOffer(on_success, on_failure, constraints);
  } else {
    guard->CreateAnswer(on_success, on_failure, constraints);
  }
}

// Close waits for callbacks in progress and for the signaling thread, so from inside one of the
// connection's own callbacks it would wait for itself.
bool RefuseInsideCallback(rtc_peer_connection* pc, const char* function) {
  if (!CallbackScope::Inside(pc->state.get())) return false;
  SetLastError(std::string(function) + ": called from inside a callback of this connection");
  return true;
}

}  // namespace

extern "C" {

// ---- Peer connection ------------------------------------------------------------------------

rtc_peer_connection* RTC_CALL rtc_pc_create(rtc_factory* factory,
                                            const rtc_ice_server* ice_servers,
                                            int32_t ice_server_count,
                                            rtc_key_provider* key_provider,
                                            const rtc_pc_observer* observer, void* user) {
  return Guard<rtc_peer_connection*>(nullptr, __func__, [&]() -> rtc_peer_connection* {
    if (!factory || !observer) return nullptr;
    RTCConfiguration config;
    const int32_t count = std::min<int32_t>(ice_server_count, kMaxIceServerSize);
    for (int32_t i = 0; i < count; ++i) {
      config.ice_servers[i].uri = Str(ice_servers[i].uri);
      config.ice_servers[i].username = Str(ice_servers[i].username);
      config.ice_servers[i].password = Str(ice_servers[i].password);
    }
    config.sdp_semantics = SdpSemantics::kUnifiedPlan;
    config.bundle_policy = kBundlePolicyMaxBundle;

    auto handle = std::make_unique<rtc_peer_connection>();
    handle->factory = factory->factory;
    handle->hub = std::make_shared<CallbackHub>(handle->state.get(), *observer, user);
    handle->cryptors = std::make_shared<CryptorSet>(
        factory->factory, key_provider ? key_provider->provider : nullptr, handle->hub);
    handle->observer = std::make_unique<PeerObserver>(handle->hub, handle->cryptors);

    scoped_refptr<RTCPeerConnection> pc =
        factory->factory->Create(config, RTCMediaConstraints::Create());
    if (!pc) {
      SetLastError("rtc_pc_create: Create failed");
      return nullptr;
    }
    pc->RegisterRTCPeerConnectionObserver(handle->observer.get());
    handle->state->pc = pc;
    return handle.release();
  });
}

rtc_sender* RTC_CALL rtc_pc_add_track(rtc_peer_connection* pc, rtc_track* track,
                                      const char* stream_id) {
  return Guard<rtc_sender*>(nullptr, __func__, [&]() -> rtc_sender* {
    if (!pc || !track) return nullptr;
    PcGuard guard(*pc->state);
    if (!guard) return nullptr;
    std::vector<libwebrtc::string> ids{Str(stream_id)};
    scoped_refptr<RTCRtpSender> sender =
        guard->AddTrack(track->track, libwebrtc::vector<libwebrtc::string>(ids));
    if (!sender) {
      SetLastError("rtc_pc_add_track: AddTrack failed");
      return nullptr;
    }
    return new rtc_sender{sender};
  });
}

rtc_sender* RTC_CALL rtc_pc_add_transceiver(rtc_peer_connection* pc, int32_t kind,
                                            int32_t direction, rtc_track* track,
                                            const char* stream_id) {
  return Guard<rtc_sender*>(nullptr, __func__, [&]() -> rtc_sender* {
    if (!pc || direction < RTC_DIRECTION_SENDRECV || direction > RTC_DIRECTION_INACTIVE) {
      SetLastError("rtc_pc_add_transceiver: bad arguments");
      return nullptr;
    }
    PcGuard guard(*pc->state);
    if (!guard) return nullptr;
    std::vector<libwebrtc::string> ids;
    if (stream_id && *stream_id) ids.push_back(Str(stream_id));
    scoped_refptr<RTCRtpTransceiverInit> init = RTCRtpTransceiverInit::Create(
        static_cast<RTCRtpTransceiverDirection>(direction), libwebrtc::vector<libwebrtc::string>(ids),
        libwebrtc::vector<scoped_refptr<RTCRtpEncodingParameters>>());
    scoped_refptr<RTCRtpTransceiver> transceiver =
        track ? guard->AddTransceiver(track->track, init)
              : guard->AddTransceiver(kind == RTC_KIND_VIDEO ? RTCMediaType::VIDEO : RTCMediaType::AUDIO,
                                      init);
    scoped_refptr<RTCRtpSender> sender = transceiver ? transceiver->sender() : nullptr;
    if (!sender) {
      SetLastError("rtc_pc_add_transceiver: AddTransceiver failed");
      return nullptr;
    }
    return new rtc_sender{sender};
  });
}

int32_t RTC_CALL rtc_pc_get_transceivers(rtc_peer_connection* pc, rtc_transceiver_info* out,
                                         int32_t capacity) {
  return Guard<int32_t>(0, __func__, [&]() -> int32_t {
    if (!pc) return 0;
    PcGuard guard(*pc->state);
    if (!guard) return 0;
    const auto transceivers = guard->transceivers();
    const int32_t count = static_cast<int32_t>(transceivers.size());
    for (int32_t i = 0; i < count && i < capacity && out; ++i) {
      const auto& transceiver = transceivers[i];
      rtc_transceiver_info& info = out[i];
      info.kind = transceiver->media_type() == RTCMediaType::VIDEO ? RTC_KIND_VIDEO : RTC_KIND_AUDIO;
      info.direction = static_cast<int32_t>(transceiver->direction());
      info.current_direction = static_cast<int32_t>(transceiver->current_direction());
      CopyOut(transceiver->mid().std_string(), info.mid, sizeof(info.mid));
      scoped_refptr<RTCRtpReceiver> receiver = transceiver->receiver();
      CopyOut(receiver ? receiver->id().std_string() : std::string(), info.receiver_id,
              sizeof(info.receiver_id));
    }
    return count;
  });
}

int32_t RTC_CALL rtc_sender_set_track(rtc_sender* sender, rtc_track* track) {
  return Guard<int32_t>(0, __func__, [&]() -> int32_t {
    if (!sender) return 0;
    scoped_refptr<RTCMediaTrack> media = track ? track->track : nullptr;
    return sender->sender->set_track(media) ? 1 : 0;
  });
}

void RTC_CALL rtc_sender_release(rtc_sender* sender) {
  GuardVoid(__func__, [&] { delete sender; });
}

rtc_data_channel* RTC_CALL rtc_pc_create_data_channel(rtc_peer_connection* pc,
                                                      const char* label) {
  return Guard<rtc_data_channel*>(nullptr, __func__, [&]() -> rtc_data_channel* {
    if (!pc) return nullptr;
    PcGuard guard(*pc->state);
    if (!guard) return nullptr;
    RTCDataChannelInit init;
    init.ordered = true;
    init.reliable = true;
    init.protocol = Str("");
    init.negotiated = false;
    init.id = -1;
    scoped_refptr<RTCDataChannel> channel = guard->CreateDataChannel(Str(label), &init);
    if (!channel) {
      SetLastError("rtc_pc_create_data_channel: CreateDataChannel failed");
      return nullptr;
    }
    return new rtc_data_channel(channel);
  });
}

void RTC_CALL rtc_pc_create_offer(rtc_peer_connection* pc, rtc_sdp_cb cb, void* ctx) {
  GuardVoid(__func__, [&] { CreateSessionDescription(pc, true, cb, ctx); });
}

void RTC_CALL rtc_pc_create_answer(rtc_peer_connection* pc, rtc_sdp_cb cb, void* ctx) {
  GuardVoid(__func__, [&] { CreateSessionDescription(pc, false, cb, ctx); });
}

void RTC_CALL rtc_pc_set_remote_description(rtc_peer_connection* pc, const char* type,
                                            const char* sdp, rtc_result_cb cb, void* ctx) {
  GuardVoid(__func__, [&] {
    auto* op = new ResultOp{pc ? pc->state.get() : nullptr, cb, ctx};
    auto fail = [op](const char* error) {
      op->Complete(0, error && *error ? error : "failed");
      delete op;
    };
    if (!pc) return fail("null peer connection");
    PcGuard guard(*pc->state);
    if (!guard) return fail("peer connection closed");
    guard->SetRemoteDescription(
        Str(sdp), Str(type),
        [op] {
          op->Complete(1, nullptr);
          delete op;
        },
        fail);
  });
}

int32_t RTC_CALL rtc_pc_add_ice_candidate(rtc_peer_connection* pc, const char* sdp_mid,
                                          int32_t sdp_mline_index, const char* candidate) {
  return Guard<int32_t>(0, __func__, [&]() -> int32_t {
    if (!pc || !candidate) return 0;
    PcGuard guard(*pc->state);
    if (!guard) return 0;
    guard->AddCandidate(Str(sdp_mid), sdp_mline_index, Str(candidate));
    return 1;
  });
}

int32_t RTC_CALL rtc_pc_prefer_codec(rtc_peer_connection* pc, int32_t kind,
                                     const char* mime_type) {
  return Guard<int32_t>(0, __func__, [&]() -> int32_t {
    if (!pc || !mime_type) return 0;
    const RTCMediaType media = kind == RTC_KIND_VIDEO ? RTCMediaType::VIDEO : RTCMediaType::AUDIO;
    scoped_refptr<RTCRtpCapabilities> capabilities =
        pc->factory->GetRtpReceiverCapabilities(media);
    if (!capabilities) return 0;

    auto lower = [](std::string s) {
      std::transform(s.begin(), s.end(), s.begin(),
                     [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
      return s;
    };
    const std::string preferred = lower(mime_type);
    std::vector<scoped_refptr<RTCRtpCodecCapability>> first;
    std::vector<scoped_refptr<RTCRtpCodecCapability>> rest;
    const auto codecs = capabilities->codecs();
    for (size_t i = 0; i < codecs.size(); ++i) {
      (lower(codecs[i]->mime_type().std_string()) == preferred ? first : rest).push_back(codecs[i]);
    }
    if (first.empty()) return 0;
    first.insert(first.end(), rest.begin(), rest.end());

    PcGuard guard(*pc->state);
    if (!guard) return 0;
    int32_t updated = 0;
    const auto transceivers = guard->transceivers();
    for (size_t i = 0; i < transceivers.size(); ++i) {
      if (transceivers[i]->media_type() != media) continue;
      transceivers[i]->SetCodecPreferences(
          libwebrtc::vector<scoped_refptr<RTCRtpCodecCapability>>(first));
      ++updated;
    }
    return updated;
  });
}

int32_t RTC_CALL rtc_pc_attach_sender_cryptors(rtc_peer_connection* pc) {
  return Guard<int32_t>(0, __func__, [&]() -> int32_t {
    if (!pc || !pc->cryptors->enabled()) return 0;
    std::vector<scoped_refptr<RTCRtpSender>> senders;
    {
      PcGuard guard(*pc->state);
      if (!guard) return 0;
      const auto all = guard->senders();
      for (size_t i = 0; i < all.size(); ++i) {
        if (all[i]->track()) senders.push_back(all[i]);
      }
    }
    int32_t attached = 0;
    for (auto& sender : senders) {
      if (pc->cryptors->AttachSender(sender)) ++attached;
    }
    return attached;
  });
}

int32_t RTC_CALL rtc_pc_attach_receiver_cryptors(rtc_peer_connection* pc) {
  return Guard<int32_t>(0, __func__, [&]() -> int32_t {
    if (!pc || !pc->cryptors->enabled()) return 0;
    std::vector<scoped_refptr<RTCRtpReceiver>> receivers;
    {
      PcGuard guard(*pc->state);
      if (!guard) return 0;
      const auto all = guard->receivers();
      for (size_t i = 0; i < all.size(); ++i) {
        if (all[i]->track()) receivers.push_back(all[i]);
      }
    }
    int32_t attached = 0;
    for (auto& receiver : receivers) {
      if (pc->cryptors->AttachReceiver(receiver)) ++attached;
    }
    return attached;
  });
}

void RTC_CALL rtc_pc_get_remote_audio_level(rtc_peer_connection* pc, rtc_audio_level_cb cb,
                                            void* ctx) {
  GuardVoid(__func__, [&] {
    if (!cb) return;
    if (!pc) return cb(ctx, -1);
    auto* op = new LevelOp{pc->state.get(), cb, ctx};
    PcGuard guard(*pc->state);
    if (!guard) {
      op->Complete(-1);
      delete op;
      return;
    }
    guard->GetStats(
        [op](const libwebrtc::vector<scoped_refptr<MediaRTCStats>> reports) {
          op->Complete(LoudestInboundAudio(reports));
          delete op;
        },
        [op](const char*) {
          op->Complete(-1);
          delete op;
        });
  });
}

void RTC_CALL rtc_pc_get_local_audio_level(rtc_peer_connection* pc, rtc_audio_level_cb cb,
                                           void* ctx) {
  GuardVoid(__func__, [&] {
    if (!cb) return;
    if (!pc) return cb(ctx, -1);
    auto* op = new LevelOp{pc->state.get(), cb, ctx};
    PcGuard guard(*pc->state);
    if (!guard) {
      op->Complete(-1);
      delete op;
      return;
    }
    guard->GetStats(
        [op](const libwebrtc::vector<scoped_refptr<MediaRTCStats>> reports) {
          op->Complete(LocalAudioLevel(reports));
          delete op;
        },
        [op](const char*) {
          op->Complete(-1);
          delete op;
        });
  });
}

void RTC_CALL rtc_pc_get_inbound_audio_packets(rtc_peer_connection* pc, rtc_audio_level_cb cb,
                                               void* ctx) {
  GuardVoid(__func__, [&] {
    if (!cb) return;
    if (!pc) return cb(ctx, -1);
    auto* op = new LevelOp{pc->state.get(), cb, ctx};
    PcGuard guard(*pc->state);
    if (!guard) {
      op->Complete(-1);
      delete op;
      return;
    }
    guard->GetStats(
        [op](const libwebrtc::vector<scoped_refptr<MediaRTCStats>> reports) {
          op->Complete(InboundAudioPackets(reports));
          delete op;
        },
        [op](const char*) {
          op->Complete(-1);
          delete op;
        });
  });
}

void RTC_CALL rtc_pc_get_receiver_audio_level(rtc_peer_connection* pc, const char* receiver_id,
                                              rtc_audio_level_cb cb, void* ctx) {
  GuardVoid(__func__, [&] {
    if (!cb) return;
    if (!pc || !receiver_id) return cb(ctx, -1);
    // A refused request may or may not have run the failure callback, so completion is once-only.
    auto op = std::make_shared<OnceLevelOp>(pc->state.get(), cb, ctx);
    PcGuard guard(*pc->state);
    if (!guard) return op->Complete(-1);
    scoped_refptr<RTCRtpReceiver> receiver;
    const auto receivers = guard->receivers();
    for (size_t i = 0; i < receivers.size(); ++i) {
      if (receivers[i]->id().std_string() == receiver_id) receiver = receivers[i];
    }
    if (!receiver) return op->Complete(-1);
    const bool started = guard->GetStats(
        receiver,
        [op](const libwebrtc::vector<scoped_refptr<MediaRTCStats>> reports) {
          op->Complete(LoudestInboundAudio(reports));
        },
        [op](const char*) { op->Complete(-1); });
    if (!started) op->Complete(-1);
  });
}

int32_t RTC_CALL rtc_pc_close(rtc_peer_connection* pc) {
  return Guard<int32_t>(0, __func__, [&]() -> int32_t {
    if (!pc) return 1;
    if (RefuseInsideCallback(pc, "rtc_pc_close")) return 0;
    pc->hub->Close();
    scoped_refptr<RTCPeerConnection> native;
    {
      std::unique_lock<std::shared_mutex> lock(pc->state->mutex);
      if (pc->state->closed) return 1;
      pc->state->closed = true;
      native = pc->state->pc;
    }
    if (native) {
      native->DeRegisterRTCPeerConnectionObserver();
      native->Close();
      pc->factory->Delete(native);
    }
    // Dropped only after Close(): a disabled cryptor would forward frames unencrypted.
    pc->cryptors->DisposeAll();
    return 1;
  });
}

int32_t RTC_CALL rtc_pc_release(rtc_peer_connection* pc) {
  return Guard<int32_t>(0, __func__, [&]() -> int32_t {
    if (!pc) return 1;
    if (RefuseInsideCallback(pc, "rtc_pc_release")) return 0;
    rtc_pc_close(pc);
    {
      std::unique_lock<std::shared_mutex> lock(pc->state->mutex);
      pc->state->pc = nullptr;
    }
    delete pc;
    return 1;
  });
}

// ---- Data channel ---------------------------------------------------------------------------

void RTC_CALL rtc_data_channel_set_observer(rtc_data_channel* channel,
                                            const rtc_data_channel_observer* observer,
                                            void* user) {
  GuardVoid(__func__, [&] {
    if (!channel || !observer) return;
    if (!channel->forwarder) {
      channel->forwarder = new DataChannelForwarder();
      channel->channel->RegisterObserver(channel->forwarder);
    }
    channel->forwarder->Activate(*observer, user);
  });
}

int32_t RTC_CALL rtc_data_channel_send(rtc_data_channel* channel, const uint8_t* data,
                                       int32_t length, int32_t binary) {
  return Guard<int32_t>(0, __func__, [&]() -> int32_t {
    if (!channel || (!data && length > 0) || length < 0) return 0;
    if (channel->channel->state() != RTCDataChannelOpen) return 0;
    channel->channel->Send(data, static_cast<uint32_t>(length), binary != 0);
    return 1;
  });
}

int32_t RTC_CALL rtc_data_channel_state(rtc_data_channel* channel) {
  return Guard<int32_t>(RTC_DC_CLOSED, __func__, [&]() -> int32_t {
    return channel ? static_cast<int32_t>(channel->channel->state()) : RTC_DC_CLOSED;
  });
}

int32_t RTC_CALL rtc_data_channel_label(rtc_data_channel* channel, char* buf, int32_t cap) {
  return Guard<int32_t>(0, __func__, [&]() -> int32_t {
    return CopyOut(channel ? channel->channel->label().std_string() : std::string(), buf, cap);
  });
}

void RTC_CALL rtc_data_channel_close(rtc_data_channel* channel) {
  GuardVoid(__func__, [&] {
    if (channel) channel->channel->Close();
  });
}

void RTC_CALL rtc_data_channel_release(rtc_data_channel* channel) {
  GuardVoid(__func__, [&] {
    if (!channel) return;
    if (channel->forwarder) {
      channel->channel->UnregisterObserver();
      channel->forwarder->Deactivate();
      // Not deleted: libwebrtc reads its observer pointer without a lock, so a message racing
      // with UnregisterObserver could still reach it. Deactivated, it is an inert few bytes.
    }
    delete channel;
  });
}

}  // extern "C"
