// In-process loopback test of the shim against the real libwebrtc build: two peer connections
// exchange SDP/ICE directly, send synthetic video both ways with E2EE, talk over the data
// channel and read stats. A second run gives the peers different keys and expects no video. A
// third has the shape of a group call: sendonly transceivers to a recvonly peer, mapped by stream
// id and mid, with per-receiver stats.
//
// Callbacks only post work to the main thread, like the app does.
#include "rtc_shim.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <functional>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace {

using Clock = std::chrono::steady_clock;

std::mutex g_queue_mutex;
std::condition_variable g_queue_cv;
std::deque<std::function<void()>> g_queue;

void Post(std::function<void()> task) {
  {
    std::lock_guard<std::mutex> lock(g_queue_mutex);
    g_queue.push_back(std::move(task));
  }
  g_queue_cv.notify_one();
}

// Runs posted tasks until `done` holds or the timeout expires.
bool RunUntil(const std::function<bool()>& done, std::chrono::milliseconds timeout) {
  const auto deadline = Clock::now() + timeout;
  while (!done()) {
    std::function<void()> task;
    {
      std::unique_lock<std::mutex> lock(g_queue_mutex);
      // Frame counters change without posting, so wake up regularly to re-check.
      const auto wake = std::min(deadline, Clock::now() + std::chrono::milliseconds(50));
      if (!g_queue_cv.wait_until(lock, wake, [] { return !g_queue.empty(); })) {
        if (Clock::now() >= deadline) return done();
        continue;
      }
      task = std::move(g_queue.front());
      g_queue.pop_front();
    }
    task();
  }
  return true;
}

int g_failures = 0;

void Check(bool condition, const std::string& what) {
  std::printf("  [%s] %s\n", condition ? "PASS" : "FAIL", what.c_str());
  if (!condition) ++g_failures;
}

struct Color {
  uint8_t y, u, v;     // what is pushed
  uint8_t b, g, r;     // what the sink should see
  const char* name;
};
const Color kRed{81, 90, 240, 0, 0, 255, "red"};
const Color kGreen{145, 54, 34, 0, 255, 0, "green"};

struct Peer {
  std::string name;
  rtc_factory* factory = nullptr;
  rtc_peer_connection* pc = nullptr;
  rtc_key_provider* keys = nullptr;
  rtc_video_source* source = nullptr;
  rtc_track* video = nullptr;
  rtc_track* audio = nullptr;
  std::vector<rtc_sender*> senders;
  std::vector<rtc_track*> remote_tracks;
  std::vector<std::pair<std::string, std::string>> track_streams;  // receiver id, stream id
  rtc_video_sink* sink = nullptr;
  rtc_data_channel* channel = nullptr;
  Peer* other = nullptr;
  Color color = kRed;

  bool remote_set = false;
  std::vector<std::function<void()>> pending_candidates;
  int connection_state = RTC_PC_STATE_NEW;
  std::vector<std::string> messages;
  std::string local_sdp;

  std::atomic<int> reentrant_close{-1};  // rtc_pc_close called from inside a state callback
  std::atomic<int> frames{0};
  std::atomic<int> matching_frames{0};
  std::atomic<int> width{0}, height{0};

  std::mutex cryptor_mutex;
  std::vector<std::pair<std::string, int>> cryptor_states;
};

bool Near(int a, int b) { return std::abs(a - b) <= 40; }

void RTC_CALL OnFrame(void* user, const uint8_t* bgra, int32_t w, int32_t h, int32_t stride,
                      int32_t) {
  auto* peer = static_cast<Peer*>(user);
  peer->frames++;
  peer->width = w;
  peer->height = h;
  const uint8_t* px = bgra + static_cast<size_t>(h / 2) * stride + static_cast<size_t>(w / 2) * 4;
  const Color& expected = peer->other->color;
  if (Near(px[0], expected.b) && Near(px[1], expected.g) && Near(px[2], expected.r) &&
      px[3] == 255) {
    peer->matching_frames++;
  }
}

