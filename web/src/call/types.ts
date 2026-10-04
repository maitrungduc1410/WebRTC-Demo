import type { EffectsSelection } from '@/effects/catalog'

export type Phase = 'idle' | 'waiting' | 'connecting' | 'connected'
export type Sharing = 'none' | 'screen' | 'file'
export type EffectsStatus = 'off' | 'loading' | 'on'
export type ServerStatus = 'connecting' | 'connected' | 'unreachable'

/** What a remote side says it is sending ("media state" message); screen = screen or file sharing */
export interface MediaState { audio: boolean; video: boolean; screen: boolean }

export interface ChatMessage {
  id: number
  text: string
  timestamp: number
  isLocal: boolean
  /** Sender label in group calls; 1:1 chat has only "You" and "Them". */
  name?: string
}

export type CallEvent =
  | { type: 'peer-joined'; name?: string }
  | { type: 'peer-left'; name?: string }
  | { type: 'error'; message: string }
  | { type: 'info'; message: string }

export const DEFAULT_REMOTE_MEDIA: MediaState = { audio: true, video: true, screen: false }

type Value<T> = { readonly value: T }

/** What the effects picker uses, from either engine. */
export interface EffectsControls {
  cameraOn: Value<boolean>
  sharing: Value<Sharing>
  localStream: Value<MediaStream | null>
  mirrorLocal: Value<boolean>
  effects: Value<EffectsSelection>
  effectsStatus: Value<EffectsStatus>
  setEffects: (next: EffectsSelection) => void
}

/** What the toolbar and the More drawer use; both the 1:1 and the group engine provide it. */
export interface CallControls {
  phase: Value<Phase>
  micOn: Value<boolean>
  cameraOn: Value<boolean>
  sharing: Value<Sharing>
  effectsStatus: Value<EffectsStatus>
  cameraCount: Value<number>
  remoteAudioMuted: Value<boolean>
  remoteVideoHidden: Value<boolean>
  canShareScreen: boolean
  canShareFile: boolean
  toggleMic: () => void
  toggleCamera: () => void
  shareScreen: () => Promise<void>
  stopSharing: () => Promise<void>
  toggleRemoteAudio: () => void
  toggleRemoteVideo: () => void
}

/** The picture-in-picture window shows one remote video: the peer, or the group's featured participant. */
export interface PipControls extends CallControls {
  localStream: Value<MediaStream | null>
  mirrorLocal: Value<boolean>
  remoteStream: Value<MediaStream | null>
  remoteMedia: Value<MediaState>
  remoteSnapshot: Value<string | null>
  remoteAudioLevel: Value<number>
  showRemotePlaceholder: Value<boolean>
}
