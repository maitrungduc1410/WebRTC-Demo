<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useElementSize } from '@vueuse/core'
import { MicOff, MonitorUp } from '@lucide/vue'
import type { Participant } from '@/call/useGroupCall'
import StageVideo from '@/components/video/StageVideo.vue'
import { cn } from '@/lib/utils'
import PeerPlaceholder from './PeerPlaceholder.vue'

const props = defineProps<{
  participant: Participant
  /** 0..1 from this participant's audio receiver */
  audioLevel: number
  speaking: boolean
  /** "Hide all video" from the More menu */
  videoHidden: boolean
}>()

const root = ref<HTMLElement>()
const player = ref<InstanceType<typeof StageVideo>>()
const { height } = useElementSize(root)

// Screen shares are letterboxed so nothing is cut off; double click switches fit and fill.
const userFit = ref<boolean | null>(null)
watch(() => props.participant.state.screen, () => { userFit.value = null })
const fit = computed(() => userFit.value ?? props.participant.state.screen)

const showVideo = computed(() => props.participant.hasVideo && props.participant.state.video && !props.videoHidden)
const roomy = computed(() => height.value >= 260)
const status = computed(() => {
  if (!props.participant.state.audio) return 'muted'
  return props.participant.state.screen ? 'presenting' : 'live'
})

defineExpose({ video: computed(() => player.value?.video) })
</script>

<template>
  <div
    ref="root"
    class="relative overflow-hidden rounded-2xl bg-zinc-900 ring-1 ring-white/10 select-none sm:rounded-3xl"
    @dblclick="userFit = !fit"
  >
    <div class="absolute inset-0">
      <StageVideo ref="player" :stream="props.participant.stream" :fit="fit" />
    </div>
    <Transition
      enter-active-class="transition-opacity duration-500"
      leave-active-class="transition-opacity duration-500"
      enter-from-class="opacity-0"
      leave-to-class="opacity-0"
    >
      <PeerPlaceholder
        v-if="!showVideo"
        :size="roomy ? 'md' : 'sm'"
        :audio-level="props.audioLevel"
        :title="roomy ? (props.videoHidden ? 'You hid their video' : 'Camera is off') : undefined"
      />
    </Transition>

    <div
      :class="cn(
        'pointer-events-none absolute inset-0 rounded-[inherit] ring-3 ring-emerald-400 ring-inset transition-opacity duration-300',
        props.speaking ? 'opacity-100' : 'opacity-0',
      )"
    />

    <div class="absolute bottom-2 left-2 flex max-w-[calc(100%-1rem)] items-center gap-1.5 rounded-full bg-black/55 py-1 pr-2.5 pl-1.5 text-xs font-medium text-white backdrop-blur-md">
      <!-- One slot for the three status marks, so swapping them resizes the pill smoothly. -->
      <span :class="cn('relative h-5 shrink-0 transition-[width] duration-300 ease-bounce', status === 'muted' ? 'w-5' : status === 'presenting' ? 'w-4' : 'w-2')">
        <span
          :class="cn(
            'absolute inset-y-0 left-0 flex size-5 items-center justify-center rounded-full bg-red-500 transition-[scale,opacity] duration-300 ease-bounce',
            status === 'muted' ? 'scale-100 opacity-100' : 'scale-50 opacity-0',
          )"
          :aria-hidden="status !== 'muted'"
          aria-label="Microphone muted"
        >
          <MicOff class="size-3" />
        </span>
        <MonitorUp
          :class="cn(
            'absolute inset-y-0 left-0.5 my-auto size-3.5 transition-[scale,opacity] duration-300 ease-bounce',
            status === 'presenting' ? 'scale-100 opacity-100' : 'scale-50 opacity-0',
          )"
          :aria-hidden="status !== 'presenting'"
          aria-label="Presenting"
        />
        <span
          :class="cn(
            'absolute inset-y-0 left-0.5 my-auto size-1.5 rounded-full bg-emerald-400 transition-[scale,opacity] duration-300 ease-bounce',
            status === 'live' ? 'scale-100 opacity-100' : 'scale-50 opacity-0',
          )"
        />
      </span>
      <span class="truncate">{{ props.participant.label }}</span>
    </div>
  </div>
</template>
