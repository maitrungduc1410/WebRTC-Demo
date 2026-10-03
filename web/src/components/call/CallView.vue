<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useElementSize, useEventListener } from '@vueuse/core'
import { AnimatePresence, motion } from 'motion-v'
import { Eye, PictureInPicture2 } from '@lucide/vue'
import { useCall } from '@/call/useCall'
import { Button } from '@/components/ui/button'
import { Drawer, DrawerContent, DrawerDescription, DrawerTitle } from '@/components/ui/drawer'
import StageVideo from '@/components/video/StageVideo.vue'
import { usePictureInPicture } from '@/composables/usePictureInPicture'
import { useCoarsePointer, useCompactLayout, useWideLayout } from '@/composables/useInputMode'
import { cn } from '@/lib/utils'
import CallToolbar from './CallToolbar.vue'
import ChatPanel from './ChatPanel.vue'
import LocalTile from './LocalTile.vue'
import MoreDrawer from './MoreDrawer.vue'
import PeerPlaceholder from './PeerPlaceholder.vue'
import PipView from './PipView.vue'
import RecentMessages from './RecentMessages.vue'
import TopBar from './TopBar.vue'
import WaitingCard from './WaitingCard.vue'

const emit = defineEmits<{ copied: [] }>()

const CONTROLS_AUTO_HIDE_MS = 4000

const call = useCall()
const wide = useWideLayout()
const compact = useCompactLayout()
const coarse = useCoarsePointer()

const stage = ref<HTMLElement>()
const stageVideo = ref<InstanceType<typeof StageVideo>>()
const localTile = ref<InstanceType<typeof LocalTile>>()
const fileInput = ref<HTMLInputElement>()
const remoteAudio = ref<HTMLAudioElement>()
const { width: stageWidth, height: stageHeight } = useElementSize(stage)

const hasRemote = computed(() => call.phase.value === 'connected')

// ---- Chat and sheets ------------------------------------------------------------------------

const chatOpen = ref(false)
const moreOpen = ref(false)
const unread = ref(0)

watch(() => call.messages.value.length, (length, previous) => {
  const last = call.messages.value[length - 1]
  if (length > previous && last && !last.isLocal && !chatOpen.value) unread.value++
  if (length === 0) unread.value = 0
})
watch(chatOpen, open => {
  if (!open) return
  unread.value = 0
  call.ensureChat()
})

// ---- Controls -------------------------------------------------------------------------------

const controlsVisible = ref(true)
const showChrome = computed(() => controlsVisible.value || !hasRemote.value)
let hideTimer: ReturnType<typeof setTimeout> | null = null

function menuOpen() {
  return !!document.querySelector('[data-slot=dropdown-menu-content]')
}

function scheduleHide() {
  if (hideTimer) clearTimeout(hideTimer)
  hideTimer = null
  if (!hasRemote.value) return
  hideTimer = setTimeout(() => {
    if (menuOpen() || moreOpen.value || (chatOpen.value && !wide.value)) return scheduleHide()
    controlsVisible.value = false
  }, CONTROLS_AUTO_HIDE_MS)
}

function wake() {
  controlsVisible.value = true
  scheduleHide()
}

watch(hasRemote, connected => {
  controlsVisible.value = true
  if (connected) scheduleHide()
})
onBeforeUnmount(() => {
  if (hideTimer) clearTimeout(hideTimer)
  if (tapTimer) clearTimeout(tapTimer)
})
// The More drawer only exists in the compact layout; left open it would keep the controls up.
watch(compact, isCompact => { if (!isCompact) moreOpen.value = false })

function onPointerMove(event: PointerEvent) {
  if (event.pointerType === 'mouse') wake()
}

// One tap shows or hides the controls; two taps switch between fit and fill.
let tapTimer: ReturnType<typeof setTimeout> | null = null
function onStageTap() {
  if (tapTimer) {
    clearTimeout(tapTimer)
    tapTimer = null
    if (hasRemote.value) toggleFit()
    return
  }
  tapTimer = setTimeout(() => {
    tapTimer = null
    if (controlsVisible.value && hasRemote.value) controlsVisible.value = false
    else wake()
  }, 250)
}

