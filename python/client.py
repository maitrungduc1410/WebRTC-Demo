import asyncio
import json
import logging
import random
import cv2
import numpy as np
import socketio
import threading
import tkinter as tk
from tkinter import ttk, scrolledtext
import queue
import platform
from aiortc import (
    RTCPeerConnection,
    RTCSessionDescription,
    RTCIceCandidate,
    VideoStreamTrack,
    RTCConfiguration,
    RTCIceServer,
    MediaStreamTrack
)
import av
from av import VideoFrame
from PIL import Image, ImageTk


# Configure logging
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("webrtc-client")

import logging

# Configure logging to suppress PyAV messages
logging.getLogger('libav').setLevel(logging.ERROR)
logging.getLogger('libav.swscaler').setLevel(logging.ERROR)

# Or if that doesn't work, try setting all logging to a higher level except your app's logger
root_logger = logging.getLogger()
root_logger.setLevel(logging.ERROR)

# Then set only your app's logger back to INFO or DEBUG
app_logger = logging.getLogger("webrtc-client")
app_logger.setLevel(logging.INFO)

# Global variables
pc = None
local_video = None
remote_video_track = None
data_channel = None
room_id = None
last_offer_id = None

# GUI related globals
command_queue = queue.Queue()
log_queue = queue.Queue()
local_frame_queue = queue.Queue(maxsize=1)
remote_frame_queue = queue.Queue(maxsize=1)


# Custom handler to redirect logs to GUI
class QueueHandler(logging.Handler):
    def __init__(self, log_queue):
        super().__init__()
        self.log_queue = log_queue

    def emit(self, record):
        self.log_queue.put(self.format(record))


# Configure logger to use our custom handler
queue_handler = QueueHandler(log_queue)
queue_handler.setFormatter(logging.Formatter('%(levelname)s: %(message)s'))
logger.addHandler(queue_handler)


class CameraVideoStreamTrack(VideoStreamTrack):
    """Video stream from the local camera."""

    def __init__(self):
        super().__init__()
        self.cap = None
        self.width = 640
        self.height = 480
        self.counter = 0
        self._start_capture()

    def _start_capture(self):
        try:
            self.cap = cv2.VideoCapture(0)
            self.cap.set(cv2.CAP_PROP_FRAME_WIDTH, self.width)
            self.cap.set(cv2.CAP_PROP_FRAME_HEIGHT, self.height)
            if not self.cap.isOpened():
                raise RuntimeError("Could not open webcam")
            logger.info("Camera opened successfully")
        except Exception as e:
            logger.error(f"Error opening camera: {e}")
            self.cap = None

    async def recv(self):
        pts, time_base = await self.next_timestamp()

        # Try to get frame from camera
        if self.cap and self.cap.isOpened():
            ret, frame = self.cap.read()
            if not ret:
                logger.warning("Failed to get frame from camera")
                frame = self._create_dummy_frame()
            else:
                # Flip horizontally for more natural view
                frame = cv2.flip(frame, 1)

                # Add frame counter
                self.counter += 1
                text = f"Frame: {self.counter}"
                cv2.putText(
                    frame, text, (10, 30),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 255, 0), 2
                )

                # Put a copy of the frame in the queue for GUI display
                try:
                    # Convert from BGR to RGB for display
                    rgb_frame = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
                    # Use put_nowait to avoid blocking
                    if not local_frame_queue.full():
                        local_frame_queue.put_nowait(rgb_frame)
                except queue.Full:
                    pass  # Skip frame if queue is full
        else:
            frame = self._create_dummy_frame()

            # Put dummy frame in queue for GUI
            try:
                rgb_frame = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
                if not local_frame_queue.full():
                    local_frame_queue.put_nowait(rgb_frame)
            except queue.Full:
                pass

        # Convert to VideoFrame for WebRTC
        video_frame = av.VideoFrame.from_ndarray(frame, format="bgr24")
        video_frame.pts = pts
        video_frame.time_base = time_base

        return video_frame

    def _create_dummy_frame(self):
        """Create a dummy frame when camera is not available."""
        self.counter += 1
        frame = np.zeros((self.height, self.width, 3), np.uint8)

        # Add a moving pattern
        x = int(self.width / 2 + self.width / 4 * np.sin(self.counter / 30))
        y = int(self.height / 2 + self.height / 4 * np.cos(self.counter / 20))
        cv2.circle(frame, (x, y), 50, (0, 0, 255), -1)  # Red circle

        # Add text indicating no camera
        cv2.putText(
            frame, "No Camera - Frame: {}".format(self.counter),
            (20, self.height - 20), cv2.FONT_HERSHEY_SIMPLEX,
            0.8, (255, 255, 255), 2
        )

        return frame

    def stop(self):
        if self.cap:
            self.cap.release()
            self.cap = None
            logger.info("Camera released")


