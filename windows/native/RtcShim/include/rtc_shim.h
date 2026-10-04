/*
 * rtc_shim: a flat C API over webrtc-sdk/libwebrtc (C++), consumed by the C# client through
 * LibraryImport.
 *
 * Rules of the boundary:
 * - Only C types cross it: opaque handles, int32_t/double, UTF-8 `const char*`, byte buffers and
 *   function pointers. No C++ type, exception or allocator is shared with the caller.
 * - Every handle returned by a `*_create`, a getter documented as "owned" or a callback documented
 *   as "owned" must be released exactly once with its `*_release` function.
 * - Strings are copied out with the buffer pattern: `int32_t f(..., char* buf, int32_t cap)` writes at
 *   most cap - 1 bytes plus a NUL and returns the full length in bytes (without the NUL). Call it
 *   again with a larger buffer when the result is >= cap.
 * - Callbacks run on WebRTC threads (signaling, worker, capture, decoder); an immediate failure
 *   (e.g. an operation on a closed connection) completes synchronously on the calling thread.
 *   An object's callbacks are serialized under a lock that its close/release waits for.
 *   So a callback must return quickly, must not block on another thread that may call into this
 *   API, and must not close or release the object it was raised for; post that work to another
 *   thread instead. Calling rtc_pc_close / rtc_pc_release from inside any callback of the same
 *   connection (observer, SDP, result or audio level) is detected: it does nothing and returns 0.
 *   rtc_data_channel_release from inside the channel's own callback is safe.
 *   `const char*` / buffer arguments are only valid during the callback.
 * - `user` / `ctx` pointers are opaque and never dereferenced by the shim.
 */
#ifndef RTC_SHIM_H_
#define RTC_SHIM_H_

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#if defined(RTC_SHIM_BUILD)
#define RTC_SHIM_API __declspec(dllexport)
#else
#define RTC_SHIM_API __declspec(dllimport)
#endif
#define RTC_CALL __cdecl
#else
#define RTC_SHIM_API __attribute__((visibility("default")))
#define RTC_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define RTC_SHIM_ABI_VERSION 7

typedef struct rtc_factory rtc_factory;
typedef struct rtc_peer_connection rtc_peer_connection;
typedef struct rtc_track rtc_track;
typedef struct rtc_sender rtc_sender;
typedef struct rtc_video_source rtc_video_source;
typedef struct rtc_video_sink rtc_video_sink;
typedef struct rtc_data_channel rtc_data_channel;
typedef struct rtc_key_provider rtc_key_provider;
typedef struct rtc_desktop_list rtc_desktop_list;
typedef struct rtc_media_source rtc_media_source;

/* ---- Enumerations (values are part of the ABI) ------------------------------------------- */

enum {
  RTC_KIND_AUDIO = 0,
  RTC_KIND_VIDEO = 1,
};

enum { /* RTCSignalingState */
  RTC_SIGNALING_STABLE = 0,
  RTC_SIGNALING_HAVE_LOCAL_OFFER = 1,
  RTC_SIGNALING_HAVE_REMOTE_OFFER = 2,
  RTC_SIGNALING_HAVE_LOCAL_PRANSWER = 3,
  RTC_SIGNALING_HAVE_REMOTE_PRANSWER = 4,
  RTC_SIGNALING_CLOSED = 5,
};

enum { /* RTCPeerConnectionState */
  RTC_PC_STATE_NEW = 0,
  RTC_PC_STATE_CONNECTING = 1,
  RTC_PC_STATE_CONNECTED = 2,
  RTC_PC_STATE_DISCONNECTED = 3,
  RTC_PC_STATE_FAILED = 4,
  RTC_PC_STATE_CLOSED = 5,
};

enum { /* RTCIceConnectionState */
  RTC_ICE_NEW = 0,
  RTC_ICE_CHECKING = 1,
  RTC_ICE_COMPLETED = 2,
  RTC_ICE_CONNECTED = 3,
  RTC_ICE_FAILED = 4,
  RTC_ICE_DISCONNECTED = 5,
  RTC_ICE_CLOSED = 6,
};

