# WebRTC-Demo
A fully WebRTC demo on Web, Android and iOS

![Demo WebRTC](./demo.gif "Demo WebRTC")

# Prerequistes
 - You need `NodeJS` installed. To check: run command `node -v` in terminal. If you haven't installed yet. Search Google to install based in your OS
 - Android Studio and an Android device (if you want to test with Android)
 
# How to run
### Start signaling server
First you need to start the signaling server, Open terminal at `node-server` and run:
```
npm install # or yarn install (to install dependencies)
npm run dev # or yarn dev
```
### Start web client
- To start web client, open terminal at `web-client` and run:
```
npm install # or yarn install (to install dependencies)
npm run server # or yarn server
```
- Then open 2 tabs on browser at `localhost:8080` join in same roomID and do some hacks

### Start android client 
- Open `android-client` in Android Studio
- Change the value of `serverAddress` in `/app/src/main/res/values/strings.xml` to IP of your machine (can check by running `ifconfig` for Mac/Linux and `ipconfig` for Windows). Keep port `4000`

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