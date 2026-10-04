<script setup lang="ts">
import { ref, watch } from 'vue'

const props = defineProps<{ stream: MediaStream | null }>()

const audio = ref<HTMLAudioElement>()

watch([audio, () => props.stream], ([el, stream]) => {
  if (!el || el.srcObject === stream) return
  el.srcObject = stream
  if (stream) el.play().catch(() => {})
}, { immediate: true })
</script>

<template>
  <audio ref="audio" autoplay />
</template>
