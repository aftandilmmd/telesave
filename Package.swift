// swift-tools-version: 6.0
import PackageDescription

// Mac App Store builds (scripts/make-appstore-pkg.sh sets TELESAVE_APPSTORE=1) leave Sparkle out: the App Store
// delivers updates there. Everywhere else Sparkle installs updates in place.
let appStore = Context.environment["TELESAVE_APPSTORE"] == "1"

let package = Package(
    name: "TeleSave",
    platforms: [.macOS(.v15)],
    products: [
        .executable(name: "TeleSave", targets: ["TeleSaveApp"]),
        .library(name: "TeleSaveCore", targets: ["TeleSaveCore"])
    ],
    dependencies: [
        .package(url: "https://github.com/Swiftgram/TDLibFramework.git", exact: "1.8.70-23383aef")
    ] + (appStore ? [] : [.package(url: "https://github.com/sparkle-project/Sparkle", exact: "2.8.1")]),
    targets: [
        // Platform-neutral logic: queue, intake, auth mapping, persistence. No TDLib dependency.
        .target(
            name: "TeleSaveCore",
            path: "Sources/TeleSaveCore",
            // One JSON file per UI language; add a file to add a language.
            resources: [.copy("Resources/Localization")]
        ),
        // TDLib JSON adapter implementing TelegramClient.
        .target(
            name: "TeleSaveTDLib",
            dependencies: [
                "TeleSaveCore",
                .product(name: "TDLibFramework", package: "TDLibFramework")
            ],
            path: "Sources/TeleSaveTDLib"
        ),
        .executableTarget(
            name: "TeleSaveApp",
            dependencies: ["TeleSaveCore", "TeleSaveTDLib"]
                + (appStore ? [] : [.product(name: "Sparkle", package: "Sparkle")]),
            path: "Sources/TeleSaveApp"
        ),
        // Command-line check that TDLib starts and accepts our parameters.
        .executableTarget(
            name: "TeleSaveSmoke",
            dependencies: ["TeleSaveCore", "TeleSaveTDLib"],
            path: "Sources/TeleSaveSmoke"
        ),
        .testTarget(
            name: "TeleSaveCoreTests",
            dependencies: ["TeleSaveCore"],
            path: "Tests/TeleSaveCoreTests"
        )
    ],
    swiftLanguageModes: [.v5]
)
