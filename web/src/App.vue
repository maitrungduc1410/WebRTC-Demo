<script script setup lang="ts">
import io, { Socket } from 'socket.io-client'
import { onBeforeMount, ref } from 'vue';
import { encryptStream, decryptStream } from "./e2ee";

const BASE_URL = 'http://localhost:4000'
const roomId = ref<number | undefined>(undefined)
const isInRoom = ref(false)
const message = ref('')
const messages = ref<string[]>([])
const dataChannelReady = ref(false)
const peersConnected = ref(false)

let peerConnection: RTCPeerConnection | null = null
let localStream: MediaStream | null = null
let remoteStream: MediaStream | null = null
let socket: Socket | null = null
let dataChannel: RTCDataChannel | null = null
let isScreenSharing = false;

const enableE2EE = false;
let encryptionKey: CryptoKey;
// For debugging purpose: this shouldSendEncryptionKey flag is use to verify whether end2end encryption is working
// if true -> send encryption key to remote peer, both peers will encrypt/decrypt the other peer's stream
// if false -> streams from initiator (who joins room first) will be encrypted, remote peer doesn't have encryption key and hence can't see initiator's stream
const shouldSendEncryptionKey = true;

const useEncryptionWorker = true;
let encryptionWorker: Worker | undefined = undefined
if (enableE2EE && useEncryptionWorker) {
  encryptionWorker = new Worker(new URL('./encryptionWorker.ts', import.meta.url), {
    type: 'module'
  });
}

