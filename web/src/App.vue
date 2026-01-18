<script setup lang="ts">
import io, { Socket } from 'socket.io-client'
import { onBeforeMount, ref, computed, nextTick } from 'vue';
import { encryptStream, decryptStream } from "./e2ee";
import {
  Video,
  Shuffle,
  ArrowRight,
  MessageCircle,
  Mic,
  MicOff,
  VideoOff,
  PhoneOff,
  MonitorUp,
  MonitorX,
  Volume2,
  VolumeX,
  Send,
  AlertCircle,
  FileVideo,
  Wallpaper,
} from 'lucide-vue-next';
import { ImageSegmenter, FilesetResolver } from '@mediapipe/tasks-vision';

const BASE_URL = 'http://localhost:4000'
const roomId = ref<string>('')
const isInRoom = ref(false)
const message = ref('')

interface Message {
  sender: string;
  text: string;
  timestamp: number;
  isLocal: boolean;
}

const messages = ref<Message[]>([])
const dataChannelReady = ref(false)
const peersConnected = ref(false)
const isAudioEnabled = ref(true)
const isVideoEnabled = ref(true)
const isRemoteAudioEnabled = ref(true)
const isScreenSharing = ref(false)
const showMessageSheet = ref(false)
const showShareMenu = ref(false)
const isVideoFileSharing = ref(false)
const isBackgroundEffectEnabled = ref(false)

// Background effect variables
let imageSegmenter: ImageSegmenter | null = null
let backgroundCanvas: HTMLCanvasElement | null = null
let backgroundCtx: CanvasRenderingContext2D | null = null
let processedStream: MediaStream | null = null
let animationFrameId: number | null = null
let backgroundImage: HTMLImageElement | null = null
let hiddenVideoElement: HTMLVideoElement | null = null
let originalCameraStream: MediaStream | null = null

// Draggable local video - start at top-right corner
const localVideoX = ref(window.innerWidth - 144) // 128px width + 16px padding
const localVideoY = ref(80) // Below top bar
let isDragging = false
let startX = 0
let startY = 0

// Video dimensions
const localVideoWidth = ref(128)
const localVideoHeight = ref(96)
const remoteVideoStyle = ref({
  width: '100%',
  height: '100%',
  objectFit: 'contain' as 'contain' | 'cover'
})

let peerConnection: RTCPeerConnection | null = null
let localStream: MediaStream | null = null
let remoteStream: MediaStream | null = null
let socket: Socket | null = null
let dataChannel: RTCDataChannel | null = null
let videoFileElement: HTMLVideoElement | null = null
let videoFileStream: MediaStream | null = null

const enableE2EE = ref(false); // User can toggle this
let encryptionKey: CryptoKey;
const shouldSendEncryptionKey = true;
const useEncryptionWorker = true;
let encryptionWorker: Worker | undefined = undefined

const connectionStatus = computed(() => {
  if (peersConnected.value) return 'Connected';
  if (isInRoom.value) return 'Connecting...';
  return 'Not connected';
});

onBeforeMount(() => {
  generateRandomId()

  socket = io(BASE_URL)
  socket.on('connect', () => {
    console.log('Socket connected')
  })

  socket.on('new user joined', async () => {
    console.log('new user joined')

    // Wait for localStream to be ready before initializing peer connection
    const waitForLocalStream = () => {
      return new Promise<void>((resolve) => {
        if (localStream) {
          resolve();
          return;
        }
        const interval = setInterval(() => {
          if (localStream) {
            clearInterval(interval);
            resolve();
          }
        }, 100);
      });
    };

    await waitForLocalStream();

    if (enableE2EE.value) {
      if (useEncryptionWorker) {
        generateEncryptionKeyUsingWorker()
      } else {
        await generateEncryptionKey()
        init()
      }
    } else {
      init()
    }
  })

  socket.on('offer', async data => {
    console.log('offer', data.offer)

    // Wait for localStream to be ready before handling offer
    const waitForLocalStream = () => {
      return new Promise<void>((resolve) => {
        if (localStream) {
          resolve();
          return;
        }
        const interval = setInterval(() => {
          if (localStream) {
            clearInterval(interval);
            resolve();
          }
        }, 100);
      });
    };

    await waitForLocalStream();

    if (!peerConnection) {
      peerConnection = new RTCPeerConnection({
        iceServers: [{ urls: 'stun:stun.l.google.com:19302' },]
      })
      initPeerEvents()
    }

    await peerConnection!.setRemoteDescription(new RTCSessionDescription(data.offer))
    const answer = await peerConnection!.createAnswer()
    await peerConnection!.setLocalDescription(answer)
    socket!.emit('answer', { answer, roomId: roomId.value })
  })

  socket.on('answer', async data => {
    const remoteDesc = new RTCSessionDescription(data.answer)
    await peerConnection!.setRemoteDescription(remoteDesc)
  })

  socket.on('new ice candidate', async data => {
    if (peerConnection) {
      await peerConnection.addIceCandidate(data.iceCandidate)
    }
  })

  socket.on('receive encryption key', async (data: { encryptionKey: ArrayBuffer }) => {
    console.log('Received encryption key:', data.encryptionKey);

    if (useEncryptionWorker && encryptionWorker) {
      encryptionWorker.postMessage({
        action: 'setKey',
        key: data.encryptionKey
      });
    } else {
      encryptionKey = await window.crypto.subtle.importKey(
        "raw",
        data.encryptionKey,
        { name: "AES-GCM" },
        true,
        ["encrypt", "decrypt"]
      );
    }

    socket!.emit('encryption key received', { roomId: roomId.value });
  });

  socket.on('remote peer received encryption key', () => {
    console.log('Remote peer received encryption key');
  });
})

async function init() {
  peerConnection = new RTCPeerConnection({
    iceServers: [{ urls: 'stun:stun.l.google.com:19302' },]
  })

  initPeerEvents()

  const offer = await peerConnection!.createOffer({
    offerToReceiveAudio: true,
    offerToReceiveVideo: true
  })
  await peerConnection!.setLocalDescription(offer)
  socket!.emit('offer', { offer, roomId: roomId.value })
}