enum { /* RTCIceGatheringState */
  RTC_ICE_GATHERING_NEW = 0,
  RTC_ICE_GATHERING_GATHERING = 1,
  RTC_ICE_GATHERING_COMPLETE = 2,
};

enum { /* RTCRtpTransceiverDirection */
  RTC_DIRECTION_SENDRECV = 0,
  RTC_DIRECTION_SENDONLY = 1,
  RTC_DIRECTION_RECVONLY = 2,
  RTC_DIRECTION_INACTIVE = 3,
  RTC_DIRECTION_STOPPED = 4,
};

enum { /* RTCDataChannelState */
  RTC_DC_CONNECTING = 0,
  RTC_DC_OPEN = 1,
  RTC_DC_CLOSING = 2,
  RTC_DC_CLOSED = 3,
};

enum { /* RTCFrameCryptionState */
  RTC_CRYPTOR_NEW = 0,
  RTC_CRYPTOR_OK = 1,
  RTC_CRYPTOR_ENCRYPTION_FAILED = 2,
  RTC_CRYPTOR_DECRYPTION_FAILED = 3,
  RTC_CRYPTOR_MISSING_KEY = 4,
  RTC_CRYPTOR_KEY_RATCHETED = 5,
  RTC_CRYPTOR_INTERNAL_ERROR = 6,
};

enum {
  RTC_KEY_DERIVATION_PBKDF2 = 0,
  RTC_KEY_DERIVATION_HKDF = 1,
};

enum {
  RTC_DESKTOP_SCREEN = 0,
  RTC_DESKTOP_WINDOW = 1,
};

enum { /* rtc_desktop_list_cb event */
  RTC_DESKTOP_SOURCE_ADDED = 0,
  RTC_DESKTOP_SOURCE_REMOVED = 1,
  RTC_DESKTOP_SOURCE_NAME_CHANGED = 2,
  RTC_DESKTOP_SOURCE_THUMBNAIL_CHANGED = 3,
};

enum { /* rtc_capture_state_cb state */
  RTC_CAPTURE_RUNNING = 0,
  RTC_CAPTURE_PAUSED = 1,
  RTC_CAPTURE_STOPPED = 2, /* desktop capture stopped, or a non-looping file reached its end */
  RTC_CAPTURE_FAILED = 3,  /* e.g. the shared window was closed, or the file cannot be decoded */
};

enum {
  RTC_LOG_VERBOSE = 0,
  RTC_LOG_INFO = 1,
  RTC_LOG_WARNING = 2,
  RTC_LOG_ERROR = 3,
  RTC_LOG_NONE = 4,
};

/* ---- Callbacks ---------------------------------------------------------------------------- */

typedef void(RTC_CALL* rtc_log_cb)(void* user, const char* message);

typedef void(RTC_CALL* rtc_sdp_cb)(void* ctx, int32_t ok, const char* type, const char* sdp,
                                   const char* error);
typedef void(RTC_CALL* rtc_result_cb)(void* ctx, int32_t ok, const char* error);
/* level is the remote inbound-rtp audioLevel in [0, 1], or -1 when no stats are available. */
typedef void(RTC_CALL* rtc_audio_level_cb)(void* ctx, double level);

/*
 * One decoded/captured frame converted to 8-bit BGRA (bytes B, G, R, A in memory, i.e.
 * DXGI_FORMAT_B8G8R8A8_UNORM). The frame is already rotated upright, so width/height are the
 * display size; `rotation` (0/90/180/270) is the rotation that was applied, for information only.
 */
typedef void(RTC_CALL* rtc_video_frame_cb)(void* user, const uint8_t* bgra, int32_t width,
                                           int32_t height, int32_t stride, int32_t rotation);

typedef void(RTC_CALL* rtc_capture_state_cb)(void* user, int32_t state);

