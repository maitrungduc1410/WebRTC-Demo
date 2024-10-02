const express = require("express");
const app = express();
const port = 4000;
const ip = require("ip");

const http = require("http");
const server = http.createServer(app);

const io = require("socket.io")(server, {
  cors: {
    origin: "*",
  },
});
const ipAddress = ip.address();

/*
  Eg: rooms = [
    {
      id: 123456,
      participants: ['socket_id_1', 'socket_id_2']
    }
  ]

  A room can only have maximum 2 participants
*/
let rooms = [];

app.get("/", (req, res) => {
  return res.json({
    message: "Hello world1",
  });
});

io.on("error", (e) => console.log(e));
io.on("connection", (socket) => {
  console.log("A client connected");
  socket.on("join room", (data) => {
    const roomId = data.roomId.toString();
    console.log("join room:", roomId);
    const index = rooms.findIndex((room) => room.id === roomId);
    console.log(index);
    if (index > -1) {
      console.log(11111);
      if (rooms[index].participants.length <= 1) {
        console.log(22222);
        if (rooms[index].participants[0] === socket.id) {
          socket.emit("message", { message: "User is already in this room" });
          return;
        }

        console.log(21);
        socket.join(roomId);
        rooms[index].participants.push(socket.id);

        socket.broadcast.to(roomId).emit("new user joined");
      } else {
        console.log(44444);
        socket.emit("message", { message: "Room is full" });
      }
    } else {
      console.log(33333);
      socket.join(roomId);
      rooms.push({
        id: roomId,
        participants: [socket.id],
      });
    }
  });

  socket.on("offer", (data) => {
    data.roomId = data.roomId.toString();
    const index = rooms.findIndex((room) => room.id === data.roomId);

    if (index > -1) {
      socket.broadcast.to(data.roomId).emit("offer", { offer: data.offer });
    } else {
      socket.emit("message", { message: "Room not found" });
    }
  });

  socket.on("answer", (data) => {
    data.roomId = data.roomId.toString();
    const index = rooms.findIndex((room) => room.id === data.roomId);

    if (index > -1) {
      socket.broadcast.to(data.roomId).emit("answer", { answer: data.answer });
    } else {
      socket.emit("message", { message: "Room not found" });
    }
  });

  socket.on("new ice candidate", (data) => {
    console.log("new ice candidate", data);
    data.roomId = data.roomId.toString();
    const index = rooms.findIndex((room) => room.id === data.roomId);

    if (index > -1) {
      socket.broadcast
        .to(data.roomId)
        .emit("new ice candidate", { iceCandidate: data.iceCandidate });
    } else {
      socket.emit("message", { message: "Room not found" });
    }
  });

  socket.on("leave room", (data) => {
    // use only for web client
    data.roomId = data.roomId.toString();
    const index = rooms.findIndex((room) => room.id === data.roomId);

    if (index > -1) {
      socket.leave(data.roomId);
      removeUserFromRoom(socket.id);
    } else {
      socket.emit("message", { message: "Room not found" });
    }
  });

  socket.on("send encryption key", (data) => {
    data.roomId = data.roomId.toString();
    const index = rooms.findIndex((room) => room.id === data.roomId);

    if (index > -1) {
      socket.broadcast
        .to(data.roomId)
        .emit("receive encryption key", { encryptionKey: data.encryptionKey });
    } else {
      console.log("Room not found");
      // socket.emit("message", { message: "Room not found" });
    }
  });

  socket.on("encryption key received", (data) => {
    data.roomId = data.roomId.toString();
    const index = rooms.findIndex((room) => room.id === data.roomId);

    if (index > -1) {
      socket.broadcast
        .to(data.roomId)
        .emit("remote peer received encryption key");
    }
  });

  socket.on("disconnect", () => {
    console.log("a client disconnected");
    removeUserFromRoom(socket.id);
  });
});

function removeUserFromRoom(id) {
  rooms.forEach((room, index) => {
    const participantIndex = room.participants.findIndex((p) => p === id);

    if (participantIndex > -1) {
      room.participants.splice(participantIndex, 1); // remove participant in room

      if (!room.participants.length) {
        // if after removing there's no participant left in room, then we delete the room
        rooms.splice(index, 1);
      }
    }
  });
}

server.listen(port, () => {
  console.log(`Example app listening on port ${port}!`);
  console.log(`Network access via: ${ipAddress}:${port}!`);
});
