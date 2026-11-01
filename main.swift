import AppKit
import Carbon
import CryptoKit
import SwiftUI

private enum ClipKind: String, Codable, CaseIterable {
    case text, image
    var title: String { self == .text ? "Text" : "Images" }
}

private struct Clip: Identifiable, Codable {
    var id = UUID()
    var kind: ClipKind
    var text: String?
    var imageData: Data?
    var imageFile: String?
    var imageBytes: Int?
    var created = Date()
    var fingerprint: String

    var title: String {
        if kind == .image {
            return "Image · \(created.formatted(date: .abbreviated, time: .shortened))"
        }
        let compact = (text ?? "").replacingOccurrences(of: "\\s+", with: " ", options: .regularExpression)
        return String(compact.prefix(110))
    }
}

@MainActor private final class ClipboardModel: ObservableObject {
    @Published var items: [Clip] = []
    @Published var query = ""
    @Published var filter: ClipKind? = nil
    @Published var selectedID: UUID?
    @Published var focusToken = 0
    @Published var pasteTargetName = "Previous app"
    @Published var showActions = false
    weak var controller: ClipboardApp?
    private var lastChangeCount = NSPasteboard.general.changeCount
    private let storeURL: URL
    private let imageDirectory: URL
    private let legacyURL: URL
    private let imageCache = NSCache<NSString, NSImage>()
    private let maxItems = 100
    private let maxBytes = 40_000_000