typedef void(RTC_CALL* rtc_desktop_list_cb)(void* user, int32_t event, const char* source_id);

typedef struct rtc_pc_observer {
  void(RTC_CALL* on_signaling_state)(void* user, int32_t state);
  void(RTC_CALL* on_connection_state)(void* user, int32_t state);
  void(RTC_CALL* on_ice_connection_state)(void* user, int32_t state);
  void(RTC_CALL* on_ice_gathering_state)(void* user, int32_t state);
  void(RTC_CALL* on_ice_candidate)(void* user, const char* sdp_mid, int32_t sdp_mline_index,
                                   const char* candidate);
  /* `track` is owned by the callee (release with rtc_track_release). When the connection was
   * created with a key provider, the receiver's frame cryptor is already attached and enabled.
   * `stream_id` is the receiver's first stream id (the remote msid), "" when it has none. A
   * receiver whose transceiver became active again is reported again, with a new track handle. */
  void(RTC_CALL* on_track)(void* user, rtc_track* track, int32_t kind, const char* receiver_id,
                           const char* stream_id);
  /* `channel` is owned by the callee (release with rtc_data_channel_release). */
  void(RTC_CALL* on_data_channel)(void* user, rtc_data_channel* channel);
  void(RTC_CALL* on_renegotiation_needed)(void* user);
  void(RTC_CALL* on_cryptor_state)(void* user, const char* participant_id, int32_t state);
} rtc_pc_observer;

typedef struct rtc_data_channel_observer {
  void(RTC_CALL* on_state)(void* user, int32_t state);
  void(RTC_CALL* on_message)(void* user, const uint8_t* data, int32_t length, int32_t binary);
} rtc_data_channel_observer;

typedef struct rtc_ice_server {
  const char* uri;
  const char* username; /* may be NULL */
  const char* password; /* may be NULL */
} rtc_ice_server;

/* Mirrors libwebrtc::KeyProviderOptions (see ARCHITECTURE.md 9.3 for the values the demo uses). */
typedef struct rtc_key_provider_options {
  int32_t shared_key;
  const uint8_t* ratchet_salt;
  int32_t ratchet_salt_length;
  const uint8_t* uncrypted_magic_bytes; /* may be NULL */
  int32_t uncrypted_magic_bytes_length;
  int32_t ratchet_window_size;
  int32_t failure_tolerance;
  int32_t key_ring_size;
  int32_t discard_frame_when_cryptor_not_ready;
  int32_t key_derivation; /* RTC_KEY_DERIVATION_* */
} rtc_key_provider_options;

/* One transceiver of a connection, as rtc_pc_get_transceivers reports it. */
typedef struct rtc_transceiver_info {
  int32_t kind;              /* RTC_KIND_* */
  int32_t direction;         /* RTC_DIRECTION_*, the local preference */
  int32_t current_direction; /* RTC_DIRECTION_*, the negotiated one as libwebrtc reports it */
  char mid[64];              /* "" until negotiated */
  char receiver_id[192];     /* the id on_track reported for this transceiver's receiver */
} rtc_transceiver_info;

/* ---- Library ------------------------------------------------------------------------------ */

RTC_SHIM_API int32_t RTC_CALL rtc_shim_abi_version(void);
/* The libwebrtc release the shim was built against, e.g. "m150.7871.03". */
RTC_SHIM_API const char* RTC_CALL rtc_shim_libwebrtc_version(void);
/* Message of the last failure on the calling thread ("" when none). Valid until the next call. */
RTC_SHIM_API const char* RTC_CALL rtc_last_error(void);

RTC_SHIM_API int32_t RTC_CALL rtc_initialize(void);
RTC_SHIM_API void RTC_CALL rtc_terminate(void);
/* `cb` == NULL removes the sink. Messages arrive on arbitrary WebRTC threads. */
RTC_SHIM_API void RTC_CALL rtc_set_log_callback(int32_t min_severity, rtc_log_cb cb, void* user);

/* ---- Factory and devices ------------------------------------------------------------------ */

