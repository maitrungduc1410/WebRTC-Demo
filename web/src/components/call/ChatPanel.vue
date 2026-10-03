<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { MessagesSquare, SendHorizontal, X } from '@lucide/vue'
import type { ChatMessage } from '@/call/useCall'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'

const props = defineProps<{
  messages: ChatMessage[]
  ready: boolean
  connected: boolean
  closable?: boolean
  autofocus?: boolean
}>()

const emit = defineEmits<{
  send: [text: string]
  close: []
}>()

const draft = ref('')
const list = ref<HTMLElement>()
const input = ref<InstanceType<typeof Input>>()

const hint = computed(() => {
  if (!props.connected) return 'Chat opens once the other person joins.'
  if (!props.ready) return 'Connecting the chat…'
  return 'Messages go straight to the other person and disappear when the call ends.'
})

function send() {
  if (!props.ready || !draft.value.trim()) return
  emit('send', draft.value)
  draft.value = ''
}

function scrollToBottom(smooth = true) {
  nextTick(() => list.value?.scrollTo({ top: list.value.scrollHeight, behavior: smooth ? 'smooth' : 'auto' }))
}

watch(() => props.messages.length, () => scrollToBottom())
onMounted(() => {
  scrollToBottom(false)
  if (props.autofocus && props.ready) {
    const el = (input.value as { $el?: HTMLElement } | undefined)?.$el
    ;(el instanceof HTMLInputElement ? el : el?.querySelector('input'))?.focus({ preventScroll: true })
  }
})

function time(timestamp: number) {
  return new Date(timestamp).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}

/** Consecutive messages from the same side share one timestamp. */
function startsGroup(index: number) {
  const previous = props.messages[index - 1]
  const message = props.messages[index]!
  return !previous || previous.isLocal !== message.isLocal || message.timestamp - previous.timestamp > 60_000
}
</script>

<template>
  <div class="flex h-full min-h-0 flex-col text-foreground">
    <div class="flex items-center justify-between gap-2 px-5 pt-4 pb-3">
      <div>
        <h2 class="text-base font-semibold">In-call messages</h2>
        <p class="text-xs text-muted-foreground">Peer-to-peer over a WebRTC data channel</p>
      </div>
      <Button v-if="props.closable" variant="ghost" size="icon" aria-label="Close chat" @click="emit('close')">
        <X />
      </Button>
    </div>

    <div ref="list" class="scrollbar-thin min-h-0 flex-1 overflow-y-auto px-4 pb-2">
      <Transition
        enter-active-class="transition duration-500 ease-out-expo"
        enter-from-class="opacity-0 translate-y-2"
      >
        <div v-if="!props.messages.length" class="flex h-full flex-col items-center justify-center gap-3 px-6 py-10 text-center">
          <div class="flex size-14 items-center justify-center rounded-2xl bg-primary/15 text-primary">
            <MessagesSquare class="size-7" />
          </div>
          <p class="font-medium">No messages yet</p>
          <p class="text-sm text-muted-foreground">{{ hint }}</p>
        </div>
      </Transition>

      <TransitionGroup
        tag="ol"
        class="flex flex-col gap-1"
        enter-active-class="transition duration-500 ease-bounce"
        enter-from-class="opacity-0 translate-y-3 scale-95"
      >
        <li
          v-for="(message, index) in props.messages"
          :key="message.id"
          :class="cn('flex flex-col', message.isLocal ? 'items-end' : 'items-start', startsGroup(index) && index > 0 && 'mt-3')"
          :style="{ transformOrigin: message.isLocal ? 'bottom right' : 'bottom left' }"
        >
          <span v-if="startsGroup(index)" class="mb-1 px-1 text-[11px] text-muted-foreground">
            {{ message.isLocal ? 'You' : 'Them' }} · {{ time(message.timestamp) }}
          </span>
          <p
            :class="cn(
              'max-w-[85%] rounded-[1.25rem] px-3.5 py-2 text-sm break-words whitespace-pre-wrap',
              message.isLocal ? 'rounded-br-md bg-primary text-primary-foreground' : 'rounded-bl-md bg-muted',
            )"
          >
            {{ message.text }}
          </p>
        </li>
      </TransitionGroup>
    </div>

    <form class="flex items-center gap-2 border-t px-4 pt-3 pb-[max(env(safe-area-inset-bottom),0.75rem)]" @submit.prevent="send">
      <Input
        ref="input"
        v-model="draft"
        :disabled="!props.ready"
        :placeholder="props.ready ? 'Send a message' : hint"
        class="h-11 flex-1 rounded-full px-4 text-base sm:text-sm"
        enterkeyhint="send"
        autocomplete="off"
      />
      <Button
        type="submit"
        size="icon-lg"
        class="size-11 rounded-full transition-[scale,opacity] duration-300 ease-bounce active:scale-90"
        :disabled="!props.ready || !draft.trim()"
        aria-label="Send"
      >
        <SendHorizontal />
      </Button>
    </form>
  </div>
</template>
