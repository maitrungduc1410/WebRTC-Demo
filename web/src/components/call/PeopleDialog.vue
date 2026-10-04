<script setup lang="ts">
import { computed } from 'vue'
import { MicOff, MonitorUp, VideoOff } from '@lucide/vue'
import type { MediaState } from '@/call/types'
import type { GroupCall } from '@/call/useGroupCall'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { cn } from '@/lib/utils'

const open = defineModel<boolean>('open', { required: true })

const props = defineProps<{ call: GroupCall }>()

interface Person { id: string; label: string; state: MediaState; you: boolean }

/** You first, then everyone else in join order. */
const people = computed<Person[]>(() => {
  const { call } = props
  const presenting = call.sharing.value !== 'none'
  const you: Person = {
    id: call.participantId.value,
    label: call.selfLabel.value,
    state: { audio: call.micOn.value, video: presenting || call.cameraOn.value, screen: presenting },
    you: true,
  }
  return [you, ...call.participants.value.map(p => ({ id: p.id, label: p.label, state: p.state, you: false }))]
})
</script>

<template>
  <Dialog v-model:open="open">
    <DialogContent class="dark gap-4 border-white/10 sm:max-w-sm">
      <DialogHeader class="pr-10">
        <DialogTitle>{{ people.length }} in call</DialogTitle>
        <DialogDescription>Others see you by the name on your row.</DialogDescription>
      </DialogHeader>
      <ul class="scrollbar-thin -mx-2 flex max-h-[min(60dvh,28rem)] flex-col gap-1 overflow-y-auto px-2">
        <li
          v-for="person in people"
          :key="person.id"
          :class="cn(
            'flex items-center gap-3 rounded-2xl px-3 py-2.5',
            person.you ? 'bg-indigo-500/15 ring-1 ring-indigo-400/40' : 'hover:bg-white/5',
          )"
        >
          <span
            :class="cn(
              'relative flex size-9 shrink-0 items-center justify-center rounded-full text-sm font-semibold text-white',
              person.you ? 'bg-gradient-to-br from-indigo-500 to-violet-500' : 'bg-zinc-700',
            )"
          >
            {{ person.label.charAt(0) }}
            <span
              :class="cn(
                'absolute inset-0 rounded-full ring-2 ring-emerald-400 transition-opacity duration-300',
                !person.you && props.call.activeSpeakerId.value === person.id ? 'opacity-100' : 'opacity-0',
              )"
            />
          </span>
          <span class="min-w-0 flex-1 truncate text-sm font-medium text-white">{{ person.label }}</span>
          <span v-if="person.you" class="shrink-0 rounded-full bg-indigo-500 px-2 py-0.5 text-[11px] font-semibold text-white">You</span>
          <span class="flex shrink-0 items-center gap-1.5 text-white/60">
            <MonitorUp v-if="person.state.screen" class="size-4" aria-label="Presenting" />
            <VideoOff v-else-if="!person.state.video" class="size-4" aria-label="Camera off" />
            <MicOff v-if="!person.state.audio" class="size-4 text-red-400" aria-label="Microphone muted" />
          </span>
        </li>
      </ul>
    </DialogContent>
  </Dialog>
</template>