RTC_SHIM_API rtc_factory* RTC_CALL rtc_factory_create(void);
RTC_SHIM_API void RTC_CALL rtc_factory_release(rtc_factory* factory);

RTC_SHIM_API int32_t RTC_CALL rtc_video_device_count(rtc_factory* factory);
/* Returns 0 on success. */
RTC_SHIM_API int32_t RTC_CALL rtc_video_device_info(rtc_factory* factory, uint32_t index,
                                                    char* name, int32_t name_cap,
                                                    char* unique_id, int32_t unique_id_cap);

/* The audio device infos take RTC_AUDIO_DEFAULT_DEVICE as an index: on Windows it reads the default
 * communications device (its guid is the endpoint id, as for listed devices); elsewhere it fails.
 * The set_*_device functions only take list indices. */
#define RTC_AUDIO_DEFAULT_DEVICE 0xFFFFu

RTC_SHIM_API int32_t RTC_CALL rtc_audio_recording_device_count(rtc_factory* factory);
RTC_SHIM_API int32_t RTC_CALL rtc_audio_recording_device_info(rtc_factory* factory,
                                                              uint32_t index, char* name,
                                                              int32_t name_cap, char* guid,
                                                              int32_t guid_cap);
RTC_SHIM_API int32_t RTC_CALL rtc_audio_set_recording_device(rtc_factory* factory,
                                                             uint32_t index);
RTC_SHIM_API int32_t RTC_CALL rtc_audio_playout_device_count(rtc_factory* factory);
RTC_SHIM_API int32_t RTC_CALL rtc_audio_playout_device_info(rtc_factory* factory, uint32_t index,
                                                            char* name, int32_t name_cap,
                                                            char* guid, int32_t guid_cap);
RTC_SHIM_API int32_t RTC_CALL rtc_audio_set_playout_device(rtc_factory* factory,
                                                           uint32_t index);

/* ---- Tracks ------------------------------------------------------------------------------- */

/* Microphone track (echo cancellation, AGC and noise suppression on). */
RTC_SHIM_API rtc_track* RTC_CALL rtc_audio_track_create(rtc_factory* factory,
                                                        const char* track_id);
RTC_SHIM_API rtc_track* RTC_CALL rtc_video_track_create(rtc_factory* factory,
                                                        rtc_video_source* source,
                                                        const char* track_id);
RTC_SHIM_API int32_t RTC_CALL rtc_track_kind(rtc_track* track);
RTC_SHIM_API int32_t RTC_CALL rtc_track_id(rtc_track* track, char* buf, int32_t cap);
RTC_SHIM_API int32_t RTC_CALL rtc_track_set_enabled(rtc_track* track, int32_t enabled);
RTC_SHIM_API int32_t RTC_CALL rtc_track_is_enabled(rtc_track* track);
RTC_SHIM_API void RTC_CALL rtc_track_release(rtc_track* track);

/* ---- Video sources ------------------------------------------------------------------------ */

/* Opens camera `device_index` (as listed by rtc_video_device_info) and starts capturing. On
 * Windows it captures through Media Foundation, as the Camera app does, trying the formats closest
 * to width x height @ fps until one delivers frames (a few seconds each), and falls back to
 * libwebrtc's DirectShow capturer for cameras Media Foundation doesn't list (DirectShow virtual
 * cameras) or can't read. It blocks until the camera delivers frames, so call it off the UI thread.
 * The log callback reports the format chosen (info) and why Media Foundation failed (warning). */
RTC_SHIM_API rtc_video_source* RTC_CALL rtc_camera_source_create(rtc_factory* factory,
                                                                 uint32_t device_index,
                                                                 uint32_t width, uint32_t height,
                                                                 uint32_t fps);
/* Starts or stops the underlying camera/desktop capturer without destroying the source (the
 * camera light goes off while stopped). Returns 1 on success. A camera cannot start again once
 * stopped (the device is released), nor once Media Foundation stopped delivering frames (unplugged,
 * taken by another app): starting it returns 0, and the caller opens a new camera source. */
