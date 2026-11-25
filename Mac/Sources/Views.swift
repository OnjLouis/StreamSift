import AppKit
import Carbon.HIToolbox
import UniformTypeIdentifiers

let functionOne = String(UnicodeScalar(NSF1FunctionKey)!)
let functionFour = String(UnicodeScalar(NSF4FunctionKey)!)

func label(_ title: String, for view: NSView, hint: String) {
    view.setAccessibilityLabel(title)
    view.setAccessibilityHelp(hint)
}

func scroll(_ document: NSView) -> NSScrollView {
    let view = NSScrollView()
    view.borderType = .bezelBorder
    view.hasVerticalScroller = true
    view.documentView = document
    return view
}

func readOnlyText(_ title: String, hint: String) -> NSTextView {
    let view = NSTextView(frame: NSRect(x: 0, y: 0, width: 400, height: 100))
    view.isEditable = false
    view.isSelectable = true
    view.isHorizontallyResizable = false
    view.isVerticallyResizable = true
    view.autoresizingMask = [.width]
    view.textContainer?.widthTracksTextView = true
    view.textContainerInset = NSSize(width: 6, height: 5)
    view.font = .systemFont(ofSize: NSFont.systemFontSize)
    label(title, for: view, hint: hint)
    return view
}

class KeyboardWindow: NSWindow {
    var help: (() -> Void)?
    var updates: (() -> Void)?
    var project: (() -> Void)?
    var cancel: (() -> Bool)?
    var findResult: ((Bool) -> Bool)?
    var keyOrder: (() -> [NSView])?

    override func sendEvent(_ event: NSEvent) {
        if event.type == .keyDown {
            let flags = event.modifierFlags.intersection([.control, .option, .shift, .command])
            switch (Int(event.keyCode), flags) {
            case (kVK_F1, []): if let help { help(); return }
            case (kVK_F1, .shift): if let updates { updates(); return }
            case (kVK_F1, .command), (kVK_F1, .control): if let project { project(); return }
            case (kVK_Escape, []): if cancel?() == true { return }
            case (kVK_F3, []), (kVK_F3, .shift): if findResult?(flags == .shift) == true { return }
            case (kVK_Tab, []), (kVK_Tab, .shift):
                if let controls = keyOrder?(), !controls.isEmpty {
                    let reverse = flags == .shift
                    let index = controls.firstIndex { $0 === focusedView }
                    let next = index.map { ($0 + (reverse ? controls.count - 1 : 1)) % controls.count } ??
                        (reverse ? controls.count - 1 : 0)
                    makeFirstResponder(controls[next])
                    return
                }
            default: break
            }
        }
        super.sendEvent(event)
    }

    var focusedView: NSView? {
        if let fieldEditor = firstResponder as? NSTextView, fieldEditor.isFieldEditor {
            return fieldEditor.delegate as? NSView
        }
        return firstResponder as? NSView
    }
}

final class SettingsTabs: NSTabView {
    override var acceptsFirstResponder: Bool { true }

    override func keyDown(with event: NSEvent) {
        let flags = event.modifierFlags.intersection([.control, .option, .shift, .command])
        guard flags.isEmpty, event.keyCode == kVK_LeftArrow || event.keyCode == kVK_RightArrow else {
            super.keyDown(with: event)
            return
        }
        let current = selectedTabViewItem.map(indexOfTabViewItem) ?? 0
        let step = event.keyCode == kVK_LeftArrow ? -1 : 1
        selectTabViewItem(at: (current + numberOfTabViewItems + step) % numberOfTabViewItems)
    }
}

enum FileResultColumn: String, CaseIterable {
    case name = "Stream", format, size, dateModified

    var title: String {
        switch self {
        case .name: return "Name"
        case .format: return "Format"
        case .size: return "Size"
        case .dateModified: return "Date modified"
        }
    }

    var width: CGFloat {
        switch self {
        case .name: return 630
        case .format: return 86
        case .size: return 94
        case .dateModified: return 183
        }
    }

