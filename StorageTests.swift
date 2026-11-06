import AppKit
import CryptoKit
import ImageIO

private enum StorageTestError: Error { case expectedFailure }

private func require(_ condition: @autoclosure () throws -> Bool) rethrows {
    let result = try condition()
    precondition(result)
}

private func mustFail(_ operation: () throws -> Void) throws {
    do { try operation() }
    catch { return }
    throw StorageTestError.expectedFailure
}

func runStorageTests() throws {
    let manager = FileManager.default
    let root = manager.temporaryDirectory.appendingPathComponent("clipboard-tests-" + UUID().uuidString)
    try manager.createDirectory(at: root, withIntermediateDirectories: true)
    defer { try? manager.removeItem(at: root) }
    let key = SymmetricKey(size: .bits256)
    let cipher = HistoryCipher(key: key)
    let secret = Data("A disposable private clipboard note".utf8)
    let first = try cipher.seal(secret, context: "history-v3")
    let second = try cipher.seal(secret, context: "history-v3")
    precondition(first != second, "Each encryption must use a fresh nonce")
    precondition(first.range(of: secret) == nil)
    try require(try cipher.open(first, context: "history-v3") == secret)
    var tampered = first
    tampered[tampered.count - 1] ^= 1
    try mustFail { _ = try cipher.open(tampered, context: "history-v3") }
    try mustFail { _ = try cipher.open(first, context: "image/test.enc") }
    try mustFail { _ = try HistoryCipher(key: SymmetricKey(size: .bits256)).open(first, context: "history-v3") }
    try mustFail { _ = try cipher.open(Data("plaintext".utf8), context: "history-v3") }

    let iconURL = Bundle.main.url(forResource: "AppIcon", withExtension: "icns")!
    let image = NSImage(contentsOf: iconURL)!.tiffRepresentation!
    let wide = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 320, pixelsHigh: 160,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    wide.bitmapData!.initialize(repeating: 0, count: wide.bytesPerRow * wide.pixelsHigh)
    let widePNG = wide.representation(using: .png, properties: [:])!
    let thumbnail = ThumbnailRenderer.png(data: widePNG)!
    let thumbnailSource = CGImageSourceCreateWithData(thumbnail as CFData, nil)!
    let thumbnailImage = CGImageSourceCreateImageAtIndex(thumbnailSource, 0, nil)!
    precondition(thumbnailImage.width == 112 && thumbnailImage.height == 56)
    precondition(ThumbnailRenderer.png(data: Data("not an image".utf8)) == nil)
    precondition(ThumbnailRenderer.png(data: widePNG, maxPixels: 0) == nil)
    var note = Clip(kind: .text, text: String(decoding: secret, as: UTF8.self), fingerprint: "test-note")
    note.pinned = true
    note.favorite = true
    let imageClip = Clip(kind: .image, imageData: image, fingerprint: "test-image")
    let folder = root.appendingPathComponent("migration")
    let oldImages = folder.appendingPathComponent("images")
    try manager.createDirectory(at: oldImages, withIntermediateDirectories: true)
    try image.write(to: oldImages.appendingPathComponent("legacy.tiff"))
    var legacyImage = imageClip
    legacyImage.imageData = nil
    legacyImage.imageFile = "legacy.tiff"
    let missing = Clip(kind: .image, imageFile: "missing.png", fingerprint: "missing")
    let traversal = Clip(kind: .image, imageFile: "../outside.png", fingerprint: "traversal")
    let legacyURL = folder.appendingPathComponent("history-v2.json")
    try JSONEncoder().encode([note, legacyImage, missing, traversal]).write(to: legacyURL)

    var storage: HistoryStorage? = try HistoryStorage(folder: folder) { allowed in
        precondition(allowed)
        return key
    }
    let loaded = try storage!.load()
    precondition(loaded.clips.count == 2 && loaded.skippedImages == 2)
    precondition(loaded.clips[0].isPinned && loaded.clips[0].isFavorite)
    precondition(!manager.fileExists(atPath: legacyURL.path))
    precondition(!manager.fileExists(atPath: oldImages.path))
    let migratedImage = loaded.clips.first { $0.kind == .image }!
    try require(try storage!.imageData(for: migratedImage) == image)
    let archive = folder.appendingPathComponent("history-v3.enc")
    let archiveBytes = try Data(contentsOf: archive)
    precondition(archiveBytes.range(of: secret) == nil)
    let permissions = try manager.attributesOfItem(atPath: archive.path)[.posixPermissions] as! NSNumber
    precondition(permissions.intValue == 0o600)
    try mustFail { _ = try HistoryStorage(folder: folder) { _ in key } }
    storage = nil

    // A missing key must never generate a replacement or overwrite the archive.
    try mustFail {
        _ = try HistoryStorage(folder: folder) { allowed in
            precondition(!allowed)
            throw HistoryStorageError.missingKey
        }
    }
    try require(try Data(contentsOf: archive) == archiveBytes)
    storage = try HistoryStorage(folder: folder) { allowed in
        precondition(!allowed)
        return key
    }
    try require(try storage!.load().clips.count == 2)
    storage = nil

    // Damage one encrypted image: keep the text and persist the recovered index.
    let encryptedImageURL = folder.appendingPathComponent("encrypted-images").appendingPathComponent(migratedImage.imageFile!)
    var imageBytes = try Data(contentsOf: encryptedImageURL)
    imageBytes[imageBytes.count - 1] ^= 1
    try imageBytes.write(to: encryptedImageURL)
    storage = try HistoryStorage(folder: folder) { _ in key }
    let recovered = try storage!.load()
    precondition(recovered.clips.count == 1 && recovered.skippedImages == 1)
    precondition(recovered.clips[0].text == note.text)
    precondition(!manager.fileExists(atPath: encryptedImageURL.path))
    let newImage = try storage!.storeImage(imageClip)
    try storage!.save([note, newImage])
    try storage!.save([note])
    precondition(!manager.fileExists(atPath: folder.appendingPathComponent("encrypted-images").appendingPathComponent(newImage.imageFile!).path))
    storage = nil

    // An unauthenticated index must not fall back to plaintext or be reset.
    var brokenArchive = try Data(contentsOf: archive)
    brokenArchive[brokenArchive.count - 1] ^= 1
    try brokenArchive.write(to: archive)
    try JSONEncoder().encode([note]).write(to: legacyURL)
    storage = try HistoryStorage(folder: folder) { _ in key }
    try mustFail { _ = try storage!.load() }
    try require(try Data(contentsOf: archive) == brokenArchive)
    precondition(manager.fileExists(atPath: legacyURL.path))
    storage = nil

    // Failure before commit leaves plaintext migration inputs intact.
    let failedFolder = root.appendingPathComponent("failed-migration")
    try manager.createDirectory(at: failedFolder, withIntermediateDirectories: true)
    let failedLegacy = failedFolder.appendingPathComponent("history.json")
    try JSONEncoder().encode([note, imageClip]).write(to: failedLegacy)
    storage = try HistoryStorage(folder: failedFolder) { _ in key }
    try manager.createDirectory(at: failedFolder.appendingPathComponent("encrypted-images").appendingPathComponent(imageClip.id.uuidString + ".enc"), withIntermediateDirectories: true)
    try mustFail { _ = try storage!.load() }
    precondition(manager.fileExists(atPath: failedLegacy.path))
    storage = nil

    // A missing index must not silently discard orphaned encrypted images.
    let orphanFolder = root.appendingPathComponent("orphaned-index")
    storage = try HistoryStorage(folder: orphanFolder) { _ in key }
    let orphanImage = try storage!.storeImage(imageClip)
    try storage!.save([orphanImage])
    storage = nil
    try manager.removeItem(at: orphanFolder.appendingPathComponent("history-v3.enc"))
    storage = try HistoryStorage(folder: orphanFolder) { allowed in
        precondition(!allowed)
        return key
    }
    try mustFail { _ = try storage!.load() }
    let orphanBytes = try storage!.imageData(for: orphanImage)
    precondition(orphanBytes == image)
    storage = nil

    // Old archives without the new optional metadata remain decodable.
    let originalJSON = "[{\"kind\":\"text\",\"id\":\"\(UUID().uuidString)\",\"created\":0,\"fingerprint\":\"old\",\"text\":\"old note\"}]"
    let old = try JSONDecoder().decode([Clip].self, from: Data(originalJSON.utf8))
    precondition(!old[0].isPinned && !old[0].isFavorite)
    let cutoff = Date()
    var expired = note
    expired.pinned = false
    expired.created = cutoff.addingTimeInterval(-60)
    var pinned = expired
    pinned.id = UUID(); pinned.pinned = true
    var recent = expired
    recent.id = UUID(); recent.created = cutoff.addingTimeInterval(60)
    let kept = retainedClips([recent, expired, pinned], cutoff: cutoff, maxItems: 2, maxBytes: 10_000)
    precondition(kept.map(\.id) == [pinned.id, recent.id])
    precondition(retainedClips([recent, pinned], cutoff: cutoff, maxItems: 1, maxBytes: 10_000).map(\.id) == [pinned.id])
    precondition(retainedClips([pinned], cutoff: cutoff, maxItems: 10, maxBytes: 1).isEmpty)
    var sensitivePin = pinned
    sensitivePin.text = "Authorization: Bearer disposable-test-value"
    precondition(retainedClips([sensitivePin], cutoff: cutoff, maxItems: 10, maxBytes: 10_000).isEmpty)
    print("Encrypted history, migration, recovery, and retention tests OK")
}
