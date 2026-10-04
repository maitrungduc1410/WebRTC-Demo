<script setup lang="ts">
import { computed } from 'vue'
import { Lock, MicOff, MonitorUp, PictureInPicture2, SwitchCamera, Users, VolumeOff } from '@lucide/vue'
import type { MediaState, Phase } from '@/call/useCall'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { Kbd } from '@/components/ui/kbd'
import { useCoarsePointer } from '@/composables/useInputMode'
import { cn } from '@/lib/utils'

const props = defineProps<{
  roomId: string
  e2ee: boolean
  phase: Phase
  remoteMedia: MediaState
  remoteAudioMuted: boolean
  compact: boolean
  showPip: boolean
  pipActive: boolean
  showSwitchCamera: boolean
  /** Group call: everyone in the room, you included. */
  peopleCount?: number
}>()

const emit = defineEmits<{
  togglePip: []
  switchCamera: []
  openPeople: []
}>()

const coarse = useCoarsePointer()

const status = computed(() => ({
  idle: { text: '', dot: 'bg-zinc-400' },
  waiting: { text: 'Waiting for someone', dot: 'bg-amber-400' },
  connecting: { text: 'Connecting…', dot: 'bg-sky-400 animate-pulse' },
  connected: { text: 'Connected', dot: 'bg-emerald-400' },
})[props.phase])

const chips = computed(() => {
  if (props.phase !== 'connected') return []
  const list = []
  if (props.peopleCount) list.push({ id: 'people', icon: Users, text: `${props.peopleCount} in call` })
  if (!props.remoteMedia.audio) list.push({ id: 'muted', icon: MicOff, text: 'Muted' })
  if (props.remoteAudioMuted) list.push({ id: 'silenced', icon: VolumeOff, text: 'Muted by you' })
  if (props.remoteMedia.screen) list.push({ id: 'presenting', icon: MonitorUp, text: 'Presenting' })
  return list
})

const circle = 'glass inline-flex size-11 items-center justify-center rounded-full text-white transition-[scale,background-color] duration-300 ease-bounce hover:scale-105 hover:bg-white/15 active:scale-90'
</script>

<template>
  <div class="flex items-start justify-between gap-3">
    <div class="flex min-w-0 flex-wrap items-center gap-2">
      <div class="glass flex h-11 items-center gap-2.5 rounded-full pr-4 pl-3.5 text-sm text-white">
        <span class="relative flex size-2.5">
          <span v-if="props.phase === 'connected'" class="absolute inset-0 animate-ripple rounded-full bg-emerald-400" />
          <span :class="cn('relative size-2.5 rounded-full transition-colors duration-500', status.dot)" />
        </span>
        <span class="font-mono font-semibold tracking-wider">{{ props.roomId }}</span>
        <Tooltip v-if="props.e2ee" :disabled="coarse">
          <TooltipTrigger as-child>
            <Lock class="size-3.5 text-emerald-300" aria-label="End-to-end encrypted" />
          </TooltipTrigger>
          <TooltipContent>End-to-end encrypted</TooltipContent>
        </Tooltip>
        <Transition
          mode="out-in"
          enter-active-class="transition duration-300 ease-out-expo"
          leave-active-class="transition duration-150"
          enter-from-class="opacity-0 -translate-y-1"
          leave-to-class="opacity-0 translate-y-1"
        >
          <span v-if="!props.compact" :key="props.phase" class="text-white/65">{{ status.text }}</span>
        </Transition>
      </div>

      <TransitionGroup
        enter-active-class="transition duration-400 ease-bounce"
        leave-active-class="transition duration-200 absolute"
        enter-from-class="scale-50 opacity-0"
        leave-to-class="scale-50 opacity-0"
        move-class="transition-transform duration-400 ease-spring"
      >
        <component
          :is="chip.id === 'people' ? 'button' : 'span'"
          v-for="chip in chips"
          :key="chip.id"
          :class="cn(
            'glass inline-flex h-8 items-center gap-1.5 rounded-full px-3 text-xs font-medium text-white',
            chip.id === 'people' && 'cursor-pointer transition-[scale,background-color] duration-300 ease-bounce hover:scale-105 hover:bg-white/15 active:scale-95',
          )"
          v-bind="chip.id === 'people' ? { type: 'button', 'aria-label': `${chip.text}, show everyone` } : {}"
          @click="chip.id === 'people' && emit('openPeople')"
        >
          <component :is="chip.icon" class="size-3.5" />
          {{ chip.text }}
        </component>
      </TransitionGroup>
    </div>

    <div class="flex shrink-0 items-center gap-2">
      <Transition
        enter-active-class="transition duration-400 ease-bounce"
        leave-active-class="transition duration-200"
        enter-from-class="scale-50 opacity-0"
        leave-to-class="scale-50 opacity-0"
      >
        <span v-if="props.showPip" class="inline-flex">
          <Tooltip :disabled="coarse">
            <TooltipTrigger as-child>
              <button
                type="button"
                :class="cn(circle, props.pipActive && 'bg-indigo-500 hover:bg-indigo-400')"
                aria-label="Picture-in-picture"
                @click="emit('togglePip')"
              >
                <PictureInPicture2 class="size-5" />
              </button>
            </TooltipTrigger>
            <TooltipContent side="bottom">
              {{ props.pipActive ? 'Close picture-in-picture' : 'Picture-in-picture' }} <Kbd>P</Kbd>
            </TooltipContent>
          </Tooltip>
        </span>
      </Transition>
      <Tooltip v-if="props.showSwitchCamera" :disabled="coarse">
        <TooltipTrigger as-child>
          <button type="button" :class="circle" aria-label="Switch camera" @click="emit('switchCamera')">
            <SwitchCamera class="size-5" />
          </button>
        </TooltipTrigger>
        <TooltipContent side="bottom">Switch camera</TooltipContent>
      </Tooltip>
    </div>
  </div>
</template>
