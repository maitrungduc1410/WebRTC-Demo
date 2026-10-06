<script setup lang="ts">
import { computed, ref } from 'vue'
import { useData, withBase } from 'vitepress'
import { useLocale, useStrings, type Text } from '../i18n'

type Platform = 'web' | 'android' | 'ios' | 'macos' | 'windows'
type Row = 'ui' | 'webrtc' | 'socket' | 'camera' | 'screen' | 'switching' | 'e2ee' | 'effects' | 'pip'
type Value = string | Text

const repo = 'https://github.com/maitrungduc1410/WebRTC-Demo/blob/master/'
const android = 'android/app/src/main/java/com/example/myapplication/webrtc/'

const platforms: Record<Platform, { name: string; rows: Record<Row, Value>; files: string[] }> = {
  web: {
    name: 'Web',
    rows: {
      ui: 'Vue 3, Tailwind CSS, shadcn-vue',
      webrtc: { en: 'The browser\'s own RTCPeerConnection', vi: 'RTCPeerConnection có sẵn của trình duyệt', zh: '浏览器自带的 RTCPeerConnection' },
      socket: 'WebSocket',
      camera: 'getUserMedia()',
      screen: 'getDisplayMedia()',
      switching: 'RTCRtpSender.replaceTrack()',
      e2ee: { en: 'Insertable Streams or RTCRtpScriptTransform, in a worker', vi: 'Insertable Streams hoặc RTCRtpScriptTransform, chạy trong worker', zh: 'Insertable Streams 或 RTCRtpScriptTransform，在 worker 中运行' },
      effects: 'MediaPipe ImageSegmenter + FaceLandmarker, canvas 2D',
      pip: 'Document Picture-in-Picture API, video PiP fallback',
    },
    files: ['web/src/call/useCall.ts', 'web/src/call/useGroupCall.ts', 'web/src/call/media.ts', 'web/src/e2ee.ts'],
  },
  android: {
    name: 'Android',
    rows: {
      ui: 'Jetpack Compose, Material 3 Expressive',
      webrtc: 'io.github.webrtc-sdk:android 150.7871.01',
      socket: 'OkHttp WebSocket',
      camera: 'Camera2 / Camera1 capturer (webrtc-sdk)',
      screen: 'MediaProjection + ScreenCapturerAndroid, foreground service',
      switching: { en: 'RtpSender.setTrack(); a file reuses the camera\'s VideoSource', vi: 'RtpSender.setTrack(); file video dùng lại VideoSource của camera', zh: 'RtpSender.setTrack()；视频文件复用摄像头的 VideoSource' },
      e2ee: 'FrameCryptor + FrameCryptorKeyProvider',
      effects: 'MediaPipe tasks-vision, GLES shaders',
      pip: { en: 'Activity picture-in-picture, enters by itself on Android 12+', vi: 'Picture-in-picture của Activity, tự vào PiP từ Android 12', zh: 'Activity 画中画，Android 12 起自动进入' },
    },
    files: [`${android}PeerConnectionClient.kt`, `${android}LocalMedia.kt`, `${android}sfu/GroupCallClient.kt`, `${android}E2eeManager.kt`],
  },
  ios: {
    name: 'iOS',
    rows: {
      ui: 'SwiftUI, Liquid Glass',
      webrtc: 'webrtc-sdk/Specs 150.7871.01 (Swift package)',
      socket: 'URLSessionWebSocketTask',
      camera: 'RTCCameraVideoCapturer',
      screen: { en: 'ReplayKit broadcast extension, frames over a Unix socket', vi: 'Broadcast extension của ReplayKit, gửi frame qua Unix socket', zh: 'ReplayKit 广播扩展，通过 Unix socket 传帧' },
      switching: { en: 'Every source feeds the same RTCVideoSource', vi: 'Mọi nguồn đều đẩy vào cùng một RTCVideoSource', zh: '所有视频源都送入同一个 RTCVideoSource' },
      e2ee: 'RTCFrameCryptor + RTCFrameCryptorKeyProvider',
      effects: 'Vision + Core Image (Metal)',
      pip: { en: 'AVPictureInPictureController, video call PiP (1:1 only)', vi: 'AVPictureInPictureController, PiP kiểu video call (chỉ 1:1)', zh: 'AVPictureInPictureController，视频通话画中画（仅 1:1）' },
    },
    files: ['ios/WebRTCDemo/PeerConnectionClient.swift', 'ios/WebRTCDemo/LocalMedia.swift', 'ios/WebRTCDemo/GroupCallClient.swift', 'ios/WebRTCDemo/FrameEncryption.swift'],
  },
  macos: {
    name: 'macOS',
    rows: {
      ui: 'SwiftUI + AppKit, Liquid Glass',
      webrtc: { en: 'Same Swift package as iOS', vi: 'Cùng Swift package với iOS', zh: '与 iOS 相同的 Swift package' },
      socket: { en: 'URLSessionWebSocketTask, the iOS code', vi: 'URLSessionWebSocketTask, dùng chung code iOS', zh: 'URLSessionWebSocketTask，与 iOS 共用代码' },
      camera: 'RTCCameraVideoCapturer',
      screen: 'ScreenCaptureKit (SCStream)',
      switching: { en: 'Every source feeds the same RTCVideoSource', vi: 'Mọi nguồn đều đẩy vào cùng một RTCVideoSource', zh: '所有视频源都送入同一个 RTCVideoSource' },
      e2ee: { en: 'The iOS code', vi: 'Dùng chung code iOS', zh: '与 iOS 共用代码' },
      effects: { en: 'The iOS pipeline: Vision + Core Image', vi: 'Pipeline của iOS: Vision + Core Image', zh: '与 iOS 相同：Vision + Core Image' },
      pip: { en: 'An always-on-top NSPanel', vi: 'Một NSPanel luôn nổi trên cùng', zh: '始终置顶的 NSPanel' },
    },
    files: ['ios/WebRTCDemoMac/MacCallView.swift', 'ios/WebRTCDemoMac/ScreenShareCapturer.swift', 'ios/WebRTCDemoMac/FloatingCallWindow.swift', 'ios/WebRTCDemo/CallViewModel.swift'],
  },
  windows: {
    name: 'Windows',
    rows: {
      ui: { en: 'WinUI 3, built in C# (no XAML pages)', vi: 'WinUI 3, dựng UI bằng C# (không dùng trang XAML)', zh: 'WinUI 3，界面用 C# 编写（不用 XAML 页面）' },
      webrtc: { en: 'libwebrtc m150.7871.03 through a small C shim', vi: 'libwebrtc m150.7871.03 qua một C shim nhỏ', zh: 'libwebrtc m150.7871.03，通过一个小型 C shim 调用' },
      socket: 'ClientWebSocket',
      camera: { en: 'Media Foundation in the shim, DirectShow fallback', vi: 'Media Foundation trong shim, DirectShow làm phương án dự phòng', zh: 'shim 中使用 Media Foundation，DirectShow 兜底' },
      screen: { en: 'libwebrtc\'s desktop capturer', vi: 'Desktop capturer của libwebrtc', zh: 'libwebrtc 的桌面采集器' },
      switching: { en: 'Swap the track on the video sender', vi: 'Đổi track trên video sender', zh: '替换视频 sender 上的 track' },
      e2ee: { en: 'libwebrtc\'s frame cryptor, through the shim', vi: 'Frame cryptor của libwebrtc, gọi qua shim', zh: 'libwebrtc 的 frame cryptor，通过 shim 调用' },
      effects: { en: 'MediaPipe models as ONNX on Windows ML, compositing in C#', vi: 'Model MediaPipe dạng ONNX chạy trên Windows ML, ghép hình bằng C#', zh: 'MediaPipe 模型转为 ONNX，在 Windows ML 上运行，用 C# 合成画面' },
      pip: { en: 'CompactOverlay window', vi: 'Cửa sổ CompactOverlay', zh: 'CompactOverlay 窗口' },
    },
    files: ['windows/native/RtcShim/include/rtc_shim.h', 'windows/src/WebRtcDemo.Core/Call/CallViewModel.cs', 'windows/src/WebRtcDemo.Core/Media/NativeCallMedia.cs', 'windows/src/WebRtcDemo.Core/Group/GroupCallClient.cs'],
  },
}

