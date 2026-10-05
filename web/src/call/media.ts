import { readonly, ref, shallowRef } from 'vue'
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
import type { CallEvent, EffectsStatus, MediaState, Sharing } from './types'

const CAMERA_CONSTRAINTS: MediaTrackConstraints = { width: { ideal: 1280 }, height: { ideal: 720 } }
// Lets the remote swap to its placeholder before our track turns into black frames
const MEDIA_STATE_DELAY_MS = 300
const MIC_METER_INTERVAL_MS = 60
/** Per tick, so the bars fall back smoothly after a word instead of dropping. */
const MIC_LEVEL_DECAY = 0.75

/** Maps a sample peak (0..1) to the bars' 0..1: -50 dBFS (room noise) to -10 dBFS (loud speech). Same on Android and iOS. */
function micLevelFromPeak(peak: number) {
  if (peak <= 0) return 0
  return Math.min(Math.max((20 * Math.log10(peak) + 50) / 40, 0), 1)
}

export interface LocalMediaHooks {
  /** False once the call ended, e.g. while the camera was still starting. */
  inCall: () => boolean
  /** The sender currently carrying our video, if any; source switches go through replaceTrack(). */
  videoSender: () => RTCRtpSender | null | undefined
  /** The sender carrying our microphone; a replacement mic goes through replaceTrack(). */
  audioSender: () => RTCRtpSender | null | undefined
  /** Tell the other side(s) about mediaState(). */
  sendMediaState: () => void
  emit: (event: CallEvent) => void
}

/**
 * Microphone, camera, screen and video-file sources, backgrounds and effects, and the local
 * preview. Engines only add micTrack() and outgoingVideoTrack() to their connection and point
 * audioSender() and videoSender() at them; everything else (switching sources, muting) happens here.
 */
