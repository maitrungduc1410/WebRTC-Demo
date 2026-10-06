# Using the app

All five apps have the same call screen. Phones are driven by touch, desktops by the mouse and the keyboard.

## Phones: Android and iOS {#phones-android-and-ios}

<DemoMedia src="/media/call-ios.png" :width="320">
An iPhone in a 1:1 call, portrait: the other person's video fills the screen, your own video sits in the corner as a small tile, and the toolbar is visible at the bottom.
</DemoMedia>

- While you are alone, your camera fills the screen and a card shows the room ID with a copy button. When the other person joins, your video shrinks into a tile in the corner.
- Tap the video to show or hide the controls. They hide by themselves after a few seconds.
- Double-tap the other person's video to switch between **fit** (the whole frame, with bars) and **fill** (cropped to the screen). Screen shares start in fit. So does a camera held the other way round from your screen, like a portrait phone on a landscape one.
- Drag your own tile to any corner. Double-tap it, or use the button at the top right, to switch between the front and back cameras.
- The toolbar has the microphone, camera, share, chat and **More** buttons, and hang up. More holds the speaker, effects, peer audio, peer video, fit or fill and the camera switch.
- Both apps work in landscape. The controls stay clear of the camera cutout and the navigation bar.

## Desktop: Web, macOS and Windows {#desktop-web-macos-and-windows}

- The video fills the window. Move the pointer to show the controls. They hide after about four seconds without movement.
- Toolbar buttons have tooltips with their shortcuts (see below).
- Microphone, speaker and camera are chosen from the menus next to the microphone and camera buttons. Share opens a picker with live thumbnails of your screens and windows (the browser shows its own picker).
- In wide windows the chat opens as a side panel. On the web, below 1024 px it opens as a bottom sheet, and in a phone browser More opens a sheet with the rest of the options.
- Drag your own video to any corner, and double-click the other person's video to switch between fit and fill.
- The picture-in-picture button keeps the call on top of other apps with mute, camera, back and hang up controls. On the Mac it also opens when you minimize the call window.

### Keyboard shortcuts {#keyboard-shortcuts}

| Key | Action |
| --- | --- |
| `M` | Microphone on or off |
| `V` | Camera on or off |
| `C` | Show or hide the chat |
| `B` | Backgrounds and effects |
| `F` | Fit or fill (1:1 calls only) |
| `P` | Picture-in-picture or the floating window |
| `Esc` | Close the chat or the effects panel |

On the Mac the same shortcuts are in the **Call** menu, plus ⇧⌘S to share, ⌘O to share a video file, ⇧⌘P for the people list in a group call and ⇧⌘E to leave. They are off while you type in the chat.

## Things that only affect you {#things-that-only-affect-you}

Muting the other person's audio or hiding their video (in More) only changes your device. They are not told.

When the other person turns their camera off, or you hide their video, you see a blurred copy of their last frame behind their avatar. The ring around the avatar pulses while they speak.

## Group calls {#group-calls}

<DemoMedia src="/media/group-web.png" :width="720">
The web client in a group call with four or five people on different platforms: the grid of tiles with their labels (for example "Android · 3f2a1c"), one tile with the green speaking ring, one with the mic-off icon, one showing the avatar because the camera is off.
</DemoMedia>

- The others are shown in a grid. Each tile is labelled with their platform and a short ID, for example `Android · 3f2a1c`, and shows a mic-off icon when their microphone is off.
- A green ring marks who is speaking.
- The people count next to the timer opens the list of everyone in the room. Your own label comes first and is highlighted, so you can find your tile on the other devices.
- People who share their screen are shown fit, everyone else fills the tile. Double-tap a tile (double-click on desktop) to switch.
- Chat goes through the SFU server and shows who sent each message. In More, muting audio or hiding video applies to everyone, including people who join later.
- On the web and iOS, group tiles show the avatar without the blurred last frame. On iOS the system picture-in-picture window is only available in 1:1 calls.
