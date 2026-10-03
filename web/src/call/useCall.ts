import io, { type Socket } from 'socket.io-client'
import { computed, readonly, ref, shallowRef } from 'vue'
import {
  decryptStream,
  deriveFrameKey,
  encryptStream,
  generateKeyMaterial,
  parseCodecMap,
  type CodecMap,
  type MediaKind,
} from '@/e2ee'
import {
  findBackground,
  findSticker,
  hasEffects,
  loadSelection,
  NO_EFFECTS,
  saveSelection,
  type EffectsSelection,
} from '@/effects/catalog'
import type { EffectsProcessor } from '@/effects/EffectsProcessor'
import { loadServerUrl, normalizeServerUrl, saveServerUrl } from './serverUrl'

const ICE_SERVERS: RTCIceServer[] = [{ urls: 'stun:stun.l.google.com:19302' }]
const DATA_CHANNEL_LABEL = 'MyApp Channel'
const CAMERA_CONSTRAINTS: MediaTrackConstraints = { width: { ideal: 1280 }, height: { ideal: 720 } }
// Lets the remote swap to its placeholder before our track turns into black frames
const MEDIA_STATE_DELAY_MS = 300
const USE_ENCRYPTION_WORKER = true

export type Phase = 'idle' | 'waiting' | 'connecting' | 'connected'
export type Sharing = 'none' | 'screen' | 'file'
export type EffectsStatus = 'off' | 'loading' | 'on'
export type ServerStatus = 'connecting' | 'connected' | 'unreachable'

/** What the remote peer says it is sending ("media state" event); screen = screen or file sharing */
export interface MediaState { audio: boolean; video: boolean; screen: boolean }

export interface ChatMessage {
  id: number
  text: string
  timestamp: number
  isLocal: boolean
}

export type CallEvent =
  | { type: 'peer-joined' }
  | { type: 'peer-left' }
  | { type: 'error'; message: string }
  | { type: 'info'; message: string }

