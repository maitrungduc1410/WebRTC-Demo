// Browser APIs that TypeScript's DOM library does not describe yet.

interface DocumentPictureInPictureOptions {
  width?: number
  height?: number
  disallowReturnToOpener?: boolean
  preferInitialWindowPlacement?: boolean
}

interface DocumentPictureInPicture extends EventTarget {
  readonly window: Window | null
  requestWindow(options?: DocumentPictureInPictureOptions): Promise<Window>
}

interface Window {
  readonly documentPictureInPicture?: DocumentPictureInPicture
}

interface HTMLVideoElement {
  captureStream?(frameRate?: number): MediaStream
  mozCaptureStream?(frameRate?: number): MediaStream
  webkitSupportsPresentationMode?(mode: string): boolean
  webkitSetPresentationMode?(mode: 'inline' | 'picture-in-picture' | 'fullscreen'): void
  readonly webkitPresentationMode?: string
}