    init() {
        let folder = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/ClipboardHistory", isDirectory: true)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true,
                                                  attributes: [.posixPermissions: 0o700])
        storeURL = folder.appendingPathComponent("history-v2.json")
        legacyURL = folder.appendingPathComponent("history.json")
        imageDirectory = folder.appendingPathComponent("images", isDirectory: true)
        try? FileManager.default.createDirectory(at: imageDirectory, withIntermediateDirectories: true,
                                                  attributes: [.posixPermissions: 0o700])
        imageCache.countLimit = 3
        if let data = try? Data(contentsOf: storeURL),
           let saved = try? JSONDecoder().decode([Clip].self, from: data),
           saved.allSatisfy({ $0.imageFile == nil ||
               FileManager.default.fileExists(atPath: imageDirectory.appendingPathComponent($0.imageFile!).path) }) {
            items = Array(saved.prefix(maxItems))
        } else if let data = try? Data(contentsOf: legacyURL),
                  let saved = try? JSONDecoder().decode([Clip].self, from: data) {
            let migrated = Array(saved.prefix(maxItems)).map { migrate($0) }
            if migrated.allSatisfy({ $0 != nil }) {
                items = migrated.compactMap { $0 }
                _ = save()
            } else {
                items = Array(saved.prefix(maxItems))
                NSLog("ClipboardHistory: migration incomplete; keeping legacy history")
            }
        }
        selectedID = items.first?.id
    }

    var visible: [Clip] {
        items.filter { clip in
            (filter == nil || clip.kind == filter) &&
            (query.isEmpty || clip.title.localizedCaseInsensitiveContains(query) ||
             (clip.text?.localizedCaseInsensitiveContains(query) ?? false))
        }
    }

    var selected: Clip? { visible.first { $0.id == selectedID } ?? visible.first }

    func capture() {
        let board = NSPasteboard.general
        guard board.changeCount != lastChangeCount else { return }
        lastChangeCount = board.changeCount
        let imageTypes = board.types ?? []
        if (imageTypes.contains(.png) || imageTypes.contains(.tiff)),
           let image = NSImage(pasteboard: board),
           let data = board.data(forType: .png) ?? image.tiffRepresentation,
           data.count <= 10_000_000 {
            add(Clip(kind: .image, text: nil, imageData: data, fingerprint: hash(data)))
        } else if let text = board.string(forType: .string), !text.isEmpty, text.utf8.count <= 1_000_000 {
            add(Clip(kind: .text, text: text, imageData: nil,
                     fingerprint: hash(Data(text.utf8))))
        }
    }

    private func hash(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    private func add(_ clip: Clip) {
        guard let stored = migrate(clip) else { return }
        let oldFiles = Set(items.compactMap(\.imageFile))
        items.removeAll { $0.fingerprint == stored.fingerprint && $0.kind == stored.kind }
        items.insert(stored, at: 0)
        while items.count > maxItems ||
              items.reduce(0, { $0 + ($1.imageBytes ?? $1.text?.utf8.count ?? 0) }) > maxBytes {
            items.removeLast()
        }
        selectedID = stored.id
        if save() { removeFiles(oldFiles.subtracting(Set(items.compactMap(\.imageFile)))) }
    }

    func useSelected() {
        guard selected != nil else { return }
        copySelected()
        controller?.pasteAndHide()
    }

    func copySelected() {
        guard let clip = selected else { return }
        let board = NSPasteboard.general
        board.clearContents()
        if clip.kind == .text, let text = clip.text {
            board.setString(text, forType: .string)
        } else if let image = image(for: clip) {
            board.writeObjects([image])
        }
        lastChangeCount = board.changeCount
    }

    func clear() {
        let oldFiles = Set(items.compactMap(\.imageFile))
        items.removeAll(); selectedID = nil
        if save() { removeFiles(oldFiles) }
        imageCache.removeAllObjects()
    }

    func image(for clip: Clip) -> NSImage? {
        guard let name = clip.imageFile else { return clip.imageData.flatMap(NSImage.init(data:)) }
        if let cached = imageCache.object(forKey: name as NSString) { return cached }
        guard let image = NSImage(contentsOf: imageDirectory.appendingPathComponent(name)) else { return nil }
        imageCache.setObject(image, forKey: name as NSString)
        return image
    }

    private func migrate(_ clip: Clip) -> Clip? {
        guard clip.kind == .image, let data = clip.imageData else { return clip }
        let isPNG = data.starts(with: [137, 80, 78, 71, 13, 10, 26, 10])
        let png = isPNG ? data : NSBitmapImageRep(data: data)?.representation(using: .png, properties: [:])
        let storedData = png ?? data
        let name = clip.id.uuidString + (png == nil ? ".tiff" : ".png")
        let path = imageDirectory.appendingPathComponent(name)
        do {
            try storedData.write(to: path, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: path.path)
        } catch {
            NSLog("ClipboardHistory: image save error %@", String(describing: error))
            return nil
        }
        var result = clip
        result.imageData = nil
        result.imageFile = name
        result.imageBytes = storedData.count
        return result
    }

    private func removeFiles(_ names: Set<String>) {
        for name in names { try? FileManager.default.removeItem(at: imageDirectory.appendingPathComponent(name)) }
    }
    func resetSearch() { query = ""; filter = nil; selectedID = items.first?.id; focusToken += 1 }

    func moveSelection(_ offset: Int) {
        let list = visible
        guard !list.isEmpty else { return }
        let current = list.firstIndex { $0.id == selectedID } ?? 0
        selectedID = list[max(0, min(list.count - 1, current + offset))].id
    }

    @discardableResult private func save() -> Bool {
        do {
            try JSONEncoder().encode(items).write(to: storeURL, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: storeURL.path)
            return true
        } catch {
            NSLog("ClipboardHistory: save error %@", String(describing: error))
            return false
        }
    }
}

