import { FaceLandmarker, FilesetResolver, ImageSegmenter, type NormalizedLandmark } from '@mediapipe/tasks-vision'
import type { BackgroundOption, StickerOption } from './catalog'
import { PlacementSmoother, placeSticker, type Placement } from './placement'

const WASM_URL = `https://cdn.jsdelivr.net/npm/@mediapipe/tasks-vision@${__MEDIAPIPE_VERSION__}/wasm`
const SEGMENTER_URL = 'https://storage.googleapis.com/mediapipe-models/image_segmenter/selfie_segmenter/float16/latest/selfie_segmenter.tflite'
const LANDMARKER_URL = 'https://storage.googleapis.com/mediapipe-models/face_landmarker/face_landmarker/float16/latest/face_landmarker.task'
const FPS = 30

// Face mesh points: the corners of both eyes, the nose tip and the middle of the lips.
const EYE_A = [33, 133] as const
const EYE_B = [362, 263] as const
const NOSE_TIP = 1
const LIPS = [13, 14] as const

let vision: ReturnType<typeof FilesetResolver.forVisionTasks> | null = null
let segmenter: Promise<ImageSegmenter> | null = null
let landmarker: Promise<FaceLandmarker> | null = null
const images = new Map<string, Promise<HTMLImageElement>>()

function loadVision() {
  vision ??= FilesetResolver.forVisionTasks(WASM_URL)
  vision.catch(() => { vision = null })
  return vision
}

function loadSegmenter() {
  segmenter ??= loadVision().then(files =>
    ImageSegmenter.createFromOptions(files, {
      baseOptions: { modelAssetPath: SEGMENTER_URL, delegate: 'GPU' },
      runningMode: 'VIDEO',
      outputCategoryMask: true,
      outputConfidenceMasks: false,
    }))
  segmenter.catch(() => { segmenter = null })
  return segmenter
}

function loadLandmarker() {
  landmarker ??= loadVision().then(files =>
    FaceLandmarker.createFromOptions(files, {
      baseOptions: { modelAssetPath: LANDMARKER_URL, delegate: 'GPU' },
      runningMode: 'VIDEO',
      numFaces: 1,
    }))
  landmarker.catch(() => { landmarker = null })
  return landmarker
}

function loadImage(url: string) {
  let pending = images.get(url)
  if (!pending) {
    pending = new Promise<HTMLImageElement>((resolve, reject) => {
      const image = new Image()
      image.decoding = 'async'
      image.onload = () => resolve(image)
      image.onerror = () => reject(new Error(`Couldn't load ${url}`))
      image.src = url
    })
    pending.catch(() => images.delete(url))
    images.set(url, pending)
  }
  return pending
}

async function loadVideo(url: string) {
  const video = document.createElement('video')
  video.muted = true
  video.loop = true
  video.playsInline = true
  video.src = url
  try {
    await video.play()
    return video
  } catch (error) {
    disposeVideo(video)
    throw error
  }
}

function disposeVideo(video: HTMLVideoElement | undefined) {
  if (!video) return
  video.pause()
  video.removeAttribute('src')
  video.load()
}

/** Releases the MediaPipe models; the next processor loads them again. */
export async function releaseModels() {
  const pending = [segmenter, landmarker]
  segmenter = landmarker = null
  for (const model of pending) (await model?.catch(() => null))?.close()
}

interface Scene {
  background: BackgroundOption
  sticker: StickerOption | null
  segmenter?: ImageSegmenter
  landmarker?: FaceLandmarker
  image?: HTMLImageElement
  video?: HTMLVideoElement
  stickerImage?: HTMLImageElement
}

/**
 * Draws the camera through the chosen background and sticker into a canvas track, which callers
 * swap into the video sender with `replaceTrack()`.
 */
export class EffectsProcessor {
  readonly track: MediaStreamTrack

  private readonly camera = document.createElement('video')
  private readonly canvas = document.createElement('canvas')
  private readonly ctx: CanvasRenderingContext2D
  private readonly maskCanvas = document.createElement('canvas')
  private readonly maskCtx: CanvasRenderingContext2D
  private readonly blurCanvas = document.createElement('canvas')
  private readonly supportsFilter: boolean
  private readonly smoother = new PlacementSmoother(0.4)
  private maskImage: ImageData | null = null
  private hasMask = false
  private scene: Scene | null = null
  private configuring = 0
  private lastVideoTime = -1
  private running = true
  private frame: number | null = null
  private timer: ReturnType<typeof setTimeout> | null = null