async function setVideoFromLocalCamera(useScreenShare = false) {
  try {
    let stream;
    if (useScreenShare) {
      stream = await navigator.mediaDevices.getDisplayMedia({ video: true, audio: true });
      
      // Listen for when user clicks Chrome's "Stop sharing" button
      stream.getVideoTracks()[0].addEventListener('ended', () => {
        console.log('Screen sharing stopped by user (Chrome button)');
        stopSharing()
      });
    } else {
      const constraints = { video: true, audio: true };
      stream = await navigator.mediaDevices.getUserMedia(constraints);
    }

    const videoElement = document.getElementById('local') as HTMLVideoElement;
    videoElement.srcObject = stream;
    localStream = stream;

    // Wait for video metadata to load and set correct dimensions
    videoElement.onloadedmetadata = () => {
      const videoWidth = videoElement.videoWidth;
      const videoHeight = videoElement.videoHeight;
      const aspectRatio = videoWidth / videoHeight;

      // Set local video dimensions maintaining aspect ratio
      // Base height for local video
      const baseHeight = 160; // Reasonable preview size
      localVideoHeight.value = baseHeight;
      localVideoWidth.value = Math.round(baseHeight * aspectRatio);

      // Update position to stay at top-right
      localVideoX.value = window.innerWidth - localVideoWidth.value - 16;
    };

    if (peerConnection) {
      const videoTrack = localStream.getVideoTracks()[0];
      const senders = peerConnection!.getSenders();
      const videoSender = senders.find(sender => sender.track?.kind === 'video');
      if (videoSender) {
        videoSender.replaceTrack(videoTrack);
      }
    }
  } catch (error) {
    console.error('Error opening video camera or screen.', error);
  }
}

function setVideoFromRemoteStream() {
  remoteStream = new MediaStream()
  const remoteVideo = document.getElementById('remote') as HTMLVideoElement
  remoteVideo.srcObject = remoteStream

  // Set up remote video dimension calculation
  remoteVideo.onloadedmetadata = () => {
    const videoWidth = remoteVideo.videoWidth;
    const videoHeight = remoteVideo.videoHeight;
    const aspectRatio = videoWidth / videoHeight;

    if (aspectRatio > 1) {
      // Landscape video
      remoteVideoStyle.value = {
        width: '100%',
        height: 'auto',
        objectFit: 'contain'
      };
    } else {
      // Portrait video
      remoteVideoStyle.value = {
        width: 'auto',
        height: '100%',
        objectFit: 'contain'
      };
    }
  };
}

function generateRandomId() {
  // random 6 digit number between 100000 and 999999
  roomId.value = (Math.floor(100000 + Math.random() * 900000)).toString()
}

async function joinRoom() {
  if (!roomId.value) return;

  // Initialize E2EE worker if enabled
  if (enableE2EE.value && useEncryptionWorker && !encryptionWorker) {
    encryptionWorker = new Worker(new URL('./encryptionWorker.ts', import.meta.url), {
      type: 'module'
    });

    // Set up worker message handler
    encryptionWorker.onmessage = async (event) => {
      const { action, key } = event.data;
      if (action === "generatedKey") {
        encryptionKey = key;
        if (shouldSendEncryptionKey) {
          const exportedKey = await window.crypto.subtle.exportKey("raw", encryptionKey);
          socket!.emit('send encryption key', { roomId: roomId.value, encryptionKey: exportedKey });
        }
        init()
      }
    };
  }

  isInRoom.value = true
  socket!.emit('join room', { roomId: roomId.value })

  // Wait for DOM to render the call screen
  await nextTick()

  await setVideoFromLocalCamera()

  // Only create remote stream if it doesn't exist yet
  if (!remoteStream) {
    setVideoFromRemoteStream()
  }

  // Update local video position to top-right once video is loaded
  await nextTick()
  const localVideo = document.getElementById('local-video-container')
  if (localVideo && localVideo.offsetWidth > 0) {
    localVideoX.value = window.innerWidth - localVideo.offsetWidth - 16
    localVideoY.value = 80 // Below top bar
  }

  // Add resize listener to update remote video dimensions
  window.addEventListener('resize', updateRemoteVideoDimensions)

  // Add global mouse/touch move and end listeners
  window.addEventListener('mousemove', handleDragMove)
  window.addEventListener('mouseup', handleDragEnd)
  window.addEventListener('touchmove', handleDragMove)
  window.addEventListener('touchend', handleDragEnd)
}

function leaveRoom() {
  socket!.emit('leave room', { roomId: roomId.value })
  onDisconnected()

  localStream!.getTracks().forEach((track) => {
    track.stop();
  });

  // Clean up video file sharing
  if (videoFileElement) {
    videoFileElement.pause();
    videoFileElement.src = '';
    videoFileElement = null;
  }
  
  if (videoFileStream) {
    videoFileStream.getTracks().forEach(track => track.stop());
    videoFileStream = null;
  }

  isInRoom.value = false
  message.value = ''
  messages.value = []
  showMessageSheet.value = false
  showShareMenu.value = false
  isScreenSharing.value = false
  isVideoFileSharing.value = false

  // Terminate E2EE worker if exists
  if (encryptionWorker) {
    encryptionWorker.terminate()
    encryptionWorker = undefined
  }

  // Clean up background effect
  if (isBackgroundEffectEnabled.value) {
    stopBackgroundProcessing()
    isBackgroundEffectEnabled.value = false
  }
  if (imageSegmenter) {
    imageSegmenter.close()
    imageSegmenter = null
  }

  // Clean up resize listener
  window.removeEventListener('resize', updateRemoteVideoDimensions)

  // Clean up drag listeners
  window.removeEventListener('mousemove', handleDragMove)
  window.removeEventListener('mouseup', handleDragEnd)
  window.removeEventListener('touchmove', handleDragMove)
  window.removeEventListener('touchend', handleDragEnd)
}

