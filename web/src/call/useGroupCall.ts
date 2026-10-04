import { computed, readonly, ref, shallowRef } from 'vue'
import { fromBase64, generateKeyMaterial, toBase64 } from '@/e2ee'
import { createFrameCrypto } from './frameCrypto'
import { createLocalMedia, preferVp8 } from './media'
import { loadSfuUrl, normalizeSfuUrl, probeServer, saveSfuUrl, webSocketUrl } from './serverUrl'
import { DEFAULT_REMOTE_MEDIA, type CallEvent, type ChatMessage, type MediaState, type Phase, type ServerStatus } from './types'

// Group call through the SFU in sfu-server/ (protocol in ARCHITECTURE.md).
// Two connections to the SFU: we offer once on the publish PC and never renegotiate it; the SFU
// offers (and re-offers) the subscribe PC, whose streams have msid = the sender's participantId.

const ICE_SERVERS: RTCIceServer[] = [{ urls: 'stun:stun.l.google.com:19302' }]
const CLIENT_NAME = 'Web'
const SPEAKER_POLL_MS = 300
/** audioLevel (0..1) above which someone counts as speaking */
const SPEAKER_THRESHOLD = 0.03
/** The highlight stays this long after the speaker goes quiet, so it does not flicker between words. */
const SPEAKER_HOLD_MS = 1200

type PcName = 'publish' | 'subscribe'

interface CandidateStats { candidateType?: string; address?: string; port?: number; networkType?: string }

/**
 * Logged when a connection drops: which network paths it tried and which one carried it, the
 * first thing to check when media fails. Silent while everything works.
 */
async function logIcePairs(name: PcName, state: RTCPeerConnectionState, stats: Promise<RTCStatsReport>) {
  // A failed connection is closed right after, and a closed one often reports no pairs.
  const report = await stats.catch(() => null)
  if (!report) return
  const describe = (id: string) => {
    const c = report.get(id) as CandidateStats | undefined
    return c ? `${c.candidateType} ${c.address ?? '?'}:${c.port}${c.networkType ? ` (${c.networkType})` : ''}` : '?'
  }
  const pairs: Record<string, unknown>[] = []
  report.forEach(s => {
    if (s.type !== 'candidate-pair') return
    const pair = s as RTCIceCandidatePairStats
    pairs.push({
      state: pair.state,
      nominated: pair.nominated,
      local: describe(pair.localCandidateId),
      remote: describe(pair.remoteCandidateId),
      requestsSent: pair.requestsSent,
      responsesReceived: pair.responsesReceived,
      requestsReceived: pair.requestsReceived,
      bytesSent: pair.bytesSent,
      bytesReceived: pair.bytesReceived,
    })
  })
  if (!pairs.length) return
  console.warn(`[group call] ${name}: ${state}, ICE candidate pairs:`)
  console.table(pairs)
}

interface ParticipantInfo { id: string; name: string; state: MediaState }

type ServerMessage =
  | { type: 'joined'; participantId: string; participants: ParticipantInfo[]; e2ee: boolean; e2eeKey?: string }
  | { type: 'answer'; pc: PcName; sdp: string }
  | { type: 'offer'; pc: PcName; sdp: string }
  | { type: 'candidate'; pc: PcName; candidate: RTCIceCandidateInit }
  | { type: 'participant joined'; participant: ParticipantInfo }
  | { type: 'participant left'; participantId: string }
  | { type: 'media state'; participantId: string; state: Partial<MediaState> }
  | { type: 'chat'; participantId: string; name: string; text: string }
  | { type: 'error'; message: string; fatal: boolean }

export interface Participant {
  id: string
  name: string
  /** name + short id, since several devices can share a name */
  label: string
  state: MediaState
  /** Everything received from this participant; null until the first track arrives. */
  stream: MediaStream | null
  hasVideo: boolean
}

