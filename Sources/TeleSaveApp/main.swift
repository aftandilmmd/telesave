import TeleSaveCore
import AppKit
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var model: AppModel?
    private var statusController: StatusItemController?
    private var serviceProvider: FinderServiceProvider?

    func applicationDidFinishLaunching(_ notification: Notification) {
        L10n.selectedCode = AppSettings.load().language
        NSApp.mainMenu = Self.makeMainMenu()
        LaunchAtLogin.enableOnFirstLaunch()
        let model = AppModel()
        self.model = model
        statusController = StatusItemController(model: model)
        let provider = FinderServiceProvider(model: model)
        serviceProvider = provider
        NSApp.servicesProvider = provider
        // Keep Finder's menu in sync with the setting (also registers the service on first launch).
        FinderService.setEnabled(model.settings.finderServiceEnabled)
        if model.phase == .needsCredentials || !model.canUpload {
            // First run: open the popover so the user sees the setup / login step.
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self] in
                guard let self, self.model?.canUpload == false else { return }
                self.statusController?.showPopover()
            }
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        model?.shutdown()
    }

    /// Accessory apps have no visible menu bar, but text fields still need Edit-menu key
    /// equivalents (⌘C/⌘V/⌘A) to work — e.g. to paste the api_hash.
    private static func makeMainMenu() -> NSMenu {
        let main = NSMenu()
        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: L("TeleSave'den çık"), action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        main.addItem(appItem)

        let editItem = NSMenuItem()
        let edit = NSMenu(title: L("Düzen"))
        edit.addItem(withTitle: L("Geri al"), action: Selector(("undo:")), keyEquivalent: "z")
        edit.addItem(withTitle: L("Yinele"), action: Selector(("redo:")), keyEquivalent: "Z")
        edit.addItem(.separator())
        edit.addItem(withTitle: L("Kes"), action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: L("Kopyala"), action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: L("Yapıştır"), action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: L("Tümünü seç"), action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = edit
        main.addItem(editItem)
        return main
    }
}

#if DEBUG
/// `TeleSave --snapshot <out.png> [light|dark] [language]`: renders the real panel with sample data (README).
@MainActor
enum Snapshot {
    static func run(_ args: [String]) -> Never {
        let out = URL(fileURLWithPath: args[0])
        let dark = args.count > 1 && args[1] == "dark"
        let language = args.count > 2 ? args[2] : "en"
        NSApp.setActivationPolicy(.prohibited)
        NSApp.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)

        let now = Date()
        func item(_ name: String, _ size: Int64, _ state: UploadItem.State, minutesAgo: Double) -> UploadItem {
            var item = UploadItem(
                file: PreparedFile(url: URL(fileURLWithPath: "/nonexistent/\(name)"), displayName: name, size: size, isTemporary: false, bookmark: nil),
                now: now.addingTimeInterval(-minutesAgo * 60))
            item.state = state
            if case .done = state { item.finishedAt = now.addingTimeInterval(-minutesAgo * 60) }
            return item
        }
        let items = [
            item("Contract 2026.pdf", 2_400_000, .done(messageId: 5), minutesAgo: 2900),
            item("Baku seaside.mov", 48_000_000, .done(messageId: 6), minutesAgo: 1450),
            item("Invoice November.xlsx", 84_000, .done(messageId: 7), minutesAgo: 75),
            item("IMG_4021.HEIC", 3_100_000, .done(messageId: 8), minutesAgo: 6),
            item("Presentation.key", 12_700_000, .uploading(progress: 0.62), minutesAgo: 1),
            item("Project files.zip", 156_000_000, .queued, minutesAgo: 0),
        ]
        let model = AppModel(
            demoUser: TelegramUser(id: 1, firstName: "Aftandil", lastName: "Mammadov", username: "aftandil", isPremium: false),
            demoItems: items, language: language)

        let size = NSSize(width: 360, height: 480)
        let host = NSHostingView(rootView: PopoverView(model: model))
        host.frame = NSRect(origin: .zero, size: size)
        let window = NSWindow(contentRect: host.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        window.contentView = host
        window.appearance = NSApp.appearance
        window.setFrameOrigin(NSPoint(x: -10_000, y: -10_000))
        window.orderFrontRegardless()
        // Let SwiftUI lay out and the file-type icons load.
        RunLoop.main.run(until: Date().addingTimeInterval(1.5))
        host.layoutSubtreeIfNeeded()
        let scale: CGFloat = 2
        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size.width * scale), pixelsHigh: Int(size.height * scale),
                                   bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                   colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        rep.size = size
        host.cacheDisplay(in: host.bounds, to: rep)
        try! rep.representation(using: .png, properties: [:])!.write(to: out)
        print("snapshot: \(out.path)")
        exit(0)
    }
}

if let index = CommandLine.arguments.firstIndex(of: "--snapshot") {
    MainActor.assumeIsolated {
        _ = NSApplication.shared
        Snapshot.run(Array(CommandLine.arguments[(index + 1)...]))
    }
}
#endif

MainActor.assumeIsolated {
    let app = NSApplication.shared
    let delegate = AppDelegate()
    app.delegate = delegate
    app.setActivationPolicy(.accessory)
    app.run()
}
