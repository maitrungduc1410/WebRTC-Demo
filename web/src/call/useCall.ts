import { computed, readonly, ref, shallowRef } from 'vue'
import { fromBase64, toBase64 } from '@/e2ee'
import { createFrameCrypto } from './frameCrypto'
import { createLocalMedia, preferVp8 } from './media'
import { loadServerUrl, normalizeServerUrl, probeServer, saveServerUrl, webSocketUrl } from './serverUrl'
import { DEFAULT_REMOTE_MEDIA, type CallEvent, type ChatMessage, type MediaState, type Phase, type ServerStatus } from './types'

export type { CallEvent, ChatMessage, EffectsStatus, MediaState, Phase, ServerStatus, Sharing } from './types'

// 1:1 call through signaling-server/ (protocol in ARCHITECTURE.md): the server pairs two sockets
// in a room and relays their messages; media goes peer to peer.

const ICE_SERVERS: RTCIceServer[] = [{ urls: 'stun:stun.l.google.com:19302' }]
const DATA_CHANNEL_LABEL = 'MyApp Channel'

type ServerMessage =
  | { type: 'peer joined' }
  | { type: 'offer'; sdp: string }
  | { type: 'answer'; sdp: string }
  | { type: 'candidate'; candidate: RTCIceCandidateInit }
  | { type: 'encryption key'; key: string }
  | { type: 'encryption key received' }
  | { type: 'media state'; state: Partial<MediaState> }
  | { type: 'error'; message: string; fatal: boolean }

export function randomRoomId() {
  return String(Math.floor(100000 + Math.random() * 900000))
}

