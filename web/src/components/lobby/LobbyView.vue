<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useIntervalFn } from '@vueuse/core'
import { motion } from 'motion-v'
import { ArrowRight, Lock, LockOpen, MessagesSquare, MonitorUp, Shuffle, User, Users, Video } from '@lucide/vue'
import { callMode, type CallMode } from '@/call/callMode'
import { DEFAULT_SERVER_URL, DEFAULT_SFU_URL } from '@/call/serverUrl'
import { randomRoomId, useCall } from '@/call/useCall'
import { useGroupCall } from '@/call/useGroupCall'
import { Button } from '@/components/ui/button'
import { Label } from '@/components/ui/label'
import { Switch } from '@/components/ui/switch'
import { useCoarsePointer } from '@/composables/useInputMode'
import { cn } from '@/lib/utils'
import HeroBadge from './HeroBadge.vue'
import LobbyBackdrop from './LobbyBackdrop.vue'
import ServerSetting from './ServerSetting.vue'

const SERVER_PROBE_INTERVAL_MS = 5000

const call = useCall()
const group = useGroupCall()
const coarse = useCoarsePointer()
const shuffleTurns = ref(0)
const serverSetting = ref<InstanceType<typeof ServerSetting>>()

const features = [
  { icon: Video, text: 'HD video' },
  { icon: MessagesSquare, text: 'Chat' },
  { icon: MonitorUp, text: 'Screen sharing' },
  { icon: Lock, text: 'End-to-end encryption' },
]

function onRoomInput(event: Event) {
  const input = event.target as HTMLInputElement
  const digits = input.value.replace(/\D/g, '').slice(0, 12)
  if (input.value !== digits) input.value = digits
  call.roomId.value = digits
}

function shuffle() {
  shuffleTurns.value++
  call.roomId.value = randomRoomId()
}

const modes: { id: CallMode; label: string; icon: typeof User }[] = [
  { id: 'p2p', label: '1:1 call', icon: User },
  { id: 'group', label: 'Group call (SFU)', icon: Users },
]
const selectedMode = computed({ get: () => callMode.value, set: mode => { callMode.value = mode } })
const isGroup = computed(() => selectedMode.value === 'group')

const server = computed(() => isGroup.value
  ? {
      label: 'Group call server',
      url: group.serverUrl.value,
      status: group.serverStatus.value,
      defaultUrl: DEFAULT_SFU_URL,
      placeholder: 'http://192.168.1.10:4001',
      setUrl: group.setServerUrl,
    }
  : {
      label: 'Signaling server',
      url: call.serverUrl.value,
      status: call.serverStatus.value,
      defaultUrl: DEFAULT_SERVER_URL,
      placeholder: 'http://192.168.1.10:4000',
      setUrl: call.setServerUrl,
    })

// Neither server has a connection before a call, so the status dot comes from polling GET /.
function checkServer() {
  if (isGroup.value) group.checkServer()
  else call.checkServer()
}
watch(isGroup, checkServer, { immediate: true })
useIntervalFn(checkServer, SERVER_PROBE_INTERVAL_MS)

function join() {
  if (!call.roomId.value || serverSetting.value?.commit() === false) return
  if (isGroup.value) group.join(call.roomId.value, call.e2ee.value)
  else call.join()
}

const rise = (delay: number) => ({
  initial: { opacity: 0, y: 24, filter: 'blur(8px)' },
  animate: { opacity: 1, y: 0, filter: 'blur(0px)' },
  transition: { type: 'spring', stiffness: 160, damping: 22, delay },
})
</script>