RTC_SHIM_API int32_t RTC_CALL rtc_video_source_set_capturing(rtc_video_source* source,
                                                             int32_t capturing);
/* A source fed by rtc_custom_source_push_i420 (e.g. processed or synthetic frames). */
RTC_SHIM_API rtc_video_source* RTC_CALL rtc_custom_source_create(rtc_factory* factory);
RTC_SHIM_API int32_t RTC_CALL rtc_custom_source_push_i420(
    rtc_video_source* source, int32_t width, int32_t height, const uint8_t* y, int32_t stride_y,
    const uint8_t* u, int32_t stride_u, const uint8_t* v, int32_t stride_v);
/* Captures a screen or window picked from an rtc_desktop_list. `cb` reports capture state. */
RTC_SHIM_API rtc_video_source* RTC_CALL rtc_desktop_source_create(
    rtc_factory* factory, rtc_media_source* media_source, uint32_t fps, int32_t show_cursor,
    rtc_capture_state_cb cb, void* user);
/* Decodes a local video file (Media Foundation, Windows only) at its own frame rate into a
 * custom source. Returns NULL on other platforms or when the file cannot be opened. */
RTC_SHIM_API rtc_video_source* RTC_CALL rtc_file_source_create(rtc_factory* factory,
                                                               const char* utf8_path,
                                                               int32_t loop,
                                                               rtc_capture_state_cb cb,
                                                               void* user);
/* Stops capturing; no state callback runs after this returns. */
RTC_SHIM_API void RTC_CALL rtc_video_source_release(rtc_video_source* source);

/* ---- Video sinks -------------------------------------------------------------------------- */

/* Delivers the frames of a video track (local or remote). max_width/max_height (0 = no limit)
 * downscale large frames, keeping the aspect ratio. */
RTC_SHIM_API rtc_video_sink* RTC_CALL rtc_video_sink_create(rtc_track* video_track,
                                                            int32_t max_width,
                                                            int32_t max_height,
                                                            rtc_video_frame_cb cb, void* user);
RTC_SHIM_API void RTC_CALL rtc_video_sink_set_max_size(rtc_video_sink* sink, int32_t max_width,
                                                       int32_t max_height);
/* No frame callback runs after this returns. */
RTC_SHIM_API void RTC_CALL rtc_video_sink_release(rtc_video_sink* sink);

/* ---- Screen / window sources -------------------------------------------------------------- */

RTC_SHIM_API rtc_desktop_list* RTC_CALL rtc_desktop_list_create(rtc_factory* factory,
                                                                int32_t type,
                                                                rtc_desktop_list_cb cb,
                                                                void* user);
/* Refreshes the list (blocking: call it off the UI thread) and returns the number of sources.
 * Thumbnails are captured asynchronously and reported with RTC_DESKTOP_SOURCE_THUMBNAIL_CHANGED. */
RTC_SHIM_API int32_t RTC_CALL rtc_desktop_list_update(rtc_desktop_list* list,
                                                      int32_t force_reload,
                                                      int32_t thumbnails);
/* Owned. */
RTC_SHIM_API rtc_media_source* RTC_CALL rtc_desktop_list_source(rtc_desktop_list* list,
                                                                int32_t index);
RTC_SHIM_API void RTC_CALL rtc_desktop_list_release(rtc_desktop_list* list);

RTC_SHIM_API int32_t RTC_CALL rtc_media_source_id(rtc_media_source* source, char* buf,
                                                  int32_t cap);
RTC_SHIM_API int32_t RTC_CALL rtc_media_source_name(rtc_media_source* source, char* buf,
                                                    int32_t cap);
RTC_SHIM_API int32_t RTC_CALL rtc_media_source_type(rtc_media_source* source);
/* JPEG thumbnail. Returns its size; copies min(size, cap) bytes when buf != NULL. */
RTC_SHIM_API int32_t RTC_CALL rtc_media_source_thumbnail(rtc_media_source* source, uint8_t* buf,
                                                         int32_t cap);