  constructor(source: MediaStreamTrack | null) {
    this.ctx = this.canvas.getContext('2d')!
    this.maskCtx = this.maskCanvas.getContext('2d', { willReadFrequently: true })!
    // Safari before 18 has no canvas filters; blur falls back to scaling down and back up.
    this.supportsFilter = typeof this.ctx.filter === 'string'
    this.camera.muted = true
    this.camera.playsInline = true
    this.track = this.canvas.captureStream(FPS).getVideoTracks()[0]!
    this.setSource(source)
    this.loop()
  }

  /** Loads what the choice needs, then switches to it. Resolves false if a newer call won. */
  async configure(background: BackgroundOption, sticker: StickerOption | null): Promise<boolean> {
    const request = ++this.configuring
    const current = this.scene
    const scene: Scene = { background, sticker }
    try {
      const needsMask = background.kind !== 'none'
      const sameVideo = background.kind === 'video' && current?.background.id === background.id
      await Promise.all([
        needsMask && loadSegmenter().then(m => { scene.segmenter = m }),
        sticker && loadLandmarker().then(m => { scene.landmarker = m }),
        sticker && loadImage(sticker.url).then(i => { scene.stickerImage = i }),
        background.kind === 'image' && loadImage(background.url!).then(i => { scene.image = i }),
        background.kind === 'video' && !sameVideo && loadVideo(background.url!).then(v => { scene.video = v }),
      ])
      if (sameVideo) scene.video = current?.video
    } catch (error) {
      if (scene.video !== current?.video) disposeVideo(scene.video)
      throw error
    }
    if (request !== this.configuring || !this.running) {
      if (scene.video !== current?.video) disposeVideo(scene.video)
      return false
    }
    if (current?.video && current.video !== scene.video) disposeVideo(current.video)
    if (current?.sticker?.id !== sticker?.id) this.smoother.reset()
    if (background.kind === 'none') this.hasMask = false
    this.scene = scene
    return true
  }

  setSource(track: MediaStreamTrack | null) {
    // The mask and face position belong to the previous camera.
    this.hasMask = false
    this.smoother.reset()
    this.camera.srcObject = track ? new MediaStream([track]) : null
    if (track) this.camera.play().catch(() => {})
  }

  stop() {
    this.running = false
    if (this.frame !== null) cancelAnimationFrame(this.frame)
    if (this.timer !== null) clearTimeout(this.timer)
    this.track.stop()
    this.camera.srcObject = null
    disposeVideo(this.scene?.video)
    this.scene = null
  }

  // requestAnimationFrame stops in a hidden tab, which is exactly when the call sits in
  // picture-in-picture; a timer keeps the peer's picture moving.
  private loop = () => {
    if (!this.running) return
    this.render()
    if (document.hidden) {
      this.timer = setTimeout(this.loop, 1000 / FPS)
    } else {
      this.frame = requestAnimationFrame(this.loop)
    }
  }

  private render() {
    const { camera, canvas, ctx, scene } = this
    const width = camera.videoWidth
    const height = camera.videoHeight
    if (!scene || camera.readyState < 2 || !width || !height) return
    // The display refreshes faster than the camera; process each camera frame once.
    if (camera.currentTime === this.lastVideoTime) return
    this.lastVideoTime = camera.currentTime

    if (canvas.width !== width || canvas.height !== height) {
      canvas.width = width
      canvas.height = height
      this.hasMask = false
    }
    const now = performance.now()
    if (scene.segmenter) this.segment(scene.segmenter, now)
    const placement = scene.landmarker && scene.sticker && scene.stickerImage
      ? this.smoother.update(this.findFace(scene.landmarker, scene.sticker, scene.stickerImage, now, width, height))
      : null

    if (scene.background.kind !== 'none' && !this.hasMask) return // never show the room unmasked

    ctx.globalCompositeOperation = 'copy'
    ctx.drawImage(camera, 0, 0, width, height)
    if (scene.background.kind !== 'none') {
      ctx.globalCompositeOperation = 'destination-in'
      if (this.supportsFilter) ctx.filter = 'blur(2px)'
      ctx.drawImage(this.maskCanvas, 0, 0, width, height)
      if (this.supportsFilter) ctx.filter = 'none'
      ctx.globalCompositeOperation = 'destination-over'
      this.drawBackground(scene, width, height)
    }
    ctx.globalCompositeOperation = 'source-over'
    if (placement && scene.stickerImage) {
      ctx.save()
      ctx.translate(placement.x, placement.y)
      ctx.rotate(placement.angle)
      ctx.drawImage(scene.stickerImage, -placement.width / 2, -placement.height / 2, placement.width, placement.height)
      ctx.restore()
    }
  }

