// swift-tools-version:5.9
import PackageDescription

// webrtc-sdk/Specs 150.7871.01, the same prebuilt binary. Its own manifest can't be resolved: it
// declares tools 5.9 but uses `.visionOS(.v26)`, which needs PackageDescription 6.2. Go back to
// https://github.com/webrtc-sdk/Specs once a release fixes that.
let package = Package(
    name: "WebRTC",
    platforms: [
        .iOS(.v13),
        .macOS(.v10_15),
    ],
    products: [
        .library(name: "WebRTC", targets: ["WebRTC"]),
    ],
    targets: [
        .binaryTarget(
            name: "WebRTC",
            url: "https://github.com/webrtc-sdk/Specs/releases/download/150.7871.01/WebRTC.xcframework.zip",
            checksum: "03815cdf2f6a0ed328c94d74cce8fd1b8d2b6e95e2b37eab66795012fcecfdfa"
        ),
    ]
)
