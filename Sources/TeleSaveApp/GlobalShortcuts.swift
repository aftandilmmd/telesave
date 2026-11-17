import AppKit
import Carbon
import SwiftUI
import TeleSaveCore

/// A key combination for a system-wide shortcut (Carbon key code + Carbon modifier flags).
struct KeyShortcut: Codable, Equatable, Hashable {
    var keyCode: UInt32
    var modifiers: UInt32

    /// ⌃⌥⌘V: the three keys next to each other at the bottom left, plus V for paste ("⌘C, then ⌃⌥⌘V").
    static let defaultUpload = KeyShortcut(keyCode: UInt32(kVK_ANSI_V), modifiers: UInt32(controlKey | optionKey | cmdKey))
    /// ⌃⌥⌘T: T for TeleSave.
    static let defaultPanel = KeyShortcut(keyCode: UInt32(kVK_ANSI_T), modifiers: UInt32(controlKey | optionKey | cmdKey))

    init(keyCode: UInt32, modifiers: UInt32) {
        self.keyCode = keyCode
        self.modifiers = modifiers
    }

    init?(event: NSEvent) {
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        var mods: UInt32 = 0
        if flags.contains(.control) { mods |= UInt32(controlKey) }
        if flags.contains(.option) { mods |= UInt32(optionKey) }
        if flags.contains(.shift) { mods |= UInt32(shiftKey) }
        if flags.contains(.command) { mods |= UInt32(cmdKey) }
        // Needs ⌃, ⌥ or ⌘: a plain or ⇧-only key would swallow normal typing everywhere.
        guard mods & UInt32(controlKey | optionKey | cmdKey) != 0 else { return nil }
        self.init(keyCode: UInt32(event.keyCode), modifiers: mods)
    }

    var displayString: String {
        var s = ""
        if modifiers & UInt32(controlKey) != 0 { s += "⌃" }
        if modifiers & UInt32(optionKey) != 0 { s += "⌥" }
        if modifiers & UInt32(shiftKey) != 0 { s += "⇧" }
        if modifiers & UInt32(cmdKey) != 0 { s += "⌘" }
        return s + Self.keyName(Int(keyCode))
    }

    private static let specialKeys: [Int: String] = [
        kVK_Return: "↩", kVK_Tab: "⇥", kVK_Space: "Space", kVK_Delete: "⌫", kVK_ForwardDelete: "⌦", kVK_Escape: "⎋",
        kVK_LeftArrow: "←", kVK_RightArrow: "→", kVK_UpArrow: "↑", kVK_DownArrow: "↓", kVK_Home: "↖", kVK_End: "↘",
        kVK_PageUp: "⇞", kVK_PageDown: "⇟",
        kVK_F1: "F1", kVK_F2: "F2", kVK_F3: "F3", kVK_F4: "F4", kVK_F5: "F5", kVK_F6: "F6", kVK_F7: "F7", kVK_F8: "F8",
        kVK_F9: "F9", kVK_F10: "F10", kVK_F11: "F11", kVK_F12: "F12", kVK_F13: "F13", kVK_F14: "F14", kVK_F15: "F15",
    ]

    /// The character the key produces on the current keyboard layout (so ⌃⌥⌘T shows "T" on QWERTY and AZERTY alike).
    private static func keyName(_ code: Int) -> String {
        if let name = specialKeys[code] { return name }
        guard let source = TISCopyCurrentKeyboardLayoutInputSource()?.takeRetainedValue(),
              let raw = TISGetInputSourceProperty(source, kTISPropertyUnicodeKeyLayoutData) else { return "#\(code)" }
        let data = Unmanaged<CFData>.fromOpaque(raw).takeUnretainedValue() as Data
        return data.withUnsafeBytes { buffer -> String in
            guard let layout = buffer.baseAddress?.assumingMemoryBound(to: UCKeyboardLayout.self) else { return "#\(code)" }
            var deadKeys: UInt32 = 0
            var chars = [UniChar](repeating: 0, count: 4)
            var length = 0
            let status = UCKeyTranslate(layout, UInt16(code), UInt16(kUCKeyActionDisplay), 0, UInt32(LMGetKbdType()),
                                        OptionBits(kUCKeyTranslateNoDeadKeysBit), &deadKeys, chars.count, &length, &chars)
            guard status == noErr, length > 0 else { return "#\(code)" }
            return String(utf16CodeUnits: chars, count: length).uppercased()
        }
    }

    /// Whether macOS itself uses this combination (System Settings › Keyboard › Keyboard Shortcuts).
    var isUsedBySystem: Bool {
        var unmanaged: Unmanaged<CFArray>?
        guard CopySymbolicHotKeys(&unmanaged) == noErr, let list = unmanaged?.takeRetainedValue() as? [[String: Any]] else { return false }
        return list.contains { entry in
            (entry[kHISymbolicHotKeyEnabled as String] as? Bool) == true
                && (entry[kHISymbolicHotKeyCode as String] as? Int) == Int(keyCode)
                && (entry[kHISymbolicHotKeyModifiers as String] as? Int).map { UInt32($0) & 0xFF00 } == modifiers & 0xFF00
        }
    }
}