interface RemoteTrack { track: MediaStreamTrack; receiver: RTCRtpReceiver }
interface RemoteTracks { audio?: RemoteTrack; video?: RemoteTrack; stream: MediaStream | null }

export function participantLabel(id: string, name: string) {
  return `${name || 'Guest'} · ${id.slice(0, 4)}`
}

function createGroupCall() {
  // ---- Reactive state -------------------------------------------------------------------------

  const roomId = ref('')
  const e2ee = ref(false)
  const inRoom = ref(false)
  const joined = ref(false)
  const participantId = ref('')
  const serverUrl = ref(loadSfuUrl())
  const serverStatus = ref<ServerStatus>('connecting')
  const messages = ref<ChatMessage[]>([])

  /** Local only and for everyone at once, like the 1:1 options. */
  const remoteAudioMuted = ref(false)
  const remoteVideoHidden = ref(false)

  /** Everyone else, in join order. Replaced on every change; streams must not be made reactive. */
  const participants = shallowRef<Participant[]>([])
  const audioLevels = shallowRef<Record<string, number>>({})
  const activeSpeakerId = ref<string | null>(null)
  /** How the others see this device; empty until the server assigns the id. */
  const selfLabel = computed(() => (participantId.value ? participantLabel(participantId.value, CLIENT_NAME) : ''))

  const phase = computed<Phase>(() => {
    if (!inRoom.value) return 'idle'
    if (!joined.value) return 'connecting'
    return participants.value.length ? 'connected' : 'waiting'
  })

  const listeners = new Set<(event: CallEvent) => void>()
  function onEvent(listener: (event: CallEvent) => void) {
    listeners.add(listener)
    return () => listeners.delete(listener)
  }
  function emit(event: CallEvent) {
    listeners.forEach(listener => listener(event))
  }

  // ---- Internals ------------------------------------------------------------------------------

  let socket: WebSocket | null = null
  /** Bumped by leave(); async work started for an earlier call checks it before touching state. */
  let session = 0
  let publishPc: RTCPeerConnection | null = null
  let subscribePc: RTCPeerConnection | null = null
  let videoTransceiver: RTCRtpTransceiver | null = null
  let audioTransceiver: RTCRtpTransceiver | null = null
  let pendingCandidates: Record<PcName, RTCIceCandidateInit[]> = { publish: [], subscribe: [] }
  /** Subscribe offers are answered one at a time. */
  let subscribeQueue: Promise<void> = Promise.resolve()
  let nextMessageId = 1

  /** Who is in the room (from signaling) and what we receive from them (from ontrack). Either can come first. */
  const infos = new Map<string, ParticipantInfo>()
  const remoteTracks = new Map<string, RemoteTracks>()

  const frameCrypto = createFrameCrypto()
  const media = createLocalMedia({
    inCall: () => inRoom.value,
    videoSender: () => videoTransceiver?.sender,
    audioSender: () => audioTransceiver?.sender,
    sendMediaState,
    emit,
  })
  let mediaReady: Promise<void> = Promise.resolve()

  let speakerTimer: ReturnType<typeof setInterval> | null = null
  let speakerSince = 0

  // ---- Signaling ------------------------------------------------------------------------------

  function send(message: object) {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(message))
  }

  function sendMediaState() {
    if (joined.value) send({ type: 'media state', state: media.mediaState() })
  }

  function connect(keyMaterial: string | undefined) {
    const ws = new WebSocket(webSocketUrl(serverUrl.value))
    socket = ws

    ws.onopen = () => {
      if (ws !== socket) return
      send({ type: 'join', roomId: roomId.value, name: CLIENT_NAME, e2ee: e2ee.value, ...(keyMaterial && { e2eeKey: keyMaterial }) })
    }

    ws.onmessage = event => {
      if (ws !== socket) return
      let message: ServerMessage
      try {
        message = JSON.parse(String(event.data))
      } catch {
        return
      }
      handleMessage(message)
    }

    // No session resume: a closed socket means the server has already dropped us from the room.
    ws.onclose = () => {
      if (ws !== socket) return
      const wasJoined = joined.value
      leave()
      emit({
        type: 'error',
        message: wasJoined ? 'Lost the connection to the group call server.' : "Can't reach the group call server.",
      })
    }
  }

  function handleMessage(message: ServerMessage) {
    switch (message.type) {
      case 'joined':
        onJoined(message)
        break
      case 'answer':
        if (message.pc === 'publish') applyPublishAnswer(message.sdp)
        break
      case 'offer':
        if (message.pc === 'subscribe') {
          const sdp = message.sdp
          subscribeQueue = subscribeQueue.then(() => answerSubscribeOffer(sdp))
        }
        break
      case 'candidate':
        addRemoteCandidate(message.pc, message.candidate)
        break
      case 'participant joined':
        if (message.participant.id === participantId.value) break
        infos.set(message.participant.id, normalizeInfo(message.participant))
        publishParticipants()
        emit({ type: 'peer-joined', name: participantLabel(message.participant.id, message.participant.name) })
        break
      case 'participant left': {
        const info = infos.get(message.participantId)
        infos.delete(message.participantId)
        remoteTracks.delete(message.participantId)
        publishParticipants()
        if (info) emit({ type: 'peer-left', name: participantLabel(info.id, info.name) })
        break
      }
      case 'media state': {
        const info = infos.get(message.participantId)
        if (!info) break
        infos.set(info.id, { ...info, state: normalizeState(message.state) })
        publishParticipants()
        break
      }
      case 'chat':
        messages.value.push({
          id: nextMessageId++,
          text: message.text,
          timestamp: Date.now(),
          isLocal: false,
          name: participantLabel(message.participantId, message.name),
        })
        break
      case 'error':
        if (message.fatal) {
          // The server closes the socket next; this message explains it better than the close would.
          leave()
          emit({ type: 'error', message: message.message })
        } else {
          emit({ type: 'info', message: message.message })
        }
        break
    }
  }

  function normalizeState(state: Partial<MediaState> | undefined): MediaState {
    return {
      audio: state?.audio ?? DEFAULT_REMOTE_MEDIA.audio,
      video: state?.video ?? DEFAULT_REMOTE_MEDIA.video,
      screen: state?.screen ?? DEFAULT_REMOTE_MEDIA.screen,
    }
  }

  function normalizeInfo(info: ParticipantInfo): ParticipantInfo {
    return { id: info.id, name: info.name, state: normalizeState(info.state) }
  }

  async function onJoined(message: Extract<ServerMessage, { type: 'joined' }>) {
    const current = session
    participantId.value = message.participantId
    message.participants?.forEach(info => infos.set(info.id, normalizeInfo(info)))
    joined.value = true
    publishParticipants()

    if (e2ee.value) {
      let key: ArrayBuffer
      try {
        key = fromBase64(message.e2eeKey ?? '')
      } catch {
        key = new ArrayBuffer(0)
      }
      if (!key.byteLength) {
        leave()
        emit({ type: 'error', message: 'The server did not send an encryption key for this room.' })
        return
      }
      await frameCrypto.setKey(key)
    }

    await mediaReady
    if (current !== session) return
    sendMediaState()
    startPublishing()
  }

  /** Server candidates are already in its SDP; this only covers servers that trickle. */
  async function addRemoteCandidate(name: PcName, candidate: RTCIceCandidateInit) {
    const pc = name === 'publish' ? publishPc : subscribePc
    if (!pc || !pc.remoteDescription) {
      pendingCandidates[name].push(candidate)
      return
    }
    await pc.addIceCandidate(candidate).catch(error => console.warn('Ignored ICE candidate:', error))
  }

  async function addPendingCandidates(name: PcName, pc: RTCPeerConnection) {
    const candidates = pendingCandidates[name]
    pendingCandidates[name] = []
    for (const candidate of candidates) {
      await pc.addIceCandidate(candidate).catch(error => console.warn('Ignored ICE candidate:', error))
    }
  }

  function createPeerConnection(name: PcName) {
    const pc = new RTCPeerConnection({ iceServers: ICE_SERVERS })
    const isCurrent = () => pc === (name === 'publish' ? publishPc : subscribePc)
    pc.onicecandidate = event => {
      // The end-of-candidates event has no candidate (or an empty one).
      if (isCurrent() && event.candidate?.candidate) {
        const { candidate, sdpMid, sdpMLineIndex } = event.candidate
        send({ type: 'candidate', pc: name, candidate: { candidate, sdpMid, sdpMLineIndex } })
      }
    }
    // Without TURN or ICE restarts a failed connection stays failed.
    pc.onconnectionstatechange = () => {
      if (!isCurrent()) return
      const state = pc.connectionState
      if (state === 'disconnected' || state === 'failed') logIcePairs(name, state, pc.getStats())
      if (state !== 'failed') return
      leave()
      emit({ type: 'error', message: "Couldn't connect the media to the group call server." })
    }
    return pc
  }

  function updateCodecMap() {
    if (!e2ee.value) return
    frameCrypto.updateCodecMap(
      publishPc?.localDescription?.sdp, publishPc?.remoteDescription?.sdp,
      subscribePc?.localDescription?.sdp, subscribePc?.remoteDescription?.sdp,
    )
  }

  // ---- Publish PC -----------------------------------------------------------------------------

  async function startPublishing() {
    const pc = createPeerConnection('publish')
    publishPc = pc

    // One sendonly audio and one sendonly video m-line, even with the mic or camera missing:
    // every later source switch is a replaceTrack() on these senders.
    const streams = [media.sendStream()]
    const audio = pc.addTransceiver(media.micTrack() ?? 'audio', { direction: 'sendonly', streams })
    const video = pc.addTransceiver(media.outgoingVideoTrack() ?? 'video', { direction: 'sendonly', streams })
    videoTransceiver = video
    audioTransceiver = audio
    preferVp8([video])
    if (e2ee.value) {
      frameCrypto.attach(audio.sender, 'encrypt', 'audio')
      frameCrypto.attach(video.sender, 'encrypt', 'video')
    }

    try {
      const offer = await pc.createOffer()
      if (pc !== publishPc) return
      await pc.setLocalDescription(offer)
      updateCodecMap()
      send({ type: 'offer', pc: 'publish', sdp: offer.sdp })
    } catch (error) {
      if (pc === publishPc) console.error('Failed to publish:', error)
    }
  }

  async function applyPublishAnswer(sdp: string) {
    const pc = publishPc
    if (!pc) return
    try {
      await pc.setRemoteDescription({ type: 'answer', sdp })
      if (pc !== publishPc) return
      await addPendingCandidates('publish', pc)
      updateCodecMap()
    } catch (error) {
      if (pc === publishPc) console.error('Failed to apply the publish answer:', error)
    }
  }

  // ---- Subscribe PC ---------------------------------------------------------------------------

  async function answerSubscribeOffer(sdp: string) {
    if (!inRoom.value) return
    if (!subscribePc) subscribePc = createSubscribePc()
    const pc = subscribePc
    try {
      await pc.setRemoteDescription({ type: 'offer', sdp })
      if (pc !== subscribePc) return
      await addPendingCandidates('subscribe', pc)
      const answer = await pc.createAnswer()
      if (pc !== subscribePc) return
      await pc.setLocalDescription(answer)
      updateCodecMap()
      send({ type: 'answer', pc: 'subscribe', sdp: answer.sdp })
    } catch (error) {
      if (pc === subscribePc) console.error('Failed to answer the subscribe offer:', error)
    }
  }

  function createSubscribePc() {
    const pc = createPeerConnection('subscribe')
    pc.ontrack = event => {
      if (pc !== subscribePc) return
      const kind = event.track.kind
      const stream = event.streams[0]
      if (!stream || (kind !== 'audio' && kind !== 'video')) return
      if (e2ee.value) frameCrypto.attach(event.receiver, 'decrypt', kind)
      event.track.enabled = kind === 'audio' ? !remoteAudioMuted.value : !remoteVideoHidden.value

      const id = stream.id
      // Inactive m-lines are recycled, so a receiver's track can move to another participant.
      remoteTracks.forEach((tracks, otherId) => {
        if (otherId !== id && tracks[kind]?.track === event.track) delete tracks[kind]
      })
      const tracks = remoteTracks.get(id) ?? { stream: null }
      tracks[kind] = { track: event.track, receiver: event.receiver }
      remoteTracks.set(id, tracks)
      // The msid stream loses the track when the SFU stops forwarding it (participant gone or m-line recycled).
      stream.onremovetrack = removed => {
        const current = remoteTracks.get(id)
        if (!current) return
        if (current.audio?.track === removed.track) delete current.audio
        if (current.video?.track === removed.track) delete current.video
        publishParticipants()
      }
      publishParticipants()
    }
    return pc
  }

  /** Rebuilds the participant list; a participant's stream object only changes with its tracks. */
  function publishParticipants() {
    const list: Participant[] = []
    remoteTracks.forEach(tracks => {
      const wanted = [tracks.audio?.track, tracks.video?.track].filter((t): t is MediaStreamTrack => !!t)
      const have = tracks.stream?.getTracks() ?? []
      if (wanted.length !== have.length || wanted.some(t => !have.includes(t))) {
        tracks.stream = wanted.length ? new MediaStream(wanted) : null
      }
    })
    infos.forEach(info => {
      const tracks = remoteTracks.get(info.id)
      list.push({
        ...info,
        label: participantLabel(info.id, info.name),
        stream: tracks?.stream ?? null,
        hasVideo: !!tracks?.video,
      })
    })
    participants.value = list
    if (activeSpeakerId.value && !infos.has(activeSpeakerId.value)) activeSpeakerId.value = null
  }

  // ---- Active speaker -------------------------------------------------------------------------

  /** Per-receiver getStats(): every forwarded track has the id "audio", so stats alone can't tell them apart. */
  async function pollAudioLevels() {
    const current = session
    const levels: Record<string, number> = {}
    await Promise.all([...remoteTracks].map(async ([id, tracks]) => {
      if (!tracks.audio || !infos.has(id)) return
      const stats = await tracks.audio.receiver.getStats().catch(() => null)
      stats?.forEach(report => {
        if (report.type === 'inbound-rtp' && report.kind === 'audio') levels[id] = report.audioLevel ?? 0
      })
    }))
    if (current !== session) return
    audioLevels.value = levels

    let loudest: string | null = null
    let max = SPEAKER_THRESHOLD
    for (const [id, level] of Object.entries(levels)) {
      if (level > max && infos.get(id)?.state.audio !== false) {
        loudest = id
        max = level
      }
    }
    const now = performance.now()
    if (loudest) {
      activeSpeakerId.value = loudest
      speakerSince = now
    } else if (activeSpeakerId.value && now - speakerSince > SPEAKER_HOLD_MS) {
      activeSpeakerId.value = null
    }
  }

  // ---- Chat and remote media ------------------------------------------------------------------

  function sendMessage(text: string) {
    const trimmed = text.trim()
    if (!joined.value || !trimmed) return false
    send({ type: 'chat', text: trimmed })
    messages.value.push({ id: nextMessageId++, text: trimmed, timestamp: Date.now(), isLocal: true })
    return true
  }

  function toggleRemoteAudio() {
    remoteAudioMuted.value = !remoteAudioMuted.value
    remoteTracks.forEach(tracks => {
      if (tracks.audio) tracks.audio.track.enabled = !remoteAudioMuted.value
    })
  }

  function toggleRemoteVideo() {
    remoteVideoHidden.value = !remoteVideoHidden.value
    remoteTracks.forEach(tracks => {
      if (tracks.video) tracks.video.track.enabled = !remoteVideoHidden.value
    })
  }

  // ---- Server address -------------------------------------------------------------------------

  async function checkServer() {
    const url = serverUrl.value
    const ok = await probeServer(url, 'sfu-server')
    if (url === serverUrl.value) serverStatus.value = ok ? 'connected' : 'unreachable'
  }

  /** Switches to another SFU server and remembers it; only possible outside a call. */
  function setServerUrl(input: string) {
    const url = normalizeSfuUrl(input)
    if (!url || inRoom.value) return false
    saveSfuUrl(url)
    if (url !== serverUrl.value) {
      serverUrl.value = url
      serverStatus.value = 'connecting'
    }
    checkServer()
    return true
  }

  // ---- Room -----------------------------------------------------------------------------------

  function join(room: string, withE2ee: boolean) {
    if (inRoom.value || !room.trim()) return
    roomId.value = room.trim()
    e2ee.value = withE2ee
    if (withE2ee) frameCrypto.start()
    inRoom.value = true
    mediaReady = media.start()
    // Becomes the room key if we are the first one in; otherwise `joined` brings the existing one.
    connect(withE2ee ? toBase64(generateKeyMaterial()) : undefined)
    speakerTimer = setInterval(pollAudioLevels, SPEAKER_POLL_MS)
  }

  function leave() {
    if (!inRoom.value) return
    session++
    const ws = socket
    socket = null
    if (ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'leave' }))
    ws?.close()

    inRoom.value = false
    joined.value = false
    participantId.value = ''
    publishPc?.close()
    subscribePc?.close()
    publishPc = subscribePc = null
    videoTransceiver = null
    audioTransceiver = null
    pendingCandidates = { publish: [], subscribe: [] }
    subscribeQueue = Promise.resolve()

    infos.clear()
    remoteTracks.clear()
    publishParticipants()
    audioLevels.value = {}
    activeSpeakerId.value = null

    media.stop()
    frameCrypto.stop()
    if (speakerTimer) clearInterval(speakerTimer)
    speakerTimer = null

    messages.value = []
    remoteAudioMuted.value = false
    remoteVideoHidden.value = false
  }

  const {
    micOn, cameraOn, sharing, effects, effectsStatus, mirrorLocal, cameraCount, micLevel, localStream, canShareScreen, canShareFile,
    toggleMic, toggleCamera, switchCamera, shareScreen, shareFile, stopSharing, setEffects,
  } = media

  return {
    // state
    serverUrl: readonly(serverUrl), serverStatus: readonly(serverStatus),
    roomId: readonly(roomId), e2ee: readonly(e2ee), inRoom: readonly(inRoom), joined: readonly(joined),
    participantId: readonly(participantId), selfLabel, phase, messages,
    participants, audioLevels, activeSpeakerId: readonly(activeSpeakerId),
    micOn, micLevel, cameraOn, remoteAudioMuted, remoteVideoHidden, sharing, effects, effectsStatus, mirrorLocal, cameraCount,
    localStream, canShareScreen, canShareFile,
    // actions
    setServerUrl, checkServer, join, leave, toggleMic, toggleCamera, switchCamera, toggleRemoteAudio, toggleRemoteVideo,
    shareScreen, shareFile, stopSharing, setEffects, sendMessage, onEvent,
  }
}

export type GroupCall = ReturnType<typeof createGroupCall>

let instance: GroupCall | null = null

/** The group call of this tab; nothing connects until join(). */
export function useGroupCall(): GroupCall {
  instance ??= createGroupCall()
  return instance
}
