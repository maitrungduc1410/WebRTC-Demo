//
//  FlutterSocketConnection.m
//  WebRTCDemo
//
//  Socket connection server (main app side) for receiving screen frames from broadcast extension
//  Copy from: https://github.com/flutter-webrtc/flutter-webrtc/tree/main/ios/flutter_webrtc/Sources/flutter_webrtc/Broadcast

#include <sys/socket.h>
#include <sys/un.h>

#import "FlutterSocketConnection.h"

@interface FlutterSocketConnection ()

@property(nonatomic, assign) int serverSocket;
@property(nonatomic, strong) dispatch_source_t listeningSource;

@property(nonatomic, strong) NSThread* networkThread;

@property(nonatomic, strong) NSInputStream* inputStream;
@property(nonatomic, strong) NSOutputStream* outputStream;

@end

@implementation FlutterSocketConnection

- (instancetype)initWithFilePath:(nonnull NSString*)filePath {
  self = [super init];

  [self setupNetworkThread];

  self.serverSocket = socket(AF_UNIX, SOCK_STREAM, 0);
  if (self.serverSocket < 0) {
    NSLog(@"failure creating socket");
    return nil;
  }

  if (![self setupSocketWithFileAtPath:filePath]) {
    close(self.serverSocket);
    return nil;
  }

  return self;
}

- (void)openWithStreamDelegate:(id<NSStreamDelegate>)streamDelegate {
  int status = listen(self.serverSocket, 10);
  if (status < 0) {
    NSLog(@"failure: socket listening");
    return;
  }

  dispatch_source_t listeningSource =
      dispatch_source_create(DISPATCH_SOURCE_TYPE_READ, self.serverSocket, 0, NULL);
  dispatch_source_set_event_handler(listeningSource, ^{
    int clientSocket = accept(self.serverSocket, NULL, NULL);
    if (clientSocket < 0) {
      NSLog(@"failure accepting connection");
      return;
    }

    // Only one broadcast extension feeds a capturer, and an NSThread cannot be started twice
    if (self.networkThread.isExecuting || self.networkThread.isFinished) {
      NSLog(@"rejecting extra connection");
      close(clientSocket);
      return;
    }

    NSLog(@"client connected");

    CFReadStreamRef readStream;
    CFWriteStreamRef writeStream;

    CFStreamCreatePairWithSocket(kCFAllocatorDefault, clientSocket, &readStream, &writeStream);

    self.inputStream = (__bridge_transfer NSInputStream*)readStream;
    self.inputStream.delegate = streamDelegate;
    [self.inputStream setProperty:@YES
                           forKey:(__bridge NSString*)kCFStreamPropertyShouldCloseNativeSocket];

    self.outputStream = (__bridge_transfer NSOutputStream*)writeStream;
    [self.outputStream setProperty:@YES
                            forKey:(__bridge NSString*)kCFStreamPropertyShouldCloseNativeSocket];

    [self.networkThread start];
    [self performSelector:@selector(scheduleStreams)
                 onThread:self.networkThread
               withObject:nil
            waitUntilDone:true];

    [self.inputStream open];
    [self.outputStream open];
  });

  self.listeningSource = listeningSource;
  dispatch_resume(listeningSource);
}

// Called from the network thread (end of stream) and from WebRTCClient, so it must be idempotent
- (void)close {
  if ([self.networkThread isExecuting]) {
    [self performSelector:@selector(unscheduleStreams)
                 onThread:self.networkThread
               withObject:nil
            waitUntilDone:true];
  }

  self.inputStream.delegate = nil;
  self.outputStream.delegate = nil;

  [self.inputStream close];
  [self.outputStream close];

  self.inputStream = nil;
  self.outputStream = nil;

  [self.networkThread cancel];

  if (self.listeningSource) {
    dispatch_source_cancel(self.listeningSource);
    self.listeningSource = nil;
  }
  if (self.serverSocket >= 0) {
    close(self.serverSocket);
    self.serverSocket = -1;
  }
}

// MARK: - Private Methods

- (void)setupNetworkThread {
  self.networkThread = [[NSThread alloc] initWithBlock:^{
    do {
      @autoreleasepool {
        [[NSRunLoop currentRunLoop] run];
      }
    } while (![NSThread currentThread].isCancelled);
  }];
  self.networkThread.qualityOfService = NSQualityOfServiceUserInitiated;
}

- (BOOL)setupSocketWithFileAtPath:(NSString*)filePath {
  struct sockaddr_un addr;
  memset(&addr, 0, sizeof(addr));
  addr.sun_family = AF_UNIX;

  if (filePath.length > sizeof(addr.sun_path)) {
    NSLog(@"failure: path too long");
    return false;
  }

  unlink(filePath.UTF8String);
  strncpy(addr.sun_path, filePath.UTF8String, sizeof(addr.sun_path) - 1);

  int status = bind(self.serverSocket, (struct sockaddr*)&addr, sizeof(addr));
  if (status < 0) {
    NSLog(@"failure: socket binding");
    return false;
  }

  return true;
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