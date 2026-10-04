<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useElementSize, useEventListener } from '@vueuse/core'
import { AnimatePresence, motion } from 'motion-v'
import { PictureInPicture2 } from '@lucide/vue'
import { DEFAULT_REMOTE_MEDIA, type PipControls } from '@/call/types'
import { useGroupCall } from '@/call/useGroupCall'
import { Button } from '@/components/ui/button'
import { Drawer, DrawerContent, DrawerDescription, DrawerTitle } from '@/components/ui/drawer'
import StreamAudio from '@/components/video/StreamAudio.vue'
import { usePictureInPicture } from '@/composables/usePictureInPicture'
import { useCompactLayout, useWideLayout } from '@/composables/useInputMode'
import { useSafeArea } from '@/composables/useSafeArea'
import CallToolbar from './CallToolbar.vue'
import ChatPanel from './ChatPanel.vue'
import EffectsPanel from './EffectsPanel.vue'
import GroupTile from './GroupTile.vue'
import LocalTile from './LocalTile.vue'
import MoreDrawer from './MoreDrawer.vue'
import PeopleDialog from './PeopleDialog.vue'
import PipView from './PipView.vue'
import RecentMessages from './RecentMessages.vue'
import TopBar from './TopBar.vue'
import WaitingCard from './WaitingCard.vue'

// The group screen keeps its controls up: the tiles are laid out between them, not under them.

const emit = defineEmits<{ copied: [] }>()

const call = useGroupCall()
const wide = useWideLayout()
const compact = useCompactLayout()
const safe = useSafeArea()

const stage = ref<HTMLElement>()
const localTile = ref<InstanceType<typeof LocalTile>>()
const fileInput = ref<HTMLInputElement>()
/** For video picture-in-picture, which floats the featured tile's <video>. */
const tiles: Record<string, InstanceType<typeof GroupTile> | null> = {}
const { width: stageWidth, height: stageHeight } = useElementSize(stage)

const hasRemote = computed(() => call.phase.value === 'connected')
const participants = computed(() => call.participants.value)

// ---- Grid -----------------------------------------------------------------------------------

const TILE_ASPECT = 4 / 3

/** Same clearances as the local tile: below the top bar, above the toolbar. */
const gridInsets = computed(() => {
  const margin = compact.value ? 12 : 20
  return {
    top: safe.top + (compact.value ? 72 : 84),
    bottom: safe.bottom + (compact.value ? 100 : 112),
    left: safe.left + margin,
    right: safe.right + margin,
  }
})
const gap = computed(() => (compact.value ? 8 : 12))

/** The column count that gives the largest tiles of roughly camera shape. */
const grid = computed(() => {
  const count = Math.max(participants.value.length, 1)
  const { top, bottom, left, right } = gridInsets.value
  const width = Math.max(stageWidth.value - left - right, 1)
  const height = Math.max(stageHeight.value - top - bottom, 1)
  let best = { columns: 1, rows: count, score: -1 }
  for (let columns = 1; columns <= count; columns++) {
    const rows = Math.ceil(count / columns)
    const cellWidth = (width - gap.value * (columns - 1)) / columns
    const cellHeight = (height - gap.value * (rows - 1)) / rows
    const score = Math.min(cellWidth, cellHeight * TILE_ASPECT)
    if (score > best.score) best = { columns, rows, score }
  }
  return best
})
const tileStyle = computed(() => {
  const { columns, rows } = grid.value
  const g = gap.value
  return {
    width: `calc((100% - ${g * (columns - 1)}px) / ${columns})`,
    height: `calc((100% - ${g * (rows - 1)}px) / ${rows})`,
  }
})

// ---- Chat and sheets ------------------------------------------------------------------------

const chatOpen = ref(false)
const moreOpen = ref(false)
const effectsOpen = ref(false)
const peopleOpen = ref(false)
const unread = ref(0)

watch(() => call.messages.value.length, (length, previous) => {
  const last = call.messages.value[length - 1]
  if (length > previous && last && !last.isLocal && !chatOpen.value) unread.value++
  if (length === 0) unread.value = 0
})
watch(chatOpen, open => { if (open) unread.value = 0 })
watch(compact, isCompact => { if (!isCompact) moreOpen.value = false })
// Effects only apply to the camera.
watch(() => call.sharing.value, sharing => { if (sharing !== 'none') effectsOpen.value = false })

// ---- Picture-in-picture ---------------------------------------------------------------------

/** Who the PiP window shows: the active speaker, else the first camera, else the first person. */
const featured = computed(() => {
  const list = participants.value
  return list.find(p => p.id === call.activeSpeakerId.value)
    ?? list.find(p => p.hasVideo && p.state.video)
    ?? list[0]
    ?? null
})

const pipCall: PipControls = {
  ...call,
  remoteStream: computed(() => featured.value?.stream ?? null),
  remoteMedia: computed(() => featured.value?.state ?? DEFAULT_REMOTE_MEDIA),
  remoteSnapshot: computed(() => null),
  remoteAudioLevel: computed(() => (featured.value ? call.audioLevels.value[featured.value.id] ?? 0 : 0)),
  showRemotePlaceholder: computed(() => !!featured.value
    && (call.remoteVideoHidden.value || !featured.value.hasVideo || !featured.value.state.video)),
}

const pip = usePictureInPicture({
  enabled: hasRemote,
  videoElement: () => (featured.value ? tiles[featured.value.id]?.video : null),
  size: () => ({ width: 480, height: 270 }),
})
const pipWindow = pip.pipWindow

// ---- Actions --------------------------------------------------------------------------------

async function switchCamera() {
  const tile = localTile.value
  if (tile && call.cameraOn.value && call.sharing.value === 'none') await tile.flip(call.switchCamera)
  else await call.switchCamera()
}

