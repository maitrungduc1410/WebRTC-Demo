//
//  FlutterBroadcastScreenCapturer.h
//  WebRTCDemo
//
//  Main app screen capturer using broadcast extension
//  Copy from: https://github.com/livekit/client-sdk-flutter/tree/main/example/ios/LiveKit%20Broadcast%20Extension

#import <Foundation/Foundation.h>
#import <WebRTC/WebRTC.h>

NS_ASSUME_NONNULL_BEGIN

extern NSString* const kRTCScreensharingSocketFD;
extern NSString* const kRTCAppGroupIdentifier;
extern NSString* const kRTCScreenSharingExtension;

@class FlutterSocketConnectionFrameReader;

@interface FlutterBroadcastScreenCapturer : RTCVideoCapturer

- (void)startCapture;
- (void)stopCapture;
- (void)stopCaptureWithCompletionHandler:(nullable void (^)(void))completionHandler;

@end

NS_ASSUME_NONNULL_END
