//
//  RTCFileVideoCapturer+URL.h
//  WebRTCDemo
//
//  Category to add URL support to RTCFileVideoCapturer
//

#import <WebRTC/WebRTC.h>

NS_ASSUME_NONNULL_BEGIN

typedef void (^RTCFileVideoCapturerURLErrorBlock)(NSError *error);

@interface RTCFileVideoCapturer (URL)

/// Start capturing from a file URL (not just bundle resources)
- (void)startCapturinggggggFromFileURL:(NSURL *)fileURL
                          onError:(nullable RTCFileVideoCapturerURLErrorBlock)errorBlock;

@end

NS_ASSUME_NONNULL_END