private struct ClipboardView: View {
    @ObservedObject var model: ClipboardModel
    @FocusState private var searchFocused: Bool
    private let bg = Color(red: 0.065, green: 0.073, blue: 0.087)
    private let side = Color(red: 0.083, green: 0.094, blue: 0.111)
    private let line = Color.white.opacity(0.11)
    private let accent = Color(red: 1.0, green: 0.38, blue: 0.27)

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 14) {
                Image(systemName: "magnifyingglass").font(.system(size: 19)).foregroundStyle(.white.opacity(0.55))
                TextField("Search in History...", text: $model.query)
                    .textFieldStyle(.plain).font(.system(size: 22))
                    .focused($searchFocused).onSubmit { model.useSelected() }
                    .onKeyPress(.downArrow) { model.moveSelection(1); return .handled }
                    .onKeyPress(.upArrow) { model.moveSelection(-1); return .handled }
                Picker("Type", selection: $model.filter) {
                    Text("All").tag(ClipKind?.none)
                    Text("Text").tag(ClipKind?.some(.text))
                    Text("Images").tag(ClipKind?.some(.image))
                }.pickerStyle(.menu).frame(width: 180)
            }.padding(.horizontal, 24).frame(height: 78)

            Rectangle().fill(line).frame(height: 1)

            HStack(spacing: 0) {
                ScrollViewReader { proxy in
                    ScrollView {
                        LazyVStack(spacing: 3) {
                            ForEach(model.visible) { clip in
                                Button {
                                    model.selectedID = clip.id
                                    searchFocused = true
                                } label: {
                                    HStack(spacing: 14) {
                                        Image(systemName: clip.kind == .text ? "doc.text" : "photo")
                                            .font(.system(size: 20)).frame(width: 27)
                                        Text(clip.title).lineLimit(1).font(.system(size: 17, weight: .medium))
                                        Spacer(minLength: 0)
                                    }
                                    .foregroundStyle(.white.opacity(0.92))
                                    .padding(.horizontal, 16).frame(height: 55)
                                    .background(model.selected?.id == clip.id ? accent.opacity(0.24) : Color.clear)
                                    .clipShape(RoundedRectangle(cornerRadius: 10))
                                }
                                .buttonStyle(.plain)
                                .simultaneousGesture(TapGesture(count: 2).onEnded {
                                    model.selectedID = clip.id; model.useSelected()
                                })
                                .id(clip.id)
                            }
                        }.padding(12)
                    }
                    .onChange(of: model.selectedID) { _, id in
                        if let id { withAnimation(.easeOut(duration: 0.15)) { proxy.scrollTo(id) } }
                    }
                }
                .frame(width: 380).background(side)

                Rectangle().fill(line).frame(width: 1)

                VStack(alignment: .leading, spacing: 0) {
                    if let clip = model.selected {
                        ScrollView {
                            if clip.kind == .text {
                                Text(clip.text ?? "").font(.system(size: 18, design: .monospaced))
                                    .textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
                                    .padding(24)
                            } else if let image = model.image(for: clip) {
                                Image(nsImage: image).resizable().scaledToFit()
                                    .frame(maxWidth: .infinity, maxHeight: 380).padding(24)
                            }
                        }
                        Spacer(minLength: 0)
                        Rectangle().fill(line).frame(height: 1)
                        VStack(spacing: 12) {
                            detail("Type", clip.kind.title)
                            detail("Date", clip.created.formatted(date: .abbreviated, time: .shortened))
                            if let text = clip.text { detail("Chars", "\(text.count)") }
                            if let bytes = clip.imageBytes {
                                detail("Size", ByteCountFormatter.string(fromByteCount: Int64(bytes), countStyle: .file))
                            }
                        }.padding(20)
                    } else {
                        ContentUnavailableView("History Empty", systemImage: "doc.on.clipboard",
                                               description: Text("Copy text or an image"))
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                    }
                }.frame(maxWidth: .infinity, maxHeight: .infinity).background(bg)
            }

            Rectangle().fill(line).frame(height: 1)

            HStack(spacing: 12) {
                Image(nsImage: NSApp.applicationIconImage).resizable().frame(width: 29, height: 29)
                Text("Clipboard History").foregroundStyle(.white.opacity(0.65)).fontWeight(.medium)
                Spacer()
                Button { model.useSelected() } label: {
                    HStack(spacing: 10) {
                        Text("Paste to \(model.pasteTargetName)")
                        keycap("↵")
                    }
                }
                .buttonStyle(.plain).fontWeight(.semibold).disabled(model.selected == nil)
                Rectangle().fill(line).frame(width: 1, height: 25).padding(.horizontal, 5)
                Button { model.showActions.toggle() } label: {
                    HStack(spacing: 8) {
                        Text("Actions")
                        keycap("⌘")
                        keycap("K")
                    }
                }
                .buttonStyle(.plain).foregroundStyle(.white.opacity(0.65))
                .popover(isPresented: $model.showActions, arrowEdge: .top) {
                    VStack(alignment: .leading, spacing: 4) {
                        action("Copy to Clipboard", systemImage: "doc.on.doc") {
                            model.copySelected(); model.controller?.hide(); model.showActions = false
                        }
                        action("Clear History", systemImage: "trash") {
                            model.clear(); model.showActions = false
                        }
                        Divider()
                        action("Quit", systemImage: "power") { NSApp.terminate(nil) }
                    }
                    .padding(10).frame(width: 220)
                    .preferredColorScheme(.dark)
                }
            }.padding(.horizontal, 20).frame(height: 58).background(side)
        }
        .background(bg).foregroundStyle(.white).preferredColorScheme(.dark)
        .frame(minWidth: 850, minHeight: 540)
        .onAppear { searchFocused = true }
        .onChange(of: model.focusToken) { _, _ in searchFocused = true }
        .onExitCommand { model.controller?.hide() }
    }

    private func detail(_ key: String, _ value: String) -> some View {
        HStack {
            Text(key).foregroundStyle(.white.opacity(0.55))
            Spacer()
            Text(value).foregroundStyle(.white.opacity(0.9))
        }.font(.system(size: 14))
    }

    private func keycap(_ value: String) -> some View {
        Text(value).font(.system(size: 15, weight: .medium))
            .foregroundStyle(.white.opacity(0.7))
            .frame(minWidth: 25, minHeight: 25)
            .background(.white.opacity(0.12), in: RoundedRectangle(cornerRadius: 6))
    }

    private func action(_ title: String, systemImage: String, run: @escaping () -> Void) -> some View {
        Button(action: run) {
            Label(title, systemImage: systemImage)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.vertical, 6)
        }.buttonStyle(.plain)
    }
}