# Remote video processing track
class RemoteVideoProcessor(MediaStreamTrack):
    kind = "video"

    def __init__(self, track):
        super().__init__()
        self.track = track
        self.counter = 0

    async def recv(self):
        frame = await self.track.recv()

        # Process the frame
        self.counter += 1

        # Convert to a format we can display
        img = frame.to_ndarray(format="bgr24")

        # Add a counter to show it's being processed
        cv2.putText(
            img, f"Remote: {self.counter}", (10, 30),
            cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 255, 0), 2
        )

        # Queue the image for display in the GUI
        try:
            rgb_img = cv2.cvtColor(img, cv2.COLOR_BGR2RGB)
            if not remote_frame_queue.full():
                remote_frame_queue.put_nowait(rgb_img)
        except queue.Full:
            pass  # Skip if queue is full

        # Return the frame
        return frame


# Create Socket.IO client
sio = socketio.AsyncClient()


# Socket.IO event handlers
@sio.event
async def connect():
    logger.info("Connected to signaling server")


@sio.event
async def disconnect():
    logger.info("Disconnected from signaling server")


@sio.event
async def message(data):
    logger.info(f"Received message: {data}")


@sio.event
async def new_user_joined():
    logger.info("New user joined the room")
    await create_offer()


@sio.event
async def offer(data):
    logger.info("Received offer")

    # Check if this is our own offer being reflected back
    if is_own_offer(data["offer"]):
        logger.info("Ignoring our own offer")
        return

    try:
        await handle_offer(data["offer"])
    except Exception as e:
        logger.error(f"Error handling offer: {e}")


@sio.event
async def answer(data):
    logger.info("Received answer")
    try:
        await handle_answer(data["answer"])
    except Exception as e:
        logger.error(f"Error handling answer: {e}")


@sio.event
async def new_ice_candidate(data):
    logger.info("Received ICE candidate")
    try:
        await handle_ice_candidate(data["iceCandidate"])
    except Exception as e:
        logger.error(f"Error handling ICE candidate: {e}")


def is_own_offer(offer):
    """Check if the offer is our own by comparing some properties."""
    global pc, last_offer_id

    if pc and pc.localDescription and pc.localDescription.type == "offer":
        # Compare by random id we added
        if last_offer_id and "offer_id:" + last_offer_id in offer["sdp"]:
            return True

        # Also check the signaling state
        if pc.signalingState == "have-local-offer":
            # If we're in have-local-offer, be cautious about accepting remote offers
            return True

    return False


async def setup_local_media():
    """Set up local video track."""
    global local_video

    try:
        local_video = CameraVideoStreamTrack()
        logger.info("Local video track created")
    except Exception as e:
        logger.error(f"Error setting up local media: {e}")
        raise