    func value(for station: Station, named name: String) -> String {
        switch self {
        case .name: return name
        case .format: return station.format
        case .size: return station.fileSizeLabel ?? "Not listed"
        case .dateModified: return station.dateModifiedLabel ?? "Not listed"
        }
    }

    static func reviewLine(for station: Station, named name: String) -> String {
        let size = Self.size.value(for: station, named: name)
        let modified = Self.dateModified.value(for: station, named: name)
        return "\(name), \(station.format), Size \(size), Date modified \(modified)"
    }
}

final class StreamTable: NSTableView {
    var copyAddress: (() -> Void)?
    var openPlayer: (() -> Void)?
    var previewStream: (() -> Void)?
    var openBrowser: (() -> Void)?
    var reviewLine: (() -> Void)?
    var actionMenu: (() -> NSMenu?)?
    var didSelectAll: (() -> Void)?
    var hasStreams = false
    private(set) var showsFileColumns = false

    func setFileColumns(_ show: Bool) {
        guard show != showsFileColumns, let name = tableColumns.first else { return }
        showsFileColumns = show
        name.title = show ? FileResultColumn.name.title : "Stream"
        if show {
            for field in FileResultColumn.allCases where field != .name {
                let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(field.rawValue))
                column.title = field.title
                column.width = field.width
                column.resizingMask = .userResizingMask
                addTableColumn(column)
            }
        } else {
            for column in tableColumns.dropFirst() { removeTableColumn(column) }
        }
        if let scroll = enclosingScrollView {
            scroll.hasHorizontalScroller = show
            let metadataWidth = tableColumns.dropFirst().reduce(CGFloat.zero) { $0 + $1.width }
            name.width = max(name.minWidth, scroll.contentSize.width - metadataWidth - intercellSpacing.width * CGFloat(numberOfColumns))
        }
    }

    override var acceptsFirstResponder: Bool { hasStreams && super.acceptsFirstResponder }

    @objc func copy(_ sender: Any?) { if selectedRow >= 0 { copyAddress?() } }

    override func selectAll(_ sender: Any?) {
        super.selectAll(sender)
        if selectedRow >= 0 { didSelectAll?() }
    }

    override func keyDown(with event: NSEvent) {
        let flags = event.modifierFlags.intersection([.control, .option, .shift, .command])
        if flags.isEmpty, event.keyCode == kVK_F4, selectedRow >= 0 {
            reviewLine?()
            return
        }
        if selectedRow >= 0 && (event.keyCode == kVK_Return || event.keyCode == kVK_ANSI_KeypadEnter) {
            if flags == .shift { openPlayer?(); return }
            if flags == .option { openBrowser?(); return }
            if flags.isEmpty { previewStream?(); return }
        }
        super.keyDown(with: event)
    }

    override func menu(for event: NSEvent) -> NSMenu? {
        let index = row(at: convert(event.locationInWindow, from: nil))
        if index >= 0, !selectedRowIndexes.contains(index) {
            selectRowIndexes(IndexSet(integer: index), byExtendingSelection: false)
        }
        return selectedRow >= 0 ? actionMenu?() : nil
    }

    // VoiceOver's Show Menu action should open the native result menu.
    override func accessibilityPerformShowMenu() -> Bool {
        guard selectedRow >= 0, let menu = actionMenu?() else { return false }
        let cell = rect(ofRow: selectedRow)
        menu.popUp(positioning: nil, at: NSPoint(x: cell.minX + 4, y: cell.maxY), in: self)
        return true
    }
}

@MainActor final class HelpPanel: NSObject {
    private let panel: KeyboardWindow
    private let body: NSTextView
    private let manual: (() -> Void)?
    private var reviewHelpManual: (() -> Void)?