@MainActor private final class ClipboardApp: NSObject, NSApplicationDelegate, NSWindowDelegate {
    private let model = ClipboardModel()
    private var panel: NSPanel!
    private var timer: Timer?
    private var hotKey: EventHotKeyRef?
    private var handler: EventHandlerRef?
    private var keyMonitor: Any?
    private var targetApp: NSRunningApplication?
    private let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)

    func applicationDidFinishLaunching(_ notification: Notification) {
        model.controller = self
        panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 1050, height: 660),
                        styleMask: [.titled, .closable, .miniaturizable, .fullSizeContentView, .resizable], backing: .buffered, defer: false)
        panel.contentView = NSHostingView(rootView: ClipboardView(model: model))
        panel.delegate = self
        panel.title = "Clipboard"
        panel.titleVisibility = .hidden
        panel.titlebarAppearsTransparent = true
        panel.isReleasedWhenClosed = false
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.center()

        statusItem.button?.image = NSImage(systemSymbolName: "doc.on.clipboard", accessibilityDescription: "Clipboard")
        statusItem.button?.target = self
        statusItem.button?.action = #selector(toggle)

        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.model.capture() }
        }

        registerShortcut()
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self, self.panel.isVisible,
                  event.modifierFlags.intersection(.deviceIndependentFlagsMask) == .command,
                  event.charactersIgnoringModifiers?.lowercased() == "k" else { return event }
            self.model.showActions.toggle()
            return nil
        }

        if CommandLine.arguments.contains("--show") {
            show()
            if !AXIsProcessTrusted() {
                let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
                _ = AXIsProcessTrustedWithOptions(options)
            }
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        timer?.invalidate()
        if let hotKey { UnregisterEventHotKey(hotKey) }
        if let handler { RemoveEventHandler(handler) }
        if let keyMonitor { NSEvent.removeMonitor(keyMonitor) }
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        hide()
        return false
    }

    @objc private func toggle() {
        if panel.isVisible { hide() } else { show() }
    }

    private func show() {
        targetApp = NSWorkspace.shared.frontmostApplication
        model.pasteTargetName = targetApp?.localizedName ?? "Previous app"
        model.resetSearch()
        panel.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func hide() {
        // Using orderOut ensures it hides in the dock/menu bar without killing the process immediately
        panel.orderOut(nil)
    }

    func pasteAndHide() {
        guard AXIsProcessTrusted() else {
            let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
            _ = AXIsProcessTrustedWithOptions(options)
            return
        }
        hide()
        let target = targetApp
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.18) {
            target?.activate(options: [])
            guard let down = CGEvent(keyboardEventSource: nil, virtualKey: 9, keyDown: true),
                  let up = CGEvent(keyboardEventSource: nil, virtualKey: 9, keyDown: false) else { return }
            down.flags = .maskCommand
            up.flags = .maskCommand
            down.post(tap: .cghidEventTap)
            up.post(tap: .cghidEventTap)
        }
    }

    private func registerShortcut() {
        var eventType = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        let callback: EventHandlerUPP = { _, _, userData in
            guard let userData else { return noErr }
            DispatchQueue.main.async {
                Unmanaged<ClipboardApp>.fromOpaque(userData).takeUnretainedValue().toggle()
            }
            return noErr
        }
        let context = UnsafeMutableRawPointer(Unmanaged.passUnretained(self).toOpaque())
        let install = InstallEventHandler(GetEventDispatcherTarget(), callback, 1, &eventType, context, &handler)
        guard install == noErr else { NSLog("ClipboardHistory: hotkey handler error %d", install); return }

        // Open the history with Cmd+Shift+Space.
        let key = EventHotKeyID(signature: OSType(0x434C4849), id: 1)
        let result = RegisterEventHotKey(UInt32(kVK_Space), UInt32(cmdKey | shiftKey), key, GetEventDispatcherTarget(), 0, &hotKey)
        if result != noErr { NSLog("ClipboardHistory: shortcut ⌘⇧Space unavailable: %d", result) }
    }
}