async def setup_peer_connection():
    """Set up WebRTC peer connection."""
    global pc, remote_video_track

    # Create a new RTCPeerConnection
    config = RTCConfiguration([
        RTCIceServer(urls=["stun:stun.l.google.com:19302"]),
        RTCIceServer(urls=["stun:stun1.l.google.com:19302"]),
        RTCIceServer(urls=["stun:stun2.l.google.com:19302"]),
        RTCIceServer(urls=["stun:stun3.l.google.com:19302"]),
        RTCIceServer(urls=["stun:stun4.l.google.com:19302"]),
    ])
    pc = RTCPeerConnection(config)

    # Add local video track
    if local_video:
        pc.addTrack(local_video)

    # Set up event handlers
    @pc.on("icecandidate")
    async def on_ice_candidate(candidate):
        if candidate:
            logger.info(f"Generated ICE candidate: {candidate.candidate}")
            await sio.emit("new ice candidate", {
                "iceCandidate": {
                    "candidate": candidate.candidate,
                    "sdpMid": candidate.sdpMid,
                    "sdpMLineIndex": candidate.sdpMLineIndex,
                },
                "roomId": room_id
            })

    @pc.on("icecandidateerror")
    def on_ice_candidate_error(error):
        logger.error(f"ICE candidate error: {error}")

    @pc.on("iceconnectionstatechange")
    def on_ice_connection_state_change():
        if pc:  # Check if pc still exists
            logger.info(f"ICE connection state changed to: {pc.iceConnectionState}")

    @pc.on("connectionstatechange")
    async def on_connection_state_change():
        if pc:  # Check if pc still exists
            logger.info(f"Connection state changed to: {pc.connectionState}")
            if pc.connectionState == "connected":
                logger.info("Peers connected!")
            elif pc.connectionState == "failed" or pc.connectionState == "closed":
                logger.info("Connection failed or closed")

    @pc.on("track")
    def on_track(track):
        global remote_video_track
        logger.info(f"Remote track received: {track.kind}")

        if track.kind == "video":
            # Create a processor for the track
            remote_video_track = RemoteVideoProcessor(track)

            # Set up ended callback
            @track.on("ended")
            async def on_ended():
                logger.info("Remote video track ended")
                remote_video_track = None

            # Start processing frames
            asyncio.create_task(process_remote_frames())

    @pc.on("datachannel")
    def on_datachannel(channel):
        global data_channel
        logger.info(f"Data channel received: {channel.label}")
        data_channel = channel

        @data_channel.on("open")
        def on_open():
            logger.info("Data channel opened")

        @data_channel.on("close")
        def on_close():
            logger.info("Data channel closed")

        @data_channel.on("message")
        def on_message(message):
            logger.info(f"Received message: {message}")


async def process_remote_frames():
    """Process frames from the remote video track."""
    global remote_video_track

    if not remote_video_track:
        return

    try:
        while True:
            try:
                await remote_video_track.recv()
            except Exception:
                break
    except Exception as e:
        logger.error(f"Error processing remote frames: {e}")


async def create_offer():
    """Create and send an offer."""
    global pc, last_offer_id

    if not pc:
        await setup_peer_connection()

    # Check if we're already in the process of negotiating
    if pc.signalingState != "stable":
        logger.info(f"Cannot create offer in signaling state: {pc.signalingState}")
        return

    # Generate a random identifier for this offer
    last_offer_id = str(random.randint(10000, 99999))

    # Create offer - MODIFIED to work with your aiortc version
    try:
        # Create offer (without options dictionary which causes error)
        offer = await pc.createOffer()

        # Add a comment with our identifier to detect our own offers
        offer.sdp += f"\r\n;offer_id:{last_offer_id}\r\n"

        # Force H.264 as preferred codec (more compatible)
        offer.sdp = prefer_codec(offer.sdp, "video", "H264")

        await pc.setLocalDescription(offer)

        logger.info("Sending offer")
        await sio.emit("offer", {
            "offer": {
                "type": pc.localDescription.type,
                "sdp": pc.localDescription.sdp
            },
            "roomId": room_id
        })
    except Exception as e:
        logger.error(f"Error creating offer: {e}")


