# WebRTC-Demo
A fully WebRTC demo on Web, Android and iOS

![Demo WebRTC](./demo.gif "Demo WebRTC")

# Prerequistes
 - You need `NodeJS` installed. To check: run command `node -v` in terminal. If you haven't installed yet. Search Google to install based in your OS
 - Android Studio and an Android device (if you want to test with Android)
 
# How to run:
- First you need to start the signaling server, Open terminal at `node-server` and run:
```
npm run dev # or yarn dev
```
- Then start the web client, open terminal at `web-client` and run:
```
npm run server # or yarn server
```
- Then open 2 tabs on browser at `localhost:8080` join in same roomID and do some hacks
- If you want play with Android, change the value of `serverAddress` in `/app/src/main/res/values/strings.xml` to your local IP and port of the server(can check by running `ifconfig`)
