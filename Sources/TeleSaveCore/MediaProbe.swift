import AVFoundation
import Foundation
import ImageIO

/// Reads width/height/duration so Telegram renders photo and video previews correctly.
public enum MediaProbe {
    public static func dimensions(path: String, kind: SendKind) async -> MediaDimensions? {
        let url = URL(fileURLWithPath: path)
        switch kind {
        case .document:
            return nil
        case .photo:
            guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
                  let props = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
                  let width = props[kCGImagePropertyPixelWidth] as? Int,
                  let height = props[kCGImagePropertyPixelHeight] as? Int else { return nil }
            // EXIF orientations 5–8 rotate the image by 90°.
            let orientation = props[kCGImagePropertyOrientation] as? Int ?? 1
            return orientation >= 5 ? MediaDimensions(width: height, height: width) : MediaDimensions(width: width, height: height)
        case .video:
            let asset = AVURLAsset(url: url)
            guard let track = try? await asset.loadTracks(withMediaType: .video).first,
                  let (size, transform) = try? await track.load(.naturalSize, .preferredTransform) else { return nil }
            let rect = CGRect(origin: .zero, size: size).applying(transform)
            let duration = (try? await asset.load(.duration).seconds) ?? 0
            return MediaDimensions(width: Int(abs(rect.width)), height: Int(abs(rect.height)), duration: Int(duration.rounded()))
        }
    }
}