void RTC_CALL OnDcState(void* user, int32_t state) {
  auto* peer = static_cast<Peer*>(user);
  Post([peer, state] {
    if (state == RTC_DC_OPEN && peer->channel) {
      const std::string text = "hello from " + peer->name;
      rtc_data_channel_send(peer->channel, reinterpret_cast<const uint8_t*>(text.data()),
                            static_cast<int32_t>(text.size()), 0);
    }
  });
}

void RTC_CALL OnDcMessage(void* user, const uint8_t* data, int32_t length, int32_t binary) {
  auto* peer = static_cast<Peer*>(user);
  std::string text(reinterpret_cast<const char*>(data), static_cast<size_t>(length));
  Post([peer, text] { peer->messages.push_back(text); });
}

const rtc_data_channel_observer kDcObserver{OnDcState, OnDcMessage};

rtc_pc_observer MakeObserver() {
  rtc_pc_observer o{};
  o.on_connection_state = [](void* user, int32_t state) {
    auto* peer = static_cast<Peer*>(user);
    if (state == RTC_PC_STATE_CONNECTED && peer->reentrant_close < 0) {
      peer->reentrant_close = rtc_pc_close(peer->pc);  // must be refused, not deadlock
    }
    Post([peer, state] { peer->connection_state = state; });
  };
  o.on_ice_candidate = [](void* user, const char* mid, int32_t index, const char* candidate) {
    auto* peer = static_cast<Peer*>(user);
    std::string m = mid ? mid : "";
    std::string c = candidate;
    Post([peer, m, index, c] {
      Peer* other = peer->other;
      auto add = [other, m, index, c] {
        if (other->pc) rtc_pc_add_ice_candidate(other->pc, m.c_str(), index, c.c_str());
      };
      if (other->remote_set) add(); else other->pending_candidates.push_back(add);
    });
  };
  o.on_track = [](void* user, rtc_track* track, int32_t kind, const char* receiver_id,
                  const char* stream_id) {
    auto* peer = static_cast<Peer*>(user);
    std::string receiver = receiver_id, stream = stream_id;
    Post([peer, track, kind, receiver, stream] {
      peer->remote_tracks.push_back(track);
      peer->track_streams.emplace_back(receiver, stream);
      if (kind == RTC_KIND_VIDEO && !peer->sink) {
        peer->sink = rtc_video_sink_create(track, 0, 0, OnFrame, peer);
      }
    });
  };
  o.on_data_channel = [](void* user, rtc_data_channel* channel) {
    auto* peer = static_cast<Peer*>(user);
    rtc_data_channel_set_observer(channel, &kDcObserver, peer);
    Post([peer, channel] { peer->channel = channel; });
  };
  o.on_cryptor_state = [](void* user, const char* participant, int32_t state) {
    auto* peer = static_cast<Peer*>(user);
    std::lock_guard<std::mutex> lock(peer->cryptor_mutex);
    peer->cryptor_states.emplace_back(participant, state);
  };
  return o;
}

const rtc_pc_observer kObserver = MakeObserver();

void SetRemoteSet(Peer* peer) {
  peer->remote_set = true;
  for (auto& add : peer->pending_candidates) add();
  peer->pending_candidates.clear();
}

rtc_key_provider* CreateKeys(uint8_t fill) {
  static const char kSalt[] = "LKFrameEncryptionKey";
  rtc_key_provider_options options{};
  options.shared_key = 1;
  options.ratchet_salt = reinterpret_cast<const uint8_t*>(kSalt);
  options.ratchet_salt_length = static_cast<int32_t>(std::strlen(kSalt));
  options.ratchet_window_size = 0;
  options.failure_tolerance = -1;
  options.key_ring_size = 16;
  options.discard_frame_when_cryptor_not_ready = 0;
  options.key_derivation = RTC_KEY_DERIVATION_PBKDF2;
  rtc_key_provider* keys = rtc_key_provider_create(&options);
  uint8_t key[32];
  for (int i = 0; i < 32; ++i) key[i] = static_cast<uint8_t>(fill + i * 7);
  if (keys) rtc_key_provider_set_shared_key(keys, 0, key, sizeof(key));
  return keys;
}