export function createLocalMedia(hooks: LocalMediaHooks) {
  const micOn = ref(true)
  const cameraOn = ref(true)
  const sharing = ref<Sharing>('none')
  /** The chosen background and sticker; remembered across calls. */
  const effects = ref<EffectsSelection>(loadSelection())
  const effectsStatus = ref<EffectsStatus>('off')
  const mirrorLocal = ref(true)
  const cameraCount = ref(0)
  /** What the local preview shows: the track currently going to the peer. */
  const localStream = shallowRef<MediaStream | null>(null)
  /** 0..1, how loud the microphone is right now; 0 while muted. */
  const micLevel = ref(0)

  const canShareScreen = typeof navigator.mediaDevices?.getDisplayMedia === 'function'
  const canShareFile = 'captureStream' in HTMLVideoElement.prototype || 'mozCaptureStream' in HTMLVideoElement.prototype

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
  let mediaStateTimer: ReturnType<typeof setTimeout> | null = null
  let startWork: Promise<void> = Promise.resolve()
  let openingCamera = false
  let meter: { context: AudioContext; timer: ReturnType<typeof setInterval> } | null = null

  function mediaState(): MediaState {
    const presenting = sharing.value !== 'none'
    return { audio: micOn.value, video: presenting || cameraOn.value, screen: presenting }
  }

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

    const sender = hooks.videoSender()
    if (sender && sender.track !== track) await sender.replaceTrack(track)
  }

  function start() {
    startWork = startNow()
    return startWork
  }

  async function startNow() {
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true, video: CAMERA_CONSTRAINTS })
      micTrack = stream.getAudioTracks()[0] ?? null
      cameraTrack = stream.getVideoTracks()[0] ?? null
    } catch {
      // One of the two may still work on its own.
      micTrack = await getTrack({ audio: true })
      cameraTrack = await getTrack({ video: CAMERA_CONSTRAINTS })
      if (!micTrack && !cameraTrack) {
        hooks.emit({ type: 'error', message: 'Camera and microphone are blocked. You can still see and hear the other person.' })
      }
    }
    if (!hooks.inCall()) {
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
      watchMic(micTrack)
      meterMic(micTrack)
    }
    // Nothing is shown or sent until a saved background is ready, so the room never flashes by.
    // A choice made while it loads replaces it, so wait for whichever load is the latest.
    if (cameraTrack && hasEffects(effects.value)) {
      applyEffects()
      await effectsSettled()
    }
    await syncVideo()
    refreshCameras()
  }

  /** Waits for the latest effects load; a choice made while one loads replaces it. */
  async function effectsSettled() {
    let work: Promise<void>
    do {
      work = effectsWork
      await work
    } while (work !== effectsWork)
  }

  /** Releases every source and resets the settings for the next call. */
  function stop() {
    micTrack?.stop()
    cameraTrack?.stop()
    screenTrack?.stop()
    micTrack = cameraTrack = screenTrack = null
    stopMeter()
    releaseFile()
    effectsRequest++
    if (processor) import('@/effects/EffectsProcessor').then(m => m.releaseModels())
    stopEffects()
    sendStream = new MediaStream()
    localStream.value = null

    if (mediaStateTimer) clearTimeout(mediaStateTimer)
    mediaStateTimer = null

    sharing.value = 'none'
    micOn.value = true
    cameraOn.value = true
  }

  async function getTrack(constraints: MediaStreamConstraints) {
    try {
      const stream = await navigator.mediaDevices.getUserMedia(constraints)
      return stream.getTracks()[0] ?? null
    } catch {
      return null
    }
  }

  /**
   * A microphone that goes away mid-call (an iPhone used as the Mac's mic disconnects, a headset is
   * unplugged) ends its track; carry on with the system default one instead of going silent.
   */
  function watchMic(track: MediaStreamTrack) {
    track.addEventListener('ended', async () => {
      if (micTrack !== track || !hooks.inCall()) return
      const next = await getTrack({ audio: true })
      if (micTrack !== track || !hooks.inCall()) {
        next?.stop()
        return
      }
      sendStream.removeTrack(track)
      micTrack = next
      meterMic(next)
      if (!next) {
        micOn.value = false
        hooks.sendMediaState()
        hooks.emit({ type: 'error', message: 'The microphone was disconnected.' })
        return
      }
      next.enabled = micOn.value
      sendStream.addTrack(next)
      watchMic(next)
      await hooks.audioSender()?.replaceTrack(next)
      hooks.emit({ type: 'info', message: `Switched to ${next.label || 'another microphone'}.` })
    })
  }

  /** Measures `track` on its own audio graph; nothing is played out. */
  function meterMic(track: MediaStreamTrack | null) {
    stopMeter()
    if (!track) return
    const context = new AudioContext()
    const analyser = context.createAnalyser()
    analyser.fftSize = 1024
    context.createMediaStreamSource(new MediaStream([track])).connect(analyser)
    context.resume().catch(() => {})
    const samples = new Float32Array(analyser.fftSize)
    const timer = setInterval(() => {
      analyser.getFloatTimeDomainData(samples)
      let peak = 0
      for (const sample of samples) peak = Math.max(peak, Math.abs(sample))
      const level = track.enabled ? micLevelFromPeak(peak) : 0
      micLevel.value = Math.max(level, micLevel.value * MIC_LEVEL_DECAY)
    }, MIC_METER_INTERVAL_MS)
    meter = { context, timer }
  }

  function stopMeter() {
    if (meter) {
      clearInterval(meter.timer)
      meter.context.close().catch(() => {})
      meter = null
    }
    micLevel.value = 0
  }

  async function refreshCameras() {
    const devices = await navigator.mediaDevices.enumerateDevices().catch(() => [])
    cameraCount.value = devices.filter(d => d.kind === 'videoinput').length
  }

  async function ensureCamera() {
    if (cameraTrack && cameraTrack.readyState === 'live') return
    const track = await getTrack({ video: CAMERA_CONSTRAINTS })
    // Turning the camera on may have opened it meanwhile; keep one.
    if (cameraTrack && cameraTrack.readyState === 'live') {
      track?.stop()
      return
    }
    cameraTrack?.stop()
    cameraTrack = track
    processor?.setSource(cameraTrack)
  }

  function toggleMic() {
    if (!micTrack) {
      hooks.emit({ type: 'error', message: 'No microphone available.' })
      return
    }
    micOn.value = !micOn.value
    micTrack.enabled = micOn.value
    hooks.sendMediaState()
  }

  async function toggleCamera() {
    // The camera is closed while presenting.
    if (sharing.value !== 'none' || openingCamera) return
    if (cameraMissing()) {
      await openCamera()
      return
    }
    const track = outgoingVideoTrack()
    if (!track) {
      hooks.emit({ type: 'error', message: 'No camera available.' })
      return
    }
    const enable = !cameraOn.value
    cameraOn.value = enable
    if (mediaStateTimer) clearTimeout(mediaStateTimer)
    if (enable) {
      track.enabled = true
      if (cameraTrack) cameraTrack.enabled = true
      mediaStateTimer = setTimeout(hooks.sendMediaState, MEDIA_STATE_DELAY_MS)
    } else {
      hooks.sendMediaState()
      mediaStateTimer = setTimeout(() => {
        if (cameraOn.value || sharing.value !== 'none') return
        const current = outgoingVideoTrack()
        if (current) current.enabled = false
        if (cameraTrack) cameraTrack.enabled = false
      }, MEDIA_STATE_DELAY_MS)
    }
  }

  /**
   * There was no camera when the call started: another app had it (Windows gives a camera to one
   * app at a time), it was plugged in since, or access was granted since. Turning it on tries again.
   * The engines keep a video sender without a track, so this needs no renegotiation.
   */
  async function openCamera() {
    openingCamera = true
    let track: MediaStreamTrack | null = null
    let failure: unknown
    try {
      // The call's own first try may still be running; two would leave one camera open.
      await startWork.catch(() => {})
      if (!cameraMissing()) return
      track = (await navigator.mediaDevices.getUserMedia({ video: CAMERA_CONSTRAINTS })).getVideoTracks()[0] ?? null
    } catch (error) {
      failure = error
    } finally {
      openingCamera = false
    }
    if (!hooks.inCall() || sharing.value !== 'none') {
      track?.stop()
      return
    }
    if (!track) {
      hooks.emit({ type: 'error', message: cameraFailure(failure) })
      return
    }
    cameraTrack?.stop()
    cameraTrack = track
    cameraOn.value = true
    processor?.setSource(track)
    if (!processor && hasEffects(effects.value)) {
      applyEffects()
      await effectsSettled()
      if (!hooks.inCall()) return
    }
    await syncVideo()
    if (mediaStateTimer) clearTimeout(mediaStateTimer)
    mediaStateTimer = setTimeout(hooks.sendMediaState, MEDIA_STATE_DELAY_MS)
    refreshCameras()
  }

  function cameraMissing() {
    return !cameraTrack || (!cameraOn.value && cameraTrack.readyState === 'ended')
  }

  function cameraFailure(error: unknown) {
    const name = error instanceof DOMException ? error.name : ''
    if (name === 'NotReadableError' || name === 'AbortError') return 'The camera is in use by another app.'
    if (name === 'NotAllowedError' || name === 'SecurityError') return 'Camera access is blocked.'
    return 'No camera available.'
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
    hooks.sendMediaState()
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
      hooks.sendMediaState()
    } catch {
      URL.revokeObjectURL(video.src)
      hooks.emit({ type: 'error', message: "Couldn't play that video." })
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
    hooks.sendMediaState()
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
    if (hooks.inCall()) applyEffects()
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
        hooks.emit({ type: 'error', message: 'No camera available.' })
        return
      }
      // MediaPipe is most of the bundle; load it the first time someone asks for it.
      const { EffectsProcessor } = await import('@/effects/EffectsProcessor')
      if (request !== effectsRequest || !hooks.inCall()) return
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
      hooks.emit({ type: 'error', message: "Couldn't load that effect." })
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

  return {
    // state
    micOn, cameraOn, sharing, effects: readonly(effects), effectsStatus: readonly(effectsStatus), mirrorLocal, cameraCount,
    micLevel: readonly(micLevel), localStream, canShareScreen, canShareFile,
    // for the engines
    micTrack: () => micTrack,
    sendStream: () => sendStream,
    outgoingVideoTrack, mediaState, start, stop,
    // actions
    toggleMic, toggleCamera, switchCamera, shareScreen, shareFile, stopSharing, setEffects,
  }
}

export type LocalMedia = ReturnType<typeof createLocalMedia>

// Android/iOS also put VP8 first when E2EE is on, so all platforms negotiate the same codec.
// The SFU only forwards VP8.
export function preferVp8(transceivers: RTCRtpTransceiver[]) {
  const capabilities = RTCRtpReceiver.getCapabilities('video')
  if (!capabilities) return
  const codecs = [...capabilities.codecs].sort((a, b) =>
    Number(b.mimeType.toLowerCase() === 'video/vp8') - Number(a.mimeType.toLowerCase() === 'video/vp8'))
  transceivers.forEach(transceiver => {
    if (transceiver.receiver.track.kind === 'video' && typeof transceiver.setCodecPreferences === 'function') {
      transceiver.setCodecPreferences(codecs)
    }
  })
}