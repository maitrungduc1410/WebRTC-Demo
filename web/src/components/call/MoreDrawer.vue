<script setup lang="ts">
import { computed, type Component } from 'vue'
import { Eye, EyeOff, Maximize, Minimize, PictureInPicture2, Sparkles, SwitchCamera, Volume2, VolumeOff } from '@lucide/vue'
import type { Call } from '@/call/useCall'
import { Drawer, DrawerContent, DrawerDescription, DrawerHeader, DrawerTitle } from '@/components/ui/drawer'
import { cn } from '@/lib/utils'

const open = defineModel<boolean>('open', { required: true })

const props = defineProps<{
  call: Call
  fit: boolean
  pipSupported: boolean
  pipActive: boolean
}>()

const emit = defineEmits<{
  toggleFit: []
  togglePip: []
  switchCamera: []
  openEffects: []
}>()

interface Option {
  id: string
  icon: Component
  label: string
  checked: boolean
  disabled?: boolean
  run: () => void
  closes?: boolean
}

const options = computed<Option[]>(() => {
  const call = props.call
  const connected = call.phase.value === 'connected'
  const sharing = call.sharing.value !== 'none'
  const list: Option[] = [
    {
      id: 'effects',
      icon: Sparkles,
      label: 'Backgrounds and effects',
      checked: call.effectsStatus.value === 'on',
      disabled: sharing,
      run: () => emit('openEffects'),
      closes: true,
    },
    {
      id: 'audio',
      icon: call.remoteAudioMuted.value ? VolumeOff : Volume2,
      label: call.remoteAudioMuted.value ? 'Their audio muted' : 'Mute their audio',
      checked: call.remoteAudioMuted.value,
      disabled: !connected,
      run: call.toggleRemoteAudio,
    },
    {
      id: 'video',
      icon: call.remoteVideoHidden.value ? EyeOff : Eye,
      label: call.remoteVideoHidden.value ? 'Their video hidden' : 'Hide their video',
      checked: call.remoteVideoHidden.value,
      disabled: !connected,
      run: call.toggleRemoteVideo,
    },
    {
      id: 'fit',
      icon: props.fit ? Minimize : Maximize,
      label: props.fit ? 'Fit to screen' : 'Fill screen',
      checked: props.fit,
      disabled: !connected,
      run: () => emit('toggleFit'),
    },
  ]
  if (call.cameraCount.value > 1) {
    list.push({ id: 'camera', icon: SwitchCamera, label: 'Switch camera', checked: false, disabled: sharing, run: () => emit('switchCamera'), closes: true })
  }
  if (props.pipSupported) {
    list.push({ id: 'pip', icon: PictureInPicture2, label: 'Picture-in-picture', checked: props.pipActive, disabled: !connected, run: () => emit('togglePip'), closes: true })
  }
  return list
})

function choose(option: Option) {
  option.run()
  if (option.closes) open.value = false
}
</script>

<template>
  <Drawer v-model:open="open">
    <DrawerContent class="dark">
      <DrawerHeader class="pb-2 text-left short:pt-1">
        <DrawerTitle>Call options</DrawerTitle>
        <DrawerDescription>Changes to the other person only apply on this device.</DrawerDescription>
      </DrawerHeader>
      <div class="scrollbar-thin grid min-h-0 grid-cols-2 gap-3 overflow-y-auto px-4 pb-[max(env(safe-area-inset-bottom),1.25rem)] sm:grid-cols-3 short:grid-cols-4">
        <button
          v-for="(option, index) in options"
          :key="option.id"
          type="button"
          :disabled="option.disabled"
          :class="cn(
            'flex h-24 flex-col justify-between rounded-3xl p-4 text-left text-sm font-medium short:h-20 short:p-3.5',
            'animate-in fade-in-0 slide-in-from-bottom-3 fill-mode-both duration-500 ease-out-expo',
            'transition-[background-color,color,scale] active:scale-95 disabled:opacity-40',
            option.checked ? 'bg-primary text-primary-foreground' : 'bg-muted text-foreground hover:bg-muted/80',
          )"
          :style="{ animationDelay: `${index * 35}ms` }"
          @click="choose(option)"
        >
          <component :is="option.icon" class="size-5" />
          {{ option.label }}
        </button>
      </div>
    </DrawerContent>
  </Drawer>
</template>
