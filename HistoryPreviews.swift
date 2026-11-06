import Foundation
import ImageIO
import UniformTypeIdentifiers

// Serial background decoding keeps scrolling from decoding many full-size images
// simultaneously. Only the small result returns to the UI; no thumbnail is saved.
actor ThumbnailRenderer {
    static let shared = ThumbnailRenderer()

    func render(read: @Sendable () throws -> Data) -> Data? {
        guard !Task.isCancelled, let data = try? read() else { return nil }
        return Self.png(data: data)
    }

    static func png(data: Data, maxPixels: Int = 112) -> Data? {
        guard maxPixels > 0, !Task.isCancelled,
              let source = CGImageSourceCreateWithData(data as CFData,
                  [kCGImageSourceShouldCache: false] as CFDictionary),
              let thumbnail = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                  kCGImageSourceCreateThumbnailFromImageAlways: true,
                  kCGImageSourceCreateThumbnailWithTransform: true,
                  kCGImageSourceThumbnailMaxPixelSize: maxPixels,
                  kCGImageSourceShouldCacheImmediately: true
              ] as CFDictionary), !Task.isCancelled else { return nil }
        let result = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(result, UTType.png.identifier as CFString, 1, nil) else { return nil }
        CGImageDestinationAddImage(destination, thumbnail, nil)
        guard CGImageDestinationFinalize(destination) else { return nil }
        return result as Data
    }
}