// ---- Remote video fit -----------------------------------------------------------------------

const remoteFrame = ref({ width: 0, height: 0 })
watch(() => call.remoteStream.value, () => { remoteFrame.value = { width: 0, height: 0 } })
// Screen shares are letterboxed so nothing is cut off. So is a peer held the other way round
// from this window: filling a landscape window with a portrait camera crops most of it away.
const crossOrientation = computed(() => {
  const { width, height } = remoteFrame.value
  return width > 0 && height > 0 && (width > height) !== (stageWidth.value > stageHeight.value)
})
const defaultFit = computed(() => call.remoteMedia.value.screen || crossOrientation.value)
const userFit = ref<boolean | null>(null)
watch(defaultFit, () => { userFit.value = null })
const fit = computed(() => userFit.value ?? defaultFit.value)
function toggleFit() {
  userFit.value = !fit.value
}

// ---- Picture-in-picture ---------------------------------------------------------------------

const pip = usePictureInPicture({
  enabled: hasRemote,
  videoElement: () => stageVideo.value?.video,
  size: () => {
    const { width, height } = remoteFrame.value
    const ratio = width && height ? Math.min(Math.max(width / height, 0.5), 2) : 16 / 9
    return ratio >= 1
      ? { width: 480, height: Math.round(480 / ratio) }
      : { width: Math.round(420 * ratio), height: 420 }
  },
})
const pipWindow = pip.pipWindow

// ---- Actions --------------------------------------------------------------------------------

async function switchCamera() {
  const tile = localTile.value
  if (tile && call.cameraOn.value && call.sharing.value === 'none') await tile.flip(call.switchCamera)
  else await call.switchCamera()
}

function pickFile() {
  fileInput.value?.click()
}

function onFilePicked(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (file) call.shareFile(file)
}

function hangUp() {
  pip.close()
  call.leave()
}

watch(() => call.remoteStream.value, stream => {
  const el = remoteAudio.value
  if (!el || el.srcObject === stream) return
  el.srcObject = stream
  if (stream) el.play().catch(() => {})
}, { flush: 'post' })

useEventListener(window, 'keydown', (event: KeyboardEvent) => {
  if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey || event.repeat) return
  const target = event.target as HTMLElement | null
  // Dialogs, menus and drawers handle their own keys (Escape included).
  if (target?.closest('[role=dialog], [role=menu]')) return
  const key = event.key.toLowerCase()
  if (key === 'escape') {
    if (chatOpen.value && wide.value) chatOpen.value = false
    return
  }
  if (target?.closest('input, textarea, select, [contenteditable=true]')) return
  const actions: Record<string, () => unknown> = {
    m: call.toggleMic,
    v: call.toggleCamera,
    c: () => { chatOpen.value = !chatOpen.value },
    b: call.toggleBackground,
    f: () => hasRemote.value && toggleFit(),
    p: () => hasRemote.value && pip.supported && pip.toggle(),
  }
  const action = actions[key]
  if (!action) return
  event.preventDefault()
  wake()
  action()
})
</script>