def prefer_codec(sdp, kind, codec):
    """Modify SDP to prefer a specific codec."""
    lines = sdp.split("\r\n")
    result = []

    # Find m line for the requested media type
    mline_index = -1
    for i, line in enumerate(lines):
        if line.startswith(f"m={kind}"):
            mline_index = i
            break

    if mline_index == -1:
        return sdp

    # Find codec PT
    codec_pt = None
    for i in range(mline_index + 1, len(lines)):
        if lines[i].startswith("a=rtpmap:"):
            desc = lines[i].split(" ")[1].lower()
            if codec.lower() in desc:
                codec_pt = lines[i].split(":")[1].split(" ")[0]
                break
        elif lines[i].startswith("m="):
            # Reached next media section
            break

    if not codec_pt:
        return sdp

    # Modify m line to put codec PT first
    mline = lines[mline_index].split(" ")
    pts = mline[3:]
    if codec_pt in pts:
        pts.remove(codec_pt)
        pts.insert(0, codec_pt)
        mline[3:] = pts
        lines[mline_index] = " ".join(mline)

    return "\r\n".join(lines)


async def handle_offer(offer_dict):
    """Handle received offer and send answer."""
    global pc

    # Check current signaling state
    if pc and pc.signalingState != "stable":
        logger.warning(f"Cannot handle offer in signaling state: {pc.signalingState}")
        return

    if not pc:
        await setup_peer_connection()

    # Set remote description
    offer = RTCSessionDescription(
        sdp=offer_dict["sdp"],
        type=offer_dict["type"]
    )

    logger.info("Setting remote description (offer)")
    await pc.setRemoteDescription(offer)

    # Create answer - MODIFIED for compatibility
    answer = await pc.createAnswer()

    # Force H.264 as preferred codec
    answer.sdp = prefer_codec(answer.sdp, "video", "H264")

    logger.info("Setting local description (answer)")
    await pc.setLocalDescription(answer)

    # Send answer
    logger.info("Sending answer")
    await sio.emit("answer", {
        "answer": {
            "type": pc.localDescription.type,
            "sdp": pc.localDescription.sdp
        },
        "roomId": room_id
    })


async def handle_answer(answer_dict):
    """Handle received answer."""
    global pc

    if not pc:
        logger.error("Received answer but no peer connection exists")
        return

    # Check if we're in a state to accept an answer
    if pc.signalingState != "have-local-offer":
        logger.warning(f"Cannot handle answer in signaling state: {pc.signalingState}")
        return

    # Set remote description
    answer = RTCSessionDescription(
        sdp=answer_dict["sdp"],
        type=answer_dict["type"]
    )

    logger.info("Setting remote description (answer)")
    await pc.setRemoteDescription(answer)


async def handle_ice_candidate(candidate_dict):
    """Handle received ICE candidate."""
    global pc

    if not pc:
        logger.error("Received ICE candidate but no peer connection exists")
        return

    candidate = RTCIceCandidate(
        sdpMid=candidate_dict["sdpMid"],
        sdpMLineIndex=candidate_dict["sdpMLineIndex"],
        candidate=candidate_dict["candidate"]
    )

    logger.info(f"Adding ICE candidate: {candidate.candidate}")
    await pc.addIceCandidate(candidate)


async def create_data_channel():
    """Create a data channel for messaging."""
    global pc, data_channel

    if not pc:
        logger.error("Cannot create data channel: no peer connection")
        return False

    # Check if we already have a data channel
    if data_channel:
        logger.info("Data channel already exists")
        return True

    # Check if we're in a state to create a data channel
    if pc.signalingState != "stable" and pc.signalingState != "have-local-offer":
        logger.warning(f"Not in ideal state to create data channel: {pc.signalingState}")
        # We'll try anyway

    data_channel = pc.createDataChannel("MyApp Channel")
    logger.info("Data channel created")

    @data_channel.on("open")
    def on_open():
        logger.info("Data channel opened")

    @data_channel.on("close")
    def on_close():
        logger.info("Data channel closed")

    @data_channel.on("message")
    def on_message(message):
        logger.info(f"Received message: {message}")

    # Only renegotiate if we're in stable state
    if pc.signalingState == "stable":
        # Need to renegotiate after creating data channel
        await create_offer()

    return True


async def send_message(message):
    """Send a message via data channel."""
    global data_channel

    if not data_channel or data_channel.readyState != "open":
        logger.error("Cannot send message: data channel not open")
        return False

    data_channel.send(message)
    logger.info(f"Sent message: {message}")
    return True