function initPeerEvents() {
  // If background effect is enabled, send processed stream instead of original
  const streamToSend = isBackgroundEffectEnabled.value && processedStream 
    ? processedStream 
    : localStream;
    
  if (streamToSend) {
    streamToSend.getTracks().forEach(track => {
      peerConnection!.addTrack(track, streamToSend!)
    })
  }

  peerConnection!.onicecandidate = event => {
    if (event.candidate) {
      socket!.emit('new ice candidate', { iceCandidate: event.candidate, roomId: roomId.value })
    }
  }

  peerConnection!.onconnectionstatechange = () => {
    console.log('Connection state change: ', peerConnection!.connectionState)
    if (peerConnection!.connectionState === 'connected') {
      peersConnected.value = true
    } else if (peerConnection!.connectionState === 'disconnected') {
      onDisconnected()
    }
  }

  peerConnection!.ontrack = (event) => {
    if (!remoteStream) {
      setVideoFromRemoteStream()
    }

    if (event.track.kind === 'video') {
      for (const track of remoteStream!.getVideoTracks()) {
        remoteStream!.removeTrack(track)
      }
    }

    if (event.track.kind === 'audio') {
      for (const track of remoteStream!.getAudioTracks()) {
        remoteStream!.removeTrack(track)
      }
    }

    remoteStream!.addTrack(event.track)
  }

  peerConnection!.ondatachannel = event => {
    dataChannel = event.channel
    initDataChannelEvents()
  };

  if (enableE2EE.value) {
    peerConnection!.getSenders().forEach(async sender => {
      if (sender.track?.kind === 'video' || sender.track?.kind === 'audio') {
        // @ts-ignore
        const senderStreams = sender.createEncodedStreams();
        const readable = senderStreams.readable;
        const writable = senderStreams.writable;

        if (useEncryptionWorker) {
          encryptionWorker?.postMessage({
            action: 'encrypt',
            readable,
            writable
          }, [readable, writable]);
        } else {
          await encryptStream(encryptionKey, readable, writable);
        }
      }
    });

    peerConnection!.getReceivers().forEach(async receiver => {
      if (receiver.track.kind === 'video' || receiver.track.kind === 'audio') {
        // @ts-ignore
        const receiverStreams = receiver.createEncodedStreams();
        const readable = receiverStreams.readable;
        const writable = receiverStreams.writable;

        if (useEncryptionWorker) {
          encryptionWorker?.postMessage({
            action: 'decrypt',
            readable,
            writable,
            shouldSendEncryptionKey
          }, [readable, writable]);
        } else {
          await decryptStream(shouldSendEncryptionKey ? encryptionKey : undefined, readable, writable);
        }
      }
    });
  }
}

async function openDataChannel() {
  dataChannel = peerConnection!.createDataChannel("MyApp Channel");

  const offer = await peerConnection!.createOffer({
    offerToReceiveAudio: true,
    offerToReceiveVideo: true
  })
  await peerConnection!.setLocalDescription(offer)
  socket!.emit('offer', { offer, roomId: roomId.value })

  initDataChannelEvents()
}

function sendMessage() {
  if (dataChannelReady.value && dataChannel && message.value.trim()) {
    const msg: Message = {
      sender: 'You',
      text: message.value,
      timestamp: Date.now(),
      isLocal: true
    };

    dataChannel!.send(message.value);
    messages.value.push(msg);
    message.value = '';

    // Auto scroll to bottom
    setTimeout(() => {
      const container = document.querySelector('.messages-container');
      if (container) {
        container.scrollTop = container.scrollHeight;
      }
    }, 10);
  }
}

function onDisconnected() {
  if (dataChannel) {
    dataChannel!.close()
    dataChannel = null
    dataChannelReady.value = false
  }

  if (peerConnection) {
    peerConnection!.close()
    peerConnection = null
    peersConnected.value = false
  }

  // Reset remote stream so it gets recreated on rejoin
  remoteStream = null
}

function initDataChannelEvents() {
  dataChannel!.onopen = e => {
    console.log('datachannel open', e)
    dataChannelReady.value = true
    messages.value = []
  };

  dataChannel!.onclose = e => {
    console.log('datachannel close', e)
    dataChannelReady.value = false
  };

  dataChannel!.onerror = e => {
    console.log('datachannel onerror', e)
    dataChannelReady.value = false
  };

  dataChannel!.onmessage = e => {
    // Regular text message
    const msg: Message = {
      sender: 'Remote',
      text: e.data,
      timestamp: Date.now(),
      isLocal: false
    };

    messages.value.push(msg);

    // Auto scroll to bottom if sheet is open
    if (showMessageSheet.value) {
      setTimeout(() => {
        const container = document.querySelector('.messages-container');
        if (container) {
          container.scrollTop = container.scrollHeight;
        }
      }, 10);
    }
  };
}

function toggleAudio() {
  if (localStream) {
    localStream.getAudioTracks().forEach(track => {
      track.enabled = !track.enabled
      isAudioEnabled.value = track.enabled
    })
  }
}

function toggleVideo() {
  if (localStream) {
    localStream.getVideoTracks().forEach(track => {
      track.enabled = !track.enabled
      isVideoEnabled.value = track.enabled
    })
  }
}

function muteRemote() {
  if (remoteStream) {
    remoteStream.getAudioTracks().forEach(track => {
      track.enabled = !track.enabled
      isRemoteAudioEnabled.value = track.enabled
    })
  }
}

function stopSharing() {
  // Stop current tracks before switching back to camera
  if (localStream) {
    localStream.getTracks().forEach(track => {
      console.log('Stopping track:', track);
      track.stop();
    });
  }
  
  if (isScreenSharing.value) {
    isScreenSharing.value = false;
  }
  
  if (isVideoFileSharing.value) {
    stopVideoFileSharing();
  }
  
  showShareMenu.value = false;
  setVideoFromLocalCamera(false);
}

