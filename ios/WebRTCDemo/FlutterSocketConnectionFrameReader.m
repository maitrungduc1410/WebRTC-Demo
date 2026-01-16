//
//  FlutterSocketConnectionFrameReader.m
//  WebRTCDemo
//
//  Frame reader that receives video frames from the broadcast extension via socket connection
//  Copy from: https://github.com/livekit/client-sdk-flutter/tree/main/example/ios/LiveKit%20Broadcast%20Extension

#include <mach/mach_time.h>

#import <ReplayKit/ReplayKit.h>
#import <WebRTC/RTCCVPixelBuffer.h>
#import <WebRTC/RTCVideoFrameBuffer.h>

#import "FlutterSocketConnection.h"
#import "FlutterSocketConnectionFrameReader.h"

const NSUInteger kMaxReadLength = 10 * 1024;

@interface Message : NSObject

@property(nonatomic, assign, readonly) CVImageBufferRef imageBuffer;
@property(nonatomic, copy, nullable) void (^didComplete)(BOOL succes, Message* message);

- (NSInteger)appendBytes:(UInt8*)buffer length:(NSUInteger)length;

@end

@interface Message ()

@property(nonatomic, assign) CVImageBufferRef imageBuffer;
@property(nonatomic, assign) int imageOrientation;
@property(nonatomic, assign) CFHTTPMessageRef framedMessage;

@end

@implementation Message

- (instancetype)init {
  self = [super init];
  if (self) {
    _framedMessage = CFHTTPMessageCreateEmpty(kCFAllocatorDefault, FALSE);
  }

  return self;
}

- (void)dealloc {
  if (_framedMessage) {
    CFRelease(_framedMessage);
  }
  if (_imageBuffer) {
    CVPixelBufferRelease(_imageBuffer);
  }
}

- (NSInteger)appendBytes:(UInt8*)buffer length:(NSUInteger)length {
  BOOL success = CFHTTPMessageAppendBytes(self.framedMessage, buffer, length);

  if (!success) {
    NSLog(@"Failed to append bytes to message");
    return -1;
  }

  if (CFHTTPMessageIsHeaderComplete(self.framedMessage)) {
    CFDataRef bodyData = CFHTTPMessageCopyBody(self.framedMessage);
    CFStringRef contentLengthString =
        CFHTTPMessageCopyHeaderFieldValue(self.framedMessage, (__bridge CFStringRef) @"Content-Length");

    NSInteger contentLength = [(__bridge NSString*)contentLengthString integerValue];

    if (bodyData) {
      NSInteger bodyLength = CFDataGetLength(bodyData);

      if (bodyLength >= contentLength) {
        NSData* imageData = (__bridge_transfer NSData*)bodyData;

        [self extractBufferFromData:imageData];

        if (self.didComplete) {
          dispatch_async(dispatch_get_main_queue(), ^{
            self.didComplete(YES, self);
          });
        }

        if (contentLengthString) {
          CFRelease(contentLengthString);
        }

        return 0;
      }

      CFRelease(bodyData);
    }

    if (contentLengthString) {
      CFRelease(contentLengthString);
    }

    return contentLength;
  }

  return kMaxReadLength;
}

- (void)extractBufferFromData:(NSData*)data {
  CFStringRef orientationString =
      CFHTTPMessageCopyHeaderFieldValue(self.framedMessage, (__bridge CFStringRef) @"Buffer-Orientation");
  self.imageOrientation = [(__bridge NSString*)orientationString intValue];
  if (orientationString) {
    CFRelease(orientationString);
  }

  CFStringRef widthString =
      CFHTTPMessageCopyHeaderFieldValue(self.framedMessage, (__bridge CFStringRef) @"Buffer-Width");
  int width = [(__bridge NSString*)widthString intValue];
  if (widthString) {
    CFRelease(widthString);
  }

  CFStringRef heightString =
      CFHTTPMessageCopyHeaderFieldValue(self.framedMessage, (__bridge CFStringRef) @"Buffer-Height");
  int height = [(__bridge NSString*)heightString intValue];
  if (heightString) {
    CFRelease(heightString);
  }

  NSDictionary* pixelBufferAttributes = @{
    (id)kCVPixelBufferWidthKey : @(width),
    (id)kCVPixelBufferHeightKey : @(height),
    (id)kCVPixelBufferPixelFormatTypeKey : @(kCVPixelFormatType_32BGRA),
    (id)kCVPixelBufferIOSurfacePropertiesKey : @{}
  };

  CVPixelBufferRef pixelBuffer;
  CVPixelBufferCreate(kCFAllocatorDefault, width, height, kCVPixelFormatType_32BGRA,
                      (__bridge CFDictionaryRef)pixelBufferAttributes, &pixelBuffer);

  [self writeImageData:data intoPixelBuffer:&pixelBuffer];

  _imageBuffer = pixelBuffer;
}