async def join_room(server_url, room_id_value):
    """Join a room on the signaling server."""
    global room_id

    room_id = str(room_id_value)

    try:
        # Connect to signaling server
        await sio.connect(server_url)
        logger.info(f"Connected to server: {server_url}")

        # Join room
        await sio.emit("join room", {"roomId": room_id})
        logger.info(f"Joined room: {room_id}")

        return True
    except Exception as e:
        logger.error(f"Error joining room: {e}")
        return False


async def leave_room():
    """Leave the current room."""
    global pc, data_channel, local_video, remote_video_track

    # Notify the signaling server
    if sio.connected:
        try:
            await sio.emit("leave room", {"roomId": room_id})
            logger.info(f"Left room: {room_id}")
        except Exception as e:
            logger.error(f"Error leaving room: {e}")

    # Stop local video
    if local_video:
        local_video.stop()
        local_video = None

    # Clean up remote video track
    remote_video_track = None

    # Close data channel
    if data_channel:
        data_channel.close()
        data_channel = None

    # Close peer connection
    if pc:
        await pc.close()
        pc = None

    # Disconnect from signaling server
    if sio.connected:
        try:
            await sio.disconnect()
            logger.info("Disconnected from signaling server")
        except Exception as e:
            logger.error(f"Error disconnecting: {e}")


async def restart_ice():
    """Restart ICE connection."""
    global pc

    if not pc:
        logger.error("No peer connection to restart")
        return False

    try:
        # In older versions of aiortc, we need to use a different approach
        # Create a new offer without options dictionary
        offer = await pc.createOffer()

        # Manually ensure ICE restart by changing the ufrag and pwd
        # Note: This is a workaround if iceRestart option isn't available
        lines = offer.sdp.split("\r\n")
        for i, line in enumerate(lines):
            if line.startswith("a=ice-ufrag:"):
                lines[i] = f"a=ice-ufrag:{random.randint(10000, 99999)}"
            elif line.startswith("a=ice-pwd:"):
                lines[i] = f"a=ice-pwd:{random.randint(10000000, 99999999)}"

        offer.sdp = "\r\n".join(lines)

        await pc.setLocalDescription(offer)
        await sio.emit("offer", {
            "offer": {
                "type": pc.localDescription.type,
                "sdp": pc.localDescription.sdp
            },
            "roomId": room_id
        })
        logger.info("Sent new offer for ICE restart")
        return True
    except Exception as e:
        logger.error(f"Error restarting ICE: {e}")
        return False


async def process_commands():
    """Process commands from the command queue."""
    while True:
        try:
            # Get command with a timeout
            command = await asyncio.get_event_loop().run_in_executor(
                None, lambda: command_queue.get(timeout=0.1)
            )

            # Process the command
            if command == "offer":
                await create_offer()
            elif command == "datachannel":
                await create_data_channel()
            elif command.startswith("send "):
                message = command[5:]
                await send_message(message)
            elif command == "restart":
                await restart_ice()
            elif command == "status":
                if pc:
                    status = f"Signaling state: {pc.signalingState}\n"
                    status += f"Connection state: {pc.connectionState}\n"
                    status += f"ICE connection state: {pc.iceConnectionState}\n"
                    status += f"ICE gathering state: {pc.iceGatheringState}\n"
                    status += f"Data channel: {data_channel.readyState if data_channel else 'None'}"
                    logger.info(f"\n{status}")
                else:
                    logger.info("No active peer connection")
            elif command == "leave":
                await leave_room()
                return  # Exit the command processing loop
            else:
                logger.warning(f"Unknown command: {command}")

            # Mark command as done
            command_queue.task_done()

        except queue.Empty:
            # No command available, continue
            await asyncio.sleep(0.01)
        except Exception as e:
            logger.error(f"Error processing command: {e}")
            await asyncio.sleep(0.1)