function toggleShareMenu() {
  // If already sharing, stop immediately
  if (isScreenSharing.value || isVideoFileSharing.value) {
    stopSharing();
    return;
  }
  
  // Otherwise show the menu
  showShareMenu.value = !showShareMenu.value;
}

function handleShareScreen() {
  isScreenSharing.value = true;
  isVideoFileSharing.value = false;
  showShareMenu.value = false;
  setVideoFromLocalCamera(true);
}

function handleShareFromFile() {
  showShareMenu.value = false;
  const input = document.createElement('input');
  input.type = 'file';
  input.accept = 'video/*';
  input.onchange = async (e: Event) => {
    const file = (e.target as HTMLInputElement).files?.[0];
    if (!file) return;
    
    await shareVideoFile(file);
  };
  input.click();
}

async function shareVideoFile(file: File) {
  try {
    // Stop current screen sharing if active
    if (isScreenSharing.value) {
      isScreenSharing.value = false;
    }
    
    // Create video element for file playback
    if (!videoFileElement) {
      videoFileElement = document.createElement('video');
      videoFileElement.loop = true;
      videoFileElement.muted = true;
    }
    
    // Load the video file
    const url = URL.createObjectURL(file);
    videoFileElement.src = url;
    videoFileElement.play();
    
    // Wait for video to be ready
    await new Promise((resolve) => {
      videoFileElement!.onloadedmetadata = resolve;
    });
    
    // Capture stream from video element
    // @ts-ignore - captureStream exists on HTMLVideoElement
    videoFileStream = videoFileElement.captureStream();
    
    if (!videoFileStream) {
      console.error('Failed to capture stream from video file');
      return;
    }
    
    // Update local video display
    const localVideoElement = document.getElementById('local') as HTMLVideoElement;
    localVideoElement.srcObject = videoFileStream;
    
    // Replace video track in peer connection
    if (peerConnection) {
      const videoTrack = videoFileStream.getVideoTracks()[0];
      const senders = peerConnection.getSenders();
      const videoSender = senders.find(sender => sender.track?.kind === 'video');
      if (videoSender) {
        await videoSender.replaceTrack(videoTrack);
      }
    }
    
    isVideoFileSharing.value = true;
    isScreenSharing.value = false;
    
    // Update dimensions when video metadata loads
    videoFileElement.onloadedmetadata = () => {
      const videoWidth = videoFileElement!.videoWidth;
      const videoHeight = videoFileElement!.videoHeight;
      const aspectRatio = videoWidth / videoHeight;

      const baseHeight = 160;
      localVideoHeight.value = baseHeight;
      localVideoWidth.value = Math.round(baseHeight * aspectRatio);
      localVideoX.value = window.innerWidth - localVideoWidth.value - 16;
    };
    
  } catch (error) {
    console.error('Error sharing video file:', error);
  }
}

function stopVideoFileSharing() {
  if (videoFileElement) {
    videoFileElement.pause();
    videoFileElement.src = '';
  }
  
  if (videoFileStream) {
    videoFileStream.getTracks().forEach(track => track.stop());
    videoFileStream = null;
  }
  
  isVideoFileSharing.value = false;
}

function toggleMessageSheet() {
  if (!dataChannelReady.value && peersConnected.value) {
    openDataChannel();
  } else {
    showMessageSheet.value = !showMessageSheet.value;
    if (showMessageSheet.value) {
      setTimeout(() => {
        const container = document.querySelector('.messages-container');
        if (container) {
          container.scrollTop = container.scrollHeight;
        }
      }, 100);
    }
  }
}

function formatTime(timestamp: number): string {
  const date = new Date(timestamp);
  return date.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
}

// Draggable local video functions
function handleDragStart(e: MouseEvent | TouchEvent) {
  isDragging = true;
  const clientX = e instanceof MouseEvent ? e.clientX : e.touches[0].clientX;
  const clientY = e instanceof MouseEvent ? e.clientY : e.touches[0].clientY;
  startX = clientX - localVideoX.value;
  startY = clientY - localVideoY.value;
}

function handleDragMove(e: MouseEvent | TouchEvent) {
  if (!isDragging) return;
  e.preventDefault();

  const clientX = e instanceof MouseEvent ? e.clientX : e.touches[0].clientX;
  const clientY = e instanceof MouseEvent ? e.clientY : e.touches[0].clientY;

  const localVideo = document.getElementById('local-video-container');
  const topBar = document.querySelector('.top-bar');
  const bottomControls = document.querySelector('.bottom-controls');

  if (!localVideo || !topBar || !bottomControls) return;

  const topBarBottom = topBar.getBoundingClientRect().bottom;
  const bottomControlsTop = bottomControls.getBoundingClientRect().top;

  let newX = clientX - startX;
  let newY = clientY - startY;

  // Constrain X
  const maxX = window.innerWidth - localVideo.offsetWidth;
  newX = Math.max(0, Math.min(newX, maxX));

  // Constrain Y between top bar and bottom controls
  newY = Math.max(topBarBottom, Math.min(newY, bottomControlsTop - localVideo.offsetHeight));

  localVideoX.value = newX;
  localVideoY.value = newY;
}

function handleDragEnd() {
  if (!isDragging) return;
  isDragging = false;

  // Snap to nearest edge (left or right)
  const localVideo = document.getElementById('local-video-container');
  if (!localVideo) return;

  const centerX = localVideoX.value + localVideo.offsetWidth / 2;
  const screenCenter = window.innerWidth / 2;

  // Determine which edge is closer
  const targetX = centerX < screenCenter ? 16 : window.innerWidth - localVideo.offsetWidth - 16;

  // Smooth animation to edge
  const startX = localVideoX.value;
  const distance = targetX - startX;
  const duration = 300; // ms
  const startTime = performance.now();

  const animate = (currentTime: number) => {
    const elapsed = currentTime - startTime;
    const progress = Math.min(elapsed / duration, 1);

    // Ease-out cubic for smooth deceleration
    const easeProgress = 1 - Math.pow(1 - progress, 3);

    localVideoX.value = startX + distance * easeProgress;

    if (progress < 1) {
      requestAnimationFrame(animate);
    }
  };

  requestAnimationFrame(animate);
}

