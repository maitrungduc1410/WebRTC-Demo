import { ref } from 'vue'

/** 1:1 goes peer-to-peer through the signaling server; group goes through the SFU server. */
export type CallMode = 'p2p' | 'group'

/** Not saved: every visit starts in the default 1:1 mode. */
export const callMode = ref<CallMode>('p2p')