function openEffects() {
  if (call.sharing.value !== 'none') return
  moreOpen.value = false
  effectsOpen.value = true
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

useEventListener(window, 'keydown', (event: KeyboardEvent) => {
  if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey || event.repeat) return
  const target = event.target as HTMLElement | null
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
    b: openEffects,
    p: () => hasRemote.value && pip.supported && pip.toggle(),
  }
  const action = actions[key]
  if (!action) return
  event.preventDefault()
  action()
})
</script>

<template>
  <div class="fixed inset-0 flex overflow-hidden bg-black text-white">
    <main ref="stage" class="relative min-w-0 flex-1 overflow-hidden">
      <div
        v-if="hasRemote"
        class="absolute flex flex-wrap content-center items-center justify-center"
        :style="{
          top: `${gridInsets.top}px`,
          bottom: `${gridInsets.bottom}px`,
          left: `${gridInsets.left}px`,
          right: `${gridInsets.right}px`,
          gap: `${gap}px`,
        }"
      >
        <TransitionGroup
          enter-active-class="transition duration-500 ease-spring"
          leave-active-class="transition duration-200 ease-out"
          enter-from-class="opacity-0 scale-90"
          leave-to-class="opacity-0 scale-95"
        >
          <GroupTile
            v-for="participant in participants"
            :key="participant.id"
            :ref="el => { tiles[participant.id] = el as InstanceType<typeof GroupTile> | null }"
            :style="tileStyle"
            :participant="participant"
            :audio-level="call.audioLevels.value[participant.id] ?? 0"
            :speaking="call.activeSpeakerId.value === participant.id"
            :video-hidden="call.remoteVideoHidden.value"
          />
        </TransitionGroup>
      </div>

      <LocalTile
        ref="localTile"
        :stream="call.localStream.value"
        :mirror="call.mirrorLocal.value"
        :pip="hasRemote"
        :stage-width="stageWidth"
        :stage-height="stageHeight"
        :controls-visible="true"
        :compact="compact"
        :mic-on="call.micOn.value"
        :mic-level="call.micLevel.value"
        :camera-on="call.cameraOn.value"
        :sharing="call.sharing.value"
        :label="call.selfLabel.value"
        @double-tap="switchCamera"
      />

      <div
        class="pointer-events-none absolute inset-0 z-20 flex flex-col justify-between
               pt-[max(env(safe-area-inset-top),0.75rem)] pr-[max(env(safe-area-inset-right),0.75rem)]
               pb-[max(env(safe-area-inset-bottom),0.75rem)] pl-[max(env(safe-area-inset-left),0.75rem)]
               sm:pt-[max(env(safe-area-inset-top),1.25rem)] sm:pr-[max(env(safe-area-inset-right),1.25rem)]
               sm:pb-[max(env(safe-area-inset-bottom),1.25rem)] sm:pl-[max(env(safe-area-inset-left),1.25rem)]"
      >
        <TopBar
          class="pointer-events-none *:pointer-events-auto"
          :room-id="call.roomId.value"
          :e2ee="call.e2ee.value"
          :phase="call.phase.value"
          :remote-media="DEFAULT_REMOTE_MEDIA"
          :remote-audio-muted="call.remoteAudioMuted.value"
          :compact="compact"
          :show-pip="pip.supported && hasRemote"
          :pip-active="pip.active.value"
          :show-switch-camera="call.cameraCount.value > 1 && call.sharing.value === 'none'"
          :people-count="participants.length + 1"
          @toggle-pip="pip.toggle"
          @switch-camera="switchCamera"
          @open-people="peopleOpen = true"
        />

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
              group
              :room-id="call.roomId.value"
              :phase="call.phase.value"
              :e2ee="call.e2ee.value"
              @copied="emit('copied')"
            />
          </Transition>

          <CallToolbar
            class="pointer-events-auto"
            group
            :call="call"
            :compact="compact"
            :chat-open="chatOpen"
            :unread="unread"
            :fit="false"
            :pip-supported="pip.supported"
            :pip-active="pip.active.value"
            @toggle-chat="chatOpen = !chatOpen"
            @open-more="moreOpen = true"
            @open-effects="openEffects"
            @pick-file="pickFile"
            @toggle-pip="pip.toggle"
            @switch-camera="switchCamera"
            @hang-up="hangUp"
          />
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
            <p class="text-sm text-white/60">It shows whoever is speaking.</p>
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
              group
              :messages="call.messages.value"
              :ready="call.joined.value"
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
        <DrawerDescription class="sr-only">Messages with everyone in this call</DrawerDescription>
        <ChatPanel
          group
          :messages="call.messages.value"
          :ready="call.joined.value"
          :connected="hasRemote"
          @send="call.sendMessage"
        />
      </DrawerContent>
    </Drawer>

    <MoreDrawer
      v-if="compact"
      v-model:open="moreOpen"
      group
      :call="call"
      :fit="false"
      :pip-supported="pip.supported"
      :pip-active="pip.active.value"
      @switch-camera="switchCamera"
      @toggle-pip="pip.toggle"
      @open-effects="openEffects"
    />

    <EffectsPanel v-model:open="effectsOpen" :call="call" :compact="compact" />
    <PeopleDialog v-model:open="peopleOpen" :call="call" />

    <input ref="fileInput" type="file" accept="video/*" class="hidden" @change="onFilePicked">
    <!-- One element per participant: every <video> is muted, so this is the only playback. -->
    <StreamAudio v-for="participant in participants" :key="participant.id" :stream="participant.stream" />

    <Teleport v-if="pipWindow" :to="pipWindow.document.body">
      <PipView :call="pipCall" :fit="!!featured?.state.screen" @hang-up="hangUp" />
    </Teleport>
  </div>
</template>