void AddLocalMedia(Peer* peer) {
  peer->source = rtc_custom_source_create(peer->factory);
  peer->video = rtc_video_track_create(peer->factory, peer->source, (peer->name + "-video").c_str());
  peer->audio = rtc_audio_track_create(peer->factory, (peer->name + "-audio").c_str());
  if (peer->audio) peer->senders.push_back(rtc_pc_add_track(peer->pc, peer->audio, "stream"));
  peer->senders.push_back(rtc_pc_add_track(peer->pc, peer->video, "stream"));
  rtc_pc_attach_sender_cryptors(peer->pc);
}

// The first payload type of the m=video line must be VP8.
bool VideoPrefersVp8(const std::string& sdp) {
  const size_t m = sdp.find("m=video ");
  if (m == std::string::npos) return false;
  const size_t eol = sdp.find_first_of("\r\n", m);
  const std::string line = sdp.substr(m, eol - m);
  size_t pos = 0;
  for (int field = 0; field < 3; ++field) pos = line.find(' ', pos) + 1;
  const std::string pt = line.substr(pos, line.find(' ', pos) - pos);
  return sdp.find("a=rtpmap:" + pt + " VP8/") != std::string::npos;
}

void Pump(std::atomic<bool>* running, Peer* a, Peer* b) {
  const int w = 320, h = 240;
  std::vector<uint8_t> y(w * h), u(w * h / 4), v(w * h / 4);
  while (*running) {
    for (Peer* peer : {a, b}) {
      std::memset(y.data(), peer->color.y, y.size());
      std::memset(u.data(), peer->color.u, u.size());
      std::memset(v.data(), peer->color.v, v.size());
      rtc_custom_source_push_i420(peer->source, w, h, y.data(), w, u.data(), w / 2, v.data(), w / 2);
    }
    std::this_thread::sleep_for(std::chrono::milliseconds(33));
  }
}

bool HasState(Peer& peer, const char* participant, int state) {
  std::lock_guard<std::mutex> lock(peer.cryptor_mutex);
  for (auto& entry : peer.cryptor_states) {
    if (entry.first == participant && entry.second == state) return true;
  }
  return false;
}