class WebRTCApp:
    def __init__(self, root, server_url, room_id):
        self.root = root
        self.server_url = server_url
        self.room_id = room_id

        self.root.title(f"WebRTC Client - Room: {room_id}")
        self.root.geometry("1300x720")
        self.root.protocol("WM_DELETE_WINDOW", self.on_closing)

        self.setup_ui()

        # Start updating the UI
        self.update_ui()

    def on_closing(self):
        # Put leave command in queue
        command_queue.put("leave")
        # Wait a bit to allow cleanup
        self.root.after(1000, self.root.destroy)

    def setup_ui(self):
        # Main frame
        main_frame = ttk.Frame(self.root)
        main_frame.pack(fill=tk.BOTH, expand=True, padx=10, pady=10)

        # Video frames
        video_frame = ttk.Frame(main_frame)
        video_frame.pack(fill=tk.BOTH, expand=True, pady=5)

        # Local video
        local_frame = ttk.LabelFrame(video_frame, text="Local Video")
        local_frame.pack(side=tk.LEFT, fill=tk.BOTH, expand=True, padx=5)

        self.local_canvas = tk.Canvas(local_frame, bg="black")
        self.local_canvas.pack(fill=tk.BOTH, expand=True)

        # Remote video
        remote_frame = ttk.LabelFrame(video_frame, text="Remote Video")
        remote_frame.pack(side=tk.RIGHT, fill=tk.BOTH, expand=True, padx=5)

        self.remote_canvas = tk.Canvas(remote_frame, bg="black")
        self.remote_canvas.pack(fill=tk.BOTH, expand=True)

        # Command and log area
        bottom_frame = ttk.Frame(main_frame)
        bottom_frame.pack(fill=tk.X, pady=5)

        # Command entry
        cmd_frame = ttk.LabelFrame(bottom_frame, text="Command")
        cmd_frame.pack(fill=tk.X, pady=5)

        self.command_entry = ttk.Entry(cmd_frame)
        self.command_entry.pack(fill=tk.X, padx=5, pady=5)
        self.command_entry.bind("<Return>", self.on_command)

        # Buttons
        button_frame = ttk.Frame(cmd_frame)
        button_frame.pack(fill=tk.X, padx=5, pady=5)

        ttk.Button(button_frame, text="Send Offer", command=lambda: self.send_command("offer")).pack(side=tk.LEFT,
                                                                                                     padx=2)
        ttk.Button(button_frame, text="Create Data Channel", command=lambda: self.send_command("datachannel")).pack(
            side=tk.LEFT, padx=2)
        ttk.Button(button_frame, text="ICE Restart", command=lambda: self.send_command("restart")).pack(side=tk.LEFT,
                                                                                                        padx=2)
        ttk.Button(button_frame, text="Status", command=lambda: self.send_command("status")).pack(side=tk.LEFT, padx=2)
        ttk.Button(button_frame, text="Leave", command=lambda: self.send_command("leave")).pack(side=tk.LEFT, padx=2)

        # Log area
        log_frame = ttk.LabelFrame(bottom_frame, text="Log")
        log_frame.pack(fill=tk.BOTH, expand=True, pady=5)

        self.log_text = scrolledtext.ScrolledText(log_frame, height=10)
        self.log_text.pack(fill=tk.BOTH, expand=True, padx=5, pady=5)
        self.log_text.config(state=tk.DISABLED)

    def send_command(self, cmd):
        if cmd == "leave":
            self.on_closing()
        else:
            command_queue.put(cmd)

    def on_command(self, event):
        command = self.command_entry.get()
        if command:
            command_queue.put(command)
            self.command_entry.delete(0, tk.END)

    def update_ui(self):
        # Update log messages
        while not log_queue.empty():
            try:
                message = log_queue.get_nowait()
                self.log_text.config(state=tk.NORMAL)
                self.log_text.insert(tk.END, message + "\n")
                self.log_text.see(tk.END)
                self.log_text.config(state=tk.DISABLED)
                log_queue.task_done()
            except queue.Empty:
                break

        # Update local video frame
        try:
            if not local_frame_queue.empty():
                frame = local_frame_queue.get_nowait()
                self.update_canvas(self.local_canvas, frame)
                local_frame_queue.task_done()
        except Exception as e:
            pass

        # Update remote video frame
        try:
            if not remote_frame_queue.empty():
                frame = remote_frame_queue.get_nowait()
                self.update_canvas(self.remote_canvas, frame)
                remote_frame_queue.task_done()
        except Exception as e:
            pass

        # Schedule next update
        self.root.after(33, self.update_ui)  # ~30 fps

    def update_canvas(self, canvas, frame):
        # Resize frame to fit canvas
        canvas_width = canvas.winfo_width()
        canvas_height = canvas.winfo_height()

        if canvas_width > 1 and canvas_height > 1:  # Ensure canvas has been drawn
            # Calculate aspect ratio
            frame_height, frame_width = frame.shape[:2]
            aspect_ratio = frame_width / frame_height

            # Calculate dimensions to maintain aspect ratio
            if canvas_width / canvas_height > aspect_ratio:
                # Canvas is wider than frame
                new_height = canvas_height
                new_width = int(canvas_height * aspect_ratio)
            else:
                # Canvas is taller than frame
                new_width = canvas_width
                new_height = int(canvas_width / aspect_ratio)

                # Resize frame
            resized_frame = cv2.resize(frame, (new_width, new_height))

            # Convert to PhotoImage
            image = Image.fromarray(resized_frame)
            photo = ImageTk.PhotoImage(image=image)

            # Update canvas
            canvas.delete("all")  # Clear previous content
            canvas.create_image(
                canvas_width // 2, canvas_height // 2,
                image=photo, anchor=tk.CENTER
            )

            # Keep a reference to prevent garbage collection
            canvas.image = photo

