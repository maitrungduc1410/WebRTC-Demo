//
//  FlutterSocketConnection.m
//  WebRTCDemo
//
//  Socket connection server (main app side) for receiving screen frames from broadcast extension
//  Copy from: https://github.com/livekit/client-sdk-flutter/tree/main/example/ios/LiveKit%20Broadcast%20Extension

#import "FlutterSocketConnection.h"
#import <os/log.h>
#include <sys/socket.h>
#include <sys/un.h>

static const os_log_t kSocketLog = OS_LOG_DEFAULT;

@interface FlutterSocketConnection ()

@property(nonatomic, strong) NSInputStream* inputStream;
@property(nonatomic, strong) NSOutputStream* outputStream;
@property(nonatomic, strong) NSThread* networkThread;
@property(nonatomic, assign) int serverSocket;

@end

@implementation FlutterSocketConnection

- (instancetype)initWithFilePath:(NSString*)filePath {
  self = [super init];
  if (self) {
    _serverSocket = socket(AF_UNIX, SOCK_STREAM, 0);
    if (_serverSocket == -1) {
      os_log_error(kSocketLog, "Failed to create server socket");
      return nil;
    }

    if (![self setupSocketWithFileAtPath:filePath]) {
      close(_serverSocket);
      return nil;
    }

    if (listen(_serverSocket, 1) < 0) {
      os_log_error(kSocketLog, "Failed to listen on socket");
      close(_serverSocket);
      return nil;
    }

    [self setupNetworkThread];
  }

  return self;
}

- (void)dealloc {
  [self close];
}

- (void)openWithStreamDelegate:(id<NSStreamDelegate>)streamDelegate {
  __weak __typeof__(self) weakSelf = self;
  
  [self.networkThread start];
  
  [NSThread detachNewThreadSelector:@selector(acceptConnectionWithDelegate:)
                           toTarget:self
                         withObject:streamDelegate];
}

- (void)acceptConnectionWithDelegate:(id<NSStreamDelegate>)streamDelegate {
  struct sockaddr_un clientAddr;
  socklen_t clientAddrLen = sizeof(clientAddr);
  
  int clientSocket = accept(self.serverSocket, (struct sockaddr*)&clientAddr, &clientAddrLen);
  if (clientSocket < 0) {
    os_log_error(kSocketLog, "Failed to accept connection");
    return;
  }

  os_log_info(kSocketLog, "Client connected");

  CFReadStreamRef readStream;
  CFWriteStreamRef writeStream;
  CFStreamCreatePairWithSocket(kCFAllocatorDefault, clientSocket, &readStream, &writeStream);

  self.inputStream = (__bridge_transfer NSInputStream*)readStream;
  self.outputStream = (__bridge_transfer NSOutputStream*)writeStream;

  [self.inputStream setProperty:@YES forKey:(__bridge NSString*)kCFStreamPropertyShouldCloseNativeSocket];
  [self.outputStream setProperty:@YES forKey:(__bridge NSString*)kCFStreamPropertyShouldCloseNativeSocket];

  self.inputStream.delegate = streamDelegate;
  self.outputStream.delegate = streamDelegate;

  [self scheduleStreams];
  [self.inputStream open];
  [self.outputStream open];

  if (self.networkThread && !self.networkThread.isExecuting) {
    [self.networkThread start];
  }

  @autoreleasepool {
    while (self.inputStream && ![NSThread currentThread].isCancelled) {
      if (![NSRunLoop.currentRunLoop runMode:NSDefaultRunLoopMode
                                  beforeDate:[NSDate dateWithTimeIntervalSinceNow:0.5]]) {
        break;
      }
    }
  }
}

- (void)close {
  [self.networkThread cancel];
  [self unscheduleStreams];
  
  [self.inputStream close];
  [self.outputStream close];
  
  self.inputStream.delegate = nil;
  self.outputStream.delegate = nil;
  
  self.inputStream = nil;
  self.outputStream = nil;

  if (self.serverSocket != -1) {
    close(self.serverSocket);
    self.serverSocket = -1;
  }
}

- (void)setupNetworkThread {
  self.networkThread = [[NSThread alloc] initWithBlock:^{
    @autoreleasepool {
      while (![NSThread currentThread].isCancelled) {
        [[NSRunLoop currentRunLoop] run];
      }
    }
  }];
  self.networkThread.qualityOfService = NSQualityOfServiceUserInitiated;
}

- (BOOL)setupSocketWithFileAtPath:(NSString*)filePath {
  struct sockaddr_un addr;
  memset(&addr, 0, sizeof(addr));
  addr.sun_family = AF_UNIX;

  if (filePath.length > sizeof(addr.sun_path)) {
    os_log_error(kSocketLog, "File path too long");
    return NO;
  }

  unlink(filePath.UTF8String);
  strncpy(addr.sun_path, filePath.UTF8String, sizeof(addr.sun_path) - 1);

  int status = bind(self.serverSocket, (struct sockaddr*)&addr, sizeof(addr));
  if (status < 0) {
    os_log_error(kSocketLog, "Failed to bind socket");
    return NO;
  }

  return YES;
}

- (void)scheduleStreams {
  [self.inputStream scheduleInRunLoop:NSRunLoop.currentRunLoop forMode:NSRunLoopCommonModes];
  [self.outputStream scheduleInRunLoop:NSRunLoop.currentRunLoop forMode:NSRunLoopCommonModes];
}

- (void)unscheduleStreams {
  [self.inputStream removeFromRunLoop:NSRunLoop.currentRunLoop forMode:NSRunLoopCommonModes];
  [self.outputStream removeFromRunLoop:NSRunLoop.currentRunLoop forMode:NSRunLoopCommonModes];
}

@end