// E2EE
async function generateEncryptionKey() {
  encryptionKey = await window.crypto.subtle.generateKey(
    { name: "AES-GCM", length: 256 },
    true,
    ["encrypt", "decrypt"]
  );

  if (shouldSendEncryptionKey) {
    const exportedKey = await window.crypto.subtle.exportKey("raw", encryptionKey);
    socket!.emit('send encryption key', { roomId: roomId.value, encryptionKey: exportedKey });
  }
}

function generateEncryptionKeyUsingWorker() {
  encryptionWorker?.postMessage({ action: 'generateKey' });
}

function updateRemoteVideoDimensions() {
  const remoteVideo = document.getElementById('remote') as HTMLVideoElement;
  if (!remoteVideo || !remoteVideo.videoWidth) return;

  const videoWidth = remoteVideo.videoWidth;
  const videoHeight = remoteVideo.videoHeight;
  const aspectRatio = videoWidth / videoHeight;

  if (aspectRatio > 1) {
    // Landscape video
    remoteVideoStyle.value = {
      width: '100%',
      height: 'auto',
      objectFit: 'contain'
    };
  } else {
    // Portrait video
    remoteVideoStyle.value = {
      width: 'auto',
      height: '100%',
      objectFit: 'contain'
    };
  }
}

// Background Effect Functions
async function initializeBackgroundEffect() {
  try {
    // Load background image
    backgroundImage = new Image();
    backgroundImage.src = '/virtual_background.jpg';
    await new Promise((resolve, reject) => {
      backgroundImage!.onload = resolve;
      backgroundImage!.onerror = reject;
    });

    // Initialize MediaPipe Image Segmenter
    const vision = await FilesetResolver.forVisionTasks(
      'https://cdn.jsdelivr.net/npm/@mediapipe/tasks-vision@latest/wasm'
    );
    
    imageSegmenter = await ImageSegmenter.createFromOptions(vision, {
      baseOptions: {
        modelAssetPath: 'https://storage.googleapis.com/mediapipe-models/image_segmenter/selfie_segmenter/float16/latest/selfie_segmenter.tflite',
        delegate: 'GPU'
      },
      runningMode: 'VIDEO',
      outputCategoryMask: true,
      outputConfidenceMasks: false,
    });

    console.log('Background effect initialized');
  } catch (error) {
    console.error('Failed to initialize background effect:', error);
  }
}

async function toggleBackgroundEffect() {
  if (!isBackgroundEffectEnabled.value) {
    // Enable background effect
    if (!imageSegmenter) {
      await initializeBackgroundEffect();
    }
    
    if (imageSegmenter && localStream) {
      startBackgroundProcessing();
      isBackgroundEffectEnabled.value = true;
    }
  } else {
    // Disable background effect
    stopBackgroundProcessing();
    isBackgroundEffectEnabled.value = false;
  }
}

function startBackgroundProcessing() {
  if (!localStream) return;

  // Store original camera stream
  originalCameraStream = localStream;

  // Create hidden video element for the original camera stream (for segmentation)
  if (!hiddenVideoElement) {
    hiddenVideoElement = document.createElement('video');
    hiddenVideoElement.autoplay = true;
    hiddenVideoElement.playsInline = true;
    hiddenVideoElement.muted = true;

    // // Do not use display:none as it may prevent video from playing
    hiddenVideoElement.style.position = 'absolute';
    hiddenVideoElement.style.width = '1px';
    hiddenVideoElement.style.height = '1px';
    hiddenVideoElement.style.opacity = '0';
    hiddenVideoElement.style.pointerEvents = 'none';
    document.body.appendChild(hiddenVideoElement);
  }
  
  hiddenVideoElement.srcObject = originalCameraStream;

  // Create canvas for processing
  if (!backgroundCanvas) {
    backgroundCanvas = document.createElement('canvas');
    backgroundCtx = backgroundCanvas.getContext('2d', { willReadFrequently: false });
  }

  // Wait for hidden video to be ready
  hiddenVideoElement.onloadedmetadata = () => {
    if (!hiddenVideoElement) return;

    const processFrame = () => {
      if (!isBackgroundEffectEnabled.value || !imageSegmenter || !backgroundCtx || !backgroundCanvas || !hiddenVideoElement) {
        return;
      }

      const width = hiddenVideoElement.videoWidth;
      const height = hiddenVideoElement.videoHeight;

      if (width === 0 || height === 0) {
        animationFrameId = requestAnimationFrame(processFrame);
        return;
      }

      // Set canvas size to match video
      if (backgroundCanvas.width !== width || backgroundCanvas.height !== height) {
        backgroundCanvas.width = width;
        backgroundCanvas.height = height;
      }

      // Segment the image
      const startTimeMs = performance.now();
      imageSegmenter.segmentForVideo(hiddenVideoElement, startTimeMs, (result) => {
        if (!backgroundCtx || !backgroundCanvas || !backgroundImage || !hiddenVideoElement) return;
        if (!result.categoryMask) return;

        const mask = result.categoryMask.getAsUint8Array();
        
        // Get video frame
        backgroundCtx.drawImage(hiddenVideoElement, 0, 0, width, height);
        const videoImageData = backgroundCtx.getImageData(0, 0, width, height);
        
        // Get background image
        backgroundCtx.drawImage(backgroundImage, 0, 0, width, height);
        const bgImageData = backgroundCtx.getImageData(0, 0, width, height);
        
        // Create blended output
        const outputImageData = backgroundCtx.createImageData(width, height);
        
        // Blend: mask value 0 = person (use video), 1 = background (use bg image)
        for (let i = 0; i < mask.length; i++) {
          const offset = i * 4;
          
          if (mask[i] === 0) {
            // Person pixel - use video
            outputImageData.data[offset] = videoImageData.data[offset];
            outputImageData.data[offset + 1] = videoImageData.data[offset + 1];
            outputImageData.data[offset + 2] = videoImageData.data[offset + 2];
            outputImageData.data[offset + 3] = 255;
          } else {
            // Background pixel - use background image
            outputImageData.data[offset] = bgImageData.data[offset];
            outputImageData.data[offset + 1] = bgImageData.data[offset + 1];
            outputImageData.data[offset + 2] = bgImageData.data[offset + 2];
            outputImageData.data[offset + 3] = 255;
          }
        }
        
        backgroundCtx.putImageData(outputImageData, 0, 0);
      });

      animationFrameId = requestAnimationFrame(processFrame);
    };

    // Start processing
    processFrame();

    // Create stream from canvas
    // @ts-ignore
    processedStream = backgroundCanvas.captureStream(30);
    const videoTrack = processedStream.getVideoTracks()[0];
    
    // Replace video track in peer connection
    if (peerConnection) {
      const senders = peerConnection.getSenders();
      const videoSender = senders.find(sender => sender.track?.kind === 'video');
      if (videoSender) {
        videoSender.replaceTrack(videoTrack);
      }
    }

    // Display the processed canvas in the local video element
    const localVideoElement = document.getElementById('local') as HTMLVideoElement;
    if (localVideoElement) {
      localVideoElement.srcObject = processedStream;
    }
  };
}