RTC_SHIM_API void RTC_CALL rtc_media_source_release(rtc_media_source* source);

/* ---- End-to-end encryption ---------------------------------------------------------------- */

RTC_SHIM_API rtc_key_provider* RTC_CALL rtc_key_provider_create(
    const rtc_key_provider_options* options);
RTC_SHIM_API int32_t RTC_CALL rtc_key_provider_set_shared_key(rtc_key_provider* provider,
                                                              int32_t index, const uint8_t* key,
                                                              int32_t length);
RTC_SHIM_API void RTC_CALL rtc_key_provider_release(rtc_key_provider* provider);

/* ---- Peer connection ---------------------------------------------------------------------- */

/* `key_provider` may be NULL (no E2EE). With a key provider, receiver cryptors are attached as
 * tracks arrive and rtc_pc_attach_sender_cryptors attaches the sender ones; every cryptor uses
 * AES-GCM and key index 0 and is enabled immediately. */
RTC_SHIM_API rtc_peer_connection* RTC_CALL rtc_pc_create(rtc_factory* factory,
                                                         const rtc_ice_server* ice_servers,
                                                         int32_t ice_server_count,
                                                         rtc_key_provider* key_provider,
                                                         const rtc_pc_observer* observer,
                                                         void* user);
/* Owned. */
RTC_SHIM_API rtc_sender* RTC_CALL rtc_pc_add_track(rtc_peer_connection* pc, rtc_track* track,
                                                   const char* stream_id);
/* Adds a transceiver of `kind` with `direction` (RTC_DIRECTION_*). `track` (may be NULL: the
 * sender then sends nothing until rtc_sender_set_track) and `stream_id` (may be NULL) only matter
 * for sending. Returns the transceiver's sender (owned). */
RTC_SHIM_API rtc_sender* RTC_CALL rtc_pc_add_transceiver(rtc_peer_connection* pc, int32_t kind,
                                                         int32_t direction, rtc_track* track,
                                                         const char* stream_id);
/* Fills at most `capacity` entries of `out` (may be NULL with capacity 0), in m-line order, and
 * returns the number of transceivers. */
RTC_SHIM_API int32_t RTC_CALL rtc_pc_get_transceivers(rtc_peer_connection* pc,
                                                      rtc_transceiver_info* out,
                                                      int32_t capacity);
/* Replaces the track on a sender without renegotiation; the sender's cryptor is kept. */
RTC_SHIM_API int32_t RTC_CALL rtc_sender_set_track(rtc_sender* sender, rtc_track* track);
RTC_SHIM_API void RTC_CALL rtc_sender_release(rtc_sender* sender);
/* Owned. */
RTC_SHIM_API rtc_data_channel* RTC_CALL rtc_pc_create_data_channel(rtc_peer_connection* pc,
                                                                   const char* label);
/* Creates an offer/answer and applies it as the local description; `cb` gets the final SDP. */
RTC_SHIM_API void RTC_CALL rtc_pc_create_offer(rtc_peer_connection* pc, rtc_sdp_cb cb,
                                               void* ctx);
RTC_SHIM_API void RTC_CALL rtc_pc_create_answer(rtc_peer_connection* pc, rtc_sdp_cb cb,
                                                void* ctx);
RTC_SHIM_API void RTC_CALL rtc_pc_set_remote_description(rtc_peer_connection* pc,
                                                         const char* type, const char* sdp,
                                                         rtc_result_cb cb, void* ctx);
RTC_SHIM_API int32_t RTC_CALL rtc_pc_add_ice_candidate(rtc_peer_connection* pc,
                                                       const char* sdp_mid,
                                                       int32_t sdp_mline_index,
                                                       const char* candidate);
/* Puts `mime_type` (e.g. "video/VP8") first in the codec preferences of every transceiver of
 * `kind`. Returns the number of transceivers updated. */
