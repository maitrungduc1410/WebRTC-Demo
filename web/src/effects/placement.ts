import type { StickerOption } from './catalog'

export interface Point { x: number; y: number }

/** Face points in pixels, y down. The two eyes can come in either order. */
export interface FacePoints {
  eyeA: Point
  eyeB: Point
  nose: Point
  mouth: Point
}

/** Where to draw a sticker: its center, size and rotation (radians, clockwise on screen). */
export interface Placement {
  x: number
  y: number
  width: number
  height: number
  angle: number
}

// Eyes to mouth over the distance between the eyes on a face looking at the camera. The larger of
// the two scales is used so a sticker doesn't shrink when the head turns sideways.
const EYES_TO_MOUTH_RATIO = 1.2

/**
 * Places a sticker on a face. The face's own axes are used, so the sticker tilts with the head:
 * "right" runs from one eye to the other, "up" from the mouth towards the eyes.
 */
export function placeSticker(face: FacePoints, sticker: StickerOption, aspect: number): Placement | null {
  const eyes = { x: (face.eyeA.x + face.eyeB.x) / 2, y: (face.eyeA.y + face.eyeB.y) / 2 }
  const rough = { x: eyes.x - face.mouth.x, y: eyes.y - face.mouth.y }
  // Up (0, -1) turns into right (1, 0).
  const towardsRight = { x: -rough.y, y: rough.x }
  const [left, right] = (face.eyeB.x - face.eyeA.x) * towardsRight.x + (face.eyeB.y - face.eyeA.y) * towardsRight.y >= 0
    ? [face.eyeA, face.eyeB]
    : [face.eyeB, face.eyeA]

  const axis = { x: right.x - left.x, y: right.y - left.y }
  const iod = Math.hypot(axis.x, axis.y)
  if (iod < 1) return null
  const ux = { x: axis.x / iod, y: axis.y / iod }
  const uy = { x: ux.y, y: -ux.x }
  const unit = Math.max(iod, Math.hypot(rough.x, rough.y) / EYES_TO_MOUTH_RATIO)

  const anchor = sticker.anchor === 'nose' ? face.nose : sticker.anchor === 'mouth' ? face.mouth : eyes
  const width = sticker.width * unit
  return {
    x: anchor.x + unit * (sticker.offsetX * ux.x + sticker.offsetY * uy.x),
    y: anchor.y + unit * (sticker.offsetX * ux.y + sticker.offsetY * uy.y),
    width,
    height: sticker.height ? sticker.height * unit : width * aspect,
    angle: Math.atan2(ux.y, ux.x),
  }
}

/** Evens out landmark jitter, and keeps the last placement through a few missed detections. */
export class PlacementSmoother {
  private current: Placement | null = null
  private missed = 0

  constructor(private readonly amount = 0.5, private readonly keepFrames = 6) {}

  update(next: Placement | null): Placement | null {
    if (!next) {
      if (++this.missed > this.keepFrames) this.current = null
      return this.current
    }
    this.missed = 0
    const prev = this.current
    if (!prev) return (this.current = next)
    const k = this.amount
    let delta = next.angle - prev.angle
    delta = Math.atan2(Math.sin(delta), Math.cos(delta))
    this.current = {
      x: prev.x + (next.x - prev.x) * (1 - k),
      y: prev.y + (next.y - prev.y) * (1 - k),
      width: prev.width + (next.width - prev.width) * (1 - k),
      height: prev.height + (next.height - prev.height) * (1 - k),
      angle: prev.angle + delta * (1 - k),
    }
    return this.current
  }

  reset() {
    this.current = null
    this.missed = 0
  }
}
