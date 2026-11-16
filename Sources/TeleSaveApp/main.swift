import TeleSaveCore
import AppKit

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

MainActor.assumeIsolated {
    let app = NSApplication.shared
    let delegate = AppDelegate()
    app.delegate = delegate
    app.setActivationPolicy(.accessory)
    app.run()
}