void RunScenario(rtc_factory* factory, bool same_key) {
  std::printf("\n== %s\n", same_key ? "E2EE, same key on both peers"
                                     : "E2EE, different keys (video must not decode)");
  Peer a, b;
  a.name = "A";
  b.name = "B";
  a.color = kRed;
  b.color = kGreen;
  a.other = &b;
  b.other = &a;
  a.factory = b.factory = factory;
  a.keys = CreateKeys(1);
  b.keys = CreateKeys(same_key ? 1 : 2);
  Check(a.keys && b.keys, "key providers created");

  rtc_ice_server stun{"stun:stun.l.google.com:19302", nullptr, nullptr};
  a.pc = rtc_pc_create(factory, &stun, 1, a.keys, &kObserver, &a);
  b.pc = rtc_pc_create(factory, nullptr, 0, b.keys, &kObserver, &b);
  Check(a.pc && b.pc, "peer connections created");
  if (!a.pc || !b.pc) return;

  // Offerer: tracks, sender cryptors, VP8 first, data channel, offer.
  AddLocalMedia(&a);
  Check(a.video != nullptr, "video track on custom source");
  std::printf("  audio track: %s\n", a.audio ? "created" : "unavailable (no audio device)");
  Check(rtc_pc_prefer_codec(a.pc, RTC_KIND_VIDEO, "video/VP8") > 0, "offerer prefers VP8");
  a.channel = rtc_pc_create_data_channel(a.pc, "MyApp Channel");
  Check(a.channel != nullptr, "data channel created");
  if (a.channel) rtc_data_channel_set_observer(a.channel, &kDcObserver, &a);

  std::atomic<bool> running{true};
  std::thread pump(Pump, &running, &a, &b);

  struct Ctx { Peer* a; Peer* b; };
  static Ctx ctx;
  ctx = {&a, &b};
  rtc_pc_create_offer(a.pc, [](void* c, int32_t ok, const char* type, const char* sdp,
                               const char* error) {
    auto* x = static_cast<Ctx*>(c);
    std::string t = ok ? type : "", s = ok ? sdp : "", e = ok ? "" : error;
    Post([x, ok, t, s, e] {
      Check(ok && t == "offer", "offer created and set locally" + (ok ? "" : ": " + e));
      if (!ok) return;
      x->a->local_sdp = s;
      Check(VideoPrefersVp8(s), "offer lists VP8 first");
      // Answerer: tracks, sender cryptors, remote description, VP8 first, answer.
      AddLocalMedia(x->b);
      rtc_pc_set_remote_description(x->b->pc, t.c_str(), s.c_str(), [](void* c2, int32_t ok2, const char* err2) {
        auto* y = static_cast<Ctx*>(c2);
        std::string e2 = ok2 ? "" : err2;
        Post([y, ok2, e2] {
          Check(ok2, "answerer applied the offer" + (ok2 ? "" : ": " + e2));
          if (!ok2) return;
          SetRemoteSet(y->b);
          rtc_pc_prefer_codec(y->b->pc, RTC_KIND_VIDEO, "video/VP8");
          rtc_pc_create_answer(y->b->pc, [](void* c3, int32_t ok3, const char* t3, const char* s3, const char* e3) {
            auto* z = static_cast<Ctx*>(c3);
            std::string type3 = ok3 ? t3 : "", sdp3 = ok3 ? s3 : "", err3 = ok3 ? "" : e3;
            Post([z, ok3, type3, sdp3, err3] {
              Check(ok3 && type3 == "answer", "answer created and set locally" + (ok3 ? "" : ": " + err3));
              if (!ok3) return;
              Check(VideoPrefersVp8(sdp3), "answer lists VP8 first");
              rtc_pc_set_remote_description(z->a->pc, type3.c_str(), sdp3.c_str(), [](void* c4, int32_t ok4, const char* e4) {
                auto* w = static_cast<Ctx*>(c4);
                std::string err4 = ok4 ? "" : e4;
                Post([w, ok4, err4] {
                  Check(ok4, "offerer applied the answer" + (ok4 ? "" : ": " + err4));
                  if (ok4) SetRemoteSet(w->a);
                });
              }, z);
            });
          }, y);
        });
      }, x);
    });
  }, &ctx);

  const bool connected = RunUntil([&] {
    return a.connection_state == RTC_PC_STATE_CONNECTED && b.connection_state == RTC_PC_STATE_CONNECTED;
  }, std::chrono::seconds(20));
  Check(connected, "both peers connected");
  Check(a.reentrant_close == 0 && b.reentrant_close == 0,
        "rtc_pc_close from inside an observer callback is refused");

  if (same_key) {
    RunUntil([&] {
      return a.matching_frames > 10 && b.matching_frames > 10 && a.messages.size() >= 1 &&
             b.messages.size() >= 1;
    }, std::chrono::seconds(20));
    Check(b.matching_frames > 10, "B decoded A's encrypted video as red BGRA (" +
          std::to_string(b.matching_frames.load()) + "/" + std::to_string(b.frames.load()) +
          " frames, " + std::to_string(b.width.load()) + "x" + std::to_string(b.height.load()) + ")");
    Check(a.matching_frames > 10, "A decoded B's encrypted video as green BGRA (" +
          std::to_string(a.matching_frames.load()) + "/" + std::to_string(a.frames.load()) + " frames)");
    Check(!b.messages.empty() && b.messages[0] == "hello from A", "B received A's chat message");
    Check(!a.messages.empty() && a.messages[0] == "hello from B", "A received B's chat message");
    Check(HasState(b, "remote", RTC_CRYPTOR_OK), "B's receiver cryptor reported ok");
    Check(HasState(a, "local", RTC_CRYPTOR_OK) || HasState(a, "remote", RTC_CRYPTOR_OK),
          "A's cryptors reported ok");

    // Disabling the track stops video (the source keeps pushing).
    rtc_track_set_enabled(a.video, 0);
    Check(rtc_track_is_enabled(a.video) == 0, "track disabled");
    rtc_track_set_enabled(a.video, 1);

    // Polled like the placeholder does: inbound-rtp audio only appears once audio has flowed.
    std::atomic<double> level{-2};
    bool callback_ran = false;
    for (int attempt = 0; attempt < 40 && level.load() < 0; ++attempt) {
      level = -2;
      rtc_pc_get_remote_audio_level(b.pc, [](void* c, double l) {
        static_cast<std::atomic<double>*>(c)->store(l);
      }, &level);
      RunUntil([&] { return level.load() > -2; }, std::chrono::seconds(2));
      callback_ran = callback_ran || level.load() > -2;
      if (level.load() < 0) RunUntil([] { return false; }, std::chrono::milliseconds(250));
    }
    std::printf("  remote audio level: %.4f\n", level.load());
    Check(callback_ran, "getStats audio level callback ran");
    if (a.audio) Check(level.load() >= 0, "inbound-rtp audio level present");

    std::atomic<double> packets{-2};
    rtc_pc_get_inbound_audio_packets(b.pc, [](void* c, double p) {
      static_cast<std::atomic<double>*>(c)->store(p);
    }, &packets);
    RunUntil([&] { return packets.load() > -2; }, std::chrono::seconds(2));
    std::printf("  inbound audio packets: %.0f\n", packets.load());
    Check(packets.load() > -2, "getStats packet count callback ran");
    if (a.audio) Check(packets.load() > 0, "inbound-rtp audio packets counted");

    std::atomic<double> local{-2};
    rtc_pc_get_local_audio_level(a.pc, [](void* c, double l) {
      static_cast<std::atomic<double>*>(c)->store(l);
    }, &local);
    RunUntil([&] { return local.load() > -2; }, std::chrono::seconds(2));
    std::printf("  local audio level: %.4f\n", local.load());
    Check(local.load() > -2, "getStats local audio level callback ran");
    if (a.audio) Check(local.load() >= 0 && local.load() <= 1, "media-source audio level present");
  } else {
    RunUntil([&] { return HasState(b, "remote", RTC_CRYPTOR_DECRYPTION_FAILED) ||
                          HasState(b, "remote", RTC_CRYPTOR_MISSING_KEY); },
             std::chrono::seconds(10));
    RunUntil([] { return false; }, std::chrono::seconds(2));
    {
      std::lock_guard<std::mutex> lock(b.cryptor_mutex);
      std::string states;
      for (auto& entry : b.cryptor_states) {
        states += " " + entry.first + "=" + std::to_string(entry.second);
      }
      std::printf("  B cryptor states:%s\n", states.c_str());
    }
    // With failure_tolerance = -1 (ARCHITECTURE 9.3) libwebrtc never reports decryption failure;
    // a key mismatch only shows as missing video.
    Check(!HasState(b, "remote", RTC_CRYPTOR_OK), "B's receiver cryptor never reported ok");
    Check(b.matching_frames == 0, "B decoded no video with the wrong key (" +
          std::to_string(b.frames.load()) + " frames)");
  }

  running = false;
  pump.join();

  // Tear down in app order: sinks, close, then the rest.
  if (a.sink) rtc_video_sink_release(a.sink);
  if (b.sink) rtc_video_sink_release(b.sink);
  rtc_pc_close(a.pc);
  rtc_pc_close(b.pc);
  Check(rtc_pc_add_ice_candidate(a.pc, "0", 0, "candidate:1 1 udp 1 127.0.0.1 9 typ host") == 0,
        "calls after close are rejected instead of crashing");
  RunUntil([] { return false; }, std::chrono::milliseconds(200));  // drain posted tasks
  for (Peer* p : {&a, &b}) {
    if (p->channel) rtc_data_channel_release(p->channel);
    for (auto* s : p->senders) rtc_sender_release(s);
    for (auto* t : p->remote_tracks) rtc_track_release(t);
    rtc_track_release(p->video);
    if (p->audio) rtc_track_release(p->audio);
    rtc_video_source_release(p->source);
    rtc_pc_release(p->pc);
    rtc_key_provider_release(p->keys);
  }
  Check(true, "teardown completed");
}

