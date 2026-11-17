import TeleSaveCore
import AppKit
import Observation
import SwiftUI

/// Menu bar icon: click toggles the popover, right-click shows a menu, and files can be dropped
/// straight onto the icon.
@MainActor
final class StatusItemController: NSObject, NSPopoverDelegate {
    private let model: AppModel
    private let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private let popover = NSPopover()
    private var dragHighlighted = false
    private var shelf: DropShelfController?
    private var pasteMonitor: Any?

    init(model: AppModel) {
        self.model = model
        super.init()

        popover.behavior = .transient
        popover.animates = true
        popover.delegate = self
        popover.contentViewController = NSHostingController(rootView: PopoverView(model: model))

        if let button = statusItem.button {
            button.imagePosition = .imageLeading
            button.toolTip = L("TeleSave — dosyaları buraya bırakın")
            // Mouse clicks are caught by the overlay below; this action serves accessibility "press".
            button.target = self
            button.action = #selector(buttonPressed)
            let dropView = StatusItemDropView(frame: button.bounds)
            dropView.autoresizingMask = [.width, .height]
            dropView.onClick = { [weak self] in self?.togglePopover() }
            dropView.onRightClick = { [weak self] in self?.showMenu() }
            dropView.onDragHighlight = { [weak self] on in
                self?.dragHighlighted = on
                self?.updateButton()
            }
            dropView.onDrop = { [weak self] urls in
                self?.model.handleDrop(urls, fromStatusItem: true)
            }
            button.addSubview(dropView)
        }

        model.onRequestPopover = { [weak self] in self?.showPopover() }
        model.onTogglePopover = { [weak self] in self?.togglePopover() }
        GlobalShortcuts.shared.onAction = { [weak model] action in model?.perform(action) }
        GlobalShortcuts.shared.onChange = { [weak model] in model?.shortcutsChanged() }
        model.applyShortcuts()

        let shelf = DropShelfController(
            anchor: { [weak self] in
                guard let button = self?.statusItem.button, let window = button.window else { return nil }
                return window.convertToScreen(button.convert(button.bounds, to: nil))
            },
            destinationTitle: { [weak model] in model?.destination.displayTitle ?? L("Saved Messages") },
            onDrop: { [weak model] urls in model?.handleDrop(urls, fromStatusItem: true) }
        )
        shelf.start()
        self.shelf = shelf

        // ⌘V in the panel pastes files/images, unless a text field is being edited.
        pasteMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self, self.popover.isShown,
                  event.modifierFlags.intersection(.deviceIndependentFlagsMask) == .command,
                  event.charactersIgnoringModifiers?.lowercased() == "v",
                  event.window === self.popover.contentViewController?.view.window,
                  !(event.window?.firstResponder is NSText),
                  !ShortcutRecorder.isRecording,
                  self.model.canUpload else { return event }
            self.model.pasteFromClipboard()
            return nil
        }
        observeModel()
        updateButton()
    }

    private func observeModel() {
        withObservationTracking {
            _ = model.statusIcon
            _ = model.settings.language
        } onChange: { [weak self] in
            Task { @MainActor in
                self?.updateButton()
                self?.observeModel()
            }
        }
    }

    private func updateButton() {
        guard let button = statusItem.button else { return }
        button.toolTip = L("TeleSave — dosyaları buraya bırakın")
        let symbol: String
        var title = ""
        var alpha: CGFloat = 1
        switch model.statusIcon {
        case .loggedOut:
            symbol = "paperplane"
            alpha = 0.5
        case .idle:
            symbol = "paperplane"
        case .uploading(let progress):
            symbol = "arrow.up.circle"
            title = " \(Int((progress * 100).rounded()))%"
        case .error:
            symbol = "exclamationmark.triangle"
        }
        let image = NSImage(systemSymbolName: dragHighlighted ? "paperplane.fill" : symbol, accessibilityDescription: "TeleSave")
        image?.isTemplate = true
        button.image = image
        button.title = title
        button.alphaValue = dragHighlighted ? 1 : alpha
        button.highlight(dragHighlighted || popover.isShown)
    }

    func togglePopover() {
        popover.isShown ? popover.performClose(nil) : showPopover()
    }

    func showPopover() {
        guard let button = statusItem.button else { return }
        NSApp.activate(ignoringOtherApps: true)
        if !popover.isShown {
            popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
        }
        popover.contentViewController?.view.window?.makeKey()
        updateButton()
    }

    func popoverDidClose(_ notification: Notification) {
        updateButton()
    }

    private func showMenu() {
        guard let button = statusItem.button else { return }
        let menu = NSMenu()
        menu.addItem(withTitle: L("Dosya seç…"), action: #selector(pickFiles), keyEquivalent: "o").target = self
        menu.addItem(withTitle: L("Panodan yapıştır"), action: #selector(paste), keyEquivalent: "v").target = self
        menu.addItem(withTitle: L("Kayıtlı Mesajları aç"), action: #selector(openTelegram), keyEquivalent: "").target = self
        menu.addItem(.separator())
        menu.addItem(withTitle: L("TeleSave'i aç"), action: #selector(openPopover), keyEquivalent: "").target = self
        menu.addItem(.separator())
        menu.addItem(withTitle: L("Çıkış"), action: #selector(quit), keyEquivalent: "q").target = self
        for item in menu.items where [#selector(pickFiles), #selector(openTelegram), #selector(paste)].contains(item.action) {
            item.isEnabled = model.canUpload
        }
        menu.autoenablesItems = false
        menu.popUp(positioning: nil, at: NSPoint(x: 0, y: button.bounds.height + 4), in: button)
    }

    @objc private func buttonPressed() { togglePopover() }
    @objc private func pickFiles() { model.pickFiles() }
    @objc private func paste() { model.pasteFromClipboard() }
    @objc private func openTelegram() { model.openInTelegram() }
    @objc private func openPopover() { showPopover() }
    @objc private func quit() { NSApp.terminate(nil) }
}

/// Transparent overlay on the status bar button that accepts file drops and forwards clicks.
final class StatusItemDropView: NSView {
    var onClick: (() -> Void)?
    var onRightClick: (() -> Void)?
    var onDragHighlight: ((Bool) -> Void)?
    var onDrop: (([URL]) -> Void)?

    override init(frame: NSRect) {
        super.init(frame: frame)
        registerForDraggedTypes([.fileURL])
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) is not supported")
    }

    private func fileURLs(from info: NSDraggingInfo) -> [URL] {
        info.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? []
    }

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        guard !fileURLs(from: sender).isEmpty else { return [] }
        onDragHighlight?(true)
        return .copy
    }

    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation {
        fileURLs(from: sender).isEmpty ? [] : .copy
    }

    override func draggingExited(_ sender: NSDraggingInfo?) {
        onDragHighlight?(false)
    }

    override func draggingEnded(_ sender: NSDraggingInfo) {
        onDragHighlight?(false)
    }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        let urls = fileURLs(from: sender)
        onDragHighlight?(false)
        guard !urls.isEmpty else { return false }
        onDrop?(urls)
        return true
    }

    override func mouseDown(with event: NSEvent) {
        if event.modifierFlags.contains(.control) {
            onRightClick?()
        } else {
            onClick?()
        }
    }

    override func rightMouseDown(with event: NSEvent) {
        onRightClick?()
    }
}