<template>
  <div class="relative min-h-dvh overflow-hidden bg-background">
    <LobbyBackdrop />

    <div
      class="relative mx-auto flex min-h-dvh w-full max-w-6xl flex-col items-center justify-center gap-10 px-6
             pt-[max(env(safe-area-inset-top),2.5rem)] pb-[max(env(safe-area-inset-bottom),2rem)]
             lg:flex-row lg:justify-between lg:gap-16 lg:px-12
             short:flex-row short:gap-8 short:py-4"
    >
      <section class="flex max-w-xl flex-col items-center text-center lg:items-start lg:text-left short:flex-1">
        <motion.div v-bind="rise(0)">
          <HeroBadge />
        </motion.div>
        <motion.h1
          v-bind="rise(0.06)"
          class="mt-8 text-4xl font-semibold tracking-tight text-balance short:mt-4 short:text-3xl sm:text-5xl lg:text-6xl"
        >
          WebRTC
          <span class="bg-gradient-to-r from-indigo-500 via-violet-500 to-sky-500 bg-clip-text text-transparent">Demo</span>
        </motion.h1>
        <motion.p v-bind="rise(0.12)" class="mt-4 max-w-md text-lg text-muted-foreground text-pretty short:mt-2 short:text-base">
          Peer-to-peer video calls, chat and screen sharing, right in your browser.
        </motion.p>
        <motion.ul v-bind="rise(0.18)" class="mt-8 hidden flex-wrap gap-2 lg:flex">
          <li
            v-for="feature in features"
            :key="feature.text"
            class="inline-flex items-center gap-2 rounded-full border bg-background/60 px-3.5 py-1.5 text-sm text-muted-foreground backdrop-blur"
          >
            <component :is="feature.icon" class="size-4 text-primary" />
            {{ feature.text }}
          </li>
        </motion.ul>
      </section>

      <motion.form
        class="w-full max-w-md shrink-0 rounded-[2rem] border bg-card/70 p-6 shadow-2xl shadow-indigo-950/10 backdrop-blur-2xl sm:p-7 short:max-w-sm short:p-5 dark:shadow-black/40"
        :initial="{ opacity: 0, y: 32, scale: 0.97 }"
        :animate="{ opacity: 1, y: 0, scale: 1 }"
        :transition="{ type: 'spring', stiffness: 140, damping: 20, delay: 0.1 }"
        @submit.prevent="join"
      >
        <Label for="room" class="text-sm font-medium text-muted-foreground">Room ID</Label>
        <div class="relative mt-2">
          <input
            id="room"
            :value="call.roomId.value"
            inputmode="numeric"
            autocomplete="off"
            enterkeyhint="go"
            placeholder="000000"
            class="h-16 w-full min-w-0 rounded-2xl border border-input bg-input/30 pr-16 pl-5 font-mono text-2xl font-semibold tracking-[0.25em] tabular-nums outline-none transition-[border-color,box-shadow] duration-200 placeholder:text-muted-foreground/50 focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 short:h-14"
            @input="onRoomInput"
          >
          <Button
            type="button"
            variant="ghost"
            size="icon-lg"
            class="absolute top-1/2 right-2 size-12 -translate-y-1/2 rounded-xl"
            aria-label="Random room"
            @click="shuffle"
          >
            <Shuffle
              class="size-5 transition-transform duration-700 ease-bounce"
              :style="{ transform: `rotate(${shuffleTurns * 180}deg)` }"
            />
          </Button>
        </div>

        <label
          :class="cn(
            'mt-4 flex cursor-pointer items-center gap-4 rounded-2xl border p-4 transition-colors duration-300',
            call.e2ee.value ? 'border-primary/40 bg-primary/10' : 'bg-muted/40 hover:bg-muted/70',
          )"
        >
          <span
            :class="cn(
              'flex size-10 shrink-0 items-center justify-center rounded-xl transition-colors duration-300',
              call.e2ee.value ? 'bg-primary text-primary-foreground' : 'bg-muted text-muted-foreground',
            )"
          >
            <Transition
              mode="out-in"
              enter-active-class="transition duration-300 ease-bounce"
              leave-active-class="transition duration-100"
              enter-from-class="scale-50 opacity-0 rotate-12"
              leave-to-class="scale-50 opacity-0"
            >
              <Lock v-if="call.e2ee.value" class="size-5" />
              <LockOpen v-else class="size-5" />
            </Transition>
          </span>
          <span class="flex-1">
            <span class="block text-sm font-medium">End-to-end encryption</span>
            <span class="block text-xs text-muted-foreground">
              {{ call.e2ee.value ? 'Frames are encrypted on this device' : isGroup ? 'Everyone must turn it on' : 'Both people must turn it on' }}
            </span>
          </span>
          <Switch v-model="call.e2ee.value" aria-label="End-to-end encryption" />
        </label>

        <Button
          type="submit"
          size="lg"
          class="group mt-5 h-14 w-full rounded-2xl text-base font-semibold shadow-lg shadow-primary/30 transition-[scale,background-color,box-shadow] duration-300 ease-bounce active:scale-[0.98]"
          :disabled="!call.roomId.value"
        >
          {{ isGroup ? 'Join group call' : 'Join room' }}
          <ArrowRight class="size-5 transition-transform duration-300 ease-bounce group-hover:translate-x-1" />
        </Button>

        <div role="radiogroup" aria-label="Call type" class="mt-4 grid grid-cols-2 gap-1 rounded-full bg-muted/50 p-1 text-xs">
          <button
            v-for="mode in modes"
            :key="mode.id"
            type="button"
            role="radio"
            :aria-checked="selectedMode === mode.id"
            :class="cn(
              'inline-flex h-8 items-center justify-center gap-1.5 rounded-full px-3 font-medium transition-[background-color,color,box-shadow] duration-200',
              'focus-visible:ring-3 focus-visible:ring-ring/50 focus-visible:outline-none',
              selectedMode === mode.id ? 'bg-background text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground',
            )"
            @click="selectedMode = mode.id"
          >
            <component :is="mode.icon" class="size-3.5" />
            {{ mode.label }}
          </button>
        </div>

        <ServerSetting
          ref="serverSetting"
          :key="selectedMode"
          :join-hint="!coarse"
          :label="server.label"
          :url="server.url"
          :status="server.status"
          :default-url="server.defaultUrl"
          :placeholder="server.placeholder"
          :set-url="server.setUrl"
        />
      </motion.form>
    </div>
  </div>
</template>
