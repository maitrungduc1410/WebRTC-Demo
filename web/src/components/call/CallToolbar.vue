<script setup lang="ts">
import { computed } from 'vue'
import {
  Ellipsis, FileVideo, Maximize, MessageSquare, Mic, MicOff, Minimize, MonitorOff, MonitorUp,
  PhoneOff, PictureInPicture2, Sparkles, SwitchCamera, Video, VideoOff, Volume2, VolumeOff, Eye, EyeOff,
} from '@lucide/vue'
import type { Call } from '@/call/useCall'
import {
  DropdownMenuCheckboxItem, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuShortcut,
} from '@/components/ui/dropdown-menu'
import { Separator } from '@/components/ui/separator'
import ToolbarButton from './ToolbarButton.vue'

const props = defineProps<{
  call: Call
  compact: boolean
  chatOpen: boolean
  unread: number
  fit: boolean
  pipSupported: boolean
  pipActive: boolean
}>()

const emit = defineEmits<{
  toggleChat: []
  openMore: []
  openEffects: []
  pickFile: []
  toggleFit: []
  togglePip: []
  switchCamera: []
  hangUp: []
}>()

const call = props.call
const connected = computed(() => call.phase.value === 'connected')
const sharing = computed(() => call.sharing.value !== 'none')
const shareOptions = computed(() => Number(call.canShareScreen) + Number(call.canShareFile))

function onShare() {
  if (sharing.value) call.stopSharing()
  else if (call.canShareScreen) call.shareScreen()
  else emit('pickFile')
}
</script>

<template>
  <div class="glass flex items-center gap-2 rounded-full p-2 shadow-2xl shadow-black/40 sm:gap-3 sm:px-3">
    <ToolbarButton
      :icon="call.micOn.value ? Mic : MicOff"
      :label="call.micOn.value ? 'Turn off microphone' : 'Turn on microphone'"
      :tone="call.micOn.value ? 'default' : 'off'"
      :pressed="!call.micOn.value"
      shortcut="M"
      @click="call.toggleMic"
    />
    <ToolbarButton
      :icon="call.cameraOn.value ? Video : VideoOff"
      :label="call.cameraOn.value ? 'Turn off camera' : 'Turn on camera'"
      :tone="call.cameraOn.value ? 'default' : 'off'"
      :pressed="!call.cameraOn.value"
      :disabled="sharing"
      shortcut="V"
      @click="call.toggleCamera"
    />

    <ToolbarButton
      v-if="sharing || shareOptions < 2"
      :icon="sharing ? MonitorOff : call.canShareScreen ? MonitorUp : FileVideo"
      :label="sharing ? 'Stop presenting' : call.canShareScreen ? 'Present your screen' : 'Share a video file'"
      :tone="sharing ? 'active' : 'default'"
      :disabled="shareOptions === 0"
      @click="onShare"
    />
    <ToolbarButton v-else :icon="MonitorUp" label="Present">
      <template #menu>
        <DropdownMenuLabel>Present to the other person</DropdownMenuLabel>
        <DropdownMenuItem @select="call.shareScreen">
          <MonitorUp /> Your screen, a window or a tab
        </DropdownMenuItem>
        <DropdownMenuItem @select="emit('pickFile')">
          <FileVideo /> A video file
        </DropdownMenuItem>
      </template>
    </ToolbarButton>

    <ToolbarButton
      v-if="!props.compact"
      :icon="Sparkles"
      label="Backgrounds and effects"
      :tone="call.effectsStatus.value === 'on' ? 'active' : 'default'"
      :disabled="sharing"
      shortcut="B"
      @click="emit('openEffects')"
    />

    <Separator v-if="!props.compact" orientation="vertical" class="mx-0.5 !h-7 bg-white/15" />

    <ToolbarButton
      :icon="MessageSquare"
      :label="props.chatOpen ? 'Close chat' : 'Chat'"
      :tone="props.chatOpen ? 'active' : 'default'"
      :badge="props.chatOpen ? 0 : props.unread"
      shortcut="C"
      @click="emit('toggleChat')"
    />

    <ToolbarButton v-if="props.compact" :icon="Ellipsis" label="More options" @click="emit('openMore')" />
    <ToolbarButton v-else :icon="Ellipsis" label="More options" menu-align="end">
      <template #menu>
        <DropdownMenuLabel>The other person</DropdownMenuLabel>
        <DropdownMenuCheckboxItem
          :model-value="call.remoteAudioMuted.value"
          :disabled="!connected"
          @select.prevent="call.toggleRemoteAudio"
        >
          <VolumeOff v-if="call.remoteAudioMuted.value" /><Volume2 v-else />
          Mute their audio
        </DropdownMenuCheckboxItem>
        <DropdownMenuCheckboxItem
          :model-value="call.remoteVideoHidden.value"
          :disabled="!connected"
          @select.prevent="call.toggleRemoteVideo"
        >
          <EyeOff v-if="call.remoteVideoHidden.value" /><Eye v-else />
          Hide their video
        </DropdownMenuCheckboxItem>
        <DropdownMenuItem :disabled="!connected" @select="emit('toggleFit')">
          <Maximize v-if="props.fit" /><Minimize v-else />
          {{ props.fit ? 'Fill the window' : 'Fit to window' }}
          <DropdownMenuShortcut>F</DropdownMenuShortcut>
        </DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuLabel>You</DropdownMenuLabel>
        <DropdownMenuItem v-if="call.cameraCount.value > 1" :disabled="sharing" @select="emit('switchCamera')">
          <SwitchCamera /> Switch camera
        </DropdownMenuItem>
        <DropdownMenuItem v-if="props.pipSupported" :disabled="!connected" @select="emit('togglePip')">
          <PictureInPicture2 />
          {{ props.pipActive ? 'Close picture-in-picture' : 'Open picture-in-picture' }}
          <DropdownMenuShortcut>P</DropdownMenuShortcut>
        </DropdownMenuItem>
      </template>
    </ToolbarButton>

    <ToolbarButton :icon="PhoneOff" label="Leave call" tone="danger" wide @click="emit('hangUp')" />
  </div>
</template>