onBeforeMount(() => {
  socket = io(BASE_URL)
  socket.on('connect', () => {
    console.log('Socket connected')
  })

  generateRandomId()

  socket.on('new user joined', async () => {
    console.log(1111, 'new user joined')

    if (enableE2EE) {
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
    console.log(2222, 'offer', data.offer)

    if (!peerConnection) {
      // no need to recreate peer connection if we already set
      // can happen in case of creating data channel where remote peer sends another offer/answer
      peerConnection = new RTCPeerConnection({
        iceServers: [{ urls: 'stun:stun.l.google.com:19302' },]
      })

      initPeerEvents() // this line MUST BE ON TOP of setRemoteDescription (see function to get detail)
    }

    await peerConnection!.setRemoteDescription(new RTCSessionDescription(data.offer))

    const answer = await peerConnection!.createAnswer()
    await peerConnection!.setLocalDescription(answer)
    socket!.emit('answer', { answer, roomId: roomId.value })
  })

  socket.on('answer', async data => {
    console.log(1111, data.answer)
    const remoteDesc = new RTCSessionDescription(data.answer)
    await peerConnection!.setRemoteDescription(remoteDesc)
  })

  socket.on('new ice candidate', async data => {
    await peerConnection!.addIceCandidate(data.iceCandidate)
  })

  if (enableE2EE) {
    socket.on('receive encryption key', async (data: { encryptionKey: ArrayBuffer }) => {
      console.log('Received encryption key:', data.encryptionKey);

      if (useEncryptionWorker) {
        encryptionWorker?.postMessage({
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
  }
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
  console.log('new user joined', offer)
  socket!.emit('offer', { offer, roomId: roomId.value })
}

async function setVideoFromLocalCamera(useScreenShare = false) {
  try {
    let stream;
    if (useScreenShare) {
      stream = await navigator.mediaDevices.getDisplayMedia({ video: true, audio: true });
    } else {
      const constraints = { video: true, audio: true };
      stream = await navigator.mediaDevices.getUserMedia(constraints);
    }

    const videoElement = document.getElementById('local') as HTMLVideoElement;
    videoElement.srcObject = stream;

    localStream = stream;

    // Replace video tracks in peer connection to switch between camera and screen

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
  console.log('setVideoFromRemoteStream', remoteStream)
  remoteStream = new MediaStream()
  const remoteVideo = document.getElementById('remote') as HTMLVideoElement
  remoteVideo.srcObject = remoteStream
}
function generateRandomId() {
  roomId.value = Math.floor(Math.random() * 999999)
}
async function joinRoom() {
  await setVideoFromLocalCamera()
  setVideoFromRemoteStream()

  socket!.emit('join room', { roomId: roomId.value })
  isInRoom.value = true
}
function leaveRoom() {
  socket!.emit('leave room', { roomId: roomId.value })

  onDisconnected()

  localStream!.getTracks().forEach((track) => { // stop local camera
    track.stop();
  });

  isInRoom.value = false
  message.value = ''
}
function initPeerEvents() {
  localStream!.getTracks().forEach(track => {
    console.log('Local track added', track)
    peerConnection!.addTrack(track, localStream!)
  })

  peerConnection!.onicecandidate = event => {
    console.log('Ice candidate: ', event.candidate)
    if (event.candidate) {
      11111
      socket!.emit('new ice candidate', { iceCandidate: event.candidate, roomId: roomId.value })
    }
  }

  // on browser this event may fire few seconds after remote disconnected
  // meanwhile on mobile it fires immediately
  // so if we test quit+rejoin a room on mobile, give browser sometime or else it shows nothing
  peerConnection!.oniceconnectionstatechange = event => {
    console.log('oniceconnectionstatechange: ', event)
  }

  peerConnection!.onicecandidateerror = event => {
    console.log('onicecandidateerror: ', event)
  }
  peerConnection!.onicegatheringstatechange = event => {
    console.log('onicegatheringstatechange: ', event)
  }
  peerConnection!.onsignalingstatechange = event => {
    console.log('onsignalingstatechange: ', event)
  }

  peerConnection!.onconnectionstatechange = event => {
    console.log('Connection state change: ', peerConnection!.connectionState)
    if (peerConnection!.connectionState === 'connected') {
      console.log('Peers connected')
      console.log(event)
      console.log(peerConnection)

      peersConnected.value = true
    } else if (peerConnection!.connectionState === 'disconnected') {
      console.log('disconnected')
      onDisconnected()
    }
  }

  peerConnection!.ontrack = (event) => { // ontrack MUST BE ON TOP of setRemoteDescription otherwise it won't fire
    console.log('Remote track added', event)


    // remove existing tracks (if any)
    // this can happen in the case remote updates its tracks (like switching from camera to screen sharing)
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
    console.log('ondatachannel', event)

    dataChannel = event.channel

    initDataChannelEvents()
  };

  if (enableE2EE) {
    peerConnection!.getSenders().forEach(async sender => {
      console.log('sender: ', sender)
      if (sender.track?.kind === 'video' || sender.track?.kind === 'audio') {
        // @ts-ignore
        const senderStreams = sender.createEncodedStreams();
        console.log('senderStreams', senderStreams)
        const readable = senderStreams.readable;
        const writable = senderStreams.writable;

        if (useEncryptionWorker) {
          // E2EE using Worker thread
          console.log('useEncryptionWorker', 1111)

          encryptionWorker?.postMessage({
            action: 'encrypt',
            readable,
            writable
          }, [readable, writable]);
        } else {
          // E2EE using Main thread
          await encryptStream(encryptionKey, readable, writable);
        }
      }
    });

    peerConnection!.getReceivers().forEach(async receiver => {
      console.log('receiver: ', receiver)
      if (receiver.track.kind === 'video' || receiver.track.kind === 'audio') {
        // @ts-ignore
        const receiverStreams = receiver.createEncodedStreams();
        const readable = receiverStreams.readable;
        const writable = receiverStreams.writable;

        if (useEncryptionWorker) {
          // E2EE using Worker thread
          encryptionWorker?.postMessage({
            action: 'decrypt',
            readable,
            writable,
            shouldSendEncryptionKey
          }, [readable, writable]);
        } else {
          // E2EE using Main thread
          // no need to decrypt remote stream if shouldSendEncryptionKey = false, instead just directly display it
          await decryptStream(shouldSendEncryptionKey ? encryptionKey : undefined, readable, writable);
        }
      }
    });
  }
}
async function openDataChannel() {
  dataChannel = peerConnection!.createDataChannel("MyApp Channel");
  console.log('Data channel created');

  // IMPORTANT: if we create data channel right after new RTCPeerConnection (before sending offer/answer) then the negotiation should be automatically
  // but here it's not, therefore we need to create offer and renegotiation

  // the initiator always creates offer (no matter who joins the room first)
  const offer = await peerConnection!.createOffer({
    offerToReceiveAudio: true,
    offerToReceiveVideo: true
  })
  await peerConnection!.setLocalDescription(offer)
  socket!.emit('offer', { offer, roomId: roomId.value })

  initDataChannelEvents()
}
function sendMessage() {
  if (dataChannelReady && dataChannel) {
    dataChannel!.send(message.value);
    message.value = ''
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

  setVideoFromRemoteStream() // after a peer is disconnected we need to reset the remote stream in the other peer, otherwise next peer connected we'll only see the black for remote peer
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
    const m = e.data;
    console.log('datachannel onmessage', m, e)
    messages.value.unshift(m)
  };
}

function toggleAudio() {
  if (localStream) {
    localStream.getAudioTracks().forEach(track => {
      track.enabled = !track.enabled
    })
  }
}

function toggleVideo() {
  if (localStream) {
    localStream.getVideoTracks().forEach(track => {
      track.enabled = !track.enabled
    })
  }
}

function muteRemote() {
  if (remoteStream) {
    remoteStream.getAudioTracks().forEach(track => {
      track.enabled = !track.enabled
    })
  }
}

function toggleScreenShare() {
  isScreenSharing = !isScreenSharing;
  setVideoFromLocalCamera(isScreenSharing);
}

// E2EE in Main thread
async function generateEncryptionKey() {
  // Generate a key for encryption/decryption using AES-GCM
  encryptionKey = await window.crypto.subtle.generateKey(
    {
      name: "AES-GCM",
      length: 256
    },
    true,
    ["encrypt", "decrypt"]
  );

  if (shouldSendEncryptionKey) {
    // Export the key to send over the signaling channel
    const exportedKey = await window.crypto.subtle.exportKey("raw", encryptionKey);
    socket!.emit('send encryption key', { roomId: roomId.value, encryptionKey: exportedKey });
  }
}

// E2EE in Worker thread

function generateEncryptionKeyUsingWorker() {
  console.log('generateEncryptionKeyUsingWorker')
  encryptionWorker?.postMessage({
    action: 'generateKey',
  });
}

if (encryptionWorker) {
  encryptionWorker.onmessage = async (event) => {
    const { action, key } = event.data;

    switch (action) {
      case "generatedKey":
        encryptionKey = key;
        if (shouldSendEncryptionKey) {
          // Export the key to send over the signaling channel
          const exportedKey = await window.crypto.subtle.exportKey("raw", encryptionKey);
          socket!.emit('send encryption key', { roomId: roomId.value, encryptionKey: exportedKey });
        }
        init()
        break;
    }
  };
}
</script>


<template>
  <div id="app">
    <div>
      <input v-model="roomId" type="text" placeholder="RoomId" name="" id="" :disabled="isInRoom">
    </div>
    <div class="action-btns" v-if="!isInRoom">
      <button @click="joinRoom">Join</button>
      <button @click="generateRandomId">Random</button>
    </div>
    <div class="action-btns" v-else>
      <div>
        <div>
          <button @click="leaveRoom">Leave room </button>
          <button @click="toggleAudio">Toggle Local Audio </button>
          <button @click="toggleVideo">Toggle Local Video </button>
          <button @click="muteRemote">Mute Remote Audio </button>
          <button @click="toggleScreenShare">Toggle Screen Share</button>
          <button v-if="!dataChannelReady && peersConnected" @click="openDataChannel">Open data channel </button>
        </div>
        <div class="send-message" v-if="dataChannelReady">
          <input placeholder="send message" v-model="message" />
          <button @click="sendMessage">Send</button>
        </div>
      </div>
      <div v-if="dataChannelReady" style="margin-left: 16px;">
        <b>Remote messages:</b>
        <div class="messages">
          <div v-for="msg in messages">{{ msg }}</div>
        </div>
      </div>
    </div>
    <div class="streams">
      <div class="stream-item">
        <h3>Local stream</h3>
        <video id="local" playsinline autoplay></video>
      </div>
      <div class="stream-item">
        <h3>Remote stream</h3>
        <video id="remote" playsinline autoplay></video>
      </div>
    </div>
  </div>
</template>

<style>
#app {
  font-family: Avenir, Helvetica, Arial, sans-serif;
  -webkit-font-smoothing: antialiased;
  -moz-osx-font-smoothing: grayscale;
  text-align: center;
  color: #2c3e50;
  margin-top: 60px;
}

video {
  border: dashed 1px #ddd;
  border-radius: 6px;
  margin: 0 10px;
}

.action-btns {
  padding: 20px 0;
  display: flex;
  justify-content: center;
}

.action-btns button {
  margin: 0 5px;
}

.streams {
  display: flex;
  justify-content: center;
}

.stream-item {
  max-width: 50%;
}

.stream-item video {
  width: 100%;
}

.send-message {
  margin-top: 8px;
}

.messages {
  height: 100px;
  overflow-y: scroll;
  text-align: left;

}
</style>
