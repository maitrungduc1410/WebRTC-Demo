<script setup lang="ts">
import { computed, ref } from 'vue'
import { Ban, Droplet, Droplets, Loader2, Play, VideoOff } from '@lucide/vue'
import type { Call } from '@/call/useCall'
import StreamVideo from '@/components/video/StreamVideo.vue'
import { backgrounds, stickers, type BackgroundOption } from '@/effects/catalog'
import { cn } from '@/lib/utils'

const props = defineProps<{ call: Call }>()
const call = props.call

type Tab = 'backgrounds' | 'filters'
const tab = ref<Tab>('backgrounds')
const tabs: { id: Tab; label: string }[] = [
  { id: 'backgrounds', label: 'Backgrounds' },
  { id: 'filters', label: 'Filters' },
]

const sharing = computed(() => call.sharing.value !== 'none')
const loading = computed(() => call.effectsStatus.value === 'loading')
const selection = computed(() => call.effects.value)

const sections = computed(() => [
  { title: 'Blur', items: backgrounds.filter(b => b.kind === 'none' || b.kind === 'blur') },
  { title: 'Pictures', items: backgrounds.filter(b => b.kind === 'image') },
  { title: 'Videos', items: backgrounds.filter(b => b.kind === 'video') },
].filter(section => section.items.length > 0))

const builtInIcons = { none: Ban, 'blur-light': Droplet, 'blur-strong': Droplets } as Record<string, typeof Ban>

function chooseBackground(option: BackgroundOption) {
  call.setEffects({ ...selection.value, background: option.id })
}

function chooseSticker(id: string | null) {
  call.setEffects({ ...selection.value, sticker: id })
}

function tileClass(selected: boolean) {
  return cn(
    'group relative overflow-hidden rounded-2xl bg-white/[0.06] outline-none transition-[scale,box-shadow] duration-200',
    'focus-visible:ring-2 focus-visible:ring-ring active:scale-95 disabled:pointer-events-none disabled:opacity-40',
    selected ? 'ring-2 ring-primary ring-offset-2 ring-offset-popover' : 'hover:ring-1 hover:ring-white/30',
  )
}
</script>

<template>
  <div class="flex min-h-0 flex-1 flex-col gap-4">
    <div class="relative aspect-video shrink-0 overflow-hidden rounded-2xl bg-black ring-1 ring-white/10">
      <StreamVideo
        v-if="call.localStream.value && call.cameraOn.value && !sharing"
        :stream="call.localStream.value"
        :mirror="call.mirrorLocal.value"
        class="size-full object-cover"
      />
      <div v-else class="flex size-full flex-col items-center justify-center gap-2 text-sm text-white/60">
        <VideoOff class="size-6" />
        {{ sharing ? 'Effects are paused while you present' : 'Your camera is off' }}
      </div>
      <Transition
        enter-active-class="transition-opacity duration-200"
        leave-active-class="transition-opacity duration-300"
        enter-from-class="opacity-0"
        leave-to-class="opacity-0"
      >
        <div v-if="loading" class="absolute inset-0 flex items-center justify-center bg-black/40 backdrop-blur-[2px]">
          <Loader2 class="size-7 animate-spin text-white" />
        </div>
      </Transition>
    </div>

    <div role="tablist" class="relative grid shrink-0 grid-cols-2 rounded-full bg-white/[0.06] p-1 text-sm font-medium">
      <div
        class="absolute inset-y-1 left-1 w-[calc(50%-0.25rem)] rounded-full bg-white/15 transition-transform duration-300 ease-out-expo"
        :style="{ transform: tab === 'filters' ? 'translateX(100%)' : 'none' }"
      />
      <button
        v-for="item in tabs"
        :id="`effects-tab-${item.id}`"
        :key="item.id"
        role="tab"
        type="button"
        :aria-selected="tab === item.id"
        :aria-controls="`effects-panel-${item.id}`"
        :class="cn('relative z-10 rounded-full py-2 transition-colors', tab === item.id ? 'text-white' : 'text-white/60 hover:text-white')"
        @click="tab = item.id"
      >
        {{ item.label }}
      </button>
    </div>

    <div
      :id="`effects-panel-${tab}`"
      role="tabpanel"
      :aria-labelledby="`effects-tab-${tab}`"
      class="scrollbar-thin -mx-1 min-h-0 flex-1 overflow-y-auto px-1 pt-1 pb-2"
    >
      <div v-if="tab === 'backgrounds'" class="space-y-5">
        <section v-for="section in sections" :key="section.title" class="space-y-2.5">
          <h3 class="text-xs font-medium tracking-wide text-white/50 uppercase">{{ section.title }}</h3>
          <div class="grid grid-cols-3 gap-2.5">
            <button
              v-for="option in section.items"
              :key="option.id"
              type="button"
              :title="option.name"
              :aria-label="option.name"
              :aria-pressed="selection.background === option.id"
              :disabled="sharing"
              :class="cn(tileClass(selection.background === option.id), 'aspect-video')"
              @click="chooseBackground(option)"
            >
              <img
                v-if="option.thumbnail"
                :src="option.thumbnail"
                alt=""
                loading="lazy"
                class="size-full object-cover transition-transform duration-500 group-hover:scale-105"
              >
              <div v-else class="flex size-full flex-col items-center justify-center gap-1 text-[11px] text-white/80">
                <component :is="builtInIcons[option.id] ?? Ban" class="size-5" />
                {{ option.name }}
              </div>
              <span
                v-if="option.kind === 'video'"
                class="absolute right-1.5 bottom-1.5 flex size-5 items-center justify-center rounded-full bg-black/60"
              >
                <Play class="size-3 fill-current" />
              </span>
            </button>
          </div>
        </section>
        <p v-if="sections.length === 1" class="text-xs text-white/50">
          Add pictures and videos to the effects folder to see them here.
        </p>
      </div>

      <div v-else class="grid grid-cols-4 gap-2.5">
        <button
          type="button"
          title="No filter"
          aria-label="No filter"
          :aria-pressed="selection.sticker === null"
          :disabled="sharing"
          :class="cn(tileClass(selection.sticker === null), 'flex aspect-square items-center justify-center text-white/80')"
          @click="chooseSticker(null)"
        >
          <Ban class="size-5" />
        </button>
        <button
          v-for="sticker in stickers"
          :key="sticker.id"
          type="button"
          :title="sticker.name"
          :aria-label="sticker.name"
          :aria-pressed="selection.sticker === sticker.id"
          :disabled="sharing"
          :class="cn(tileClass(selection.sticker === sticker.id), 'aspect-square p-2.5')"
          @click="chooseSticker(sticker.id)"
        >
          <img
            :src="sticker.url"
            alt=""
            loading="lazy"
            class="size-full object-contain transition-transform duration-300 group-hover:scale-110"
          >
        </button>
      </div>
    </div>
  </div>
</template>
