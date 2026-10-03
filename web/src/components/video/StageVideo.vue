<script setup lang="ts">
import { computed, ref } from 'vue'
import { useElementSize } from '@vueuse/core'
import StreamVideo from './StreamVideo.vue'

const props = defineProps<{
  stream: MediaStream | null
  fit: boolean
}>()

const emit = defineEmits<{
  size: [width: number, height: number]
}>()

const container = ref<HTMLElement>()
const player = ref<InstanceType<typeof StreamVideo>>()
const { width, height } = useElementSize(container)
const frame = ref({ width: 0, height: 0 })

// The video is laid out just large enough to cover the stage, and "fit" scales it down. Switching
// is then a transform animation; resizing the <video> itself would re-layout every frame.
const cover = computed(() => {
  const { width: vw, height: vh } = frame.value
  if (!vw || !vh || !width.value || !height.value) return { width: width.value, height: height.value }
  const scale = Math.max(width.value / vw, height.value / vh)
  return { width: vw * scale, height: vh * scale }
})
const fitScale = computed(() =>
  Math.min(width.value / Math.max(cover.value.width, 1), height.value / Math.max(cover.value.height, 1)) || 1)

function onSize(w: number, h: number) {
  frame.value = { width: w, height: h }
  emit('size', w, h)
}

defineExpose({ video: computed(() => player.value?.video) })
</script>

<template>
  <div ref="container" class="relative size-full overflow-hidden">
    <StreamVideo
      ref="player"
      :stream="props.stream"
      class="absolute top-1/2 left-1/2 max-w-none object-cover transition-transform duration-700 ease-spring"
      :style="{
        width: `${cover.width}px`,
        height: `${cover.height}px`,
        transform: `translate(-50%, -50%) scale(${props.fit ? fitScale : 1})`,
      }"
      @size="onSize"
    />
  </div>
</template>
