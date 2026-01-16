//
//  FlutterSocketConnection.h
//  WebRTCDemo
//
//  Socket connection server (main app side) for receiving screen frames from broadcast extension
//  Copy from: https://github.com/livekit/client-sdk-flutter/tree/main/example/ios/LiveKit%20Broadcast%20Extension

#import <Foundation/Foundation.h>

NS_ASSUME_NONNULL_BEGIN

@interface FlutterSocketConnection : NSObject

- (instancetype)initWithFilePath:(nonnull NSString*)filePath;
- (void)openWithStreamDelegate:(id<NSStreamDelegate>)streamDelegate;
- (void)close;

@end

NS_ASSUME_NONNULL_END