RTC_SHIM_API int32_t RTC_CALL rtc_pc_prefer_codec(rtc_peer_connection* pc, int32_t kind,
                                                  const char* mime_type);
/* Returns the number of cryptors attached by this call; senders without a track are skipped
 * (call again after rtc_sender_set_track gives them one). */
RTC_SHIM_API int32_t RTC_CALL rtc_pc_attach_sender_cryptors(rtc_peer_connection* pc);
/* Attaches a decrypting cryptor to every receiver that has none yet, e.g. right after
 * rtc_pc_set_remote_description and before the answer, so a receiver reused by a renegotiation
 * decrypts from its first frame. Returns the number attached. */
RTC_SHIM_API int32_t RTC_CALL rtc_pc_attach_receiver_cryptors(rtc_peer_connection* pc);
/* The loudest inbound audio level of the whole connection. */
RTC_SHIM_API void RTC_CALL rtc_pc_get_remote_audio_level(rtc_peer_connection* pc,
                                                         rtc_audio_level_cb cb, void* ctx);
/* The inbound audio level of one receiver (an SFU forwards every track with the same track id,
 * so per-participant levels need per-receiver stats). -1 when it is unknown. */
RTC_SHIM_API void RTC_CALL rtc_pc_get_receiver_audio_level(rtc_peer_connection* pc,
                                                           const char* receiver_id,
                                                           rtc_audio_level_cb cb, void* ctx);
/* Our own microphone as WebRTC measures it before encoding: the audio media-source audioLevel
 * in [0, 1] (a peak over the last ~100 ms), -1 when no audio track is attached or recording. */
RTC_SHIM_API void RTC_CALL rtc_pc_get_local_audio_level(rtc_peer_connection* pc,
                                                        rtc_audio_level_cb cb, void* ctx);
/* The packets received on all inbound audio streams (inbound-rtp packetsReceived), reported
 * through the level callback as a count; -1 when no stats are available. */
RTC_SHIM_API void RTC_CALL rtc_pc_get_inbound_audio_packets(rtc_peer_connection* pc,
                                                            rtc_audio_level_cb cb, void* ctx);
/* Closes the connection and drops the cryptors. No observer callback runs after this returns;
 * pending rtc_sdp_cb/rtc_result_cb may still complete (with ok = 0) or never be called.
 * Blocks until a callback in progress on another thread returns. Returns 1, or 0 (and does
 * nothing, see rtc_last_error) when called from inside a callback of this connection. */
RTC_SHIM_API int32_t RTC_CALL rtc_pc_close(rtc_peer_connection* pc);
/* Closes, then frees the handle. Returns 0 and keeps the handle valid under the same condition
 * as rtc_pc_close. */
RTC_SHIM_API int32_t RTC_CALL rtc_pc_release(rtc_peer_connection* pc);

/* ---- Data channel ------------------------------------------------------------------------- */

RTC_SHIM_API void RTC_CALL rtc_data_channel_set_observer(rtc_data_channel* channel,
                                                         const rtc_data_channel_observer* observer,
                                                         void* user);
RTC_SHIM_API int32_t RTC_CALL rtc_data_channel_send(rtc_data_channel* channel,
                                                    const uint8_t* data, int32_t length,
                                                    int32_t binary);
RTC_SHIM_API int32_t RTC_CALL rtc_data_channel_state(rtc_data_channel* channel);
RTC_SHIM_API int32_t RTC_CALL rtc_data_channel_label(rtc_data_channel* channel, char* buf,
                                                     int32_t cap);
RTC_SHIM_API void RTC_CALL rtc_data_channel_close(rtc_data_channel* channel);
/* Unregisters the observer and frees the handle without closing the channel; no callback runs
 * after this returns (other than the one it is called from, if any). */
RTC_SHIM_API void RTC_CALL rtc_data_channel_release(rtc_data_channel* channel);

#ifdef __cplusplus
}
#endif

#endif /* RTC_SHIM_H_ */