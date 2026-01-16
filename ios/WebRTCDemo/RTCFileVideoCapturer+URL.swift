//
//  RTCFileVideoCapturer+URL.swift
//  WebRTCDemo
//
//  Extension to add support for capturing from arbitrary file URLs
//  RTCFileVideoCapturer only exposes a method to capture from bundle resources. E.g: fileVideoCapturer.startCapturing(fromFileNamed: "video.mp4")
//  We need to extend it to support arbitrary file URLs.

import Foundation
import WebRTC

extension RTCFileVideoCapturer {
    
    /// Start capturing from a file at the given URL
    /// - Parameters:
    ///   - fileURL: The URL of the video file to capture from
    ///   - onError: Error callback
    func startCapturing(fromFileURL fileURL: URL, onError: @escaping (Error) -> Void) {
        print("Starting RTCFileVideoCapturer with file URL: \(fileURL)")
        // Dispatch to background queue (mimicking the original implementation)
        DispatchQueue.global(qos: .default).async { [weak self] in
            guard let self = self else { return }
            
            // Set status to started (1)
            self.setValue(1, forKey: "_status")
            
            // Reset presentation time
            self.setValue(CMTime.zero, forKey: "_lastPresentationTime")
            
            // Set the file URL
            self.setValue(fileURL, forKey: "_fileURL")
            
            // Call the private setupReaderOnError: method
            let selector = NSSelectorFromString("setupReaderOnError:")
            if self.responds(to: selector) {
                // Create error block wrapper
                let errorBlock: @convention(block) (Error) -> Void = { error in
                    onError(error)
                }
                
                // Call the method
                self.perform(selector, with: errorBlock)
            } else {
                print("RTCFileVideoCapturer does not respond to setupReaderOnError:")
            }
        }
    }
}
