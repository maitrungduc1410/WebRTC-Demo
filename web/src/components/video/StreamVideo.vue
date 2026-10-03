<script setup lang="ts">
import { ref, watch } from 'vue'

const props = defineProps<{
  stream: MediaStream | null
  mirror?: boolean
}>()

const emit = defineEmits<{
  size: [width: number, height: number]
}>()

const video = ref<HTMLVideoElement>()

watch([video, () => props.stream], ([el, stream]) => {
  if (!el || el.srcObject === stream) return
  el.srcObject = stream
  if (stream) el.play().catch(() => {})
}, { immediate: true })

function reportSize() {
  const el = video.value
  if (el?.videoWidth && el.videoHeight) emit('size', el.videoWidth, el.videoHeight)
}

defineExpose({ video })
</script>

<template>
  <!-- Always muted: remote audio plays from a single <audio> element in the main window. -->
  <video
    ref="video"
    autoplay
    playsinline
    muted
    :class="mirror && '-scale-x-100'"
    @loadedmetadata="reportSize"
    @resize="reportSize"
  />
</template>