  private segment(model: ImageSegmenter, timestamp: number) {
    try {
      model.segmentForVideo(this.camera, timestamp, result => {
        const mask = result.categoryMask
        if (!mask) return
        if (this.maskCanvas.width !== mask.width || this.maskCanvas.height !== mask.height || !this.maskImage) {
          this.maskCanvas.width = mask.width
          this.maskCanvas.height = mask.height
          this.maskImage = this.maskCtx.createImageData(mask.width, mask.height)
        }
        const categories = mask.getAsUint8Array()
        const alpha = this.maskImage.data
        // Category 0 is the person.
        for (let i = 0; i < categories.length; i++) {
          alpha[i * 4 + 3] = categories[i] === 0 ? 255 : 0
        }
        this.maskCtx.putImageData(this.maskImage, 0, 0)
        this.hasMask = true
      })
    } catch (error) {
      console.warn('Segmentation failed:', error)
    }
  }

  private findFace(model: FaceLandmarker, sticker: StickerOption, image: HTMLImageElement,
    timestamp: number, width: number, height: number): Placement | null {
    let points: NormalizedLandmark[] | undefined
    try {
      points = model.detectForVideo(this.camera, timestamp).faceLandmarks[0]
    } catch (error) {
      console.warn('Face tracking failed:', error)
    }
    if (!points || points.length < 468) return null
    const at = (...indices: readonly number[]) => ({
      x: indices.reduce((sum, i) => sum + points[i]!.x, 0) / indices.length * width,
      y: indices.reduce((sum, i) => sum + points[i]!.y, 0) / indices.length * height,
    })
    return placeSticker(
      { eyeA: at(...EYE_A), eyeB: at(...EYE_B), nose: at(NOSE_TIP), mouth: at(...LIPS) },
      sticker,
      image.naturalHeight / image.naturalWidth,
    )
  }

  private drawBackground(scene: Scene, width: number, height: number) {
    const { ctx } = this
    if (scene.background.kind === 'blur') {
      const radius = Math.max(2, Math.round((scene.background.blur ?? 0.02) * width))
      if (this.supportsFilter) {
        ctx.filter = `blur(${radius}px)`
        // Drawn oversized so the blur doesn't fade into the transparent area past the edges.
        ctx.drawImage(this.camera, -radius * 2, -radius * 2, width + radius * 4, height + radius * 4)
        ctx.filter = 'none'
      } else {
        const small = this.blurCanvas
        const scale = Math.min(1, 2 / radius)
        small.width = Math.max(1, Math.round(width * scale))
        small.height = Math.max(1, Math.round(height * scale))
        small.getContext('2d')!.drawImage(this.camera, 0, 0, small.width, small.height)
        ctx.imageSmoothingQuality = 'high'
        ctx.drawImage(small, 0, 0, width, height)
      }
      return
    }
    const media = scene.image ?? scene.video
    if (!media) return
    const mediaWidth = media instanceof HTMLImageElement ? media.naturalWidth : media.videoWidth
    const mediaHeight = media instanceof HTMLImageElement ? media.naturalHeight : media.videoHeight
    if (!mediaWidth || !mediaHeight) return
    const scale = Math.max(width / mediaWidth, height / mediaHeight)
    const w = mediaWidth * scale
    const h = mediaHeight * scale
    ctx.drawImage(media, (width - w) / 2, (height - h) / 2, w, h)
  }
}