struct SdpResult {
  bool done = false, ok = false;
  std::string type, sdp, error;
};

void RTC_CALL OnSdpResult(void* c, int32_t ok, const char* type, const char* sdp, const char* error) {
  auto* r = static_cast<SdpResult*>(c);
  std::string t = ok ? type : "", s = ok ? sdp : "", e = ok ? "" : (error ? error : "");
  Post([r, ok, t, s, e] {
    r->ok = ok != 0;
    r->type = t;
    r->sdp = s;
    r->error = e;
    r->done = true;
  });
}

struct OpResult {
  bool done = false, ok = false;
  std::string error;
};

void RTC_CALL OnOpResult(void* c, int32_t ok, const char* error) {
  auto* r = static_cast<OpResult*>(c);
  std::string e = ok ? "" : (error ? error : "");
  Post([r, ok, e] {
    r->ok = ok != 0;
    r->error = e;
    r->done = true;
  });
}

SdpResult Describe(rtc_peer_connection* pc, bool offer) {
  SdpResult result;
  if (offer) rtc_pc_create_offer(pc, OnSdpResult, &result);
  else rtc_pc_create_answer(pc, OnSdpResult, &result);
  RunUntil([&] { return result.done; }, std::chrono::seconds(10));
  return result;
}

bool ApplyRemote(Peer* peer, const SdpResult& description) {
  OpResult result;
  rtc_pc_set_remote_description(peer->pc, description.type.c_str(), description.sdp.c_str(),
                                OnOpResult, &result);
  RunUntil([&] { return result.done; }, std::chrono::seconds(10));
  if (result.ok) SetRemoteSet(peer);
  return result.ok;
}

