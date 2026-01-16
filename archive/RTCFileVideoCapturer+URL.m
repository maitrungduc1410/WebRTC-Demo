//
//  RTCFileVideoCapturer+URL.m
//  WebRTCDemo
//
//  Implementation of URL support for RTCFileVideoCapturer
//

#import "RTCFileVideoCapturer+URL.h"
#import <AVFoundation/AVFoundation.h>
#import <objc/runtime.h>

// Define the status enum to match the private implementation
typedef NS_ENUM(NSInteger, RTCFileVideoCapturerStatus) {
    RTCFileVideoCapturerStatusNotInitialized,
    RTCFileVideoCapturerStatusStarted,
    RTCFileVideoCapturerStatusStopped
};

@interface RTCFileVideoCapturer (Private)
@property(nonatomic, strong) NSURL *fileURL;
- (void)setupReaderOnError:(id)errorBlock;
@end

@implementation RTCFileVideoCapturer (URL)

- (void)startCapturinggggggFromFileURL:(NSURL *)fileURL
                          onError:(nullable RTCFileVideoCapturerURLErrorBlock)errorBlock {
    NSLog(@"RTCFileVideoCapturer: Starting capture from file URL: %@", fileURL);
    
    // Set the status to started (access the private _status ivar)
    Ivar statusIvar = class_getInstanceVariable([self class], "_status");
    if (statusIvar) {
        NSInteger status = RTCFileVideoCapturerStatusStarted;
        // Set the ivar value directly using pointer manipulation (safe for primitive types)
        ptrdiff_t offset = ivar_getOffset(statusIvar);
        *(NSInteger *)((char *)(__bridge void *)self + offset) = status;
    }
    
    // Dispatch to background queue like the original implementation
    dispatch_async(dispatch_get_global_queue(DISPATCH_QUEUE_PRIORITY_DEFAULT, 0), ^{
        // Set the presentation time to zero
        CMTime zeroTime = CMTimeMake(0, 0);
        NSValue *timeValue = [NSValue valueWithCMTime:zeroTime];
        [self setValue:timeValue forKey:@"lastPresentationTime"];
        
        // Set the file URL using the private property
        [self setValue:fileURL forKey:@"fileURL"];
        
        // Call the private setup method
        if ([self respondsToSelector:@selector(setupReaderOnError:)]) {
            NSMethodSignature *signature = [self methodSignatureForSelector:@selector(setupReaderOnError:)];
            NSInvocation *invocation = [NSInvocation invocationWithMethodSignature:signature];
            [invocation setTarget:self];
            [invocation setSelector:@selector(setupReaderOnError:)];
            
            // Pass the error block as parameter
            id block = errorBlock ? errorBlock : ^(NSError *error) {
                NSLog(@"RTCFileVideoCapturer error: %@", error);
            };
            [invocation setArgument:&block atIndex:2]; // index 0 is self, 1 is _cmd, 2 is first arg
            [invocation invoke];
        } else {
            if (errorBlock) {
                NSError *error = [NSError errorWithDomain:@"RTCFileVideoCapturer"
                                                     code:-1
                                                 userInfo:@{NSLocalizedDescriptionKey: @"setupReaderOnError method not available"}];
                errorBlock(error);
            }
        }
    });
}

@end