def on_closing(self):
    # Put leave command in queue
    command_queue.put("leave")
    # Wait a bit to allow cleanup
    self.root.after(1000, self.root.destroy)

async def main_async(server_url, room_id):
    """Async main function."""
    try:
        # Print system info
        logger.info(f"System: {platform.system()} {platform.release()}")

        # Set up local media
        await setup_local_media()

        # Join room
        success = await join_room(server_url, room_id)

        if success:
            # Start processing commands
            await process_commands()
        else:
            logger.error("Failed to join room. Exiting...")

    except Exception as e:
        logger.error(f"Error in main_async: {e}")
    finally:
        # Ensure cleanup
        await leave_room()

def run_asyncio_loop(loop, server_url, room_id):
    """Run the asyncio event loop in a separate thread."""
    asyncio.set_event_loop(loop)
    loop.run_until_complete(main_async(server_url, room_id))

def main():
    import argparse

    # Parse command line arguments
    parser = argparse.ArgumentParser(description="WebRTC video client with GUI")
    parser.add_argument("--server", type=str, default="http://192.168.1.4:4000", help="Signaling server URL")
    parser.add_argument("--room", type=str, help="Room ID (random if not specified)")
    parser.add_argument("--verbose", action="store_true", help="Enable verbose logging")
    args = parser.parse_args()

    # Set verbose logging if requested
    if args.verbose:
        logging.basicConfig(level=logging.DEBUG)
        logging.getLogger("aiortc").setLevel(logging.DEBUG)
        logging.getLogger("aioice").setLevel(logging.DEBUG)

    # Get server URL from command line or use default
    server_url = args.server

    # Get room ID from command line or generate random
    if args.room:
        chosen_room_id = args.room
    else:
        chosen_room_id = str(random.randint(100000, 999999))

    # Create asyncio loop
    loop = asyncio.new_event_loop()

    # Start asyncio loop in a separate thread
    threading.Thread(
        target=run_asyncio_loop,
        args=(loop, server_url, chosen_room_id),
        daemon=True
    ).start()

    # Create and start the Tkinter GUI
    root = tk.Tk()
    app = WebRTCApp(root, server_url, chosen_room_id)
    root.mainloop()

    # After GUI closes, stop the asyncio loop
    loop.call_soon_threadsafe(loop.stop)

if __name__ == "__main__":
    main()