enum ShortcutAction: UInt32, CaseIterable {
    case uploadClipboard = 1
    case togglePanel = 2
}

/// System-wide shortcuts through Carbon's RegisterEventHotKey: works in the App Sandbox and needs no
/// Accessibility permission.
@MainActor
final class GlobalShortcuts {
    static let shared = GlobalShortcuts()

    var onAction: ((ShortcutAction) -> Void)?
    /// Called after the registered set (or its failures) changed.
    var onChange: (() -> Void)?
    /// Actions whose shortcut could not be registered (another app already holds it).
    private(set) var failed: Set<ShortcutAction> = []

    private var refs: [ShortcutAction: EventHotKeyRef] = [:]
    private var wanted: [ShortcutAction: KeyShortcut] = [:]
    private var suspended = false
    private var handler: EventHandlerRef?

    private init() {
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
            var id = EventHotKeyID()
            GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                              nil, MemoryLayout<EventHotKeyID>.size, nil, &id)
            if let action = ShortcutAction(rawValue: id.id) {
                MainActor.assumeIsolated { GlobalShortcuts.shared.onAction?(action) }
            }
            return noErr
        }, 1, &spec, nil, &handler)
    }

    /// Registers the given shortcuts, replacing the previous ones. Returns the actions that failed.
    @discardableResult
    func apply(_ shortcuts: [ShortcutAction: KeyShortcut]) -> Set<ShortcutAction> {
        wanted = shortcuts
        guard !suspended else { return failed }
        unregisterAll()
        failed = []
        for (action, shortcut) in shortcuts {
            var ref: EventHotKeyRef?
            let status = RegisterEventHotKey(shortcut.keyCode, shortcut.modifiers,
                                             EventHotKeyID(signature: 0x5453_5631 /* TSV1 */, id: action.rawValue),
                                             GetApplicationEventTarget(), 0, &ref)
            if status == noErr, let ref { refs[action] = ref } else { failed.insert(action) }
        }
        onChange?()
        return failed
    }

    /// While a new shortcut is being recorded, the current ones must not fire.
    func setSuspended(_ value: Bool) {
        suspended = value
        if value { unregisterAll() } else { apply(wanted) }
    }

    private func unregisterAll() {
        refs.values.forEach { UnregisterEventHotKey($0) }
        refs = [:]
    }
}

/// Settings row control: shows the shortcut; click, then press the new combination. Esc cancels, ⌫ turns it off.
struct ShortcutRecorder: View {
    @Binding var shortcut: KeyShortcut?
    var conflict: String?

    @State private var recording = false
    @State private var monitor: Any?
    @State private var hint: String?

    /// Read by the panel's ⌘V handler, so ⌘V can be recorded as a shortcut.
    @MainActor static var isRecording = false

    var body: some View {
        VStack(alignment: .trailing, spacing: 3) {
            HStack(spacing: 4) {
                Button(action: toggle) {
                    Text(recording ? L("Tuşlara basın…") : (shortcut?.displayString ?? L("Yok")))
                        .monospacedDigit()
                        .frame(minWidth: 86)
                }
                .help(L("Değiştirmek için tıklayın, sonra yeni tuş kombinasyonuna basın. Esc: vazgeç, ⌫: kapat."))
                if shortcut != nil && !recording {
                    Button {
                        shortcut = nil
                    } label: {
                        Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary)
                    }
                    .buttonStyle(.borderless)
                    .help(L("Kısayolu kapat"))
                }
            }
            if let message = hint ?? conflict {
                Text(message).font(.caption).foregroundStyle(.orange).multilineTextAlignment(.trailing)
            }
        }
        .onDisappear(perform: stop)
    }

    private func toggle() {
        recording ? stop() : start()
    }

    private func start() {
        recording = true
        hint = nil
        Self.isRecording = true
        GlobalShortcuts.shared.setSuspended(true)
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            let plain = event.modifierFlags.intersection([.command, .control, .option, .shift]).isEmpty
            if plain && event.keyCode == UInt16(kVK_Escape) {
                stop()
            } else if plain && (event.keyCode == UInt16(kVK_Delete) || event.keyCode == UInt16(kVK_ForwardDelete)) {
                shortcut = nil
                stop()
            } else if let new = KeyShortcut(event: event) {
                shortcut = new
                stop()
            } else {
                hint = L("⌃, ⌥ veya ⌘ ile birlikte bir tuşa basın.")
            }
            return nil
        }
    }

    private func stop() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
        if recording {
            recording = false
            Self.isRecording = false
            GlobalShortcuts.shared.setSuspended(false)
        }
    }
}
