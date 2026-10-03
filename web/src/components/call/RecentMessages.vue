<script setup lang="ts">
import { computed, ref } from 'vue'
import { useIntervalFn } from '@vueuse/core'
import type { ChatMessage } from '@/call/useCall'

const props = defineProps<{
  messages: ChatMessage[]
  visible: boolean
}>()

const emit = defineEmits<{ open: [] }>()

const MAX_RECENT_MESSAGES = 3
const BUBBLE_LIFETIME_MS = 6000

const now = ref(Date.now())
useIntervalFn(() => { now.value = Date.now() }, 500)
const shown = computed(() => props.visible
  ? props.messages.slice(-MAX_RECENT_MESSAGES).filter(m => now.value - m.timestamp < BUBBLE_LIFETIME_MS)
  : [])
</script>

<template>
  <TransitionGroup
    tag="div"
    class="relative flex w-72 max-w-[calc(100vw-2rem)] flex-col items-start gap-2"
    enter-active-class="transition duration-500 ease-bounce"
    leave-active-class="transition duration-300 ease-out absolute! bottom-0"
    enter-from-class="opacity-0 translate-y-4 scale-90"
    leave-to-class="opacity-0 scale-90"
    move-class="transition-transform duration-500 ease-spring"
  >
    <button
      v-for="message in shown"
      :key="message.id"
      type="button"
      class="glass max-w-full origin-bottom-left rounded-2xl rounded-bl-md px-3.5 py-2 text-left text-sm text-white shadow-lg shadow-black/30 transition-[background-color] hover:bg-white/15"
      @click="emit('open')"
    >
      <span class="block text-[11px] font-semibold text-white/55">{{ message.isLocal ? 'You' : 'Them' }}</span>
      <span class="line-clamp-3 break-words">{{ message.text }}</span>
    </button>
  </TransitionGroup>
</template>
