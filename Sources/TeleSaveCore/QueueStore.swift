import Foundation

/// Persists the upload queue as JSON so unfinished uploads resume after relaunch.
public final class QueueStore: @unchecked Sendable {
    private let fileURL: URL
    private let lock = NSLock()

    public init(fileURL: URL) {
        self.fileURL = fileURL
    }

    /// Loads saved items. Items that were mid-upload come back as queued: their TDLib message ids
    /// belonged to the previous process and are gone.
    public func load() -> [UploadItem] {
        lock.lock(); defer { lock.unlock() }
        guard let data = try? Data(contentsOf: fileURL),
              let items = try? JSONDecoder().decode([UploadItem].self, from: data) else { return [] }
        return items.map { item in
            var item = item
            if case .uploading = item.state {
                item.state = .queued
                item.messageId = nil
            }
            item.notBefore = nil
            return item
        }
    }

    public func save(_ items: [UploadItem]) {
        lock.lock(); defer { lock.unlock() }
        do {
            try FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            let data = try JSONEncoder().encode(items)
            try data.write(to: fileURL, options: .atomic)
        } catch {
            NSLog("TeleSave: queue save failed: \(error)")
        }
    }
}