function createCall() {
  // ---- Reactive state -------------------------------------------------------------------------

  const roomId = ref(randomRoomId())
  const e2ee = ref(false)
  const inRoom = ref(false)
  const serverUrl = ref(loadServerUrl())
  const serverStatus = ref<ServerStatus>('connecting')
  const negotiating = ref(false)
  const peersConnected = ref(false)
  const dataChannelReady = ref(false)
  const messages = ref<ChatMessage[]>([])

  const remoteAudioMuted = ref(false)
  /** Local only, the remote is not notified */
  const remoteVideoHidden = ref(false)

  const remoteMedia = ref<MediaState>({ ...DEFAULT_REMOTE_MEDIA })
  const remoteSnapshot = ref<string | null>(null)
  const remoteAudioLevel = ref(0)

  const remoteStream = shallowRef<MediaStream | null>(null)

  const phase = computed<Phase>(() => {
    if (!inRoom.value) return 'idle'
    if (peersConnected.value) return 'connected'
    return negotiating.value ? 'connecting' : 'waiting'
  })
  const showRemotePlaceholder = computed(() =>
    peersConnected.value && (remoteVideoHidden.value || !remoteMedia.value.video))

  const listeners = new Set<(event: CallEvent) => void>()
  function onEvent(listener: (event: CallEvent) => void) {
    listeners.add(listener)
    return () => listeners.delete(listener)
  }
  function emit(event: CallEvent) {
    listeners.forEach(listener => listener(event))
  }

  // ---- Connection internals -------------------------------------------------------------------

  let socket: WebSocket | null = null
  let peerConnection: RTCPeerConnection | null = null
  let dataChannel: RTCDataChannel | null = null
  let pendingCandidates: RTCIceCandidateInit[] = []
  let nextMessageId = 1

  const frameCrypto = createFrameCrypto()

  /** Camera, microphone, sharing and effects; we only hand it our senders. */
  const media = createLocalMedia({
    inCall: () => inRoom.value,
    videoSender,
    audioSender: () => peerConnection?.getSenders().find(s => s.track?.kind === 'audio'),
    sendMediaState,
    emit,
  })
  let mediaReady: Promise<void> = Promise.resolve()

  let remoteAudioTrack: MediaStreamTrack | null = null
  let remoteVideoTrack: MediaStreamTrack | null = null
  const snapshotVideo = document.createElement('video')
  snapshotVideo.muted = true
  snapshotVideo.playsInline = true

  let snapshotTimer: ReturnType<typeof setInterval> | null = null
  let audioLevelTimer: ReturnType<typeof setInterval> | null = null

  // ---- Signaling ------------------------------------------------------------------------------

  function send(message: object) {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(message))
  }

  /** Opened on join and closed on leave; nothing is sent outside a call. */
  function connect() {
    const ws = new WebSocket(webSocketUrl(serverUrl.value))
    socket = ws
    let opened = false

    ws.onopen = () => {
      if (ws !== socket) return
      opened = true
      send({ type: 'join', roomId: roomId.value })
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

    // No reconnect: the server has already freed our seat, so a closed socket ends the call,
    // even if the media between the two peers is still flowing.
    ws.onclose = () => {
      if (ws !== socket) return
      leave()
      emit({
        type: 'error',
        message: opened ? 'Lost the connection to the signaling server.' : "Can't reach the signaling server.",
      })
    }
  }

  function handleMessage(message: ServerMessage) {
    switch (message.type) {
      case 'peer joined':
        onPeerJoined()
        break
      case 'offer':
        onOffer(message.sdp)
        break
      case 'answer':
        onAnswer(message.sdp)
        break
      case 'candidate':
        onRemoteCandidate(message.candidate)
        break
      case 'encryption key':
        onEncryptionKey(message.key)
        break
      case 'media state':
        remoteMedia.value = {
          audio: message.state?.audio ?? true,
          video: message.state?.video ?? true,
          screen: message.state?.screen ?? false,
        }
        break
      case 'error':
        if (message.fatal) {
          leave()
          emit({
            type: 'error',
            message: message.message === 'Room is full' ? 'That room already has two people in it.' : message.message,
          })
        } else {
          emit({ type: 'info', message: message.message })
        }
        break
    }
  }

  /** The other person just joined our room: we start the call. */
  async function onPeerJoined() {
    await mediaReady
    if (!inRoom.value) return
    if (e2ee.value) {
      const material = await frameCrypto.generateKey()
      // Null: the call ended while the key was being derived.
      if (!material) return
      // The server relays in order on one socket, so the key reaches the peer before the offer.
      send({ type: 'encryption key', key: toBase64(material) })
    }
    startCall()
  }

  async function onOffer(sdp: string) {
    await mediaReady
    if (!inRoom.value) return
    if (!peerConnection) {
      peerConnection = createPeerConnection(false)
    }
    const pc = peerConnection
    try {
      await pc.setRemoteDescription({ type: 'offer', sdp })
      if (pc !== peerConnection) return
      prepareVideoSender(pc)
      await addPendingCandidates(pc)
      applyE2EECodecPreferences()
      const answer = await pc.createAnswer()
      if (pc !== peerConnection) return
      await pc.setLocalDescription(answer)
      updateCodecMap()
      send({ type: 'answer', sdp: answer.sdp })
    } catch (error) {
      if (pc === peerConnection) console.error('Failed to answer the call:', error)
    }
  }

  async function onAnswer(sdp: string) {
    const pc = peerConnection
    if (!pc) return
    try {
      await pc.setRemoteDescription({ type: 'answer', sdp })
      if (pc !== peerConnection) return
      await addPendingCandidates(pc)
      updateCodecMap()
    } catch (error) {
      if (pc === peerConnection) console.error('Failed to apply the answer:', error)
    }
  }

  async function onRemoteCandidate(candidate: RTCIceCandidateInit) {
    const pc = peerConnection
    // Candidates can beat the offer here, for example while our camera is still starting.
    if (!pc || !pc.remoteDescription) {
      pendingCandidates.push(candidate)
      return
    }
    await pc.addIceCandidate(candidate).catch(error => console.warn('Ignored ICE candidate:', error))
  }

  async function onEncryptionKey(key: string) {
    if (!e2ee.value) return
    let material: ArrayBuffer
    try {
      material = fromBase64(key)
    } catch {
      console.error('Received an invalid encryption key')
      return
    }
    await frameCrypto.setKey(material)
    send({ type: 'encryption key received' })
  }

  function sendMediaState() {
    if (!inRoom.value) return
    send({ type: 'media state', state: media.mediaState() })
  }

  async function checkServer() {
    const url = serverUrl.value
    const ok = await probeServer(url, 'signaling-server')
    if (url === serverUrl.value) serverStatus.value = ok ? 'connected' : 'unreachable'
  }

  /** Switches to another signaling server and remembers it; only possible outside a call. */
  function setServerUrl(input: string) {
    const url = normalizeServerUrl(input)
    if (!url || inRoom.value) return false
    saveServerUrl(url)
    if (url !== serverUrl.value) {
      serverUrl.value = url
      serverStatus.value = 'connecting'
    }
    checkServer()
    return true
  }

  // ---- Peer connection ------------------------------------------------------------------------

  async function startCall() {
    // "peer joined" while a call exists means the remote left and came back
    onDisconnected()
    const pc = createPeerConnection(true)
    peerConnection = pc
    applyE2EECodecPreferences()
    // Negotiated with the first offer: opening the chat later must not renegotiate the call.
    createChatChannel()

    try {
      const offer = await pc.createOffer({ offerToReceiveAudio: true, offerToReceiveVideo: true })
      if (pc !== peerConnection) return
      await pc.setLocalDescription(offer)
      updateCodecMap()
      send({ type: 'offer', sdp: offer.sdp })
    } catch (error) {
      if (pc === peerConnection) console.error('Failed to start the call:', error)
    }
  }

  async function addPendingCandidates(pc: RTCPeerConnection) {
    const candidates = pendingCandidates
    pendingCandidates = []
    for (const candidate of candidates) {
      await pc.addIceCandidate(candidate).catch(error => console.warn('Ignored ICE candidate:', error))
    }
  }

  /** @param offering We make the offer; otherwise the remote offer brings the m-lines. */
  function createPeerConnection(offering: boolean) {
    const pc = new RTCPeerConnection({ iceServers: ICE_SERVERS })
    negotiating.value = true

    for (const track of [media.micTrack(), media.outgoingVideoTrack()]) {
      if (track) pc.addTrack(track, media.sendStream())
    }
    // Without a camera, still offer to send video, so one that opens later only replaces the track.
    if (offering && !media.outgoingVideoTrack()) {
      pc.addTransceiver('video', { direction: 'sendrecv', streams: [media.sendStream()] })
    }

    // Events of a replaced connection must not touch the current one.
    pc.onicecandidate = event => {
      if (pc === peerConnection && event.candidate) {
        send({ type: 'candidate', candidate: event.candidate.toJSON() })
      }
    }

    pc.onconnectionstatechange = () => {
      if (pc !== peerConnection) return
      if (pc.connectionState === 'connected') {
        const first = !peersConnected.value
        peersConnected.value = true
        sendMediaState()
        if (first) emit({ type: 'peer-joined' })
      } else if (pc.connectionState === 'disconnected' || pc.connectionState === 'failed') {
        const wasConnected = peersConnected.value
        onDisconnected()
        if (wasConnected) emit({ type: 'peer-left' })
      }
    }

    pc.ontrack = event => {
      if (pc !== peerConnection) return
      const kind = event.track.kind
      if (e2ee.value && (kind === 'video' || kind === 'audio')) {
        frameCrypto.attach(event.receiver, 'decrypt', kind)
      }
      if (kind === 'video') {
        event.track.enabled = !remoteVideoHidden.value
        remoteVideoTrack = event.track
      } else if (kind === 'audio') {
        event.track.enabled = !remoteAudioMuted.value
        remoteAudioTrack = event.track
      }
      publishRemoteStream()
    }

    pc.ondatachannel = event => {
      if (pc !== peerConnection) return
      dataChannel = event.channel
      initDataChannelEvents(event.channel)
    }

    attachCryptors(pc)
    return pc
  }

  /** Every sender (with a track or not yet) and receiver gets its cryptor before media flows. */
  function attachCryptors(pc: RTCPeerConnection) {
    if (!e2ee.value) return
    pc.getTransceivers().forEach(transceiver => {
      const kind = transceiver.receiver.track.kind
      if (kind !== 'video' && kind !== 'audio') return
      frameCrypto.attach(transceiver.sender, 'encrypt', kind)
      frameCrypto.attach(transceiver.receiver, 'decrypt', kind)
    })
  }

  /**
   * Answering without a camera: the offer's video m-line came in receive-only on our side; answer
   * that we send too (nothing until a camera opens), so a camera later only replaces the track.
   */
  function prepareVideoSender(pc: RTCPeerConnection) {
    pc.getTransceivers().forEach(transceiver => {
      // Our own addTrack() transceivers are sendrecv already.
      if (transceiver.receiver.track.kind !== 'video' || transceiver.mid === null || transceiver.direction !== 'recvonly') return
      transceiver.direction = 'sendrecv'
      transceiver.sender.setStreams?.(media.sendStream())
      // A camera that opened while the offer was being applied had no sender to go to yet.
      const track = media.outgoingVideoTrack()
      if (track && !transceiver.sender.track) {
        transceiver.sender.replaceTrack(track).catch(error => console.warn('Video track not attached:', error))
      }
    })
    attachCryptors(pc)
  }

  function publishRemoteStream() {
    const tracks = [remoteAudioTrack, remoteVideoTrack].filter((t): t is MediaStreamTrack => !!t)
    const stream = tracks.length ? new MediaStream(tracks) : null
    remoteStream.value = stream
    snapshotVideo.srcObject = stream
    if (stream) snapshotVideo.play().catch(() => {})
  }

  function onDisconnected() {
    pendingCandidates = []
    if (dataChannel) {
      dataChannel.close()
      dataChannel = null
      dataChannelReady.value = false
    }
    if (peerConnection) {
      peerConnection.close()
      peerConnection = null
    }
    negotiating.value = false
    peersConnected.value = false
    remoteAudioTrack = remoteVideoTrack = null
    publishRemoteStream()
    remoteMedia.value = { ...DEFAULT_REMOTE_MEDIA }
    remoteSnapshot.value = null
    remoteAudioLevel.value = 0
  }

  /** The current video sender; source switches replace its track instead of renegotiating. */
  function videoSender() {
    const transceivers = peerConnection?.getTransceivers().filter(t => t.receiver.track.kind === 'video') ?? []
    return (transceivers.find(t => t.sender.track) ?? transceivers.find(t => t.currentDirection !== 'recvonly'))?.sender
  }

  // ---- E2EE -----------------------------------------------------------------------------------

  function applyE2EECodecPreferences() {
    if (!e2ee.value || !peerConnection) return
    preferVp8(peerConnection.getTransceivers())
  }

  function updateCodecMap() {
    if (!e2ee.value || !peerConnection) return
    frameCrypto.updateCodecMap(peerConnection.localDescription?.sdp, peerConnection.remoteDescription?.sdp)
  }

  // ---- Chat -----------------------------------------------------------------------------------

  function createChatChannel() {
    dataChannel = peerConnection!.createDataChannel(DATA_CHANNEL_LABEL)
    initDataChannelEvents(dataChannel)
  }

  function initDataChannelEvents(channel: RTCDataChannel) {
    channel.onopen = () => {
      if (channel !== dataChannel) return
      dataChannelReady.value = true
      messages.value = []
    }
    channel.onclose = () => {
      if (channel !== dataChannel) return
      dataChannelReady.value = false
      // The other side closed its connection (hung up); ICE takes several seconds to notice on its own.
      if (peersConnected.value) {
        onDisconnected()
        emit({ type: 'peer-left' })
      }
    }
    channel.onerror = () => {
      if (channel === dataChannel) dataChannelReady.value = false
    }
    channel.onmessage = event => {
      messages.value.push({ id: nextMessageId++, text: String(event.data), timestamp: Date.now(), isLocal: false })
    }
  }

  /** Only needed with an older peer that answered our call without a data channel in its offer. */
  async function ensureChat() {
    if (dataChannel || !peersConnected.value || !peerConnection) return
    createChatChannel()
    const pc = peerConnection
    const offer = await pc.createOffer({ offerToReceiveAudio: true, offerToReceiveVideo: true })
    if (pc !== peerConnection) return
    await pc.setLocalDescription(offer)
    updateCodecMap()
    send({ type: 'offer', sdp: offer.sdp })
  }

  function sendMessage(text: string) {
    const trimmed = text.trim()
    if (!dataChannelReady.value || !dataChannel || !trimmed) return false
    dataChannel.send(trimmed)
    messages.value.push({ id: nextMessageId++, text: trimmed, timestamp: Date.now(), isLocal: true })
    return true
  }

  // ---- Remote media ---------------------------------------------------------------------------

  function toggleRemoteAudio() {
    remoteAudioMuted.value = !remoteAudioMuted.value
    if (remoteAudioTrack) remoteAudioTrack.enabled = !remoteAudioMuted.value
  }

  function toggleRemoteVideo() {
    remoteVideoHidden.value = !remoteVideoHidden.value
    if (remoteVideoTrack) remoteVideoTrack.enabled = !remoteVideoHidden.value
  }

  /** Keeps a tiny copy of the last non-black remote frame for the blurred "video off" backdrop. */
  function captureRemoteSnapshot() {
    if (showRemotePlaceholder.value) return
    const video = snapshotVideo
    if (video.readyState < 2 || !video.videoWidth) return
    const width = 36
    const height = Math.max(1, Math.round(width * video.videoHeight / video.videoWidth))
    const canvas = document.createElement('canvas')
    canvas.width = width
    canvas.height = height
    const ctx = canvas.getContext('2d', { willReadFrequently: true })
    if (!ctx) return
    ctx.drawImage(video, 0, 0, width, height)
    const pixels = ctx.getImageData(0, 0, width, height).data
    let luma = 0
    for (let i = 0; i < pixels.length; i += 4) {
      luma += 0.299 * pixels[i]! + 0.587 * pixels[i + 1]! + 0.114 * pixels[i + 2]!
    }
    // Disabled tracks render black; keep the last real picture instead.
    if (luma / (pixels.length / 4) < 16) return
    remoteSnapshot.value = canvas.toDataURL('image/jpeg', 0.7)
  }

  /** Remote audioLevel (0..1) drives the avatar ring; only polled while the placeholder is visible. */
  async function pollRemoteAudioLevel() {
    if (!peerConnection || !showRemotePlaceholder.value || remoteAudioMuted.value) {
      remoteAudioLevel.value = 0
      return
    }
    const stats = await peerConnection.getStats()
    stats.forEach(report => {
      if (report.type === 'inbound-rtp' && report.kind === 'audio') {
        remoteAudioLevel.value = report.audioLevel ?? 0
      }
    })
  }

  // ---- Room -----------------------------------------------------------------------------------

  function join() {
    roomId.value = roomId.value.trim()
    if (!roomId.value || inRoom.value) return
    if (e2ee.value) frameCrypto.start()
    inRoom.value = true
    mediaReady = media.start()
    connect()
    snapshotTimer = setInterval(captureRemoteSnapshot, 500)
    audioLevelTimer = setInterval(pollRemoteAudioLevel, 250)
  }

  function leave() {
    if (!inRoom.value) return
    const ws = socket
    socket = null
    if (ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'leave' }))
    ws?.close()
    inRoom.value = false
    onDisconnected()
    media.stop()
    frameCrypto.stop()

    if (snapshotTimer) clearInterval(snapshotTimer)
    if (audioLevelTimer) clearInterval(audioLevelTimer)
    snapshotTimer = audioLevelTimer = null

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
    serverUrl: readonly(serverUrl), serverStatus: readonly(serverStatus), roomId, e2ee, inRoom, phase, peersConnected, dataChannelReady, messages,
    micOn, micLevel, cameraOn, remoteAudioMuted, remoteVideoHidden, sharing, effects, effectsStatus, mirrorLocal, cameraCount,
    remoteMedia, remoteSnapshot, remoteAudioLevel, showRemotePlaceholder,
    localStream, remoteStream,
    canShareScreen, canShareFile,
    // actions
    setServerUrl, checkServer, join, leave, toggleMic, toggleCamera, switchCamera, toggleRemoteAudio, toggleRemoteVideo,
    shareScreen, shareFile, stopSharing, setEffects, sendMessage, ensureChat, onEvent,
  }
}

export type Call = ReturnType<typeof createCall>

let instance: Call | null = null

/** The one 1:1 call of this tab; nothing connects until join(). */
export function useCall(): Call {
  instance ??= createCall()
  return instance
}