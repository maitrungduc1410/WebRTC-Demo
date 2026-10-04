<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { animate, useMotionValue } from 'motion-v'
import { MicOff, MonitorUp } from '@lucide/vue'
import type { Sharing } from '@/call/useCall'
import StreamVideo from '@/components/video/StreamVideo.vue'
import MicLevel from './MicLevel.vue'
import PeerPlaceholder from './PeerPlaceholder.vue'
import { useSafeArea } from '@/composables/useSafeArea'
import { cn } from '@/lib/utils'

type Corner = 'top-left' | 'top-right' | 'bottom-left' | 'bottom-right'

const props = defineProps<{
  stream: MediaStream | null
  mirror: boolean
  /** Alone: the tile is the whole stage. With a peer: a draggable tile in a corner. */
  pip: boolean
  stageWidth: number
  stageHeight: number
  controlsVisible: boolean
  compact: boolean
  micOn: boolean
  /** 0..1 from our own microphone */
  micLevel: number
  cameraOn: boolean
  sharing: Sharing
  /** Group call: how the others see this device, shown next to "You". */
  label?: string
}>()

const emit = defineEmits<{
  tap: []
  doubleTap: []
}>()

const root = ref<HTMLElement>()
const safe = useSafeArea()
const corner = ref<Corner>('top-right')
const frame = ref({ width: 4, height: 3 })

const x = useMotionValue(0)
const y = useMotionValue(0)
const width = useMotionValue(0)
const height = useMotionValue(0)
const radius = useMotionValue(0)
const rotate = useMotionValue(0)

const tileSize = computed(() => {
  const aspect = Math.min(Math.max(frame.value.width / frame.value.height, 0.5), 2)
  const clamp = (v: number, min: number, max: number) => Math.min(Math.max(v, min), max)
  const w = aspect >= 1
    ? (props.compact ? 160 : clamp(props.stageWidth * 0.2, 220, 320))
    : (props.compact ? 112 : clamp(props.stageWidth * 0.12, 150, 210))
  return { width: w, height: w / aspect }
})

const limits = computed(() => {
  const margin = props.compact ? 12 : 20
  return {
    left: safe.left + margin,
    right: safe.right + margin,
    top: safe.top + (props.controlsVisible ? (props.compact ? 72 : 84) : margin),
    bottom: safe.bottom + (props.controlsVisible ? (props.compact ? 100 : 112) : margin),
  }
})

function cornerPosition(c: Corner) {
  const { width: w, height: h } = tileSize.value
  const { left, right, top, bottom } = limits.value
  return {
    x: c.endsWith('left') ? left : props.stageWidth - w - right,
    y: c.startsWith('top') ? top : props.stageHeight - h - bottom,
  }
}

const target = computed(() => props.pip
  ? { ...cornerPosition(corner.value), ...tileSize.value, radius: props.compact ? 20 : 24 }
  : { x: 0, y: 0, width: props.stageWidth, height: props.stageHeight, radius: 0 })

const snappy = { type: 'spring', stiffness: 380, damping: 34 } as const
const soft = { type: 'spring', stiffness: 170, damping: 24 } as const

let ready = false
function moveTo(t = target.value, transition: object = snappy, velocity = { x: 0, y: 0 }) {
  if (!ready) {
    x.jump(t.x); y.jump(t.y); width.jump(t.width); height.jump(t.height); radius.jump(t.radius)
    ready = t.width > 0
    return
  }
  animate(x, t.x, { ...transition, velocity: velocity.x })
  animate(y, t.y, { ...transition, velocity: velocity.y })
  animate(width, t.width, transition)
  animate(height, t.height, transition)
  animate(radius, t.radius, transition)
}

watch(() => props.pip, () => moveTo(target.value, soft))
watch(target, t => { if (!drag) moveTo(t) }, { deep: true })

// Written straight to the element: these change every frame and Vue does not need to know.
function apply() {
  const el = root.value
  if (!el) return
  el.style.transform = `translate3d(${x.get()}px, ${y.get()}px, 0) perspective(1000px) rotateY(${rotate.get()}deg)`
  el.style.width = `${width.get()}px`
  el.style.height = `${height.get()}px`
  el.style.borderRadius = `${radius.get()}px`
}
const unsubscribe = [x, y, width, height, radius, rotate].map(value => value.on('change', apply))
onMounted(() => {
  moveTo()
  apply()
})
onBeforeUnmount(() => {
  unsubscribe.forEach(stop => stop())
  if (tapTimer) clearTimeout(tapTimer)
})

// ---- Gestures -------------------------------------------------------------------------------

let drag: { id: number; startX: number; startY: number; originX: number; originY: number; moved: boolean } | null = null
let lastTap = 0
let tapTimer: ReturnType<typeof setTimeout> | null = null

function onPointerDown(event: PointerEvent) {
  if (!props.pip || event.button > 0) return
  root.value?.setPointerCapture(event.pointerId)
  x.stop()
  y.stop()
  drag = { id: event.pointerId, startX: event.clientX, startY: event.clientY, originX: x.get(), originY: y.get(), moved: false }
}

function onPointerMove(event: PointerEvent) {
  if (!drag || event.pointerId !== drag.id) return
  const dx = event.clientX - drag.startX
  const dy = event.clientY - drag.startY
  if (!drag.moved && Math.hypot(dx, dy) < 5) return
  drag.moved = true
  x.set(drag.originX + dx)
  y.set(drag.originY + dy)
}