const DEFAULT_REMOTE_MEDIA: MediaState = { audio: true, video: true, screen: false }

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

  const micOn = ref(true)
  const cameraOn = ref(true)
  const remoteAudioMuted = ref(false)
  /** Local only, the remote is not notified */
  const remoteVideoHidden = ref(false)
  const sharing = ref<Sharing>('none')
  /** The chosen background and sticker; remembered across calls. */
  const effects = ref<EffectsSelection>(loadSelection())
  const effectsStatus = ref<EffectsStatus>('off')
  const mirrorLocal = ref(true)
  const cameraCount = ref(0)

  const remoteMedia = ref<MediaState>({ ...DEFAULT_REMOTE_MEDIA })
  const remoteSnapshot = ref<string | null>(null)
  const remoteAudioLevel = ref(0)

  /** What the local preview shows: the track currently going to the peer. */
  const localStream = shallowRef<MediaStream | null>(null)
  const remoteStream = shallowRef<MediaStream | null>(null)

  const phase = computed<Phase>(() => {
    if (!inRoom.value) return 'idle'
    if (peersConnected.value) return 'connected'
    return negotiating.value ? 'connecting' : 'waiting'
  })
  const showRemotePlaceholder = computed(() =>
    peersConnected.value && (remoteVideoHidden.value || !remoteMedia.value.video))

  const canShareScreen = typeof navigator.mediaDevices?.getDisplayMedia === 'function'
  const canShareFile = 'captureStream' in HTMLVideoElement.prototype || 'mozCaptureStream' in HTMLVideoElement.prototype

  const listeners = new Set<(event: CallEvent) => void>()
  function onEvent(listener: (event: CallEvent) => void) {
    listeners.add(listener)
    return () => listeners.delete(listener)
  }
  function emit(event: CallEvent) {
    listeners.forEach(listener => listener(event))
  }

  // ---- Connection internals -------------------------------------------------------------------

  let socket: Socket
  let peerConnection: RTCPeerConnection | null = null
  let dataChannel: RTCDataChannel | null = null
  let pendingCandidates: RTCIceCandidateInit[] = []
  let nextMessageId = 1

  let encryptionKey: CryptoKey | undefined
  let codecMap: CodecMap = {}
  let encryptionWorker: Worker | undefined
  const transformedRtpObjects = new WeakSet<RTCRtpSender | RTCRtpReceiver>()

  // ---- Media internals ------------------------------------------------------------------------

  /** Every outgoing track belongs to this stream, so the peer sees one msid across source switches. */
  let sendStream = new MediaStream()
  let micTrack: MediaStreamTrack | null = null
  let cameraTrack: MediaStreamTrack | null = null
  let screenTrack: MediaStreamTrack | null = null
  let fileTrack: MediaStreamTrack | null = null
  let fileVideo: HTMLVideoElement | null = null
  let processor: EffectsProcessor | null = null
  /** What the processor currently draws; `effects` runs ahead of it while assets load. */
  let appliedEffects: EffectsSelection = NO_EFFECTS
  let effectsRequest = 0
  let effectsWork: Promise<void> = Promise.resolve()
  let mediaReady: Promise<void> = Promise.resolve()

  let remoteAudioTrack: MediaStreamTrack | null = null
  let remoteVideoTrack: MediaStreamTrack | null = null
  const snapshotVideo = document.createElement('video')
  snapshotVideo.muted = true
  snapshotVideo.playsInline = true

  let mediaStateTimer: ReturnType<typeof setTimeout> | null = null
  let snapshotTimer: ReturnType<typeof setInterval> | null = null
  let audioLevelTimer: ReturnType<typeof setInterval> | null = null

  // ---- Signaling ------------------------------------------------------------------------------

  /**
   * Socket.IO buffers emits while offline and replays them after reconnecting, when they would
   * reach the peer's next call. Signaling only makes sense for the connection it was made for.
   */
  function signal(event: string, data: object) {
    if (socket.connected) socket.emit(event, data)
  }

  function connectSocket() {
    socket = io(serverUrl.value, { forceNew: true })
    serverStatus.value = 'connecting'

    socket.on('disconnect', () => { serverStatus.value = 'connecting' })
    // Socket.IO keeps retrying; the status changes again on the next successful connect.
    socket.on('connect_error', () => { serverStatus.value = 'unreachable' })

    socket.on('connect', () => {
      serverStatus.value = 'connected'
      // Joining while offline was dropped, and after a reconnect the server has already removed
      // us from the room; either way the peer still in it starts a new call once we (re)join.
      if (inRoom.value) {
        onDisconnected()
        signal('join room', { roomId: roomId.value })
      }
    })

    socket.on('message', (data: { message?: string }) => {
      const text = data?.message
      if (!text || !inRoom.value) return
      if (text === 'Room is full') {
        leave()
        emit({ type: 'error', message: 'That room already has two people in it.' })
      } else {
        emit({ type: 'info', message: text })
      }
    })

    socket.on('new user joined', async () => {
      await mediaReady
      if (!inRoom.value) return
      if (e2ee.value) {
        if (USE_ENCRYPTION_WORKER) {
          encryptionWorker?.postMessage({ action: 'generateKey' })
        } else if (await generateEncryptionKey()) {
          startCall()
        }
      } else {
        startCall()
      }
    })

    socket.on('offer', async (data: { offer: RTCSessionDescriptionInit }) => {
      await mediaReady
      if (!inRoom.value) return
      if (!peerConnection) {
        peerConnection = createPeerConnection()
      }
      const pc = peerConnection
      try {
        await pc.setRemoteDescription(new RTCSessionDescription(data.offer))
        if (pc !== peerConnection) return
        await addPendingCandidates(pc)
        applyE2EECodecPreferences()
        const answer = await pc.createAnswer()
        if (pc !== peerConnection) return
        await pc.setLocalDescription(answer)
        updateCodecMap()
        signal('answer', { answer, roomId: roomId.value })
      } catch (error) {
        if (pc === peerConnection) console.error('Failed to answer the call:', error)
      }
    })

    socket.on('answer', async (data: { answer: RTCSessionDescriptionInit }) => {
      const pc = peerConnection
      if (!pc) return
      try {
        await pc.setRemoteDescription(new RTCSessionDescription(data.answer))
        if (pc !== peerConnection) return
        await addPendingCandidates(pc)
        updateCodecMap()
      } catch (error) {
        if (pc === peerConnection) console.error('Failed to apply the answer:', error)
      }
    })

    socket.on('new ice candidate', async (data: { iceCandidate: RTCIceCandidateInit }) => {
      const pc = peerConnection
      // Candidates can beat the offer here, for example while our camera is still starting.
      if (!pc || !pc.remoteDescription) {
        pendingCandidates.push(data.iceCandidate)
        return
      }
      await pc.addIceCandidate(data.iceCandidate).catch(error => console.warn('Ignored ICE candidate:', error))
    })

    socket.on('receive encryption key', async (data: { encryptionKey: ArrayBuffer }) => {
      if (!e2ee.value) return
      if (USE_ENCRYPTION_WORKER && encryptionWorker) {
        encryptionWorker.postMessage({ action: 'setKey', key: data.encryptionKey })
      } else {
        encryptionKey = await deriveFrameKey(data.encryptionKey)
      }
      signal('encryption key received', { roomId: roomId.value })
    })

    socket.on('media state', (data: { state: Partial<MediaState> }) => {
      remoteMedia.value = {
        audio: data.state?.audio ?? true,
        video: data.state?.video ?? true,
        screen: data.state?.screen ?? false,
      }
    })
  }

  function sendMediaState() {
    if (!inRoom.value) return
    const presenting = sharing.value !== 'none'
    signal('media state', {
      roomId: roomId.value,
      state: { audio: micOn.value, video: presenting || cameraOn.value, screen: presenting },
    })
  }

  /** Switches to another signaling server and remembers it; only possible outside a call. */
  function setServerUrl(input: string) {
    const url = normalizeServerUrl(input)
    if (!url || inRoom.value) return false
    saveServerUrl(url)
    if (url === serverUrl.value) {
      if (!socket.connected) socket.connect()
      return true
    }
    serverUrl.value = url
    socket.off()
    socket.disconnect()
    connectSocket()
    return true
  }

  // ---- Peer connection ------------------------------------------------------------------------

  async function startCall() {
    // "new user joined" while a call exists means the remote restarted it
    onDisconnected()
    const pc = createPeerConnection()
    peerConnection = pc
    applyE2EECodecPreferences()
    // Negotiated with the first offer: opening the chat later must not renegotiate the call.
    createChatChannel()

    try {
      const offer = await pc.createOffer({ offerToReceiveAudio: true, offerToReceiveVideo: true })
      if (pc !== peerConnection) return
      await pc.setLocalDescription(offer)
      updateCodecMap()
      signal('offer', { offer, roomId: roomId.value })
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

  function createPeerConnection() {
    const pc = new RTCPeerConnection({ iceServers: ICE_SERVERS })
    negotiating.value = true

    for (const track of [micTrack, outgoingVideoTrack()]) {
      if (track) pc.addTrack(track, sendStream)
    }

    // Events of a replaced connection must not touch the current one.
    pc.onicecandidate = event => {
      if (pc === peerConnection && event.candidate) {
        signal('new ice candidate', { iceCandidate: event.candidate, roomId: roomId.value })
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
        attachFrameTransform(event.receiver, 'decrypt', kind)
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

    if (e2ee.value) {
      pc.getSenders().forEach(sender => {
        const kind = sender.track?.kind
        if (kind === 'video' || kind === 'audio') attachFrameTransform(sender, 'encrypt', kind)
      })
      pc.getReceivers().forEach(receiver => {
        const kind = receiver.track.kind
        if (kind === 'video' || kind === 'audio') attachFrameTransform(receiver, 'decrypt', kind)
      })
    }

    return pc
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

  // ---- E2EE -----------------------------------------------------------------------------------

  function attachFrameTransform(
    senderOrReceiver: RTCRtpSender | RTCRtpReceiver,
    operation: 'encrypt' | 'decrypt',
    kind: MediaKind,
  ) {
    if (transformedRtpObjects.has(senderOrReceiver)) return

    const target = senderOrReceiver as any
    const transformOptions = { kind, getKey: () => encryptionKey, getCodecMap: () => codecMap }

    if (typeof target.createEncodedStreams === 'function') {
      const { readable, writable } = target.createEncodedStreams()
      transformedRtpObjects.add(senderOrReceiver)
      if (USE_ENCRYPTION_WORKER && encryptionWorker) {
        encryptionWorker.postMessage({ action: operation, kind, readable, writable }, [readable, writable])
      } else {
        const pipe = operation === 'encrypt' ? encryptStream : decryptStream
        pipe(transformOptions, readable, writable).catch(error => console.error('E2EE pipeline closed:', error))
      }
    } else if ('RTCRtpScriptTransform' in window && encryptionWorker) {
      // @ts-ignore
      target.transform = new RTCRtpScriptTransform(encryptionWorker, { operation, kind })
      transformedRtpObjects.add(senderOrReceiver)
    } else {
      console.error('E2EE is not supported in this browser')
    }
  }

  // Android/iOS also put VP8 first when E2EE is on, so all platforms negotiate the same codec.
  function applyE2EECodecPreferences() {
    if (!e2ee.value || !peerConnection) return
    const capabilities = RTCRtpReceiver.getCapabilities('video')
    if (!capabilities) return
    const codecs = [...capabilities.codecs].sort((a, b) =>
      Number(b.mimeType.toLowerCase() === 'video/vp8') - Number(a.mimeType.toLowerCase() === 'video/vp8'))
    peerConnection.getTransceivers().forEach(transceiver => {
      if (transceiver.receiver.track.kind === 'video' && typeof transceiver.setCodecPreferences === 'function') {
        transceiver.setCodecPreferences(codecs)
      }
    })
  }

  function updateCodecMap() {
    if (!e2ee.value || !peerConnection) return
    codecMap = parseCodecMap(peerConnection.localDescription?.sdp, peerConnection.remoteDescription?.sdp)
    encryptionWorker?.postMessage({ action: 'setCodecMap', codecMap })
  }

  /** False when the call ended while the key was being derived. */
  async function generateEncryptionKey() {
    const material = generateKeyMaterial()
    const key = await deriveFrameKey(material)
    if (!inRoom.value) return false
    encryptionKey = key
    signal('send encryption key', { roomId: roomId.value, encryptionKey: material })
    return true
  }

  function startEncryptionWorker() {
    if (!e2ee.value || !USE_ENCRYPTION_WORKER || encryptionWorker) return
    const worker = new Worker(new URL('../encryptionWorker.ts', import.meta.url), { type: 'module' })
    encryptionWorker = worker
    worker.onmessage = event => {
      // A key generated for a call that has ended since must not start a new one.
      if (worker !== encryptionWorker || !inRoom.value) return
      const { action, key } = event.data
      if (action === 'generatedKey') {
        signal('send encryption key', { roomId: roomId.value, encryptionKey: key })
        startCall()
      }
    }
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
    signal('offer', { offer, roomId: roomId.value })
  }

  function sendMessage(text: string) {
    const trimmed = text.trim()
    if (!dataChannelReady.value || !dataChannel || !trimmed) return false
    dataChannel.send(trimmed)
    messages.value.push({ id: nextMessageId++, text: trimmed, timestamp: Date.now(), isLocal: true })
    return true
  }

  // ---- Local media ----------------------------------------------------------------------------

  function outgoingVideoTrack(): MediaStreamTrack | null {
    if (sharing.value === 'screen') return screenTrack
    if (sharing.value === 'file') return fileTrack
    if (processor && hasEffects(appliedEffects)) return processor.track
    return cameraTrack
  }

  /** Points the preview and the video sender at the current source; no renegotiation needed. */
  async function syncVideo() {
    const track = outgoingVideoTrack()
    // A presentation is always sent; the camera setting applies to the camera again afterwards.
    if (track) track.enabled = sharing.value !== 'none' || cameraOn.value
    localStream.value = track ? new MediaStream([track]) : null
    mirrorLocal.value = sharing.value === 'none' && cameraTrack?.getSettings().facingMode !== 'environment'

    sendStream.getVideoTracks().forEach(t => sendStream.removeTrack(t))
    if (track) sendStream.addTrack(track)

    const sender = videoSender()
    if (sender && sender.track !== track) await sender.replaceTrack(track)
  }

  function videoSender() {
    const transceivers = peerConnection?.getTransceivers().filter(t => t.receiver.track.kind === 'video') ?? []
    return (transceivers.find(t => t.sender.track) ?? transceivers.find(t => t.currentDirection !== 'recvonly'))?.sender
  }

  async function startMedia() {
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true, video: CAMERA_CONSTRAINTS })
      micTrack = stream.getAudioTracks()[0] ?? null
      cameraTrack = stream.getVideoTracks()[0] ?? null
    } catch {
      // One of the two may still work on its own.
      micTrack = await getTrack({ audio: true })
      cameraTrack = await getTrack({ video: CAMERA_CONSTRAINTS })
      if (!micTrack && !cameraTrack) {
        emit({ type: 'error', message: 'Camera and microphone are blocked. You can still see and hear the other person.' })
      }
    }
    if (!inRoom.value) {
      micTrack?.stop()
      cameraTrack?.stop()
      micTrack = cameraTrack = null
      return
    }
    if (!micTrack) micOn.value = false
    if (!cameraTrack) cameraOn.value = false
    if (micTrack) {
      micTrack.enabled = micOn.value
      sendStream.addTrack(micTrack)
    }
    // Nothing is shown or sent until a saved background is ready, so the room never flashes by.
    // A choice made while it loads replaces it, so wait for whichever load is the latest.
    if (cameraTrack && hasEffects(effects.value)) {
      applyEffects()
      let work: Promise<void>
      do {
        work = effectsWork
        await work
      } while (work !== effectsWork)
    }
    await syncVideo()
    refreshCameras()
  }

  async function getTrack(constraints: MediaStreamConstraints) {
    try {
      const stream = await navigator.mediaDevices.getUserMedia(constraints)
      return stream.getTracks()[0] ?? null
    } catch {
      return null
    }
  }

  async function refreshCameras() {
    const devices = await navigator.mediaDevices.enumerateDevices().catch(() => [])
    cameraCount.value = devices.filter(d => d.kind === 'videoinput').length
  }

  async function ensureCamera() {
    if (cameraTrack && cameraTrack.readyState === 'live') return
    cameraTrack = await getTrack({ video: CAMERA_CONSTRAINTS })
    processor?.setSource(cameraTrack)
  }

  function toggleMic() {
    if (!micTrack) {
      emit({ type: 'error', message: 'No microphone available.' })
      return
    }
    micOn.value = !micOn.value
    micTrack.enabled = micOn.value
    sendMediaState()
  }

  function toggleCamera() {
    // The camera is closed while presenting.
    if (sharing.value !== 'none') return
    const track = outgoingVideoTrack()
    if (!track) {
      emit({ type: 'error', message: 'No camera available.' })
      return
    }
    const enable = !cameraOn.value
    cameraOn.value = enable
    if (mediaStateTimer) clearTimeout(mediaStateTimer)
    if (enable) {
      track.enabled = true
      if (cameraTrack) cameraTrack.enabled = true
      mediaStateTimer = setTimeout(sendMediaState, MEDIA_STATE_DELAY_MS)
    } else {
      sendMediaState()
      mediaStateTimer = setTimeout(() => {
        if (cameraOn.value || sharing.value !== 'none') return
        const current = outgoingVideoTrack()
        if (current) current.enabled = false
        if (cameraTrack) cameraTrack.enabled = false
      }, MEDIA_STATE_DELAY_MS)
    }
  }

  async function switchCamera(): Promise<boolean> {
    if (sharing.value !== 'none' || !cameraTrack) return false
    const devices = (await navigator.mediaDevices.enumerateDevices()).filter(d => d.kind === 'videoinput')
    if (devices.length < 2) return false
    const current = cameraTrack.getSettings().deviceId
    const index = devices.findIndex(d => d.deviceId === current)
    const next = devices[(index + 1) % devices.length]!

    // Phones usually cannot open two cameras at once, so the current one goes first.
    cameraTrack.stop()
    cameraTrack = await getTrack({ video: { ...CAMERA_CONSTRAINTS, deviceId: { exact: next.deviceId } } })
      ?? await getTrack({ video: CAMERA_CONSTRAINTS })
    if (cameraTrack) cameraTrack.enabled = cameraOn.value
    processor?.setSource(cameraTrack)
    await syncVideo()
    return true
  }

  // ---- Sharing --------------------------------------------------------------------------------

  async function shareScreen() {
    if (!canShareScreen) return
    let stream: MediaStream
    try {
      stream = await navigator.mediaDevices.getDisplayMedia({ video: { frameRate: 30 }, audio: false })
    } catch {
      return // the user closed the picker
    }
    const track = stream.getVideoTracks()[0]
    if (!track) return
    track.contentHint = 'detail'
    // The browser's own "Stop sharing" button
    track.addEventListener('ended', () => {
      if (screenTrack === track) stopSharing()
    })

    releaseFile()
    screenTrack?.stop()
    screenTrack = track
    pauseCamera()
    sharing.value = 'screen'
    await syncVideo()
    sendMediaState()
  }

  async function shareFile(file: File) {
    const video = document.createElement('video')
    video.loop = true
    video.muted = true
    video.playsInline = true
    video.src = URL.createObjectURL(file)
    try {
      await video.play()
      const stream = video.captureStream?.() ?? video.mozCaptureStream?.()
      const track = stream?.getVideoTracks()[0]
      if (!track) throw new Error('no video track')

      screenTrack?.stop()
      screenTrack = null
      releaseFile()
      fileVideo = video
      fileTrack = track
      pauseCamera()
      sharing.value = 'file'
      await syncVideo()
      sendMediaState()
    } catch {
      URL.revokeObjectURL(video.src)
      emit({ type: 'error', message: "Couldn't play that video." })
    }
  }

  async function stopSharing() {
    if (sharing.value === 'none') return
    sharing.value = 'none'
    screenTrack?.stop()
    screenTrack = null
    releaseFile()
    await ensureCamera()
    if (cameraTrack) cameraTrack.enabled = cameraOn.value
    await syncVideo()
    sendMediaState()
  }

  /** The camera is off while presenting; it comes back when sharing stops. */
  function pauseCamera() {
    cameraTrack?.stop()
    cameraTrack = null
    processor?.setSource(null)
  }

  function releaseFile() {
    if (fileVideo) {
      fileVideo.pause()
      URL.revokeObjectURL(fileVideo.src)
      fileVideo.removeAttribute('src')
      fileVideo = null
    }
    fileTrack?.stop()
    fileTrack = null
  }

  // ---- Backgrounds and effects ----------------------------------------------------------------

  function setEffects(next: EffectsSelection) {
    if (next.background === effects.value.background && next.sticker === effects.value.sticker) return
    effects.value = next
    saveSelection(next)
    if (inRoom.value) applyEffects()
  }

  function applyEffects() {
    effectsWork = runEffects()
    return effectsWork
  }

  /** Brings the processor in line with `effects`; a newer call supersedes an older one. */
  async function runEffects() {
    const request = ++effectsRequest
    const selection = effects.value
    if (!hasEffects(selection)) {
      stopEffects()
      await syncVideo()
      return
    }
    effectsStatus.value = 'loading'
    try {
      if (sharing.value === 'none') await ensureCamera()
      if (request !== effectsRequest) return
      if (!cameraTrack && sharing.value === 'none') {
        effectsStatus.value = hasEffects(appliedEffects) ? 'on' : 'off'
        emit({ type: 'error', message: 'No camera available.' })
        return
      }
      // MediaPipe is most of the bundle; load it the first time someone asks for it.
      const { EffectsProcessor } = await import('@/effects/EffectsProcessor')
      if (request !== effectsRequest || !inRoom.value) return
      processor ??= new EffectsProcessor(sharing.value === 'none' ? cameraTrack : null)
      const target = processor
      const applied = await target.configure(findBackground(selection.background), findSticker(selection.sticker))
      if (!applied || request !== effectsRequest || target !== processor) return
      appliedEffects = selection
      effectsStatus.value = 'on'
      await syncVideo()
    } catch (error) {
      if (request !== effectsRequest) return
      console.error('Failed to apply effects:', error)
      emit({ type: 'error', message: "Couldn't load that effect." })
      // Back to whatever was showing before.
      effects.value = appliedEffects
      saveSelection(appliedEffects)
      if (hasEffects(appliedEffects)) {
        effectsStatus.value = 'on'
      } else {
        stopEffects()
        await syncVideo()
      }
    }
  }

  function stopEffects() {
    processor?.stop()
    processor = null
    appliedEffects = NO_EFFECTS
    effectsStatus.value = 'off'
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
    startEncryptionWorker()
    inRoom.value = true
    mediaReady = startMedia()
    signal('join room', { roomId: roomId.value })
    snapshotTimer = setInterval(captureRemoteSnapshot, 500)
    audioLevelTimer = setInterval(pollRemoteAudioLevel, 250)
  }

  function leave() {
    if (!inRoom.value) return
    signal('leave room', { roomId: roomId.value })
    inRoom.value = false
    onDisconnected()

    micTrack?.stop()
    cameraTrack?.stop()
    screenTrack?.stop()
    micTrack = cameraTrack = screenTrack = null
    releaseFile()
    effectsRequest++
    if (processor) import('@/effects/EffectsProcessor').then(m => m.releaseModels())
    stopEffects()
    sendStream = new MediaStream()
    localStream.value = null

    encryptionWorker?.terminate()
    encryptionWorker = undefined
    encryptionKey = undefined
    codecMap = {}

    if (snapshotTimer) clearInterval(snapshotTimer)
    if (audioLevelTimer) clearInterval(audioLevelTimer)
    if (mediaStateTimer) clearTimeout(mediaStateTimer)
    snapshotTimer = audioLevelTimer = mediaStateTimer = null

    messages.value = []
    sharing.value = 'none'
    micOn.value = true
    cameraOn.value = true
    remoteAudioMuted.value = false
    remoteVideoHidden.value = false
  }

  connectSocket()

  return {
    // state
    serverUrl: readonly(serverUrl), serverStatus: readonly(serverStatus), roomId, e2ee, inRoom, phase, peersConnected, dataChannelReady, messages,
    micOn, cameraOn, remoteAudioMuted, remoteVideoHidden, sharing, effects: readonly(effects), effectsStatus: readonly(effectsStatus), mirrorLocal, cameraCount,
    remoteMedia, remoteSnapshot, remoteAudioLevel, showRemotePlaceholder,
    localStream, remoteStream,
    canShareScreen, canShareFile,
    // actions
    setServerUrl, join, leave, toggleMic, toggleCamera, switchCamera, toggleRemoteAudio, toggleRemoteVideo,
    shareScreen, shareFile, stopSharing, setEffects, sendMessage, ensureChat, onEvent,
  }
}

export type Call = ReturnType<typeof createCall>

let instance: Call | null = null

/** The one call of this tab; the socket connects on first use. */
export function useCall(): Call {
  instance ??= createCall()
  return instance
}
