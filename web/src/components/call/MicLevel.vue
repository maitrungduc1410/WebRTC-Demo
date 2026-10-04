<script setup lang="ts">
import { computed } from 'vue'
import { MicOff } from '@lucide/vue'
import { cn } from '@/lib/utils'

// Three bars that follow your own microphone, as in Google Meet: proof the call hears you.

const props = defineProps<{
  /** 0..1 */
  level: number
  muted: boolean
  size: 'sm' | 'lg'
}>()

/** The middle bar moves the most. */
const BAR_GAIN = [0.6, 1, 0.6]

const bar = computed(() => props.size === 'lg' ? { width: 5, min: 5, max: 22 } : { width: 3, min: 3, max: 12 })
const heights = computed(() =>
  BAR_GAIN.map(gain => bar.value.min + (bar.value.max - bar.value.min) * (props.muted ? 0 : props.level) * gain))
</script>

<template>
  <!-- The root has no transition or position of its own: the parent's enter/leave transition and
       placement go on it. -->
  <div
    :class="cn('text-white', props.size === 'lg' ? 'size-12' : 'size-7')"
    :aria-label="props.muted ? 'Microphone muted' : 'Your microphone level'"
    role="img"
  >
    <div class="relative size-full">
      <div
        :class="cn(
          'absolute inset-0 rounded-full backdrop-blur-md transition-colors duration-300',
          props.muted ? 'bg-red-500' : 'bg-black/55',
        )"
      />
      <MicOff
        :class="cn(
          'absolute inset-0 m-auto transition-[scale,opacity] duration-300 ease-bounce',
          props.size === 'lg' ? 'size-5' : 'size-3.5',
          props.muted ? 'scale-100 opacity-100' : 'scale-50 opacity-0',
        )"
      />
      <div
        :class="cn(
          'absolute inset-0 flex items-center justify-center transition-[scale,opacity] duration-300 ease-bounce',
          props.size === 'lg' ? 'gap-1' : 'gap-[3px]',
          props.muted ? 'scale-50 opacity-0' : 'scale-100 opacity-100',
        )"
      >
        <span
          v-for="(height, index) in heights"
          :key="index"
          class="rounded-full bg-white transition-[height] duration-100 ease-out"
          :style="{ width: `${bar.width}px`, height: `${height}px` }"
        />
      </div>
    </div>
  </div>
</template>
