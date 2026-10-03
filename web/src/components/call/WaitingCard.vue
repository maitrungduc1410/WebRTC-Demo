<script setup lang="ts">
import { computed } from 'vue'
import { useClipboard } from '@vueuse/core'
import { Check, Copy, Lock } from '@lucide/vue'
import type { Phase } from '@/call/useCall'

const props = defineProps<{
  roomId: string
  phase: Phase
  e2ee: boolean
}>()

const emit = defineEmits<{ copied: [] }>()

const { copy, copied } = useClipboard({ copiedDuring: 1800, legacy: true })
const title = computed(() => props.phase === 'connecting' ? 'Connecting' : 'Waiting for someone to join')

async function copyRoomId() {
  await copy(props.roomId)
  emit('copied')
}
</script>

<template>
  <div class="glass w-full max-w-sm rounded-[1.75rem] p-5 text-white shadow-2xl shadow-black/40">
    <div class="flex items-center gap-2 text-sm text-white/75">
      <span>{{ title }}</span>
      <span class="flex gap-0.5" aria-hidden="true">
        <span v-for="i in 3" :key="i" class="size-1 animate-dot rounded-full bg-current" :style="{ animationDelay: `${i * 0.16}s` }" />
      </span>
    </div>

    <div class="mt-3 flex items-center justify-between gap-3">
      <div>
        <p class="text-xs font-medium tracking-wide text-white/50 uppercase">Room ID</p>
        <p class="font-mono text-3xl font-semibold tracking-[0.18em] tabular-nums">{{ props.roomId }}</p>
      </div>
      <button
        type="button"
        class="relative inline-flex h-11 items-center gap-2 overflow-hidden rounded-full bg-white px-4 text-sm font-semibold text-zinc-900 transition-[scale,background-color] duration-300 ease-bounce hover:bg-white/90 active:scale-95"
        @click="copyRoomId"
      >
        <Transition
          mode="out-in"
          enter-active-class="transition duration-300 ease-bounce"
          leave-active-class="transition duration-100"
          enter-from-class="scale-50 opacity-0"
          leave-to-class="scale-50 opacity-0"
        >
          <Check v-if="copied" class="size-4 text-emerald-600" />
          <Copy v-else class="size-4" />
        </Transition>
        {{ copied ? 'Copied' : 'Copy' }}
      </button>
    </div>

    <p class="mt-3 text-sm text-white/60">
      Open the app on another device and join the same room.
    </p>
    <p v-if="props.e2ee" class="mt-2 flex items-center gap-1.5 text-xs text-emerald-300">
      <Lock class="size-3.5" /> End-to-end encryption is on. The other person must turn it on too.
    </p>
  </div>
</template>
