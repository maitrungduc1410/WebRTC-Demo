<script setup lang="ts">
import { computed, onBeforeUnmount, watchEffect } from 'vue'
import { usePreferredDark } from '@vueuse/core'
import { AnimatePresence, motion } from 'motion-v'
import { toast } from 'vue-sonner'
import { useCall } from '@/call/useCall'
import CallView from '@/components/call/CallView.vue'
import LobbyView from '@/components/lobby/LobbyView.vue'
import { Toaster } from '@/components/ui/sonner'
import { TooltipProvider } from '@/components/ui/tooltip'

const call = useCall()
const preferredDark = usePreferredDark()
// The lobby follows the system; the call is always dark, like the native apps.
const dark = computed(() => call.inRoom.value || preferredDark.value)
watchEffect(() => {
  document.documentElement.classList.toggle('dark', dark.value)
  document.querySelector('meta[name=theme-color]')?.setAttribute('content', dark.value ? '#09090b' : '#ffffff')
})

const stopEvents = call.onEvent(event => {
  switch (event.type) {
    case 'peer-joined':
      toast.success('They joined the call')
      break
    case 'peer-left':
      toast('They left the call', { description: 'You can wait here until they come back.' })
      break
    case 'error':
      toast.error(event.message)
      break
    case 'info':
      toast(event.message)
      break
  }
})
onBeforeUnmount(stopEvents)
</script>

<template>
  <TooltipProvider :delay-duration="250">
    <AnimatePresence mode="wait">
      <motion.div
        v-if="!call.inRoom.value"
        key="lobby"
        :exit="{ opacity: 0, scale: 1.04, filter: 'blur(12px)' }"
        :transition="{ duration: 0.3, ease: [0.4, 0, 1, 1] }"
      >
        <LobbyView />
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