const t = useStrings({
  en: {
    rows: { ui: 'UI', webrtc: 'WebRTC', socket: 'Signaling socket', camera: 'Camera', screen: 'Screen sharing', switching: 'Switching sources', e2ee: 'E2EE', effects: 'Backgrounds and stickers', pip: 'Picture-in-picture' },
    files: 'Start reading here',
    more: 'Everything about this platform',
  },
  vi: {
    rows: { ui: 'UI', webrtc: 'WebRTC', socket: 'Socket signaling', camera: 'Camera', screen: 'Chia sẻ màn hình', switching: 'Đổi nguồn video', e2ee: 'E2EE', effects: 'Background và sticker', pip: 'Picture-in-picture' },
    files: 'Nên đọc từ đây',
    more: 'Xem chi tiết nền tảng này',
  },
  zh: {
    rows: { ui: '界面', webrtc: 'WebRTC', socket: '信令 socket', camera: '摄像头', screen: '屏幕共享', switching: '切换视频源', e2ee: 'E2EE', effects: '背景和贴纸', pip: '画中画' },
    files: '从这里开始读',
    more: '查看这个平台的详细说明',
  },
})

const locale = useLocale()
const { localeIndex } = useData()
const selected = ref<Platform>('web')
const current = computed(() => platforms[selected.value])
const order = Object.keys(platforms) as Platform[]
const rowNames = Object.keys(platforms.web.rows) as Row[]

const text = (value: Value) => (typeof value === 'string' ? value : value[locale.value])
const pageLink = computed(() => withBase(`${localeIndex.value === 'root' ? '' : `/${localeIndex.value}`}/platforms/${selected.value}`))
</script>

<template>
  <div class="demo-card platform-picker">
    <div class="demo-row tabs">
      <button v-for="name in order" :key="name" class="demo-button" :class="{ active: selected === name }" @click="selected = name">
        {{ platforms[name].name }}
      </button>
    </div>
    <dl>
      <template v-for="row in rowNames" :key="row">
        <dt>{{ t.rows[row] }}</dt>
        <dd>{{ text(current.rows[row]) }}</dd>
      </template>
    </dl>
    <div class="files">
      <span class="demo-muted">{{ t.files }}</span>
      <a v-for="file in current.files" :key="file" :href="repo + file" target="_blank" rel="noreferrer"><code>{{ file.split('/').pop() }}</code></a>
    </div>
    <a class="more" :href="pageLink">{{ t.more }} →</a>
  </div>
</template>

<style scoped>
.tabs {
  margin-bottom: 12px;
}

dl {
  display: grid;
  grid-template-columns: minmax(120px, max-content) 1fr;
  gap: 6px 16px;
  margin: 0;
  font-size: 14px;
}

dt {
  color: var(--vp-c-text-2);
}

dd {
  margin: 0;
}

.files {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin-top: 14px;
}

.more {
  display: inline-block;
  margin-top: 12px;
  font-size: 14px;
  font-weight: 500;
}

@media (max-width: 520px) {
  dl {
    grid-template-columns: 1fr;
  }

  dd {
    margin-bottom: 6px;
  }
}
</style>
