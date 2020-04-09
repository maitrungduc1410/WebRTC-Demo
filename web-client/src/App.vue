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
      <button @click="leaveRoom">Leave room </button>
    </div>
    <div class="message" v-if="message.length">
      {{ message }}
    </div>
    <div class="streams">
      <div class="stream-item">
        <h3>Local stream</h3>
        <video id="local" playsinline autoplay muted></video>
      </div>
      <div class="stream-item">
        <h3>Remote stream</h3>
        <video id="remote" playsinline autoplay muted></video>
      </div>
    </div>
  </div>
</template>

<script>

export default {
  data () {
    return {
      peerConnection: null,
      peerConfig: {
        configuration: {
          offerToReceiveAudio: true,
          offerToReceiveVideo: true
        },
        iceServers: [{ urls: 'stun:stun.l.google.com:19302' }]
      },
      localStream: null,
      remoteStream: null,
      roomId: '',
      isInRoom: false,
      message: ''
    }
  },
  created() {
    this.generateRandomId()

    this.$root.socket.on('new user joined', async () => {
      this.peerConnection= new RTCPeerConnection(this.peerConfig)

      this.initPeerEvents()

      const offer = await this.peerConnection.createOffer()
      await this.peerConnection.setLocalDescription(offer)
      this.$root.socket.emit('offer', { offer, roomId: this.roomId })
    })

    this.$root.socket.on('offer', async data => {
      this.peerConnection= new RTCPeerConnection(this.peerConfig)

      this.initPeerEvents() // this line MUST BE ON TOP of setRemoteDescription (see function to get detail)

      await this.peerConnection.setRemoteDescription(new RTCSessionDescription(data.offer))
      const answer = await this.peerConnection.createAnswer()
      await this.peerConnection.setLocalDescription(answer)
      this.$root.socket.emit('answer', { answer, roomId: this.roomId })
    })

    this.$root.socket.on('answer', async data => {
      const remoteDesc = new RTCSessionDescription(data.answer)
      await this.peerConnection.setRemoteDescription(remoteDesc)
    })

    this.$root.socket.on('new ice candidate', async data => {
      await this.peerConnection.addIceCandidate(data.iceCandidate)
    })

    this.$root.socket.on('message', data => {
      this.message = data.message
    })
  },
  methods: {
    async setVideoFromLocalCamera () {
      try {
        const constraints = { video: true, audio: true }
        const stream = await navigator.mediaDevices.getUserMedia(constraints)
        const videoElement = document.getElementById('local')
        videoElement.srcObject = stream

        this.localStream = stream
      } catch (error) {
        console.error('Error opening video camera.', error)
      }
    },
    setVideoFromRemoteStream () {
      this.remoteStream = new MediaStream()
      const remoteVideo = document.getElementById('remote')
      remoteVideo.srcObject = this.remoteStream
    },
    generateRandomId () {
      this.roomId = Math.floor(Math.random() * 999999)
    },
    async joinRoom () {
      await this.setVideoFromLocalCamera()
      this.setVideoFromRemoteStream()

      this.$root.socket.emit('join room', { roomId: this.roomId })
      this.isInRoom = true
    },
    leaveRoom () {
      this.$root.socket.emit('leave room', { roomId: this.roomId })

      if (this.peerConnection) {
        this.peerConnection.close() // close peerConnection if connected
      }

      this.localStream.getTracks().forEach((track) => { // stop local camera
        track.stop();
      });

      this.setVideoFromRemoteStream() // reset remote stream

      this.isInRoom = false

      this.message = ''
    },
    initPeerEvents() {
      this.localStream.getTracks().forEach(track => {
        this.peerConnection.addTrack(track, this.localStream)
      })

      this.peerConnection.onicecandidate = event => {
        if (event.candidate) {
          // console.log('Ice candidate: ', event.candidate)
          this.$root.socket.emit('new ice candidate', { iceCandidate: event.candidate, roomId: this.roomId })
        }
      }

      this.peerConnection.onconnectionstatechange = event => {
        if (this.peerConnection.connectionState === 'connected') {
          console.log('Peers connected')
          console.log(event)
        } else if (this.peerConnection.connectionState === 'disconnected') {
          console.log('disconnected')
          this.peerConnection.close()

          this.setVideoFromRemoteStream() // after a peer is disconnected we need to reset the remote stream in the other peer, otherwise next peer connected we'll only see the black for remote peer
        }
      }

      this.peerConnection.ontrack = (event) => { // ontrack MUST BE ON TOP of setRemoteDescription otherwise it won't fire
        this.remoteStream.addTrack(event.track, this.remoteStream)
      }
    }
  }
}
</script>

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
}

.action-btns button {
  margin: 0 5px;
}

.streams {
  display: flex;
  justify-content: center;
}

.message {
  color: #721c24;
  background-color: #f8d7da;
  border-color: #f5c6cb;
  border: 1px solid transparent;
  border-radius: .25rem;
  padding: 10px 0;
}
</style>