function onPointerUp(event: PointerEvent) {
  if (!drag || event.pointerId !== drag.id) return
  const moved = drag.moved
  drag = null
  if (!moved) {
    onTap()
    return
  }
  // Where a flick would carry the tile decides the corner, not where the finger let go.
  const velocity = { x: x.getVelocity(), y: y.getVelocity() }
  const projectedX = x.get() + velocity.x * 0.18 + tileSize.value.width / 2
  const projectedY = y.get() + velocity.y * 0.18 + tileSize.value.height / 2
  const vertical = projectedY < props.stageHeight / 2 ? 'top' : 'bottom'
  const horizontal = projectedX < props.stageWidth / 2 ? 'left' : 'right'
  corner.value = `${vertical}-${horizontal}`
  moveTo({ ...target.value }, { type: 'spring', stiffness: 300, damping: 28 }, velocity)
}

function onTap() {
  const now = performance.now()
  if (now - lastTap < 280) {
    if (tapTimer) clearTimeout(tapTimer)
    tapTimer = null
    lastTap = 0
    emit('doubleTap')
    return
  }
  lastTap = now
  tapTimer = setTimeout(() => {
    tapTimer = null
    emit('tap')
  }, 280)
}

/** Turns the tile edge-on, runs the switch while it is invisible, then shows the new camera. */
async function flip(run: () => Promise<unknown>) {
  await animate(rotate, 90, { duration: 0.17, ease: 'easeIn' })
  await run()
  rotate.jump(-90)
  await animate(rotate, 0, { type: 'spring', stiffness: 260, damping: 20 })
}

defineExpose({ flip })
</script>

<template>
  <div
    ref="root"
    :class="cn(
      'absolute top-0 left-0 z-10 overflow-hidden bg-zinc-900 will-change-transform select-none',
      'transition-[box-shadow,scale] duration-300',
      props.pip
        ? 'cursor-grab touch-none shadow-2xl shadow-black/50 ring-1 ring-white/20 hover:scale-[1.02] active:cursor-grabbing'
        : 'pointer-events-none',
    )"
    @pointerdown="onPointerDown"
    @pointermove="onPointerMove"
    @pointerup="onPointerUp"
    @pointercancel="onPointerUp"
  >
    <StreamVideo
      v-if="props.sharing !== 'screen'"
      :stream="props.stream"
      :mirror="props.mirror"
      class="pointer-events-none size-full object-cover"
      @size="(w, h) => (frame = { width: w, height: h })"
    />

    <Transition
      enter-active-class="transition-opacity duration-300"
      leave-active-class="transition-opacity duration-300"
      enter-from-class="opacity-0"
      leave-to-class="opacity-0"
    >
      <div
        v-if="props.sharing === 'screen'"
        class="absolute inset-0 flex flex-col items-center justify-center gap-2 bg-gradient-to-br from-indigo-950 to-zinc-900 text-white"
      >
        <MonitorUp :class="props.pip ? 'size-7 animate-pulse' : 'size-12 animate-pulse'" />
        <span :class="props.pip ? 'text-xs font-medium' : 'text-base font-medium'">You're presenting</span>
      </div>
      <PeerPlaceholder
        v-else-if="!props.cameraOn && props.sharing === 'none'"
        :size="props.pip ? 'sm' : 'lg'"
        :title="props.pip ? undefined : 'Your camera is off'"
      />
    </Transition>

    <Transition
      enter-active-class="transition duration-300 ease-bounce"
      leave-active-class="transition duration-200"
      enter-from-class="scale-50 opacity-0"
      leave-to-class="scale-50 opacity-0"
    >
      <div
        v-if="props.pip"
        :class="cn(
          'pointer-events-none absolute bottom-2 left-2 flex items-center gap-1.5 rounded-full bg-black/55 py-1 pr-2.5 pl-1.5 text-xs font-medium text-white backdrop-blur-md',
          props.micOn ? 'max-w-[calc(100%-3rem)]' : 'max-w-[calc(100%-1rem)]',
        )"
      >
        <!-- Collapsed, the negative margin leaves "You" where the pill's wider left padding would. -->
        <span
          :class="cn(
            'flex h-5 shrink-0 items-center justify-center transition-[width,margin] duration-300 ease-bounce',
            props.micOn ? '-mr-0.5 w-0' : 'w-5',
          )"
        >
          <span
            :class="cn(
              'flex size-5 shrink-0 items-center justify-center rounded-full bg-red-500 transition-[scale,opacity] duration-300 ease-bounce',
              props.micOn ? 'scale-50 opacity-0' : 'scale-100 opacity-100',
            )"
          >
            <MicOff class="size-3" />
          </span>
        </span>
        <span class="shrink-0">You</span>
        <span v-if="props.label" class="truncate text-white/65">· {{ props.label }}</span>
      </div>
    </Transition>

    <!-- Your microphone: in the tile's corner as a picture-in-picture (the pill already marks it
         muted), on the left edge while the tile is the whole stage. -->
    <Transition
      enter-active-class="transition duration-300 ease-bounce"
      leave-active-class="transition duration-200"
      enter-from-class="scale-50 opacity-0"
      leave-to-class="scale-50 opacity-0"
    >
      <MicLevel
        v-if="props.pip && props.micOn"
        :level="props.micLevel"
        :muted="false"
        size="sm"
        class="pointer-events-none absolute right-2 bottom-2"
      />
    </Transition>
    <Transition
      enter-active-class="transition duration-500 ease-bounce"
      leave-active-class="transition duration-200"
      enter-from-class="scale-50 opacity-0"
      leave-to-class="scale-50 opacity-0"
    >
      <MicLevel
        v-if="!props.pip"
        :level="props.micLevel"
        :muted="!props.micOn"
        size="lg"
        class="pointer-events-none absolute top-1/2 -translate-y-1/2"
        :style="{ left: `${safe.left + (props.compact ? 16 : 24)}px` }"
      />
    </Transition>
  </div>
</template>
