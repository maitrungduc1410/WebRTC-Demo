<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useStrings } from '../i18n'
import Icon from './Icon.vue'

const props = defineProps<{ open: boolean; svg: string; svgId: string; source: string }>()
const emit = defineEmits<{ 'update:open': [value: boolean] }>()

const t = useStrings({
  en: {
    title: 'Diagram',
    hint: 'Drag to move, scroll or pinch to zoom, or use + - 0 and the arrow keys',
    fitWidth: 'Fit width',
    copy: 'Copy Mermaid source',
    copied: 'Copied',
    close: 'Close',
    up: 'Move up',
    down: 'Move down',
    left: 'Move left',
    right: 'Move right',
    reset: 'Fit to screen',
    zoomIn: 'Zoom in',
    zoomOut: 'Zoom out',
  },
  vi: {
    title: 'Sơ đồ',
    hint: 'Kéo để di chuyển, cuộn chuột hoặc chụm hai ngón để phóng to, thu nhỏ. Có thể dùng phím + - 0 và phím mũi tên',
    fitWidth: 'Vừa chiều rộng',
    copy: 'Copy mã Mermaid',
    copied: 'Đã copy',
    close: 'Đóng',
    up: 'Di chuyển lên',
    down: 'Di chuyển xuống',
    left: 'Di chuyển sang trái',
    right: 'Di chuyển sang phải',
    reset: 'Vừa màn hình',
    zoomIn: 'Phóng to',
    zoomOut: 'Thu nhỏ',
  },
  zh: {
    title: '图表',
    hint: '拖动平移，滚轮或双指缩放，也可以用 + - 0 和方向键',
    fitWidth: '适应宽度',
    copy: '复制 Mermaid 源码',
    copied: '已复制',
    close: '关闭',
    up: '上移',
    down: '下移',
    left: '左移',
    right: '右移',
    reset: '适应屏幕',
    zoomIn: '放大',
    zoomOut: '缩小',
  },
})

const MIN_SCALE = 0.1
const MAX_SCALE = 8
const PADDING = 24
const STEP = 80

const dialog = ref<HTMLDialogElement>()
const viewport = ref<HTMLDivElement>()
const content = ref<HTMLDivElement>()
const x = ref(0)
const y = ref(0)
const k = ref(1)
const dragging = ref(false)
const copied = ref(false)
let width = 1
let height = 1
let downOnBackdrop = false
let copiedTimer: ReturnType<typeof setTimeout> | undefined
const pointers = new Map<number, { x: number; y: number }>()

// The page already shows this SVG, and its markers and styles are looked up by id, so the copy needs its own ids.
const dialogSvg = computed(() => props.svg.split(props.svgId).join(`${props.svgId}-dialog`))

const clamp = (value: number) => Math.min(MAX_SCALE, Math.max(MIN_SCALE, value))

function bounds() {
  return viewport.value!.getBoundingClientRect()
}

function measure() {
  const el = content.value?.querySelector('svg')
  if (!el) return
  const box = el.viewBox.baseVal
  const size = box && box.width ? box : el.getBBox()
  width = size.width || 1
  height = size.height || 1
  el.style.maxWidth = 'none'
  el.setAttribute('width', String(width))
  el.setAttribute('height', String(height))
}

function fit() {
  const r = bounds()
  k.value = clamp(Math.min((r.width - PADDING * 2) / width, (r.height - PADDING * 2) / height, 2))
  x.value = (r.width - width * k.value) / 2
  y.value = (r.height - height * k.value) / 2
}

function fitWidth() {
  const r = bounds()
  k.value = clamp((r.width - PADDING * 2) / width)
  x.value = (r.width - width * k.value) / 2
  y.value = height * k.value < r.height ? (r.height - height * k.value) / 2 : PADDING
}

function zoomAt(factor: number, cx: number, cy: number) {
  const next = clamp(k.value * factor)
  const ratio = next / k.value
  x.value = cx - (cx - x.value) * ratio
  y.value = cy - (cy - y.value) * ratio
  k.value = next
}

function zoom(factor: number) {
  const r = bounds()
  zoomAt(factor, r.width / 2, r.height / 2)
}

function pan(dx: number, dy: number) {
  x.value += dx
  y.value += dy
}

