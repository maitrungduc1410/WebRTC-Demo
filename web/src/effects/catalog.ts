// The effects folder at the repository root is shared with the Android and iOS apps.
const manifests = import.meta.glob<unknown>('../../../effects/*.json', { eager: true, import: 'default' })
const files = import.meta.glob<string>('../../../effects/{backgrounds,thumbnails,stickers}/*.{jpg,png,mp4}', {
  eager: true,
  import: 'default',
  query: '?url',
})

const ROOT = '../../../effects/'

export type BackgroundKind = 'none' | 'blur' | 'image' | 'video'

export interface BackgroundOption {
  id: string
  name: string
  kind: BackgroundKind
  /** Blur radius as a fraction of the frame width. */
  blur?: number
  url?: string
  thumbnail?: string
}

export type StickerAnchor = 'eyes' | 'nose' | 'mouth'

/** Sizes and offsets are in units of the distance between the eyes; positive offsetY is up. */
export interface StickerOption {
  id: string
  name: string
  url: string
  anchor: StickerAnchor
  width: number
  /** Stretches the artwork; without it the picture keeps its own aspect ratio. */
  height?: number
  offsetX: number
  offsetY: number
}

export interface EffectsSelection {
  background: string
  sticker: string | null
}

export const NO_EFFECTS: EffectsSelection = { background: 'none', sticker: null }

const BUILT_IN: BackgroundOption[] = [
  { id: 'none', name: 'None', kind: 'none' },
  { id: 'blur-light', name: 'Slight blur', kind: 'blur', blur: 0.008 },
  { id: 'blur-strong', name: 'Blur', kind: 'blur', blur: 0.02 },
]

function asset(path: unknown) {
  return typeof path === 'string' ? files[ROOT + path] : undefined
}

function list(manifest: string, key: string): Record<string, unknown>[] {
  const value = (manifests[`${ROOT}${manifest}`] as Record<string, unknown> | undefined)?.[key]
  return Array.isArray(value) ? value : []
}

function readBackgrounds(): BackgroundOption[] {
  const reserved = new Set(BUILT_IN.map(b => b.id))
  const assets: BackgroundOption[] = []
  for (const entry of list('backgrounds.json', 'backgrounds')) {
    const url = asset(entry.file)
    const kind = entry.type === 'video' ? 'video' : entry.type === 'image' ? 'image' : null
    if (typeof entry.id !== 'string' || reserved.has(entry.id) || !url || !kind) continue
    reserved.add(entry.id)
    assets.push({
      id: entry.id,
      name: typeof entry.name === 'string' ? entry.name : entry.id,
      kind,
      url,
      thumbnail: asset(entry.thumbnail) ?? (kind === 'image' ? url : undefined),
    })
  }
  return [...BUILT_IN, ...assets]
}

function readStickers(): StickerOption[] {
  const anchors: StickerAnchor[] = ['eyes', 'nose', 'mouth']
  const stickers: StickerOption[] = []
  for (const entry of list('stickers.json', 'stickers')) {
    const url = asset(entry.file)
    if (typeof entry.id !== 'string' || !url) continue
    stickers.push({
      id: entry.id,
      name: typeof entry.name === 'string' ? entry.name : entry.id,
      url,
      anchor: anchors.includes(entry.anchor as StickerAnchor) ? entry.anchor as StickerAnchor : 'eyes',
      width: Number(entry.width) || 1,
      height: Number(entry.height) || undefined,
      offsetX: Number(entry.offsetX) || 0,
      offsetY: Number(entry.offsetY) || 0,
    })
  }
  return stickers
}

export const backgrounds = readBackgrounds()
export const stickers = readStickers()

export function findBackground(id: string) {
  return backgrounds.find(b => b.id === id) ?? backgrounds[0]!
}

export function findSticker(id: string | null) {
  return id ? stickers.find(s => s.id === id) ?? null : null
}

export function hasEffects(selection: EffectsSelection) {
  return findBackground(selection.background).kind !== 'none' || findSticker(selection.sticker) !== null
}

const STORAGE_KEY = 'effects'

/** The last choice, dropping anything that is no longer bundled. */
export function loadSelection(): EffectsSelection {
  try {
    const saved = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? 'null') as Partial<EffectsSelection> | null
    return {
      background: findBackground(String(saved?.background ?? 'none')).id,
      sticker: findSticker(typeof saved?.sticker === 'string' ? saved.sticker : null)?.id ?? null,
    }
  } catch {
    return NO_EFFECTS
  }
}

export function saveSelection(selection: EffectsSelection) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(selection))
  } catch {
    // Private mode; the choice just isn't remembered.
  }
}