    init(title: String, text: String, bodyLabel: String = "Help text", manual: (() -> Void)? = nil) {
        self.manual = manual
        panel = KeyboardWindow(contentRect: NSRect(x: 0, y: 0, width: 480, height: 205),
                               styleMask: [.titled, .resizable], backing: .buffered, defer: false)
        body = readOnlyText(bodyLabel, hint: "")
        super.init()
        panel.title = title
        body.string = text
        let open = NSButton(title: "Open Manual", target: self, action: #selector(openManual))
        let close = NSButton(title: "Close", target: self, action: #selector(closePanel))
        panel.cancel = { [weak self] in self?.closePanel(); return true }
        let box = scroll(body)
        let buttons = NSStackView(views: manual == nil ? [close] : [open, close])
        let content = NSStackView(views: [box, buttons])
        content.orientation = .vertical
        content.alignment = .trailing
        content.edgeInsets = NSEdgeInsets(top: 14, left: 14, bottom: 14, right: 14)
        panel.contentView = content
        box.widthAnchor.constraint(equalTo: content.widthAnchor, constant: -28).isActive = true
        box.setContentHuggingPriority(.defaultLow, for: .vertical)
        body.nextKeyView = manual == nil ? close : open
        open.nextKeyView = close
        close.nextKeyView = body
        let order = manual == nil ? [body, close] as [NSView] : [body, open, close]
        panel.keyOrder = { order }
    }

    func present(on parent: NSWindow, over review: NSWindow? = nil) {
        let focusOwner = review ?? parent
        let old = focusOwner.firstResponder
        let completion: (NSApplication.ModalResponse) -> Void = { [self] response in
            if let old { focusOwner.makeFirstResponder(old) }
            if response == .OK { manual?() }
        }
        if review != nil { parent.beginCriticalSheet(panel, completionHandler: completion) }
        else { parent.beginSheet(panel, completionHandler: completion) }
        panel.makeFirstResponder(body)
        body.setSelectedRange(NSRange(location: 0, length: 0))
    }

    func enableReviewHelp(manual: @escaping () -> Void) {
        reviewHelpManual = manual
        panel.help = { [weak self] in self?.showReviewHelp() }
    }

    func showReviewHelp() {
        guard panel.isKeyWindow, let parent = panel.sheetParent, let manual = reviewHelpManual else { return }
        let reading = panel.firstResponder === body
        HelpPanel(title: reading ? "Review Text Help" : "Close Help",
                  text: reading ? ContextGuide.reviewText : ContextGuide.reviewClose, manual: manual)
            .present(on: parent, over: panel)
    }

    @objc private func closePanel() { panel.sheetParent?.endSheet(panel, returnCode: .cancel) }
    @objc private func openManual() { panel.sheetParent?.endSheet(panel, returnCode: .OK) }
}

@MainActor final class PreferencesWindow: NSObject, NSWindowDelegate {
    let window: KeyboardWindow
    private let tabs = SettingsTabs()
    private let sounds = NSButton(checkboxWithTitle: "Search sounds", target: nil, action: nil)
    private let fallback = NSButton(checkboxWithTitle: "Automatically try other search methods", target: nil, action: nil)
    private let mediaPlayer = NSPopUpButton()
    private let choosePlayer = NSButton(title: "Choose Player...", target: nil, action: nil)
    private var customPlayerPath: String?
    private let updates = NSButton(checkboxWithTitle: "Check for updates at startup", target: nil, action: nil)
    private let folder = NSTextField()
    private let apply: (Preferences) -> Void
    private let manual: () -> Void
    private let soundsHint = "Play sounds when a search starts, succeeds or fails."
    private let fallbackHint = "If the chosen method finds no playable stream or cannot finish, try the other methods. This can start a temporary browser and check the public directory. Escape cancels the whole search."
    private let folderHint = "Choose where Shift+Return saves playlists. Command+S starts its Save dialog in the same folder."
    private let playerHint = "Choose what plays a result when you press Return. Automatic uses your default playlist app when available. Use Choose Player to find an application manually."
    private let updateHint = "Check for a newer signed release when StreamSift opens. Updates are installed only after you choose Install Update."

    init(current: Preferences, manual: @escaping () -> Void, apply: @escaping (Preferences) -> Void) {
        self.manual = manual
        self.apply = apply
        window = KeyboardWindow(contentRect: NSRect(x: 0, y: 0, width: 600, height: 250),
                                styleMask: [.titled, .closable], backing: .buffered, defer: false)
        super.init()
        window.title = "StreamSift Settings"
        window.isReleasedWhenClosed = false
        window.cancel = { [weak self] in self?.window.performClose(nil); return true }
        window.help = { [weak self] in self?.showHelp() }
        sounds.state = current.sounds ? .on : .off
        fallback.state = current.automaticFallback ? .on : .off
        customPlayerPath = current.customPlayerPath
        let choices = MediaPlayerChoice.choices(customPath: customPlayerPath)
        mediaPlayer.addItems(withTitles: choices.map(playerLabel))
        mediaPlayer.selectItem(at: choices.firstIndex(of: current.mediaPlayer) ?? 0)
        updates.state = current.startupUpdates ? .on : .off
        folder.stringValue = current.folder.path
        folder.isBezeled = true
        folder.isEditable = true
        folder.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        label("Search sounds", for: sounds, hint: soundsHint)
        label("Automatically try other search methods", for: fallback, hint: fallbackHint)
        label("Check for updates at startup", for: updates, hint: updateHint)
        label("Playlist folder", for: folder, hint: folderHint)
        label("Media player", for: mediaPlayer, hint: playerHint)
        choosePlayer.target = self
        choosePlayer.action = #selector(browsePlayer)
        label("Choose Player", for: choosePlayer, hint: "Select an application that can open an M3U8 playlist. This choice is used only by StreamSift.")
        let playerLabel = NSTextField(labelWithString: "Media player:")
        playerLabel.setAccessibilityServesAsTitleForUIElements([mediaPlayer])
        let playerRow = NSStackView(views: [playerLabel, mediaPlayer, choosePlayer])
        playerRow.alignment = .centerY
        let browse = NSButton(title: "Browse...", target: self, action: #selector(browseFolder))
        let reset = NSButton(title: "Use Default Folder", target: self, action: #selector(defaultFolder))
        label("Browse", for: browse, hint: "Choose the playlist folder in Finder.")
        label("Use Default Folder", for: reset, hint: "Use the StreamSift folder inside Music.")
        let folderLabel = NSTextField(labelWithString: "Playlist folder:")
        folderLabel.setAccessibilityServesAsTitleForUIElements([folder])
        let row = NSStackView(views: [folderLabel, folder, browse])
        let general = NSStackView(views: [sounds, fallback, playerRow, row, reset])
        general.orientation = .vertical
        general.alignment = .leading
        general.edgeInsets = NSEdgeInsets(top: 15, left: 15, bottom: 15, right: 15)
        row.widthAnchor.constraint(equalTo: general.widthAnchor, constant: -30).isActive = true
        let updateStack = NSStackView(views: [updates])
        updateStack.orientation = .vertical
        updateStack.alignment = .leading
        updateStack.edgeInsets = general.edgeInsets
        for (name, content) in [("General", general), ("Updates", updateStack)] {
            let tab = NSTabViewItem(identifier: name)
            tab.label = name
            tab.view = content
            tabs.addTabViewItem(tab)
        }
        let ok = NSButton(title: "OK", target: self, action: #selector(accept))
        ok.keyEquivalent = "\r"
        let cancel = NSButton(title: "Cancel", target: self, action: #selector(dismiss))
        let actions = NSStackView(views: [cancel, ok])
        let content = NSStackView(views: [tabs, actions])
        content.orientation = .vertical
        content.alignment = .trailing
        content.edgeInsets = NSEdgeInsets(top: 14, left: 14, bottom: 14, right: 14)
        tabs.widthAnchor.constraint(equalTo: content.widthAnchor, constant: -28).isActive = true
        window.contentView = content
        window.keyOrder = { [weak self, weak browse, weak reset, weak ok, weak cancel] in
            guard let self, let ok, let cancel else { return [] }
            let controls: [NSView?] = tabs.selectedTabViewItem?.identifier as? String == "Updates" ?
                [tabs, updates, cancel, ok] : [tabs, sounds, fallback, mediaPlayer, choosePlayer, folder, browse, reset, cancel, ok]
            return controls.compactMap { $0 }
        }
        window.initialFirstResponder = tabs
        window.center()
    }

    @objc private func dismiss() { window.performClose(nil) }
    private func playerLabel(_ choice: MediaPlayerChoice) -> String {
        guard choice == .custom, let customPlayerPath else { return choice.title }
        return "Custom (\(choice.displayName(customPath: customPlayerPath)))"
    }

    @objc private func browsePlayer() {
        let panel = NSOpenPanel()
        panel.title = "Choose Player"
        panel.prompt = "Choose"
        panel.canChooseDirectories = false
        panel.allowedContentTypes = [.applicationBundle]
        panel.directoryURL = customPlayerPath.map { URL(fileURLWithPath: $0).deletingLastPathComponent() }
            ?? URL(fileURLWithPath: "/Applications", isDirectory: true)
        panel.beginSheetModal(for: window) { [weak self] response in
            guard let self, response == .OK else { return }
            guard let app = StreamPreview.customApplication(panel.url?.path) else {
                HelpPanel(title: "Choose Player", text: "Choose a media player application.", manual: manual).present(on: window)
                return
            }
            customPlayerPath = app.path
            let choices = MediaPlayerChoice.choices(customPath: customPlayerPath)
            let index = choices.firstIndex(of: .custom)!
            let title = playerLabel(.custom)
            if let item = mediaPlayer.item(at: index) { item.title = title }
            else { mediaPlayer.addItem(withTitle: title) }
            mediaPlayer.selectItem(at: index)
            window.makeFirstResponder(mediaPlayer)
        }
    }

    @objc private func browseFolder() {
        let panel = NSOpenPanel()
        panel.title = "Choose Playlist Folder"
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.directoryURL = URL(fileURLWithPath: folder.stringValue)
        panel.beginSheetModal(for: window) { [weak self] response in
            if response == .OK, let url = panel.url { self?.folder.stringValue = url.path }
        }
    }

    @objc private func defaultFolder() { folder.stringValue = Preferences.defaultFolder.path }

    @objc private func accept() {
        let choices = MediaPlayerChoice.choices(customPath: customPlayerPath)
        let selected = choices.indices.contains(mediaPlayer.indexOfSelectedItem)
            ? choices[mediaPlayer.indexOfSelectedItem] : .automatic
        let path = folder.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        let normalized = Preferences.validFolder(path)
        guard path.isEmpty || normalized != nil || URL(fileURLWithPath: path).standardizedFileURL.path == Preferences.defaultFolder.path else {
            HelpPanel(title: "Playlist Folder", text: "Enter a full folder path or choose Browse.", manual: manual).present(on: window)
            return
        }
        var chosen = Preferences()
        chosen.sounds = sounds.state == .on
        chosen.automaticFallback = fallback.state == .on
        chosen.startupUpdates = updates.state == .on
        chosen.customFolder = normalized
        chosen.mediaPlayer = selected
        chosen.customPlayerPath = customPlayerPath
        apply(chosen)
        dismiss()
    }

    func showHelp() {
        guard window.attachedSheet == nil else { return }
        let focused = (NSApp.accessibilityFocusedUIElement as? NSView) ?? window.focusedView
        let contents = focused?.accessibilityHelp().flatMap { $0.isEmpty ? nil : $0 } ??
            "Set up search options, playback, the playlist folder and update checks."
        HelpPanel(title: focused?.accessibilityLabel() ?? "Settings Help", text: contents, manual: manual).present(on: window)
    }
}