function local(e: { clientX: number; clientY: number }) {
  const r = bounds()
  return { x: e.clientX - r.left, y: e.clientY - r.top }
}

function pinch() {
  const [a, b] = [...pointers.values()]
  return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2, distance: Math.hypot(a.x - b.x, a.y - b.y) || 1 }
}

function onPointerDown(e: PointerEvent) {
  if (e.pointerType === 'mouse' && e.button !== 0) return
  if (pointers.size >= 2) return
  viewport.value!.setPointerCapture(e.pointerId)
  pointers.set(e.pointerId, local(e))
  dragging.value = true
}

function onPointerMove(e: PointerEvent) {
  const previous = pointers.get(e.pointerId)
  if (!previous) return
  const point = local(e)
  if (pointers.size === 1) {
    pan(point.x - previous.x, point.y - previous.y)
    pointers.set(e.pointerId, point)
    return
  }
  const before = pinch()
  pointers.set(e.pointerId, point)
  const after = pinch()
  zoomAt(after.distance / before.distance, before.x, before.y)
  pan(after.x - before.x, after.y - before.y)
}

function release(pointerId: number) {
  if (viewport.value?.hasPointerCapture(pointerId)) viewport.value.releasePointerCapture(pointerId)
  pointers.delete(pointerId)
  dragging.value = pointers.size > 0
}

function onPointerUp(e: PointerEvent) {
  release(e.pointerId)
}

function onWheel(e: WheelEvent) {
  const unit = e.deltaMode === WheelEvent.DOM_DELTA_LINE ? 16 : e.deltaMode === WheelEvent.DOM_DELTA_PAGE ? bounds().height : 1
  const delta = e.deltaY * unit
  // Trackpad pinches arrive as wheel events with ctrlKey and small deltas, so they get a bigger multiplier.
  const point = local(e)
  zoomAt(Math.exp(-delta * (e.ctrlKey ? 0.01 : 0.002)), point.x, point.y)
}

function onKeydown(e: KeyboardEvent) {
  if (e.altKey || e.ctrlKey || e.metaKey) return
  const actions: Record<string, () => void> = {
    '+': () => zoom(1.25),
    '=': () => zoom(1.25),
    '-': () => zoom(0.8),
    _: () => zoom(0.8),
    '0': fit,
    ArrowUp: () => pan(0, -STEP),
    ArrowDown: () => pan(0, STEP),
    ArrowLeft: () => pan(-STEP, 0),
    ArrowRight: () => pan(STEP, 0),
  }
  const action = actions[e.key]
  if (!action) return
  e.preventDefault()
  action()
}

function onDialogClick(e: MouseEvent) {
  if (downOnBackdrop && e.target === dialog.value) close()
}

async function copy() {
  try {
    await navigator.clipboard.writeText(props.source)
  } catch {
    return
  }
  copied.value = true
  clearTimeout(copiedTimer)
  copiedTimer = setTimeout(() => (copied.value = false), 1500)
}

function close() {
  dialog.value?.close()
}

function onClose() {
  pointers.forEach((_, pointerId) => release(pointerId))
  document.documentElement.style.removeProperty('overflow')
  emit('update:open', false)
}

watch(
  () => props.open,
  async (open) => {
    if (!dialog.value) return
    if (!open) {
      if (dialog.value.open) close()
      return
    }
    if (dialog.value.open) return
    dialog.value.showModal()
    document.documentElement.style.overflow = 'hidden'
    await nextTick()
    if (!dialog.value?.open) return
    measure()
    fit()
  },
)

watch(dialogSvg, async () => {
  if (!dialog.value?.open) return
  await nextTick()
  measure()
  fit()
})

onBeforeUnmount(() => {
  clearTimeout(copiedTimer)
  if (dialog.value?.open) document.documentElement.style.removeProperty('overflow')
})
</script>