function stopBackgroundProcessing() {
  if (animationFrameId !== null) {
    cancelAnimationFrame(animationFrameId);
    animationFrameId = null;
  }

  if (processedStream) {
    processedStream.getTracks().forEach(track => track.stop());
    processedStream = null;
  }

  // Remove hidden video element
  if (hiddenVideoElement) {
    hiddenVideoElement.srcObject = null;
    if (hiddenVideoElement.parentNode) {
      hiddenVideoElement.parentNode.removeChild(hiddenVideoElement);
    }
    hiddenVideoElement = null;
  }

  // Restore original video stream to local video element
  const videoElement = document.getElementById('local') as HTMLVideoElement;
  if (videoElement && originalCameraStream) {
    videoElement.srcObject = originalCameraStream;
    
    // Restore original track in peer connection
    if (peerConnection) {
      const videoTrack = originalCameraStream.getVideoTracks()[0];
      const senders = peerConnection.getSenders();
      const videoSender = senders.find(sender => sender.track?.kind === 'video');
      if (videoSender) {
        videoSender.replaceTrack(videoTrack);
      }
    }
  }

  originalCameraStream = null;
}
</script>

<template>
  <!-- Join Room Screen -->
  <div v-if="!isInRoom"
    class="fixed inset-0 bg-gradient-to-br from-gray-900 via-gray-800 to-gray-900 flex items-center justify-center px-6">
    <div
      class="w-full max-w-sm glass-panel border border-white/10 rounded-3xl p-8 shadow-2xl flex flex-col items-center gap-8">
      <div class="flex flex-col items-center text-center gap-2">
        <div
          class="w-16 h-16 rounded-full bg-blue-500/20 flex items-center justify-center mb-2 ring-1 ring-white/10 shadow-lg shadow-blue-500/10">
          <Video :size="32" class="text-blue-500" />
        </div>
        <h1 class="text-white text-2xl font-bold tracking-tight">Join Meeting</h1>
        <p class="text-white/60 text-sm">Enter a room number to join an existing call or start a random one.</p>
      </div>

      <div class="w-full space-y-5">
        <div class="relative group">
          <input v-model="roomId" @keyup.enter="joinRoom"
            class="block w-full rounded-xl border-0 bg-black/20 py-4 px-4 text-white text-lg placeholder:text-white/20 ring-1 ring-inset ring-white/10 focus:ring-2 focus:ring-inset focus:ring-blue-500 focus:bg-black/30 transition-all text-center tracking-widest font-semibold shadow-inner"
            inputmode="numeric" pattern="[0-9]*" placeholder="Room Number" type="text" />
        </div>

        <!-- E2EE Checkbox -->
        <label class="flex items-center gap-3 px-2 py-1 cursor-pointer group">
          <input v-model="enableE2EE" type="checkbox"
            class="w-5 h-5 rounded border-2 border-white/20 bg-black/20 text-blue-500 focus:ring-2 focus:ring-blue-500 cursor-pointer" />
          <div class="flex-1">
            <span class="text-white text-sm font-medium">Enable End-to-End Encryption</span>
            <p class="text-white/40 text-xs mt-0.5">Encrypt video and audio streams (Web-to-Web only)</p>
          </div>
        </label>

        <div class="flex flex-col gap-3">
          <button @click="joinRoom" :disabled="!roomId"
            class="w-full bg-blue-500 hover:bg-blue-600 disabled:bg-gray-600 disabled:cursor-not-allowed text-white font-bold py-3.5 rounded-xl shadow-lg shadow-blue-500/25 transition-all transform active:scale-95 flex items-center justify-center gap-2 cursor-pointer">
            <span>Join Room</span>
            <ArrowRight :size="16" />
          </button>

          <div class="relative flex items-center py-1">
            <div class="flex-grow border-t border-white/10"></div>
            <span class="flex-shrink-0 mx-3 text-white/30 text-[10px] uppercase tracking-wider font-semibold">Or</span>
            <div class="flex-grow border-t border-white/10"></div>
          </div>

          <button @click="generateRandomId"
            class="w-full bg-white/5 hover:bg-white/10 border border-white/10 text-white font-medium py-3.5 rounded-xl transition-all flex items-center justify-center gap-2 group active:scale-95 cursor-pointer">
            <Shuffle :size="20" class="text-white/60 group-hover:text-white transition-colors" />
            <span>Join Random Room</span>
          </button>
        </div>
      </div>
    </div>

    <div class="absolute bottom-8">
      <p class="text-white/20 text-xs font-medium">WebRTC Demo App</p>
    </div>
  </div>

  <!-- Call Screen -->
  <div v-else class="fixed inset-0 bg-gray-900 overflow-hidden">
    <!-- Remote Video (Full Screen) -->
    <div class="absolute inset-0 bg-gray-800 flex items-center justify-center">
      <video id="remote" :style="remoteVideoStyle" class="max-w-full max-h-full" playsinline autoplay></video>
      
      <!-- Remote Mute Indicators -->
      <div class="absolute top-4 left-4 flex flex-col gap-2 z-10">
        <!-- Locally muted remote -->
        <div v-if="!isRemoteAudioEnabled"
          class="bg-yellow-500/90 p-2 rounded-lg flex items-center gap-2 shadow-lg backdrop-blur-sm">
          <VolumeX :size="16" class="text-white" />
          <span class="text-white text-xs font-medium">Muted by You</span>
        </div>
      </div>
      
      <div class="absolute inset-0 bg-gradient-to-b from-black/60 via-transparent to-black/80 pointer-events-none">
      </div>
    </div>

    <!-- Top Bar -->
    <div class="relative z-20 pt-12 px-4 pb-4 flex items-start justify-between top-bar">
      <div class="w-10 h-10"></div>

      <div class="flex flex-col items-center flex-1">
        <h2 class="text-white text-lg font-bold leading-tight tracking-tight drop-shadow-md">
          Room: {{ roomId }}
        </h2>
        <div class="flex items-center gap-2 mt-1">
          <span v-if="peersConnected" class="w-2 h-2 rounded-full bg-green-500 animate-pulse"></span>
          <p class="text-white/80 text-xs font-medium tracking-wide drop-shadow-sm">
            {{ connectionStatus }}
          </p>
        </div>
      </div>
    </div>

    <!-- Message Overlay -->
    <div v-if="messages.length > 0"
      class="absolute left-4 top-32 bottom-72 z-10 w-72 overflow-y-auto space-y-2 pointer-events-none">
      <div v-for="(msg, idx) in messages.slice(-5)" :key="idx"
        class="bg-black/50 backdrop-blur-sm rounded-lg p-3 shadow-lg pointer-events-auto opacity-70">
        <div class="flex items-start gap-2">
          <div class="flex-1 min-w-0">
            <p class="text-white/60 text-xs font-bold mb-0.5">{{ msg.sender }}</p>
            <p class="text-white text-sm break-words">{{ msg.text }}</p>
            <p class="text-white/60 text-[10px] mt-1">{{ formatTime(msg.timestamp) }}</p>
          </div>
        </div>
      </div>
    </div>

    <!-- Local Video Preview -->
    <div id="local-video-container" @mousedown="handleDragStart" @touchstart="handleDragStart" :style="{
      left: localVideoX + 'px',
      top: localVideoY + 'px',
      width: localVideoWidth + 'px',
      height: localVideoHeight + 'px'
    }"
      class="fixed rounded-xl overflow-hidden border-2 border-white/20 shadow-2xl z-10 bg-gray-700 cursor-move touch-none">
      <video id="local" class="w-full h-full object-cover pointer-events-none" playsinline autoplay muted></video>
      <div v-if="!isAudioEnabled"
        class="absolute bottom-2 right-2 bg-red-500/90 p-1 rounded-full flex items-center justify-center shadow-sm">
        <MicOff :size="12" class="text-white" />
      </div>
    </div>

    <!-- Bottom Controls -->
    <div class="absolute bottom-0 left-0 right-0 z-30 flex flex-col items-center pb-8 px-4 w-full bottom-controls">
      <!-- Secondary Controls -->
      <div class="flex items-center justify-center gap-8 w-full max-w-sm px-4 mb-6">
        <button @click="toggleMessageSheet" class="cursor-pointer flex flex-col items-center gap-2 group">
          <div
            class="flex items-center justify-center w-12 h-12 rounded-full glass-panel hover:bg-white/10 transition-colors text-white border border-white/5 shadow-sm relative">
            <MessageCircle v-if="dataChannelReady" :size="20" />
            <AlertCircle v-else :size="20" />
            <div v-if="messages.length > 0 && !showMessageSheet"
              class="absolute -top-1 -right-1 w-5 h-5 bg-red-500 rounded-full text-white text-[10px] font-bold flex items-center justify-center">
              {{ messages.length > 9 ? '9+' : messages.length }}
            </div>
          </div>
          <span class="text-[10px] font-medium text-white/80">Chat</span>
        </button>

        <div class="relative flex flex-col items-center gap-2 group">
          <button @click="toggleShareMenu" class="cursor-pointer flex flex-col items-center gap-2 group">
            <div
              class="flex items-center justify-center w-12 h-12 rounded-full glass-panel hover:bg-white/10 transition-colors text-white border border-white/5 shadow-sm relative">
              <MonitorX v-if="isScreenSharing || isVideoFileSharing" :size="20" />
              <MonitorUp v-else :size="20" />
            </div>
            <span class="text-[10px] font-medium text-white/80">Share</span>
          </button>

          <!-- Share Dropdown Menu -->
          <Transition name="fade-scale">
            <div v-if="showShareMenu" @click.stop
              class="absolute bottom-full mb-2 right-0 bg-gray-800/95 backdrop-blur-md rounded-xl shadow-2xl border border-white/10 overflow-hidden min-w-[180px]">
              <button @click="handleShareScreen"
                class="w-full px-4 py-3 hover:bg-white/10 transition-colors flex items-center gap-3 text-white text-sm font-medium">
                <MonitorUp :size="18" />
                <span>Share Screen</span>
              </button>
              <div class="h-px bg-white/10"></div>
              <button @click="handleShareFromFile"
                class="w-full px-4 py-3 hover:bg-white/10 transition-colors flex items-center gap-3 text-white text-sm font-medium">
                <FileVideo :size="18" />
                <span>Share from File</span>
              </button>
            </div>
          </Transition>
        </div>

        <button @click="muteRemote" class="cursor-pointer flex flex-col items-center gap-2 group">
          <div
            class="flex items-center justify-center w-12 h-12 rounded-full glass-panel hover:bg-white/10 transition-colors text-white border border-white/5 shadow-sm">
            <Volume2 v-if="isRemoteAudioEnabled" :size="20" />
            <VolumeX v-else :size="20" />
          </div>
          <span class="text-[10px] font-medium text-white/80">Mute Remote</span>
        </button>

        <button @click="toggleBackgroundEffect" class="cursor-pointer flex flex-col items-center gap-2 group">
          <div
            :class="[
              'flex items-center justify-center w-12 h-12 rounded-full glass-panel hover:bg-white/10 transition-colors text-white border border-white/5 shadow-sm',
              isBackgroundEffectEnabled ? '!bg-blue-500/50 !border-blue-400/50' : ''
            ]">
            <Wallpaper :size="20" />
          </div>
          <span class="text-[10px] font-medium text-white/80">Background</span>
        </button>
      </div>

      <!-- Primary Controls -->
      <div
        class="flex items-center justify-around w-full max-w-xs px-2 py-4 rounded-3xl glass-panel shadow-lg border border-white/5">
        <button @click="toggleAudio" class="cursor-pointer flex flex-col items-center justify-center gap-1 group w-20">
          <div
            class="flex items-center justify-center w-14 h-14 rounded-full bg-white/10 group-hover:bg-white/20 transition-all text-white">
            <Mic v-if="isAudioEnabled" :size="28" />
            <MicOff v-else :size="28" />
          </div>
          <span class="text-[10px] font-medium text-white/70">Mute</span>
        </button>

        <button @click="leaveRoom"
          class="cursor-pointer flex flex-col items-center justify-center gap-1 group w-20 -mt-2">
          <div
            class="flex items-center justify-center w-16 h-16 rounded-full bg-red-500 hover:bg-red-600 shadow-red-500/30 shadow-lg transition-all text-white transform hover:scale-105 active:scale-95">
            <PhoneOff :size="32" />
          </div>
          <span class="text-[10px] font-bold text-white mt-1">End</span>
        </button>

        <button @click="toggleVideo" class="cursor-pointer flex flex-col items-center justify-center gap-1 group w-20">
          <div
            class="flex items-center justify-center w-14 h-14 rounded-full bg-white/10 group-hover:bg-white/20 transition-all text-white">
            <Video v-if="isVideoEnabled" :size="28" />
            <VideoOff v-else :size="28" />
          </div>
          <span class="text-[10px] font-medium text-white/70">Video</span>
        </button>
      </div>
    </div>

    <!-- Message Bottom Sheet -->
    <Transition name="slide-up">
      <div v-if="showMessageSheet" class="absolute inset-0 z-40 flex items-end">
        <div @click="showMessageSheet = false" class="absolute inset-0 bg-black/50"></div>
        <div class="relative w-full bg-gray-800 rounded-t-3xl shadow-2xl max-h-[80vh] flex flex-col">
          <!-- Handle -->
          <div class="flex justify-center pt-3 pb-2">
            <div class="w-12 h-1.5 bg-gray-600 rounded-full"></div>
          </div>

          <!-- Header -->
          <div class="px-6 py-3 border-b border-gray-700">
            <h3 class="text-white font-bold text-lg">Messages</h3>
          </div>

          <!-- Messages -->
          <div class="flex-1 overflow-y-auto px-6 py-4 messages-container">
            <div v-if="messages.length === 0" class="text-center text-gray-500 py-8">
              No messages yet
            </div>
            <div v-else class="space-y-3">
              <div v-for="(msg, idx) in messages" :key="idx" class="flex flex-col">
                <div class="bg-gray-700/80 rounded-lg p-3 max-w-md">
                  <p class="text-white/60 text-xs font-bold mb-1">{{ msg.sender }}</p>
                  <p class="text-white text-sm">{{ msg.text }}</p>
                  <p class="text-white/40 text-[10px] mt-1 text-right">{{ formatTime(msg.timestamp) }}</p>
                </div>
              </div>
            </div>
          </div>

          <!-- Input -->
          <div class="px-6 py-4 border-t border-gray-700 bg-gray-800">
            <div class="flex items-center gap-2">
              <input v-model="message" @keyup.enter="sendMessage" type="text" placeholder="Type a message..."
                class="flex-1 bg-gray-700 text-white rounded-full px-4 py-3 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500" />
              <button @click="sendMessage" :disabled="!message.trim()"
                class="w-12 h-12 rounded-full bg-blue-500 hover:bg-blue-600 disabled:bg-gray-600 disabled:cursor-not-allowed flex items-center justify-center text-white transition-all transform active:scale-95">
                <Send :size="20" />
              </button>
            </div>
          </div>
        </div>
      </div>
    </Transition>
  </div>