std::vector<rtc_transceiver_info> Transceivers(rtc_peer_connection* pc) {
  const int32_t count = rtc_pc_get_transceivers(pc, nullptr, 0);
  std::vector<rtc_transceiver_info> infos(static_cast<size_t>(std::max(count, 0)));
  rtc_pc_get_transceivers(pc, infos.data(), static_cast<int32_t>(infos.size()));
  return infos;
}

// The SFU shape of a group call: a publisher with sendonly transceivers (audio added without a
// track, as when there is no microphone) and a subscriber that only receives, mapping each
// receiver to the publisher through its stream id and mid.
void RunTransceiverScenario(rtc_factory* factory) {
  std::printf("\n== sendonly publisher, recvonly subscriber (group call shape)\n");
  Peer a, b;
  a.name = "pub";
  b.name = "sub";
  a.color = kRed;
  b.color = kGreen;
  a.other = &b;
  b.other = &a;
  a.factory = b.factory = factory;
  a.keys = CreateKeys(3);
  b.keys = CreateKeys(3);
  a.pc = rtc_pc_create(factory, nullptr, 0, a.keys, &kObserver, &a);
  b.pc = rtc_pc_create(factory, nullptr, 0, b.keys, &kObserver, &b);
  Check(a.pc && b.pc, "peer connections created");
  if (!a.pc || !b.pc) return;

  a.source = rtc_custom_source_create(factory);
  a.video = rtc_video_track_create(factory, a.source, "pub-video");
  rtc_sender* audio_sender = rtc_pc_add_transceiver(a.pc, RTC_KIND_AUDIO, RTC_DIRECTION_SENDONLY,
                                                    nullptr, "publisher-1");
  rtc_sender* video_sender = rtc_pc_add_transceiver(a.pc, RTC_KIND_VIDEO, RTC_DIRECTION_SENDONLY,
                                                    a.video, "publisher-1");
  Check(audio_sender && video_sender, "sendonly transceivers added (audio without a track)");
  Check(rtc_pc_add_transceiver(a.pc, RTC_KIND_VIDEO, 7, nullptr, nullptr) == nullptr,
        "a bad direction is rejected");
  if (audio_sender) a.senders.push_back(audio_sender);
  if (video_sender) a.senders.push_back(video_sender);
  Check(rtc_pc_attach_sender_cryptors(a.pc) == 1, "one sender cryptor (the trackless sender waits)");
  a.audio = rtc_audio_track_create(factory, "pub-audio");
  if (a.audio && audio_sender) {
    Check(rtc_sender_set_track(audio_sender, a.audio) == 1, "audio track set on the sender later");
    Check(rtc_pc_attach_sender_cryptors(a.pc) == 1, "its cryptor is attached then");
  }
  Check(rtc_pc_prefer_codec(a.pc, RTC_KIND_VIDEO, "video/VP8") == 1, "publisher prefers VP8");

  std::atomic<bool> running{true};
  std::thread pump([&] {
    const int w = 320, h = 240;
    std::vector<uint8_t> y(w * h, kRed.y), u(w * h / 4, kRed.u), v(w * h / 4, kRed.v);
    while (running) {
      rtc_custom_source_push_i420(a.source, w, h, y.data(), w, u.data(), w / 2, v.data(), w / 2);
      std::this_thread::sleep_for(std::chrono::milliseconds(33));
    }
  });

  const SdpResult offer = Describe(a.pc, true);
  Check(offer.ok && VideoPrefersVp8(offer.sdp), "publish offer lists VP8 first");
  Check(offer.sdp.find("a=msid:publisher-1 ") != std::string::npos, "offer carries the stream id");
  Check(ApplyRemote(&b, offer), "subscriber applied the offer");
  Check(rtc_pc_attach_receiver_cryptors(b.pc) == 0, "receiver cryptors were already attached on track");
  const SdpResult answer = Describe(b.pc, false);
  Check(answer.ok && answer.sdp.find("a=recvonly") != std::string::npos, "subscriber answers recvonly");
  Check(ApplyRemote(&a, answer), "publisher applied the answer");

  const bool connected = RunUntil([&] {
    return b.connection_state == RTC_PC_STATE_CONNECTED && b.track_streams.size() >= 2 &&
           b.matching_frames > 10;
  }, std::chrono::seconds(20));
  Check(connected, "connected; subscriber decoded the encrypted video (" +
                       std::to_string(b.matching_frames.load()) + " frames)");
  bool streams_match = b.track_streams.size() == 2;
  for (auto& entry : b.track_streams) streams_match = streams_match && entry.second == "publisher-1";
  Check(streams_match, "on_track reports the publisher's stream id for both receivers");

  const auto infos = Transceivers(b.pc);
  bool mapped = infos.size() == 2;
  for (auto& info : infos) {
    bool known = false;
    for (auto& entry : b.track_streams) known = known || entry.first == info.receiver_id;
    mapped = mapped && known && info.mid[0] != '\0';
    std::printf("  subscriber transceiver mid=%s kind=%d direction=%d current=%d receiver=%s\n",
                info.mid, info.kind, info.direction, info.current_direction, info.receiver_id);
  }
  Check(mapped, "every subscriber transceiver has a mid and the receiver id on_track reported");
  Check(infos.size() == 2 && infos[0].current_direction == RTC_DIRECTION_RECVONLY,
        "negotiated direction is recvonly");
  const auto published = Transceivers(a.pc);
  Check(published.size() == 2 && published[0].direction == RTC_DIRECTION_SENDONLY &&
            published[1].kind == RTC_KIND_VIDEO,
        "publisher transceivers are sendonly audio + video in order");

  std::string audio_receiver;
  for (auto& info : infos) {
    if (info.kind == RTC_KIND_AUDIO) audio_receiver = info.receiver_id;
  }
  std::atomic<double> level{-2};
  bool callback_ran = false;
  for (int attempt = 0; attempt < 40 && level.load() < 0; ++attempt) {
    level = -2;
    rtc_pc_get_receiver_audio_level(b.pc, audio_receiver.c_str(), [](void* c, double l) {
      static_cast<std::atomic<double>*>(c)->store(l);
    }, &level);
    RunUntil([&] { return level.load() > -2; }, std::chrono::seconds(2));
    callback_ran = callback_ran || level.load() > -2;
    if (level.load() < 0) RunUntil([] { return false; }, std::chrono::milliseconds(250));
  }
  std::printf("  receiver audio level: %.4f\n", level.load());
  Check(callback_ran, "per-receiver audio level callback ran");
  if (a.audio) Check(level.load() >= 0, "per-receiver inbound-rtp audio level present");
  level = -2;
  rtc_pc_get_receiver_audio_level(b.pc, "no-such-receiver", [](void* c, double l) {
    static_cast<std::atomic<double>*>(c)->store(l);
  }, &level);
  RunUntil([&] { return level.load() > -2; }, std::chrono::seconds(2));
  Check(level.load() == -1, "an unknown receiver reports -1");

  running = false;
  pump.join();
  if (b.sink) rtc_video_sink_release(b.sink);
  rtc_pc_close(a.pc);
  rtc_pc_close(b.pc);
  Check(rtc_pc_get_transceivers(b.pc, nullptr, 0) == 0, "no transceivers after close");
  RunUntil([] { return false; }, std::chrono::milliseconds(200));
  for (Peer* p : {&a, &b}) {
    for (auto* s : p->senders) rtc_sender_release(s);
    for (auto* t : p->remote_tracks) rtc_track_release(t);
    if (p->video) rtc_track_release(p->video);
    if (p->audio) rtc_track_release(p->audio);
    if (p->source) rtc_video_source_release(p->source);
    rtc_pc_release(p->pc);
    rtc_key_provider_release(p->keys);
  }
  Check(true, "teardown completed");
}