- (void)writeImageData:(NSData*)data intoPixelBuffer:(CVPixelBufferRef*)pixelBuffer {
  CIContext* imageContext = [CIContext context];
  CIImage* image = [CIImage imageWithData:data];

  CVPixelBufferLockBaseAddress(*pixelBuffer, 0);

  [imageContext render:image toCVPixelBuffer:*pixelBuffer];

  CVPixelBufferUnlockBaseAddress(*pixelBuffer, 0);
}

@end

// MARK: -

@interface FlutterSocketConnectionFrameReader () <NSStreamDelegate>

@property(nonatomic, strong) FlutterSocketConnection* connection;
@property(nonatomic, strong) Message* message;

@end

@implementation FlutterSocketConnectionFrameReader {
  mach_timebase_info_data_t _timebaseInfo;
  NSInteger _readLength;
  int64_t _startTimeStampNs;
}

- (instancetype)initWithDelegate:(__weak id<RTCVideoCapturerDelegate>)delegate {
  self = [super initWithDelegate:delegate];
  if (self) {
    mach_timebase_info(&_timebaseInfo);
  }

  return self;
}

- (void)startCaptureWithConnection:(FlutterSocketConnection*)connection {
  _startTimeStampNs = -1;

  self.connection = connection;
  self.message = nil;

  [self.connection openWithStreamDelegate:self];
}

- (void)stopCapture {
  [self.connection close];
}

// MARK: Private Methods

- (void)readBytesFromStream:(NSInputStream*)stream {
  if (!stream.hasBytesAvailable) {
    return;
  }

  if (!self.message) {
    self.message = [[Message alloc] init];
    _readLength = kMaxReadLength;

    __weak __typeof__(self) weakSelf = self;
    self.message.didComplete = ^(BOOL success, Message* message) {
      if (success) {
        [weakSelf didCaptureVideoFrame:message.imageBuffer
                       withOrientation:message.imageOrientation];
      }

      weakSelf.message = nil;
    };
  }

  uint8_t buffer[_readLength];
  NSInteger numberOfBytesRead = [stream read:buffer maxLength:_readLength];
  if (numberOfBytesRead < 0) {
    NSLog(@"Error reading bytes from stream");
    return;
  }

  _readLength = [self.message appendBytes:buffer length:numberOfBytesRead];
  if (_readLength == -1 || _readLength > kMaxReadLength) {
    _readLength = kMaxReadLength;
  }
}

- (void)didCaptureVideoFrame:(CVPixelBufferRef)pixelBuffer
             withOrientation:(CGImagePropertyOrientation)orientation {
  int64_t currentTime = mach_absolute_time();
  int64_t currentTimeStampNs = currentTime * _timebaseInfo.numer / _timebaseInfo.denom;

  if (_startTimeStampNs < 0) {
    _startTimeStampNs = currentTimeStampNs;
  }

  RTCCVPixelBuffer* rtcPixelBuffer = [[RTCCVPixelBuffer alloc] initWithPixelBuffer:pixelBuffer];
  int64_t frameTimeStampNs = currentTimeStampNs - _startTimeStampNs;

  RTCVideoRotation rotation;
  switch (orientation) {
    case kCGImagePropertyOrientationLeft:
      rotation = RTCVideoRotation_90;
      break;
    case kCGImagePropertyOrientationDown:
      rotation = RTCVideoRotation_180;
      break;
    case kCGImagePropertyOrientationRight:
      rotation = RTCVideoRotation_270;
      break;
    default:
      rotation = RTCVideoRotation_0;
      break;
  }

  RTCVideoFrame* videoFrame = [[RTCVideoFrame alloc] initWithBuffer:[rtcPixelBuffer toI420]
                                                           rotation:rotation
                                                        timeStampNs:frameTimeStampNs];

  [self.delegate capturer:self didCaptureVideoFrame:videoFrame];
}

@end

@implementation FlutterSocketConnectionFrameReader (NSStreamDelegate)

- (void)stream:(NSStream*)aStream handleEvent:(NSStreamEvent)eventCode {
  switch (eventCode) {
    case NSStreamEventOpenCompleted:
      NSLog(@"Server stream open completed");
      break;
    case NSStreamEventHasBytesAvailable:
      [self readBytesFromStream:(NSInputStream*)aStream];
      break;
    case NSStreamEventEndEncountered:
      NSLog(@"Server stream end encountered");
      [self stopCapture];
      break;
    case NSStreamEventErrorOccurred:
      NSLog(@"Server stream error encountered: %@", aStream.streamError.localizedDescription);
      break;

    default:
      break;
  }
}

@end
