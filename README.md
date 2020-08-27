# WebRTC-Demo
A fully WebRTC demo on Web, Android and iOS

![Demo WebRTC](./demo.gif "Demo WebRTC")

# Prerequistes
 - You need `NodeJS` installed. To check: run command `node -v` in terminal. If you haven't installed yet. Search Google to install based in your OS
 - Android Studio and an Android device (if you want to test with Android)
 
# How to run
## Start signaling server
First you need to start the signaling server, Open terminal at `node-server` and run:
```
npm install # or yarn install (to install dependencies)
npm run dev # or yarn dev
```
Server will be listening at: `localhost:4000
## Start clients
The usage of all clients are same, you just need to join clients in same room by input same roomID.
### Web client
- To start web client, open terminal at `web-client` and run:
```
npm install # or yarn install (to install dependencies)
npm run serve # or yarn serve
```
Then open 2 browsers at `localhost:8080` to test

> Note: audio from local and remote stream is disabled by default to remove Echo during call, if you want to turn on audio simply remove `muted` from 2 `<video>` element in `App.vue`

### Android client 
- Open `android-client` in Android Studio and wait for Gradle to be synced
- Change the value of `serverAddress` in `/app/src/main/res/values/strings.xml` to IP of your machine (can check by running `ifconfig` for Mac/Linux and `ipconfig` for Windows). Keep port `4000`

### iOS client
- Open terminal at `ios-client` and run: `pod install` to install dependencies
- Then `WebRTCDemo.xcworkspace` (NOT `WebRTCDemo.xcodeproj`, note the filename)
- Change the URL string in `CallViewController` to your local IP and keep the port `4000` (Eg: `http://192.168.1.129:4000`)

# Note when develop with Android
When develop, to get a detail debug information. Do the following:
- Go to `/app/src/main/AndroidManifest.xml`, comment `android:process=":CallActivityProcess"` in `CallActivity`
- Go to `PeerConnectionClient.java`, in method `onDestroy`, change like below:
```java
public void onDestroy() {
  factory.dispose();
  socketClient.disconnect();
  socketClient.close();
}

// If you finish developing, remember to change it back to initial setup like below
// public void onDestroy() {
//   android.os.Process.killProcess(android.os.Process.myPid());
// }
// And change the AndroidManifest.xml like before
```
- By doing this you'll get crash in some cases when you navigate back to previous activity and join room again, but you'll can easily debug your app during development