<template>
  <div class="fixed inset-0 flex overflow-hidden bg-black text-white">
    <main
      ref="stage"
      :class="cn('relative min-w-0 flex-1 overflow-hidden', !showChrome && !coarse && 'cursor-none')"
      @pointermove="onPointerMove"
    >
      <div class="absolute inset-0" @click="onStageTap" />

      <Transition
        enter-active-class="transition duration-700 ease-out-expo"
        leave-active-class="transition duration-300"
        enter-from-class="opacity-0 scale-105"
        leave-to-class="opacity-0"
      >
        <div v-if="hasRemote" class="pointer-events-none absolute inset-0">
          <StageVideo
            ref="stageVideo"
            :stream="call.remoteStream.value"
            :fit="fit"
            @size="(w, h) => (remoteFrame = { width: w, height: h })"
          />
          <Transition
            enter-active-class="transition-opacity duration-500"
            leave-active-class="transition-opacity duration-500"
            enter-from-class="opacity-0"
            leave-to-class="opacity-0"
          >
            <PeerPlaceholder
              v-if="call.showRemotePlaceholder.value"
              :snapshot="call.remoteSnapshot.value"
              :audio-level="call.remoteAudioLevel.value"
              :title="call.remoteVideoHidden.value ? 'You hid their video' : 'Camera is off'"
              :subtitle="call.remoteMedia.value.audio ? undefined : 'Microphone muted'"
            >
              <Button
                v-if="call.remoteVideoHidden.value"
                variant="secondary"
                class="pointer-events-auto mt-1 rounded-full bg-white/15 text-white backdrop-blur-md hover:bg-white/25"
                @click="call.toggleRemoteVideo"
              >
                <Eye /> Show video
              </Button>
            </PeerPlaceholder>
          </Transition>
        </div>
      </Transition>

      <LocalTile
        ref="localTile"
        :stream="call.localStream.value"
        :mirror="call.mirrorLocal.value"
        :pip="hasRemote"
        :stage-width="stageWidth"
        :stage-height="stageHeight"
        :controls-visible="showChrome"
        :compact="compact"
        :mic-on="call.micOn.value"
        :camera-on="call.cameraOn.value"
        :sharing="call.sharing.value"
        @tap="wake"
        @double-tap="switchCamera"
      />

      <div
        :class="cn('pointer-events-none absolute inset-x-0 top-0 h-40 bg-gradient-to-b from-black/60 to-transparent transition-opacity duration-500',
                   showChrome ? 'opacity-100' : 'opacity-0')"
      />
      <div
        :class="cn('pointer-events-none absolute inset-x-0 bottom-0 h-56 bg-gradient-to-t from-black/70 to-transparent transition-opacity duration-500',
                   showChrome ? 'opacity-100' : 'opacity-0')"
      />

      <div
        class="pointer-events-none absolute inset-0 z-20 flex flex-col justify-between
               pt-[max(env(safe-area-inset-top),0.75rem)] pr-[max(env(safe-area-inset-right),0.75rem)]
               pb-[max(env(safe-area-inset-bottom),0.75rem)] pl-[max(env(safe-area-inset-left),0.75rem)]
               sm:pt-[max(env(safe-area-inset-top),1.25rem)] sm:pr-[max(env(safe-area-inset-right),1.25rem)]
               sm:pb-[max(env(safe-area-inset-bottom),1.25rem)] sm:pl-[max(env(safe-area-inset-left),1.25rem)]"
      >
        <Transition
          enter-active-class="transition duration-500 ease-spring"
          leave-active-class="transition duration-300 ease-out"
          enter-from-class="opacity-0 -translate-y-4"
          leave-to-class="opacity-0 -translate-y-4"
        >
          <TopBar
            v-if="showChrome"
            class="pointer-events-none *:pointer-events-auto"
            :room-id="call.roomId.value"
            :e2ee="call.e2ee.value"
            :phase="call.phase.value"
            :remote-media="call.remoteMedia.value"
            :remote-audio-muted="call.remoteAudioMuted.value"
            :compact="compact"
            :show-pip="pip.supported && hasRemote"
            :pip-active="pip.active.value"
            :show-switch-camera="call.cameraCount.value > 1 && call.sharing.value === 'none'"
            @toggle-pip="pip.toggle"
            @switch-camera="switchCamera"
          />
        </Transition>

        <div class="flex flex-col items-center gap-3">
          <div class="flex w-full justify-start">
            <RecentMessages
              class="pointer-events-auto"
              :messages="call.messages.value"
              :visible="!chatOpen && !pipWindow"
              @open="chatOpen = true"
            />
          </div>

          <Transition
            enter-active-class="transition duration-600 ease-spring"
            leave-active-class="transition duration-300 ease-out"
            enter-from-class="opacity-0 translate-y-6 scale-95"
            leave-to-class="opacity-0 translate-y-4 scale-95"
          >
            <WaitingCard
              v-if="!hasRemote"
              class="pointer-events-auto"
              :room-id="call.roomId.value"
              :phase="call.phase.value"
              :e2ee="call.e2ee.value"
              @copied="emit('copied')"
            />
          </Transition>

          <Transition
            enter-active-class="transition duration-500 ease-spring"
            leave-active-class="transition duration-300 ease-out"
            enter-from-class="opacity-0 translate-y-6"
            leave-to-class="opacity-0 translate-y-6"
          >
            <CallToolbar
              v-if="showChrome"
              class="pointer-events-auto"
              :call="call"
              :compact="compact"
              :chat-open="chatOpen"
              :unread="unread"
              :fit="fit"
              :pip-supported="pip.supported"
              :pip-active="pip.active.value"
              @toggle-chat="chatOpen = !chatOpen"
              @open-more="moreOpen = true"
              @pick-file="pickFile"
              @toggle-fit="toggleFit"
              @toggle-pip="pip.toggle"
              @switch-camera="switchCamera"
              @hang-up="hangUp"
            />
          </Transition>
        </div>
      </div>

      <Transition
        enter-active-class="transition duration-500 ease-out-expo"
        leave-active-class="transition duration-300"
        enter-from-class="opacity-0"
        leave-to-class="opacity-0"
      >
        <div
          v-if="pipWindow"
          class="absolute inset-0 z-30 flex flex-col items-center justify-center gap-5 bg-zinc-950/80 px-6 text-center backdrop-blur-2xl"
        >
          <div class="flex size-20 items-center justify-center rounded-3xl bg-indigo-500/20 text-indigo-300 ring-1 ring-indigo-400/30">
            <PictureInPicture2 class="size-10 animate-breathe" />
          </div>
          <div class="space-y-1">
            <p class="text-lg font-semibold">Your call is in a floating window</p>
            <p class="text-sm text-white/60">It stays on top while you use other tabs and apps.</p>
          </div>
          <Button size="lg" class="rounded-full px-6" @click="pip.close">Bring it back here</Button>
        </div>
      </Transition>
    </main>

    <AnimatePresence>
      <motion.aside
        v-if="wide && chatOpen"
        key="chat"
        class="shrink-0 overflow-hidden"
        :initial="{ width: 0, opacity: 0 }"
        :animate="{ width: 400, opacity: 1 }"
        :exit="{ width: 0, opacity: 0 }"
        :transition="{ type: 'spring', stiffness: 280, damping: 34 }"
      >
        <div class="h-full w-[400px] p-3">
          <div class="h-full overflow-hidden rounded-[1.75rem] border border-white/10 bg-zinc-900">
            <ChatPanel
              closable
              autofocus
              :messages="call.messages.value"
              :ready="call.dataChannelReady.value"
              :connected="hasRemote"
              @send="call.sendMessage"
              @close="chatOpen = false"
            />
          </div>
        </div>
      </motion.aside>
    </AnimatePresence>

    <Drawer v-if="!wide" v-model:open="chatOpen">
      <DrawerContent class="h-[80dvh] p-2">
        <DrawerTitle class="sr-only">Chat</DrawerTitle>
        <DrawerDescription class="sr-only">Messages with the other person in this call</DrawerDescription>
        <ChatPanel
          :messages="call.messages.value"
          :ready="call.dataChannelReady.value"
          :connected="hasRemote"
          @send="call.sendMessage"
        />
      </DrawerContent>
    </Drawer>

    <MoreDrawer
      v-if="compact"
      v-model:open="moreOpen"
      :call="call"
      :fit="fit"
      :pip-supported="pip.supported"
      :pip-active="pip.active.value"
      @toggle-fit="toggleFit"
      @toggle-pip="pip.toggle"
      @switch-camera="switchCamera"
    />

    <input ref="fileInput" type="file" accept="video/*" class="hidden" @change="onFilePicked">
    <audio ref="remoteAudio" autoplay />

    <Teleport v-if="pipWindow" :to="pipWindow.document.body">
      <PipView :call="call" :fit="fit" @hang-up="hangUp" />
    </Teleport>
  </div>
</template>
