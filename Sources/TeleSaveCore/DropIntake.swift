import Foundation

public struct IntakeRejection: Equatable, Sendable {
    public enum Reason: Equatable, Sendable {
        case empty
        case tooLarge(limit: Int64)
        case unreadable
        case duplicate
        case zipFailed
        case tooManyFiles(count: Int)
    }

    public let name: String
    public let reason: Reason

    public var message: String {
        switch reason {
        case .empty: return L("%@: dosya boş.", name)
        case .tooLarge(let limit):
            return L("%@: %@ sınırını aşıyor.", name, ByteCountFormatter.string(fromByteCount: limit, countStyle: .file))
        case .unreadable: return L("%@: okunamadı.", name)
        case .duplicate: return L("%@: az önce zaten gönderildi.", name)
        case .zipFailed: return L("%@: klasör sıkıştırılamadı.", name)
        case .tooManyFiles(let count):
            return L("%@: %@ dosya içeriyor; tek tek en fazla %@ dosya gönderilir. Zip olarak göndermeyi deneyin.",
                     name, String(count), String(DropIntake.maxFilesPerFolder))
        }
    }
}

/// How a dropped folder is sent. Telegram has no folder message, so it is either one zip or its files.
public enum FolderMode: String, Codable, CaseIterable, Sendable {
    case zip
    case expand
}

public struct IntakeResult: Equatable, Sendable {
    public var accepted: [PreparedFile] = []
    public var rejected: [IntakeRejection] = []
}