// An operation on a closed connection fails synchronously on the calling thread, inside the
// connection's state lock: close/release from that callback must be refused, not deadlock.
void RunReentrantClose(rtc_factory* factory) {
  std::printf("\n== close/release from inside a callback\n");
  struct Result {
    rtc_peer_connection* pc;
    int completed = 0, closed = -1, released = -1;
    std::string error;
  };
  Peer peer;
  Result result{rtc_pc_create(factory, nullptr, 0, nullptr, &kObserver, &peer)};
  Check(result.pc != nullptr, "peer connection created");
  if (!result.pc) return;
  peer.pc = result.pc;
  Check(rtc_pc_close(result.pc) == 1, "rtc_pc_close returns 1");
  rtc_pc_create_offer(result.pc, [](void* c, int32_t ok, const char*, const char*, const char*) {
    auto* r = static_cast<Result*>(c);
    r->completed = ok ? 1 : 2;
    r->closed = rtc_pc_close(r->pc);
    r->released = rtc_pc_release(r->pc);
    r->error = rtc_last_error();
  }, &result);
  Check(result.completed == 2, "offer on a closed connection failed synchronously");
  Check(result.closed == 0 && result.released == 0, "close and release were refused");
  Check(result.error.find("inside a callback") != std::string::npos,
        "rtc_last_error explains why (" + result.error + ")");
  Check(rtc_pc_release(result.pc) == 1, "release outside the callback succeeds");
}

}  // namespace

int main() {
  std::printf("rtc_shim ABI %d, libwebrtc %s\n", rtc_shim_abi_version(), rtc_shim_libwebrtc_version());
  if (!rtc_initialize()) {
    std::printf("rtc_initialize failed: %s\n", rtc_last_error());
    return 1;
  }
  rtc_factory* factory = rtc_factory_create();
  Check(factory != nullptr, std::string("factory created ") + (factory ? "" : rtc_last_error()));
  if (!factory) return 1;
  std::printf("  cameras: %d, microphones: %d, speakers: %d\n", rtc_video_device_count(factory),
              rtc_audio_recording_device_count(factory), rtc_audio_playout_device_count(factory));

  RunScenario(factory, true);
  RunScenario(factory, false);
  RunTransceiverScenario(factory);
  RunReentrantClose(factory);

  rtc_factory_release(factory);
  rtc_terminate();
  std::printf("\n%s (%d failure%s)\n", g_failures ? "FAILED" : "OK", g_failures, g_failures == 1 ? "" : "s");
  return g_failures ? 1 : 0;
}
