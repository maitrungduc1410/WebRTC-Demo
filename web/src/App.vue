<script setup lang="ts">
import { computed, onBeforeUnmount, watchEffect } from 'vue'
import { usePreferredDark } from '@vueuse/core'
import { AnimatePresence, motion } from 'motion-v'
import { toast } from 'vue-sonner'
import type { CallEvent } from '@/call/types'
import { useCall } from '@/call/useCall'
import { useGroupCall } from '@/call/useGroupCall'
import CallView from '@/components/call/CallView.vue'
import GroupCallView from '@/components/call/GroupCallView.vue'
import LobbyView from '@/components/lobby/LobbyView.vue'
import { Toaster } from '@/components/ui/sonner'
import { TooltipProvider } from '@/components/ui/tooltip'

const call = useCall()
const group = useGroupCall()
const inCall = computed(() => call.inRoom.value || group.inRoom.value)
const preferredDark = usePreferredDark()
// The lobby follows the system; the call is always dark, like the native apps.
const dark = computed(() => inCall.value || preferredDark.value)
watchEffect(() => {
  document.documentElement.classList.toggle('dark', dark.value)
  document.querySelector('meta[name=theme-color]')?.setAttribute('content', dark.value ? '#09090b' : '#ffffff')
})

function onCallEvent(event: CallEvent) {
  switch (event.type) {
    case 'peer-joined':
      toast.success(event.name ? `${event.name} joined` : 'They joined the call')
      break
    case 'peer-left':
      if (event.name) toast(`${event.name} left`)
      else toast('They left the call', { description: 'You can wait here until they come back.' })
      break
    case 'error':
      toast.error(event.message)
      break
    case 'info':
      toast(event.message)
      break
  }
}
const stopEvents = call.onEvent(onCallEvent)
const stopGroupEvents = group.onEvent(onCallEvent)
onBeforeUnmount(() => {
  stopEvents()
  stopGroupEvents()
})
</script>

<template>
  <TooltipProvider :delay-duration="250">
    <AnimatePresence mode="wait">
      <motion.div
        v-if="!inCall"
        key="lobby"
        :exit="{ opacity: 0, scale: 1.04, filter: 'blur(12px)' }"
        :transition="{ duration: 0.3, ease: [0.4, 0, 1, 1] }"
      >
        <LobbyView />
      </motion.div>
      <motion.div
        v-else-if="group.inRoom.value"
        key="group-call"
        :initial="{ opacity: 0 }"
        :animate="{ opacity: 1 }"
        :exit="{ opacity: 0 }"
        :transition="{ duration: 0.4 }"
      >
        <GroupCallView @copied="toast.success('Room ID copied')" />
      </motion.div>
      <motion.div
        v-else
        key="call"
        :initial="{ opacity: 0 }"
        :animate="{ opacity: 1 }"
        :exit="{ opacity: 0 }"
        :transition="{ duration: 0.4 }"
      >
        <CallView @copied="toast.success('Room ID copied')" />
      </motion.div>
    </AnimatePresence>
    <Toaster position="top-center" :theme="dark ? 'dark' : 'light'" :offset="{ top: 'max(env(safe-area-inset-top), 16px)' }" />
  </TooltipProvider>
</template>
