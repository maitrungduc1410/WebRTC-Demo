import { computed, onScopeDispose, ref, shallowRef, watch, type Ref } from 'vue'

export type PipMode = 'document' | 'video' | null

const documentPipSupported = typeof window !== 'undefined' && 'documentPictureInPicture' in window

function videoPipSupported() {
  if (typeof document === 'undefined') return false
  if (document.pictureInPictureEnabled) return true
  const probe = document.createElement('video')
  return !!probe.webkitSupportsPresentationMode?.('picture-in-picture')
}

/** Copies the page's styles so the components rendered into the PiP window look the same. */
function copyStyles(target: Window) {
  for (const sheet of Array.from(document.styleSheets)) {
    try {
      const style = target.document.createElement('style')
      style.textContent = Array.from(sheet.cssRules).map(rule => rule.cssText).join('\n')
      target.document.head.appendChild(style)
    } catch {
      // Cross-origin sheets cannot be read; link them instead.
      if (!sheet.href) continue
      const link = target.document.createElement('link')
      link.rel = 'stylesheet'
      link.href = sheet.href
      target.document.head.appendChild(link)
    }
  }
}

interface Options {
  /** Whether the call has something worth floating; auto-enter only applies then. */
  enabled: Ref<boolean>
  /** Preferred window size, usually from the remote video's aspect ratio. */
  size: () => { width: number; height: number }
  /** The stage's remote <video>, for browsers that only float a single video. */
  videoElement: () => HTMLVideoElement | null | undefined
}

/**
 * Floats the call above other tabs and apps, like Google Meet.
 *
 * Chrome and Edge get Document Picture-in-Picture: a small always-on-top window with our own
 * components, which also opens by itself when the user switches tabs during a call
 * (`enterpictureinpicture` media session action). Other browsers float the remote <video>.
 */
export function usePictureInPicture({ enabled, size, videoElement }: Options) {
  const pipWindow = shallowRef<Window | null>(null)
  const videoPipActive = ref(false)
  const mode: PipMode = documentPipSupported ? 'document' : videoPipSupported() ? 'video' : null
  const supported = mode !== null
  const active = computed(() => !!pipWindow.value || videoPipActive.value)

  async function openDocumentPip() {
    if (pipWindow.value) return
    const pip = await window.documentPictureInPicture!.requestWindow(size())
    copyStyles(pip)
    pip.document.documentElement.className = document.documentElement.className
    pip.document.title = document.title
    pip.addEventListener('pagehide', () => {
      if (pipWindow.value === pip) pipWindow.value = null
    }, { once: true })
    pipWindow.value = pip
  }

  async function openVideoPip() {
    const video = videoElement()
    if (!video || video.readyState < 1) return
    if (document.pictureInPictureEnabled && video.requestPictureInPicture) {
      await video.requestPictureInPicture()
      videoPipActive.value = true
      video.addEventListener('leavepictureinpicture', () => { videoPipActive.value = false }, { once: true })
    } else if (video.webkitSetPresentationMode) {
      video.webkitSetPresentationMode('picture-in-picture')
      videoPipActive.value = true
      const onChange = () => {
        if (video.webkitPresentationMode !== 'picture-in-picture') {
          videoPipActive.value = false
          video.removeEventListener('webkitpresentationmodechanged', onChange)
        }
      }
      video.addEventListener('webkitpresentationmodechanged', onChange)
    }
  }

  async function open() {
    try {
      if (mode === 'document') await openDocumentPip()
      else if (mode === 'video') await openVideoPip()
    } catch (error) {
      console.warn('Picture-in-picture failed:', error)
    }
  }

  function close() {
    pipWindow.value?.close()
    pipWindow.value = null
    if (videoPipActive.value) {
      if (document.pictureInPictureElement) document.exitPictureInPicture().catch(() => {})
      else videoElement()?.webkitSetPresentationMode?.('inline')
      videoPipActive.value = false
    }
  }

  function toggle() {
    return active.value ? close() : open()
  }

  // Chrome 134+ calls this when a tab using the camera or microphone is hidden.
  function setAutoEnter(on: boolean) {
    if (!('mediaSession' in navigator)) return
    try {
      navigator.mediaSession.setActionHandler('enterpictureinpicture' as MediaSessionAction, on ? () => { open() } : null)
    } catch {
      // Older browsers do not know the action.
    }
  }

  watch(enabled, on => setAutoEnter(on && supported), { immediate: true })
  onScopeDispose(() => {
    setAutoEnter(false)
    close()
  })

  return { supported, mode, pipWindow, active, open, close, toggle }
}