</template>

<style scoped>
.glass-panel {
  background: rgba(16, 25, 34, 0.75);
  backdrop-filter: blur(12px);
  -webkit-backdrop-filter: blur(12px);
}

.slide-up-enter-active,
.slide-up-leave-active {
  transition: all 0.3s cubic-bezier(0.4, 0, 0.2, 1);
}

.slide-up-enter-from,
.slide-up-leave-to {
  transform: translateY(100%);
  opacity: 0;
}

.slide-up-enter-to,
.slide-up-leave-from {
  transform: translateY(0);
  opacity: 1;
}

.messages-container::-webkit-scrollbar {
  width: 6px;
}

.messages-container::-webkit-scrollbar-track {
  background: rgba(0, 0, 0, 0.1);
  border-radius: 3px;
}

.messages-container::-webkit-scrollbar-thumb {
  background: rgba(255, 255, 255, 0.2);
  border-radius: 3px;
}

.messages-container::-webkit-scrollbar-thumb:hover {
  background: rgba(255, 255, 255, 0.3);
}

.fade-scale-enter-active,
.fade-scale-leave-active {
  transition: all 0.2s cubic-bezier(0.4, 0, 0.2, 1);
}

.fade-scale-enter-from,
.fade-scale-leave-to {
  opacity: 0;
  transform: translateY(8px) scale(0.95);
}

.fade-scale-enter-to,
.fade-scale-leave-from {
  opacity: 1;
  transform: translateY(0) scale(1);
}
</style>
