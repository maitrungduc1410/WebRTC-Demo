//
//  FlutterSocketConnectionFrameReader.h
//  WebRTCDemo
//
//  Frame reader that receives video frames from the broadcast extension via socket connection
//  Copy from: https://github.com/livekit/client-sdk-flutter/tree/main/example/ios/LiveKit%20Broadcast%20Extension

#import <AVFoundation/AVFoundation.h>
#import <WebRTC/RTCVideoCapturer.h>

NS_ASSUME_NONNULL_BEGIN

@class FlutterSocketConnection;

@interface FlutterSocketConnectionFrameReader : RTCVideoCapturer

- (instancetype)initWithDelegate:(__weak id<RTCVideoCapturerDelegate>)delegate;
- (void)startCaptureWithConnection:(nonnull FlutterSocketConnection*)connection;
- (void)stopCapture;

@end

NS_ASSUME_NONNULL_END
