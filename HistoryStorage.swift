import AppKit
import CryptoKit
import Security
import Darwin

enum HistoryStorageError: LocalizedError {
    case keychain(OSStatus), missingKey, invalidFormat, unsafeImageName, storageInUse

    var errorDescription: String? {
        switch self {
        case .keychain(let status): return "Keychain access failed (\(status)). Unlock your login keychain and retry."
        case .missingKey: return "The encryption key is missing. Existing encrypted history has been preserved. Restore its original Keychain item; a new key cannot decrypt it."
        case .invalidFormat: return "History could not be authenticated or its format is unsupported. Existing files have been preserved."
        case .unsafeImageName: return "An invalid image file reference was rejected."
        case .storageInUse: return "Another copy of Clipboard History is using this history. Quit it and retry."
        }
    }
}

enum HistoryKeychain {
    static func loadOrCreate(allowCreation: Bool) throws -> SymmetricKey {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: "local.artemiy.clipboardhistory.encryption",
            kSecAttrAccount as String: "history-aes256-v1",
            kSecAttrSynchronizable as String: false
        ]
        var lookup = query
        lookup[kSecReturnData as String] = true
        lookup[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(lookup as CFDictionary, &result)
        if status == errSecSuccess {
            guard let bytes = result as? Data, bytes.count == 32 else { throw HistoryStorageError.invalidFormat }
            return SymmetricKey(data: bytes)
        }
        guard status == errSecItemNotFound else { throw HistoryStorageError.keychain(status) }
        guard allowCreation else { throw HistoryStorageError.missingKey }

        // Use the login Keychain for local/ad-hoc builds. Explicitly trust only
        // this application, never an empty ACL or a file next to the ciphertext.
        var access: SecAccess?
        let accessStatus = SecAccessCreate("Clipboard History encryption key" as CFString, nil, &access)
        guard accessStatus == errSecSuccess, let access else { throw HistoryStorageError.keychain(accessStatus) }
        let key = SymmetricKey(size: .bits256)
        var attributes = query
        attributes[kSecAttrLabel as String] = "Clipboard History encryption key"
        attributes[kSecAttrAccess as String] = access
        attributes[kSecValueData as String] = key.withUnsafeBytes { Data($0) }
        let added = SecItemAdd(attributes as CFDictionary, nil)
        if added == errSecDuplicateItem { return try loadOrCreate(allowCreation: false) }
        guard added == errSecSuccess else { throw HistoryStorageError.keychain(added) }
        return key
    }
}

struct HistoryCipher: Sendable {
    let key: SymmetricKey
    private let header = Data("CHENC1".utf8)

    func seal(_ plaintext: Data, context: String) throws -> Data {
        let box = try AES.GCM.seal(plaintext, using: key, authenticating: Data(context.utf8))
        guard let combined = box.combined else { throw HistoryStorageError.invalidFormat }
        return header + combined
    }

    func open(_ ciphertext: Data, context: String) throws -> Data {
        guard ciphertext.starts(with: header) else { throw HistoryStorageError.invalidFormat }
        return try AES.GCM.open(AES.GCM.SealedBox(combined: ciphertext.dropFirst(header.count)),
                                using: key, authenticating: Data(context.utf8))
    }
}

final class HistoryStorage {
    struct LoadResult {
        var clips: [Clip]
        var skippedImages: Int
    }
    private struct Archive: Codable { var version = 3; var clips: [Clip] }
    let folder: URL
    private let manifest: URL
    private let images: URL
    private let cipher: HistoryCipher
    private let manager = FileManager.default
    private var lockDescriptor: Int32 = -1

