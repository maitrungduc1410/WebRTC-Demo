const http = require("http");
const os = require("os");
const { WebSocketServer, WebSocket } = require("ws");

const port = Number(process.env.PORT) || 4000;
const HEARTBEAT_MS = 25_000;

/*
  Signaling for 1:1 calls: JSON text frames over a WebSocket on /ws, each with a "type".
  The server only pairs two sockets per room and relays their messages; see ARCHITECTURE.md.

  rooms: roomId -> Set of sockets (at most 2)
*/
const rooms = new Map();

/** Relayed as is to the other participant in the room. */
const RELAYED = new Set([
  "offer",
  "answer",
  "candidate",
  "encryption key",
  "encryption key received",
  "media state",
]);

// GET / lets the web lobby check that the server is up.
const server = http.createServer((req, res) => {
  res.setHeader("Access-Control-Allow-Origin", "*");
  if (req.method === "GET" && req.url === "/") {
    res.setHeader("Content-Type", "application/json");
    res.end(JSON.stringify({ name: "signaling-server", ok: true }));
    return;
  }
  res.statusCode = 404;
  res.end();
});

const wss = new WebSocketServer({ server, path: "/ws" });

wss.on("connection", (socket) => {
  console.log("A client connected");
  socket.roomId = null;
  socket.alive = true;
  socket.on("pong", () => {
    socket.alive = true;
  });

  socket.on("message", (data) => {
    let message;
    try {
      message = JSON.parse(data.toString());
    } catch {
      return;
    }
    if (!message || typeof message.type !== "string") return;

    if (message.type === "join") {
      join(socket, message.roomId);
    } else if (message.type === "leave") {
      leave(socket);
    } else if (RELAYED.has(message.type)) {
      relay(socket, message);
    }
  });

  socket.on("close", () => {
    console.log("A client disconnected");
    leave(socket);
  });
});

function join(socket, rawRoomId) {
  const roomId = String(rawRoomId ?? "").trim();
  if (!roomId) {
    send(socket, { type: "error", message: "Missing room id", fatal: true });
    return;
  }
  if (socket.roomId === roomId) {
    send(socket, { type: "error", message: "You are already in this room", fatal: false });
    return;
  }
  leave(socket);

  const room = rooms.get(roomId) ?? new Set();
  if (room.size >= 2) {
    send(socket, { type: "error", message: "Room is full", fatal: true });
    return;
  }
  room.add(socket);
  rooms.set(roomId, room);
  socket.roomId = roomId;
  console.log(`join room ${roomId} (${room.size}/2)`);

  // The one already waiting starts the call (sends the offer).
  others(socket).forEach((other) => send(other, { type: "peer joined" }));
}

function leave(socket) {
  const roomId = socket.roomId;
  if (!roomId) return;
  socket.roomId = null;
  const room = rooms.get(roomId);
  if (!room) return;
  room.delete(socket);
  if (!room.size) rooms.delete(roomId);
}

function relay(socket, message) {
  others(socket).forEach((other) => send(other, message));
}

function others(socket) {
  const room = socket.roomId && rooms.get(socket.roomId);
  return room ? [...room].filter((s) => s !== socket) : [];
}

function send(socket, message) {
  if (socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify(message));
}

// A socket that stops answering pings (phone lost its network) is dropped, which frees its seat.
const heartbeat = setInterval(() => {
  wss.clients.forEach((socket) => {
    if (!socket.alive) {
      socket.terminate();
      return;
    }
    socket.alive = false;
    socket.ping();
  });
}, HEARTBEAT_MS);
wss.on("close", () => clearInterval(heartbeat));

function networkAddress() {
  for (const addresses of Object.values(os.networkInterfaces())) {
    for (const address of addresses ?? []) {
      if (address.family === "IPv4" && !address.internal) return address.address;
    }
  }
  return "localhost";
}

server.listen(port, () => {
  console.log(`Signaling server listening on port ${port}`);
  console.log(`Network access via: ${networkAddress()}:${port}`);
});
