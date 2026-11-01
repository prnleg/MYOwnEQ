// swift-tools-version:5.10
import PackageDescription

let rustLibDir = Context.packageDirectory + "/build/rust"

let package = Package(
    name: "MyOwnEQ",
    platforms: [.macOS("14.2")],
    targets: [
        .systemLibrary(name: "CMyOwnEQ", path: "Sources/CMyOwnEQ"),
        .executableTarget(
            name: "MyOwnEQ",
            dependencies: ["CMyOwnEQ"],
            path: "Sources/MyOwnEQ",
            linkerSettings: [
                .unsafeFlags(["-L", rustLibDir]),
                .linkedFramework("CoreAudio"),
                .linkedFramework("AudioToolbox"),
                .linkedFramework("IOKit"),
            ]
        ),
    ]
)
