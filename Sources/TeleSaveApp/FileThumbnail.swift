import AppKit
import QuickLookThumbnailing
import SwiftUI
import UniformTypeIdentifiers

/// Quick Look thumbnail of a queued file (photos, videos, PDFs, documents), falling back to the
/// Finder icon for its type when no preview exists or the file is gone (e.g. a deleted temp zip).
struct FileThumbnail: View {
    let path: String
    var size: CGFloat = 36

    @State private var image: NSImage?
    @Environment(\.displayScale) private var scale

    var body: some View {
        Group {
            if let image {
                Image(nsImage: image)
                    .resizable()
                    .scaledToFill()
            } else {
                Image(nsImage: ThumbnailCache.typeIcon(for: path))
                    .resizable()
                    .scaledToFit()
            }
        }
        .frame(width: size, height: size)
        .clipShape(RoundedRectangle(cornerRadius: 6))
        .task(id: path) {
            image = await ThumbnailCache.thumbnail(for: path, size: CGSize(width: size, height: size), scale: scale)
        }
        .accessibilityHidden(true)
    }
}

@MainActor
enum ThumbnailCache {
    private static let cache = NSCache<NSString, NSImage>()
    /// Paths with no real preview, so the list does not keep asking Quick Look while progress updates.
    private static var misses = Set<String>()

    static func thumbnail(for path: String, size: CGSize, scale: CGFloat) async -> NSImage? {
        if let cached = cache.object(forKey: path as NSString) { return cached }
        guard !misses.contains(path), FileManager.default.fileExists(atPath: path) else { return nil }

        let request = QLThumbnailGenerator.Request(
            fileAt: URL(fileURLWithPath: path),
            size: size,
            scale: scale,
            representationTypes: .thumbnail
        )
        do {
            let representation = try await QLThumbnailGenerator.shared.generateBestRepresentation(for: request)
            let image = representation.nsImage
            cache.setObject(image, forKey: path as NSString)
            return image
        } catch {
            misses.insert(path)
            return nil
        }
    }

    /// Finder icon; uses the extension when the file is no longer readable.
    static func typeIcon(for path: String) -> NSImage {
        if FileManager.default.fileExists(atPath: path) {
            return NSWorkspace.shared.icon(forFile: path)
        }
        let type = UTType(filenameExtension: (path as NSString).pathExtension) ?? .data
        return NSWorkspace.shared.icon(for: type)
    }
}