    init(folder: URL, keyProvider: (Bool) throws -> SymmetricKey = HistoryKeychain.loadOrCreate) throws {
        self.folder = folder
        manifest = folder.appendingPathComponent("history-v3.enc")
        images = folder.appendingPathComponent("encrypted-images", isDirectory: true)
        try manager.createDirectory(at: folder, withIntermediateDirectories: true,
                                    attributes: [.posixPermissions: 0o700])
        let descriptor = Darwin.open(folder.appendingPathComponent("history.lock").path,
                                     O_CREAT | O_RDWR | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard descriptor >= 0 else { throw HistoryStorageError.storageInUse }
        var initialized = false
        defer { if !initialized { Darwin.close(descriptor) } }
        guard flock(descriptor, LOCK_EX | LOCK_NB) == 0 else { throw HistoryStorageError.storageInUse }
        // Orphaned encrypted images also count as an existing store. A missing
        // index must not cause key replacement or deletion of recoverable data.
        let hasEncryptedImages: Bool
        if manager.fileExists(atPath: images.path) {
            hasEncryptedImages = !(try manager.contentsOfDirectory(atPath: images.path)).isEmpty
        } else {
            hasEncryptedImages = false
        }
        cipher = HistoryCipher(key: try keyProvider(!manager.fileExists(atPath: manifest.path) && !hasEncryptedImages))
        try manager.createDirectory(at: images, withIntermediateDirectories: true,
                                    attributes: [.posixPermissions: 0o700])
        try manager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: folder.path)
        try manager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: images.path)
        lockDescriptor = descriptor
        initialized = true
    }

    deinit { if lockDescriptor >= 0 { Darwin.close(lockDescriptor) } }

    func load() throws -> LoadResult {
        if manager.fileExists(atPath: manifest.path) {
            let archive = try JSONDecoder().decode(Archive.self, from:
                cipher.open(Data(contentsOf: manifest), context: "history-v3"))
            guard archive.version == 3 else { throw HistoryStorageError.invalidFormat }
            let result = try recoverImages(archive.clips)
            // Commit recovered references before removing any obsolete files.
            if result.skippedImages > 0 { try save(result.clips) }
            try removeLegacyFiles()
            return result
        }

        let current = folder.appendingPathComponent("history-v2.json")
        let legacy = folder.appendingPathComponent("history.json")
        let source = manager.fileExists(atPath: current.path) ? current : legacy
        guard manager.fileExists(atPath: source.path) else {
            guard try manager.contentsOfDirectory(atPath: images.path).isEmpty else {
                throw HistoryStorageError.invalidFormat
            }
            try save([])
            // No legacy archive means there is no recoverable plaintext history.
            try removeLegacyFiles()
            return LoadResult(clips: [], skippedImages: 0)
        }
        let oldClips = try JSONDecoder().decode([Clip].self, from: Data(contentsOf: source))
        var migrated: [Clip] = []
        var skipped = 0
        for clip in oldClips {
            if clip.kind == .text { migrated.append(clip); continue }
            let data: Data?
            if let embedded = clip.imageData { data = embedded }
            else if let name = clip.imageFile {
                do {
                    let path = try safeImageURL(name, in: folder.appendingPathComponent("images"))
                    data = try Data(contentsOf: path)
                } catch {
                    guard isRecoverableImageError(error) else { throw error }
                    data = nil
                }
            } else { data = nil }
            guard let data, NSImage(data: data) != nil else { skipped += 1; continue }
            var imageClip = clip
            imageClip.imageData = data
            imageClip.imageFile = nil
            // Write errors abort migration; they are never treated as corrupt images.
            migrated.append(try storeImage(imageClip))
        }
        try save(migrated)
        // Verify the committed ciphertext before deleting the plaintext originals.
        _ = try cipher.open(Data(contentsOf: manifest), context: "history-v3")
        try removeLegacyFiles()
        return LoadResult(clips: migrated, skippedImages: skipped)
    }

    func save(_ clips: [Clip]) throws {
        let encoded = try JSONEncoder().encode(Archive(clips: clips))
        try write(cipher.seal(encoded, context: "history-v3"), to: manifest)
        let referenced = Set(clips.compactMap(\.imageFile))
        for file in (try? manager.contentsOfDirectory(at: images, includingPropertiesForKeys: nil)) ?? []
            where !referenced.contains(file.lastPathComponent) {
            // Cleanup failure must not turn a successfully committed archive into
            // a failed transaction. These files contain only ciphertext.
            try? manager.removeItem(at: file)
        }
    }

    func storeImage(_ clip: Clip) throws -> Clip {
        guard clip.kind == .image, let data = clip.imageData else { return clip }
        let name = clip.id.uuidString + ".enc"
        let path = try safeImageURL(name, in: images)
        try write(cipher.seal(data, context: "image/" + name), to: path)
        var stored = clip
        stored.imageFile = name
        stored.imageBytes = data.count
        stored.imageData = nil
        return stored
    }

    func imageData(for clip: Clip) throws -> Data {
        try imageReader(for: clip)()
    }

    func imageReader(for clip: Clip) throws -> @Sendable () throws -> Data {
        guard let name = clip.imageFile, name == clip.id.uuidString + ".enc" else {
            throw HistoryStorageError.unsafeImageName
        }
        let path = try safeImageURL(name, in: images)
        let cipher = self.cipher
        return { try cipher.open(Data(contentsOf: path), context: "image/" + name) }
    }

    private func recoverImages(_ clips: [Clip]) throws -> LoadResult {
        var recovered: [Clip] = []
        for clip in clips {
            if clip.kind == .text { recovered.append(clip); continue }
            do {
                if NSImage(data: try imageData(for: clip)) != nil { recovered.append(clip) }
            } catch {
                // Permission, I/O, and disk errors stop loading instead of
                // permanently dropping temporarily inaccessible images.
                guard isRecoverableImageError(error) else { throw error }
            }
        }
        return LoadResult(clips: recovered, skippedImages: clips.count - recovered.count)
    }

    private func isRecoverableImageError(_ error: Error) -> Bool {
        if error is CryptoKitError { return true }
        if let storageError = error as? HistoryStorageError {
            switch storageError {
            case .unsafeImageName, .invalidFormat: return true
            default: return false
            }
        }
        let cocoa = error as NSError
        return cocoa.domain == NSCocoaErrorDomain &&
            (cocoa.code == NSFileReadNoSuchFileError || cocoa.code == NSFileNoSuchFileError)
    }

    private func safeImageURL(_ name: String, in directory: URL) throws -> URL {
        guard !name.isEmpty, !name.contains("/"), !name.contains("\\"), name != ".", name != ".." else {
            throw HistoryStorageError.unsafeImageName
        }
        let url = directory.appendingPathComponent(name)
        if (try? url.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true {
            throw HistoryStorageError.unsafeImageName
        }
        return url
    }

    private func write(_ data: Data, to url: URL) throws {
        try data.write(to: url, options: .atomic)
        try manager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }

    private func removeLegacyFiles() throws {
        for name in ["history-v2.json", "history.json", "images"] {
            let url = folder.appendingPathComponent(name)
            if manager.fileExists(atPath: url.path) { try manager.removeItem(at: url) }
        }
    }
}
