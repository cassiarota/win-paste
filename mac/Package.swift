// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "FineClipboard",
    platforms: [.macOS(.v13)],
    targets: [
        // Vendored PHC reference Argon2id (used for the password-vault KDF).
        .target(
            name: "CArgon2",
            path: "Sources/CArgon2",
            publicHeadersPath: "include",
            cSettings: [
                .headerSearchPath("include"),
                .headerSearchPath("."),
            ]
        ),
        .executableTarget(
            name: "FineClipboard",
            dependencies: ["CArgon2"],
            path: "Sources/FineClipboard",
            linkerSettings: [
                .linkedFramework("Cocoa"),
                .linkedFramework("Carbon"),
                .linkedFramework("CryptoKit"),
                .linkedFramework("Vision"),
                .linkedLibrary("sqlite3"),
            ]
        )
    ]
)