<template>
  <dialog
    ref="dialog"
    class="mermaid-dialog"
    :aria-label="t.title"
    @close="onClose"
    @keydown="onKeydown"
    @pointerdown="downOnBackdrop = $event.target === dialog"
    @click="onDialogClick"
  >
    <div class="bar">
      <span class="hint" :title="t.hint">{{ t.hint }}</span>
      <span class="visually-hidden" aria-live="polite">{{ copied ? t.copied : '' }}</span>
      <span class="scale">{{ Math.round(k * 100) }}%</span>
      <button type="button" :title="t.fitWidth" :aria-label="t.fitWidth" @click="fitWidth">
        <Icon name="fitWidth" />
      </button>
      <button type="button" :title="copied ? t.copied : t.copy" :aria-label="copied ? t.copied : t.copy" @click="copy">
        <Icon :name="copied ? 'check' : 'copy'" />
      </button>
      <button type="button" :title="t.close" :aria-label="t.close" @click="close">
        <Icon name="close" />
      </button>
    </div>
    <div class="stage">
      <div
        ref="viewport"
        class="viewport"
        :class="{ dragging }"
        @pointerdown="onPointerDown"
        @pointermove="onPointerMove"
        @pointerup="onPointerUp"
        @pointercancel="onPointerUp"
        @wheel.prevent="onWheel"
      >
        <div
          ref="content"
          class="content"
          :style="{ transform: `translate(${x}px, ${y}px) scale(${k})` }"
          v-html="dialogSvg"
        />
      </div>
      <div class="pad">
        <span />
        <button type="button" :title="t.up" :aria-label="t.up" @click="pan(0, -STEP)"><Icon name="up" /></button>
        <button type="button" :title="t.zoomIn" :aria-label="t.zoomIn" @click="zoom(1.25)">
          <Icon name="zoomIn" />
        </button>
        <button type="button" :title="t.left" :aria-label="t.left" @click="pan(-STEP, 0)"><Icon name="left" /></button>
        <button type="button" :title="t.reset" :aria-label="t.reset" @click="fit"><Icon name="reset" /></button>
        <button type="button" :title="t.right" :aria-label="t.right" @click="pan(STEP, 0)"><Icon name="right" /></button>
        <span />
        <button type="button" :title="t.down" :aria-label="t.down" @click="pan(0, STEP)"><Icon name="down" /></button>
        <button type="button" :title="t.zoomOut" :aria-label="t.zoomOut" @click="zoom(0.8)">
          <Icon name="zoomOut" />
        </button>
      </div>
    </div>
  </dialog>
</template>

<style scoped>
.mermaid-dialog {
  width: min(1400px, 96vw);
  height: min(900px, 92vh);
  height: min(900px, 92dvh);
  max-width: none;
  max-height: none;
  margin: auto;
  padding: 0;
  border: 1px solid var(--vp-c-divider);
  border-radius: 12px;
  background: var(--vp-c-bg);
  color: var(--vp-c-text-1);
  text-align: left;
  overflow: hidden;
}

.mermaid-dialog[open] {
  display: flex;
  flex-direction: column;
}

.mermaid-dialog::backdrop {
  background: rgba(0, 0, 0, 0.6);
}

.bar {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 8px 8px 16px;
  border-bottom: 1px solid var(--vp-c-divider);
}

.hint {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  font-size: 13px;
  color: var(--vp-c-text-2);
  white-space: nowrap;
  text-overflow: ellipsis;
}

.scale {
  min-width: 48px;
  font-size: 13px;
  font-variant-numeric: tabular-nums;
  text-align: right;
  color: var(--vp-c-text-2);
}

.stage {
  position: relative;
  flex: 1;
  min-height: 0;
}

.viewport {
  position: absolute;
  inset: 0;
  overflow: hidden;
  touch-action: none;
  cursor: grab;
  background: var(--vp-c-bg-soft);
}

.viewport.dragging {
  cursor: grabbing;
}

.content {
  position: absolute;
  top: 0;
  left: 0;
  transform-origin: 0 0;
  will-change: transform;
  user-select: none;
}

.content :deep(svg) {
  display: block;
}

/* The dialog sits inside .vp-doc too, so its labels need the same line-height reset as the inline diagram. */
.content :deep(svg p) {
  line-height: inherit;
}

.pad {
  position: absolute;
  right: 16px;
  bottom: 16px;
  display: grid;
  grid-template-columns: repeat(3, 36px);
  gap: 4px;
}

button {
  display: grid;
  place-items: center;
  width: 36px;
  height: 36px;
  border: 1px solid var(--vp-c-divider);
  border-radius: 6px;
  background: var(--vp-c-bg);
  color: var(--vp-c-text-2);
}

button:hover {
  color: var(--vp-c-brand-1);
  border-color: var(--vp-c-brand-1);
}

.pad button {
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.12);
}
</style>
