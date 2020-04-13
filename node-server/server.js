const express = require("express")
const app = express()
const port = 4000

const http = require("http")
const server = http.createServer(app)

const io = require("socket.io")(server)

/*
  Eg: rooms = [
    {
      id: 123456,
      participants: ['socket_id_1', 'socket_id_2']
    }
  ]

  A room can only have maximum 2 participants
*/
let rooms = []

app.get('/', (req, res) => {
  return res.json({
    message: 'Hello world1'
  })
})

io.on("error", e => console.log(e))
io.on("connection", socket => {
  console.log('A client connected')
  socket.on('join room', data => {
    data.roomId = data.roomId.toString()
    const index = rooms.findIndex(room => room.id === data.roomId)

    if (index > -1) {
      if (rooms[index].participants.length <= 1) {
        if (rooms[index].participants[0] === socket.id) {
          socket.emit('message', { message: 'User is already in this room' })
          return
        }
        socket.join(data.roomId)
        rooms[index].participants.push(socket.id)

        socket.broadcast.to(data.roomId).emit('new user joined')
      } else {
        socket.emit('message', { message: 'Room is full' })
      }
    } else {
      socket.join(data.roomId)
      rooms.push({
        id: data.roomId,
        participants: [socket.id]
      })
    }
  })

  socket.on('offer', data => {
    data.roomId = data.roomId.toString()
    const index = rooms.findIndex(room => room.id === data.roomId)

    if (index > -1) {
      socket.broadcast.to(data.roomId).emit('offer', { offer: data.offer })
    } else {
      socket.emit('message', { message: 'Room not found' })
    }
  })

  socket.on('answer', data => {
    data.roomId = data.roomId.toString()
    const index = rooms.findIndex(room => room.id === data.roomId)

    if (index > -1) {
      socket.broadcast.to(data.roomId).emit('answer', { answer: data.answer })
    } else {
      socket.emit('message', { message: 'Room not found' })
    }
  })

  socket.on('new ice candidate', data => {
    data.roomId = data.roomId.toString()
    const index = rooms.findIndex(room => room.id === data.roomId)

    if (index > -1) {
      socket.broadcast.to(data.roomId).emit('new ice candidate', { iceCandidate: data.iceCandidate })
    } else {
      socket.emit('message', { message: 'Room not found' })
    }
  })

  socket.on('leave room', data => { // use only for web client
    data.roomId = data.roomId.toString()
    const index = rooms.findIndex(room => room.id === data.roomId)

    if (index > -1) {
      socket.leave(data.roomId)
      removeUserFromRoom(socket.id)
    } else {
      socket.emit('message', { message: 'Room not found' })
    }
  })

  socket.on('disconnect', () => {
    console.log('a client disconnected')
    removeUserFromRoom(socket.id)
  })
})

function removeUserFromRoom(id) {
  rooms.forEach((room, index) => {
    const participantIndex = room.participants.findIndex(p => p === id)

    if (participantIndex > -1) {
      room.participants.splice(participantIndex, 1) // remove participant in room

      if (!room.participants.length) { // if after removing there's no participant left in room, then we delete the room
        rooms.splice(index, 1)
      }
    }
  })
}

server.listen(port, () => console.log(`Server is running on port ${port}`))