if CommandLine.arguments.contains("--self-test") {
    let text = Clip(kind: .text, text: "Example", imageData: nil, fingerprint: "x")
    let restored = try! JSONDecoder().decode([Clip].self, from: JSONEncoder().encode([text]))
    precondition(restored.count == 1 && restored[0].title == "Example")
    let iconURL = Bundle.main.url(forResource: "AppIcon", withExtension: "icns")!
    let icon = NSImage(contentsOf: iconURL)!
    let tiff = icon.tiffRepresentation!
    let png = NSBitmapImageRep(data: tiff)!.representation(using: .png, properties: [:])!
    precondition(png.starts(with: [137, 80, 78, 71]))
    let imageClip = Clip(kind: .image, text: nil, imageData: nil,
                         imageFile: "test.png", imageBytes: png.count, fingerprint: "image-test")
    let imageRestored = try! JSONDecoder().decode([Clip].self, from: JSONEncoder().encode([imageClip]))
    precondition(imageRestored[0].imageFile == "test.png" && imageRestored[0].imageBytes == png.count)
    print("ClipboardHistory self-test OK")
} else {
    let app = NSApplication.shared
    app.setActivationPolicy(.accessory)
    let delegate = MainActor.assumeIsolated { ClipboardApp() }
    app.delegate = delegate
    app.run()
}
