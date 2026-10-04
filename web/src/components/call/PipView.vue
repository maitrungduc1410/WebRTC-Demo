<script setup lang="ts">
import { computed, ref } from 'vue'
import { Mic, MicOff, MonitorUp, PhoneOff, Video, VideoOff } from '@lucide/vue'
import type { PipControls } from '@/call/types'
import StreamVideo from '@/components/video/StreamVideo.vue'
import PeerPlaceholder from './PeerPlaceholder.vue'
import { cn } from '@/lib/utils'

// Rendered into the Document Picture-in-Picture window. Only CSS animates here: the opener tab
// is hidden while this window shows, and its requestAnimationFrame (which Motion uses) is paused.

const props = defineProps<{
  call: PipControls
  fit: boolean
}>()

const emit = defineEmits<{ hangUp: [] }>()

const call = props.call
const hasRemote = computed(() => call.phase.value === 'connected' && !!call.remoteStream.value)
const localFrame = ref({ width: 4, height: 3 })
const localAspect = computed(() => Math.min(Math.max(localFrame.value.width / localFrame.value.height, 0.5), 2))

const button = 'inline-flex size-10 items-center justify-center rounded-full transition-[scale,background-color] duration-300 ease-bounce hover:scale-110 active:scale-90 disabled:pointer-events-none disabled:opacity-40'
</script>

<template>
  <div class="group relative h-dvh w-screen overflow-hidden bg-black font-sans text-white select-none">
    <template v-if="hasRemote">
      <StreamVideo
        :stream="call.remoteStream.value"
        :class="cn('absolute inset-0 size-full', props.fit ? 'object-contain' : 'object-cover')"
      />
      <Transition
        enter-active-class="transition-opacity duration-500"
        leave-active-class="transition-opacity duration-500"
        enter-from-class="opacity-0"
        leave-to-class="opacity-0"
      >
        <PeerPlaceholder
          v-if="call.showRemotePlaceholder.value"
          size="md"
          :snapshot="call.remoteSnapshot.value"
          :audio-level="call.remoteAudioLevel.value"
          :title="call.remoteVideoHidden.value ? 'You hid their video' : 'Camera is off'"
          :subtitle="call.remoteMedia.value.audio ? undefined : 'Microphone muted'"
        />
      </Transition>
    </template>
    <template v-else>
      <StreamVideo
        :stream="call.localStream.value"
        :mirror="call.mirrorLocal.value"
        class="absolute inset-0 size-full object-cover opacity-60"
      />
      <div class="absolute inset-0 flex items-center justify-center bg-black/40 text-sm font-medium">
        {{ call.phase.value === 'connecting' ? 'Connecting…' : 'Waiting for someone to join' }}
      </div>
    </template>

    <!-- Status -->
    <div class="pointer-events-none absolute top-2 left-2 flex gap-1.5">
      <span
        v-if="hasRemote && !call.remoteMedia.value.audio"
        class="inline-flex h-7 animate-in items-center gap-1 rounded-full bg-black/55 px-2.5 text-xs font-medium backdrop-blur-md zoom-in-75 fade-in-0 duration-300"
      >
        <MicOff class="size-3.5" /> Muted
      </span>
      <span
        v-if="hasRemote && call.remoteMedia.value.screen"
        class="inline-flex h-7 animate-in items-center gap-1 rounded-full bg-black/55 px-2.5 text-xs font-medium backdrop-blur-md zoom-in-75 fade-in-0 duration-300"
      >
        <MonitorUp class="size-3.5" /> Presenting
      </span>
    </div>

    <!-- Your camera -->
    <div
      v-if="hasRemote"
      class="absolute right-2 bottom-2 w-[30%] max-w-44 min-w-20 overflow-hidden rounded-xl bg-zinc-900 shadow-xl ring-1 ring-white/20 transition-[translate] duration-500 ease-spring group-hover:-translate-y-14"
      :style="{ aspectRatio: String(localAspect) }"
    >
      <StreamVideo
        v-if="call.sharing.value !== 'screen'"
        :stream="call.localStream.value"
        :mirror="call.mirrorLocal.value"
        class="size-full object-cover"
        @size="(w, h) => (localFrame = { width: w, height: h })"
      />
      <div v-if="call.sharing.value === 'screen'" class="absolute inset-0 flex items-center justify-center bg-indigo-950">
        <MonitorUp class="size-5" />
      </div>
      <PeerPlaceholder v-else-if="!call.cameraOn.value && call.sharing.value === 'none'" size="sm" />
    </div>

    <!-- Controls -->
    <div
      class="absolute inset-x-0 bottom-0 flex translate-y-3 justify-center gap-2 bg-gradient-to-t from-black/70 to-transparent pt-8 pb-3 opacity-0 transition duration-300 ease-out-expo group-hover:translate-y-0 group-hover:opacity-100 focus-within:translate-y-0 focus-within:opacity-100"
    >
      <button
        type="button"
        :class="cn(button, call.micOn.value ? 'bg-white/20 hover:bg-white/30' : 'bg-red-500')"
        :aria-label="call.micOn.value ? 'Turn off microphone' : 'Turn on microphone'"
        @click="call.toggleMic"
      >
        <Mic v-if="call.micOn.value" class="size-[18px]" /><MicOff v-else class="size-[18px]" />
      </button>
      <button
        type="button"
        :class="cn(button, call.cameraOn.value ? 'bg-white/20 hover:bg-white/30' : 'bg-red-500')"
        :aria-label="call.cameraOn.value ? 'Turn off camera' : 'Turn on camera'"
        :disabled="call.sharing.value !== 'none'"
        @click="call.toggleCamera"
      >
        <Video v-if="call.cameraOn.value" class="size-[18px]" /><VideoOff v-else class="size-[18px]" />
      </button>
      <button type="button" :class="cn(button, 'w-14 bg-red-600 hover:bg-red-500')" aria-label="Leave call" @click="emit('hangUp')">
        <PhoneOff class="size-[18px]" />
      </button>
    </div>
  </div>
</template>