/// Turns dropped URLs into uploadable files: zips folders, validates size, rejects recent duplicates.
public actor DropIntake {
    private let stagingDirectory: URL
    private let duplicateWindow: TimeInterval
    private let now: @Sendable () -> Date
    private var recent: [String: Date] = [:]

    public init(stagingDirectory: URL, duplicateWindow: TimeInterval = 60, now: @escaping @Sendable () -> Date = { Date() }) {
        self.stagingDirectory = stagingDirectory
        self.duplicateWindow = duplicateWindow
        self.now = now
    }

    public static let maxFilesPerFolder = 1000

    /// - folderMode: `.zip` sends a folder as one archive; `.expand` sends every file inside it
    ///   (hidden files skipped, app bundles and other packages still zipped).
    /// - temporary: the files were created by TeleSave (e.g. a pasted image) and are deleted after upload.
    public func prepare(_ urls: [URL], maxFileSize: Int64, folderMode: FolderMode = .zip, temporary: Bool = false) -> IntakeResult {
        var result = IntakeResult()
        let fm = FileManager.default
        let current = now()
        recent = recent.filter { current.timeIntervalSince($0.value) < duplicateWindow }

        var candidates: [URL] = []
        for url in urls {
            guard folderMode == .expand, Self.isPlainDirectory(url) else {
                candidates.append(url)
                continue
            }
            let files = Self.filesInside(url)
            if files.count > Self.maxFilesPerFolder {
                result.rejected.append(IntakeRejection(name: url.lastPathComponent, reason: .tooManyFiles(count: files.count)))
            } else if files.isEmpty {
                result.rejected.append(IntakeRejection(name: url.lastPathComponent, reason: .empty))
            } else {
                candidates.append(contentsOf: files)
            }
        }

        for url in candidates {
            let name = url.lastPathComponent
            var isDir: ObjCBool = false
            guard fm.fileExists(atPath: url.path, isDirectory: &isDir), fm.isReadableFile(atPath: url.path) else {
                result.rejected.append(IntakeRejection(name: name, reason: .unreadable))
                continue
            }

            let file: PreparedFile
            if isDir.boolValue {
                guard let zipped = zip(directory: url) else {
                    result.rejected.append(IntakeRejection(name: name, reason: .zipFailed))
                    continue
                }
                file = zipped
            } else {
                guard let size = Self.fileSize(url) else {
                    result.rejected.append(IntakeRejection(name: name, reason: .unreadable))
                    continue
                }
                file = PreparedFile(url: url, displayName: name, size: size, isTemporary: temporary,
                                    bookmark: temporary ? nil : Self.bookmark(for: url))
            }

            if file.size == 0 {
                result.rejected.append(IntakeRejection(name: name, reason: .empty))
                discard(file)
                continue
            }
            if file.size > maxFileSize {
                result.rejected.append(IntakeRejection(name: file.displayName, reason: .tooLarge(limit: maxFileSize)))
                discard(file)
                continue
            }
            let key = Self.duplicateKey(original: url, size: file.size)
            if recent[key] != nil {
                result.rejected.append(IntakeRejection(name: file.displayName, reason: .duplicate))
                discard(file)
                continue
            }
            recent[key] = current
            result.accepted.append(file)
        }
        return result
    }

    private func discard(_ file: PreparedFile) {
        if file.isTemporary {
            try? FileManager.default.removeItem(at: file.url.deletingLastPathComponent())
        }
    }

    private func zip(directory: URL) -> PreparedFile? {
        let fm = FileManager.default
        let folder = stagingDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        let destination = folder.appendingPathComponent(directory.lastPathComponent + ".zip")
        do {
            try fm.createDirectory(at: folder, withIntermediateDirectories: true)
        } catch {
            return nil
        }

        var coordinatorError: NSError?
        var copyError: Error?
        // `.forUploading` makes the coordinator produce a zip archive of the directory.
        NSFileCoordinator().coordinate(readingItemAt: directory, options: .forUploading, error: &coordinatorError) { zipURL in
            do {
                try fm.copyItem(at: zipURL, to: destination)
            } catch {
                copyError = error
            }
        }
        guard coordinatorError == nil, copyError == nil, let size = Self.fileSize(destination) else {
            try? fm.removeItem(at: folder)
            return nil
        }
        return PreparedFile(url: destination, displayName: destination.lastPathComponent, size: size, isTemporary: true, bookmark: nil)
    }

    /// A directory that is not a package (an .app or similar bundle is treated like a file and zipped).
    static func isPlainDirectory(_ url: URL) -> Bool {
        let values = try? url.resourceValues(forKeys: [.isDirectoryKey, .isPackageKey])
        return values?.isDirectory == true && values?.isPackage != true
    }

    /// Regular files and packages inside `directory`, recursively, hidden items skipped, sorted by path.
    static func filesInside(_ directory: URL) -> [URL] {
        let keys: [URLResourceKey] = [.isRegularFileKey, .isPackageKey]
        guard let enumerator = FileManager.default.enumerator(
            at: directory, includingPropertiesForKeys: keys, options: [.skipsHiddenFiles, .skipsPackageDescendants]
        ) else { return [] }
        var files: [URL] = []
        for case let url as URL in enumerator {
            let values = try? url.resourceValues(forKeys: Set(keys))
            if values?.isRegularFile == true || values?.isPackage == true {
                files.append(url)
            }
        }
        return files.sorted { $0.path.localizedStandardCompare($1.path) == .orderedAscending }
    }

    static func fileSize(_ url: URL) -> Int64? {
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: url.path) else { return nil }
        return (attrs[.size] as? NSNumber)?.int64Value
    }

    static func duplicateKey(original url: URL, size: Int64) -> String {
        let mtime = (try? FileManager.default.attributesOfItem(atPath: url.path)[.modificationDate] as? Date)?
            .timeIntervalSince1970 ?? 0
        return "\(url.standardizedFileURL.path)|\(size)|\(mtime)"
    }

    static func bookmark(for url: URL) -> Data? {
        // Security-scoped bookmark lets a sandboxed app reopen the file after relaunch.
        (try? url.bookmarkData(options: .withSecurityScope, includingResourceValuesForKeys: nil, relativeTo: nil))
            ?? (try? url.bookmarkData())
    }
}
