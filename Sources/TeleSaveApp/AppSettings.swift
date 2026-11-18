import Foundation
import ServiceManagement
import TeleSaveCore

struct AppSettings: Equatable {
    var sendMediaCompressed = false
    var groupAsAlbum = false
    var captionTemplate = ""
    var maxConcurrent = 2
    var notificationsEnabled = true
    var openPopoverOnDrop = true
    var folderMode: FolderMode = .zip
    /// Show a "Dosyalar" tab with every file in the selected chat, not only TeleSave's uploads.
    var showChatFiles = true
    /// UI language code, or `L10n.systemCode` to follow macOS.
    var language = L10n.systemCode
    /// "Send to TeleSave" in Finder's right-click menu.
    var finderServiceEnabled = true
    /// System-wide shortcut that uploads what is on the clipboard (nil = off).
    var uploadShortcut: KeyShortcut? = .defaultUpload
    /// System-wide shortcut that opens/closes the panel (nil = off).
    var panelShortcut: KeyShortcut? = .defaultPanel
    /// Pasting also sends copied text (off by default: the clipboard often holds things not meant to be shared).
    var pasteTextEnabled = false
    var textPasteMode: TextPasteMode = .message

    /// Mac App Store build (`-DAPPSTORE`): updates come from the App Store, and the Finder menu is managed in
    /// System Settings (toggling it needs a sandbox exception the App Store does not allow).
    static var isAppStoreBuild: Bool {
        #if APPSTORE
        return true
        #else
        return false
        #endif
    }

    private enum Key {
        static let sendMediaCompressed = "sendMediaCompressed"
        static let groupAsAlbum = "groupAsAlbum"
        static let captionTemplate = "captionTemplate"
        static let maxConcurrent = "maxConcurrent"
        static let notificationsEnabled = "notificationsEnabled"
        static let openPopoverOnDrop = "openPopoverOnDrop"
        static let folderMode = "folderMode"
        static let showChatFiles = "showChatFiles"
        static let language = "appLanguage"
        static let finderServiceEnabled = "finderServiceEnabled"
        static let launchAtLoginConfigured = "launchAtLoginConfigured"
        static let uploadShortcut = "uploadShortcut"
        static let panelShortcut = "panelShortcut"
        static let pasteTextEnabled = "pasteTextEnabled"
        static let textPasteMode = "textPasteMode"
    }

    static func load(from defaults: UserDefaults = .standard) -> AppSettings {
        var s = AppSettings()
        if defaults.object(forKey: Key.sendMediaCompressed) != nil { s.sendMediaCompressed = defaults.bool(forKey: Key.sendMediaCompressed) }
        if defaults.object(forKey: Key.groupAsAlbum) != nil { s.groupAsAlbum = defaults.bool(forKey: Key.groupAsAlbum) }
        s.captionTemplate = defaults.string(forKey: Key.captionTemplate) ?? ""
        if defaults.object(forKey: Key.maxConcurrent) != nil { s.maxConcurrent = min(4, max(1, defaults.integer(forKey: Key.maxConcurrent))) }
        if defaults.object(forKey: Key.notificationsEnabled) != nil { s.notificationsEnabled = defaults.bool(forKey: Key.notificationsEnabled) }
        if defaults.object(forKey: Key.openPopoverOnDrop) != nil { s.openPopoverOnDrop = defaults.bool(forKey: Key.openPopoverOnDrop) }
        if defaults.object(forKey: Key.showChatFiles) != nil { s.showChatFiles = defaults.bool(forKey: Key.showChatFiles) }
        if defaults.object(forKey: Key.finderServiceEnabled) != nil { s.finderServiceEnabled = defaults.bool(forKey: Key.finderServiceEnabled) }
        s.language = defaults.string(forKey: Key.language) ?? L10n.systemCode
        s.folderMode = defaults.string(forKey: Key.folderMode).flatMap(FolderMode.init(rawValue:)) ?? .zip
        s.pasteTextEnabled = defaults.bool(forKey: Key.pasteTextEnabled)
        s.textPasteMode = defaults.string(forKey: Key.textPasteMode).flatMap(TextPasteMode.init(rawValue:)) ?? .message
        // Stored as JSON; "null" means the user turned the shortcut off.
        if let data = defaults.data(forKey: Key.uploadShortcut) { s.uploadShortcut = try? JSONDecoder().decode(KeyShortcut?.self, from: data) }
        if let data = defaults.data(forKey: Key.panelShortcut) { s.panelShortcut = try? JSONDecoder().decode(KeyShortcut?.self, from: data) }
        return s
    }

    func save(to defaults: UserDefaults = .standard) {
        defaults.set(sendMediaCompressed, forKey: Key.sendMediaCompressed)
        defaults.set(groupAsAlbum, forKey: Key.groupAsAlbum)
        defaults.set(captionTemplate, forKey: Key.captionTemplate)
        defaults.set(maxConcurrent, forKey: Key.maxConcurrent)
        defaults.set(notificationsEnabled, forKey: Key.notificationsEnabled)
        defaults.set(openPopoverOnDrop, forKey: Key.openPopoverOnDrop)
        defaults.set(folderMode.rawValue, forKey: Key.folderMode)
        defaults.set(showChatFiles, forKey: Key.showChatFiles)
        defaults.set(language, forKey: Key.language)
        defaults.set(finderServiceEnabled, forKey: Key.finderServiceEnabled)
        defaults.set(pasteTextEnabled, forKey: Key.pasteTextEnabled)
        defaults.set(textPasteMode.rawValue, forKey: Key.textPasteMode)
        defaults.set(try? JSONEncoder().encode(uploadShortcut), forKey: Key.uploadShortcut)
        defaults.set(try? JSONEncoder().encode(panelShortcut), forKey: Key.panelShortcut)
    }

    var shortcuts: [ShortcutAction: KeyShortcut] {
        var map: [ShortcutAction: KeyShortcut] = [:]
        map[.uploadClipboard] = uploadShortcut
        map[.togglePanel] = panelShortcut
        return map
    }

    var queueSettings: UploadQueue.Settings {
        UploadQueue.Settings(
            maxConcurrent: maxConcurrent,
            sendMediaCompressed: sendMediaCompressed,
            groupAsAlbum: groupAsAlbum,
            caption: CaptionTemplate(captionTemplate)
        )
    }
}

enum LaunchAtLogin {
    private static let configuredKey = "launchAtLoginConfigured"

    /// On by default: turned on once at first launch. Afterwards the user's choice is left alone.
    static func enableOnFirstLaunch(defaults: UserDefaults = .standard) {
        guard !defaults.bool(forKey: configuredKey) else { return }
        defaults.set(true, forKey: configuredKey)
        do {
            try set(true)
        } catch {
            NSLog("TeleSave: could not enable launch at login: \(error)")
        }
    }

    static var isEnabled: Bool {
        SMAppService.mainApp.status == .enabled
    }

    static func set(_ enabled: Bool) throws {
        if enabled {
            try SMAppService.mainApp.register()
        } else {
            try SMAppService.mainApp.unregister()
        }
    }
}

enum AppPaths {
    static var root: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("TeleSave", isDirectory: true)
    }

    static var database: URL { root.appendingPathComponent("tdlib/db", isDirectory: true) }
    static var files: URL { root.appendingPathComponent("tdlib/files", isDirectory: true) }
    static var log: URL { root.appendingPathComponent("logs/tdlib.log") }
    static var staging: URL { root.appendingPathComponent("staging", isDirectory: true) }
    static var queue: URL { root.appendingPathComponent("queue.json") }
    static var sentMessages: URL { root.appendingPathComponent("sent-messages.json") }
}
