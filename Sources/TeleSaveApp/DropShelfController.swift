import TeleSaveCore
import AppKit
import SwiftUI

/// Drop target that appears under the menu bar icon while files are dragged toward the menu bar.
///
/// Recent macOS versions draw menu bar items in a Window Server-owned menu bar, so drops onto the
/// status item itself may never reach the app; and holding a drag at the top screen edge opens the
/// Spaces bar. A real in-app panel just below the menu bar avoids both.
@MainActor
final class DropShelfController {
    private let anchor: () -> NSRect?
    private let destinationTitle: () -> String
    private let onDrop: ([URL]) -> Void

    private var panel: NSPanel?
    private var dropView: ShelfDropView?
    private var monitors: [Any] = []
    /// Drag pasteboard change count when the mouse went down; a different value means this press started a new drag.
    private var baselineChangeCount = NSPasteboard(name: .drag).changeCount
    private var hideTask: Task<Void, Never>?

    /// Distance from the bottom of the menu bar within which the shelf appears.
    private let triggerDistance: CGFloat = 120
    private let size = NSSize(width: 260, height: 84)

    init(anchor: @escaping () -> NSRect?, destinationTitle: @escaping () -> String, onDrop: @escaping ([URL]) -> Void) {
        self.anchor = anchor
        self.destinationTitle = destinationTitle
        self.onDrop = onDrop
    }

    func start() {
        // Global mouse monitors need no Accessibility permission; they only see events of other apps.
        if let down = NSEvent.addGlobalMonitorForEvents(matching: .leftMouseDown, handler: { [weak self] _ in
            Task { @MainActor in self?.baselineChangeCount = NSPasteboard(name: .drag).changeCount }
        }) { monitors.append(down) }
        if let drag = NSEvent.addGlobalMonitorForEvents(matching: .leftMouseDragged, handler: { [weak self] _ in
            Task { @MainActor in self?.dragMoved(to: NSEvent.mouseLocation) }
        }) { monitors.append(drag) }
        if let up = NSEvent.addGlobalMonitorForEvents(matching: .leftMouseUp, handler: { [weak self] _ in
            // Let a drop on the shelf finish before hiding it.
            Task { @MainActor in self?.hide(after: 0.3) }
        }) { monitors.append(up) }
    }

    func stop() {
        monitors.forEach(NSEvent.removeMonitor)
        monitors.removeAll()
        hide(after: 0)
    }

    private var isFileDrag: Bool {
        let pasteboard = NSPasteboard(name: .drag)
        guard pasteboard.changeCount != baselineChangeCount else { return false }
        return pasteboard.types?.contains(.fileURL) == true
    }

    private func dragMoved(to point: NSPoint) {
        guard isFileDrag, let screen = NSScreen.screens.first(where: { $0.frame.contains(point) }) else { return }
        let menuBarBottom = screen.visibleFrame.maxY
        let nearMenuBar = point.y >= menuBarBottom - triggerDistance
        let overShelf = panel?.isVisible == true && panel!.frame.insetBy(dx: -60, dy: -60).contains(point)
        if nearMenuBar || overShelf {
            show(on: screen, cursor: point)
        } else if panel?.isVisible == true {
            hide(after: 0.4)
        }
    }

    private func show(on screen: NSScreen, cursor: NSPoint) {
        hideTask?.cancel()
        hideTask = nil
        let panel = self.panel ?? makePanel()
        dropView?.update(destination: destinationTitle())

        // Under the menu bar icon when it is on this screen, otherwise under the cursor.
        let iconRect = anchor().flatMap { screen.frame.intersects($0) ? $0 : nil }
        let centerX = iconRect?.midX ?? cursor.x
        var origin = NSPoint(x: centerX - size.width / 2, y: screen.visibleFrame.maxY - size.height - 6)
        origin.x = min(max(origin.x, screen.visibleFrame.minX + 6), screen.visibleFrame.maxX - size.width - 6)
        if !panel.isVisible || panel.frame.origin != origin {
            panel.setFrame(NSRect(origin: origin, size: size), display: true)
        }
        if !panel.isVisible {
            panel.alphaValue = 0
            panel.orderFrontRegardless()
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.12
                panel.animator().alphaValue = 1
            }
        }
    }

    private func hide(after delay: TimeInterval) {
        guard panel?.isVisible == true else { return }
        hideTask?.cancel()
        hideTask = Task { [weak self] in
            if delay > 0 { try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000)) }
            guard !Task.isCancelled, let self else { return }
            // Still dragging over the shelf: keep it.
            if self.dropView?.isTargeted == true { return }
            self.panel?.orderOut(nil)
            self.dropView?.isTargeted = false
        }
    }

    private func makePanel() -> NSPanel {
        let panel = NSPanel(contentRect: NSRect(origin: .zero, size: size),
                            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isFloatingPanel = true
        panel.level = .popUpMenu
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient]
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = true
        panel.hidesOnDeactivate = false
        let view = ShelfDropView(frame: NSRect(origin: .zero, size: size))
        view.onDrop = { [weak self] urls in
            self?.panel?.orderOut(nil)
            self?.onDrop(urls)
        }
        panel.contentView = view
        self.panel = panel
        self.dropView = view
        return panel
    }
}

/// The shelf's content: a rounded drop area that highlights while files hover over it.
final class ShelfDropView: NSView {
    var onDrop: (([URL]) -> Void)?
    var isTargeted = false {
        didSet { host.rootView = ShelfContent(destination: destination, targeted: isTargeted) }
    }

    private var destination = "Saved Messages"
    private let host = NSHostingView(rootView: ShelfContent(destination: "Saved Messages", targeted: false))

    override init(frame: NSRect) {
        super.init(frame: frame)
        host.frame = bounds
        host.autoresizingMask = [.width, .height]
        addSubview(host)
        registerForDraggedTypes([.fileURL])
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) is not supported")
    }

    func update(destination: String) {
        guard destination != self.destination else { return }
        self.destination = destination
        host.rootView = ShelfContent(destination: destination, targeted: isTargeted)
    }

    private func fileURLs(_ info: NSDraggingInfo) -> [URL] {
        info.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? []
    }

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        guard !fileURLs(sender).isEmpty else { return [] }
        isTargeted = true
        return .copy
    }

    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation {
        isTargeted ? .copy : []
    }

    override func draggingExited(_ sender: NSDraggingInfo?) {
        isTargeted = false
    }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        let urls = fileURLs(sender)
        isTargeted = false
        guard !urls.isEmpty else { return false }
        onDrop?(urls)
        return true
    }
}

private struct ShelfContent: View {
    let destination: String
    let targeted: Bool

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: targeted ? "paperplane.fill" : "paperplane")
                .font(.system(size: 24, weight: .medium))
                .foregroundStyle(Color.accentColor)
            VStack(alignment: .leading, spacing: 2) {
                Text(targeted ? L("Bırakın") : L("TeleSave'e bırakın"))
                    .font(.headline)
                Text(destination)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 16)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 14))
        .overlay(
            RoundedRectangle(cornerRadius: 14)
                .strokeBorder(targeted ? Color.accentColor : Color.secondary.opacity(0.25),
                              style: StrokeStyle(lineWidth: targeted ? 2 : 1, dash: targeted ? [] : [6, 4]))
        )
        .scaleEffect(targeted ? 1.03 : 1)
        .animation(.easeOut(duration: 0.12), value: targeted)
    }
}
