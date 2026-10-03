import { FilesetResolver, ImageSegmenter } from '@mediapipe/tasks-vision'

const WASM_URL = `https://cdn.jsdelivr.net/npm/@mediapipe/tasks-vision@${__MEDIAPIPE_VERSION__}/wasm`
const MODEL_URL = 'https://storage.googleapis.com/mediapipe-models/image_segmenter/selfie_segmenter/float16/latest/selfie_segmenter.tflite'
const BACKGROUND_URL = '/virtual_background.jpg'
const FPS = 30

let segmenter: Promise<ImageSegmenter> | null = null
let background: Promise<HTMLImageElement> | null = null

function loadSegmenter() {
  segmenter ??= FilesetResolver.forVisionTasks(WASM_URL).then(vision =>
    ImageSegmenter.createFromOptions(vision, {
      baseOptions: { modelAssetPath: MODEL_URL, delegate: 'GPU' },
      runningMode: 'VIDEO',
      outputCategoryMask: true,
      outputConfidenceMasks: false,
    }))
  segmenter.catch(() => { segmenter = null })
  return segmenter
}

function loadBackground() {
  background ??= new Promise((resolve, reject) => {
    const image = new Image()
    image.onload = () => resolve(image)
    image.onerror = reject
    image.src = BACKGROUND_URL
  })
  background.catch(() => { background = null })
  return background
}

/** Releases the MediaPipe model; the next processor loads it again. */
export async function releaseSegmenter() {
  const pending = segmenter
  segmenter = null
  ;(await pending?.catch(() => null))?.close()
}

/**
 * Replaces everything but the person in a camera track with a picture. The output is a canvas
 * track, so callers swap it into the video sender with `replaceTrack()`.
 */
export class BackgroundProcessor {
  readonly track: MediaStreamTrack

  private readonly video = document.createElement('video')
  private readonly canvas = document.createElement('canvas')
  private readonly ctx: CanvasRenderingContext2D
  private readonly maskCanvas = document.createElement('canvas')
  private readonly maskCtx: CanvasRenderingContext2D
  private maskImage: ImageData | null = null
  private lastVideoTime = -1
  private running = true
  private frame: number | null = null
  private timer: ReturnType<typeof setTimeout> | null = null

  private constructor(
    private readonly segmenter: ImageSegmenter,
    private readonly image: HTMLImageElement,
    source: MediaStreamTrack,
  ) {
    this.ctx = this.canvas.getContext('2d')!
    this.maskCtx = this.maskCanvas.getContext('2d', { willReadFrequently: true })!
    this.video.muted = true
    this.video.playsInline = true
    this.track = this.canvas.captureStream(FPS).getVideoTracks()[0]!
    this.setSource(source)
    this.loop()
  }

  static async create(source: MediaStreamTrack) {
    const [segmenter, image] = await Promise.all([loadSegmenter(), loadBackground()])
    return new BackgroundProcessor(segmenter, image, source)
  }

  setSource(track: MediaStreamTrack | null) {
    this.video.srcObject = track ? new MediaStream([track]) : null
    if (track) this.video.play().catch(() => {})
  }

  stop() {
    this.running = false
    if (this.frame !== null) cancelAnimationFrame(this.frame)
    if (this.timer !== null) clearTimeout(this.timer)
    this.track.stop()
    this.video.srcObject = null
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
    const { video, canvas, ctx } = this
    const width = video.videoWidth
    const height = video.videoHeight
    if (video.readyState < 2 || !width || !height) return
    // The display refreshes faster than the camera; segment each camera frame once.
    if (video.currentTime === this.lastVideoTime) return
    this.lastVideoTime = video.currentTime

    if (canvas.width !== width || canvas.height !== height) {
      canvas.width = this.maskCanvas.width = width
      canvas.height = this.maskCanvas.height = height
      this.maskImage = this.maskCtx.createImageData(width, height)
    }

    this.segmenter.segmentForVideo(video, performance.now(), result => {
      const mask = result.categoryMask
      if (!mask || !this.maskImage) return
      const categories = mask.getAsUint8Array()
      const alpha = this.maskImage.data
      // Category 0 is the person.
      for (let i = 0; i < categories.length; i++) {
        alpha[i * 4 + 3] = categories[i] === 0 ? 255 : 0
      }
      this.maskCtx.putImageData(this.maskImage, 0, 0)

      ctx.globalCompositeOperation = 'copy'
      ctx.drawImage(video, 0, 0, width, height)
      ctx.globalCompositeOperation = 'destination-in'
      ctx.filter = 'blur(2px)'
      ctx.drawImage(this.maskCanvas, 0, 0)
      ctx.filter = 'none'
      ctx.globalCompositeOperation = 'destination-over'
      this.drawCover(width, height)
      ctx.globalCompositeOperation = 'source-over'
    })
  }

  private drawCover(width: number, height: number) {
    const { image, ctx } = this
    const scale = Math.max(width / image.naturalWidth, height / image.naturalHeight)
    const w = image.naturalWidth * scale
    const h = image.naturalHeight * scale
    ctx.drawImage(image, (width - w) / 2, (height - h) / 2, w, h)
  }
}
