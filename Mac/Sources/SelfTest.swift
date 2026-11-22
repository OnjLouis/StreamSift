import AppKit
import Carbon.HIToolbox

func selfTest() throws {
    func require(_ passed: @autoclosure () -> Bool, _ label: String) throws {
        if !passed() { throw UpdateFailure.invalid(label) }
    }

    let root = FileManager.default.temporaryDirectory.appendingPathComponent("StreamSift-test-\(UUID().uuidString)")
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
    defer { try? FileManager.default.removeItem(at: root) }
    let store = "me.onj.streamsift.test.\(UUID().uuidString)"
    let defaults = UserDefaults(suiteName: store)!
    defer { defaults.removePersistentDomain(forName: store) }
    var prefs = Preferences(reading: defaults)
    try require(prefs.sounds && prefs.startupUpdates && !prefs.automaticFallback && prefs.mediaPlayer == .automatic &&
                prefs.folder == Preferences.defaultFolder, "Initial settings")
    defaults.set(false, forKey: "CheckUpdatesAtStartup")
    try require(!Preferences(reading: defaults).startupUpdates, "An explicit startup update opt-out stays off")
    defaults.set(true, forKey: "CheckUpdatesAtStartup")
    try require(!Preferences(reading: defaults).automaticFallback, "Older settings leave fallback off")
    prefs.sounds = false
    prefs.startupUpdates = true
    prefs.automaticFallback = true
    prefs.mediaPlayer = .vlc
    prefs.customFolder = root.path
    prefs.save(to: defaults)
    try require(prefs == Preferences(reading: defaults), "Settings round trip")
    var history = SearchHistory(reading: defaults)
    try require(history.entries.isEmpty, "No default history")
    let failed = history.remember("radio.example/no-stream", verifiedStreams: 0, in: defaults)
    try require(!failed, "An unsuccessful search is not remembered")
    for address in ["radio.example/live?token=private", "radio.example/live#private", "https://user@radio.example/live"] {
        let saved = history.remember(address, verifiedStreams: 1, in: defaults)
        try require(!saved, "Sensitive addresses are not remembered")
    }
    for index in 0...SearchHistory.capacity {
        _ = history.remember("radio.example/\(index)", verifiedStreams: 1, in: defaults)
    }
    try require(history.entries.count == 10 && history.entries.first == "radio.example/10" &&
                history.entries.last == "radio.example/1", "Newest first and oldest evicted")
    _ = history.remember(" RADIO.EXAMPLE/5 ", verifiedStreams: 1, in: defaults)
    try require(history.entries.first == "RADIO.EXAMPLE/5" && !history.entries.contains("radio.example/5") &&
                SearchHistory(reading: defaults).entries == history.entries, "Deduplicated history survives relaunch")
    history.clear(in: defaults)
    try require(SearchHistory(reading: defaults).entries.isEmpty && Preferences(reading: defaults) == prefs,
                "Clear only search history")
    try require(Preferences.validFolder("path/to/folder") == nil &&
                Preferences.validFolder("/tmp/\nfolder") == nil, "Invalid paths")
    try require(Playlist.stem("...") == "Stream" && Playlist.stem("a/b:c") == "a_b_c", "Playlist filenames")
    let row = Station(name: "Lyd \u{e9}\nFM", url: "https://radio.example/stream", format: "MP3", bitrate: 320,
                      foundOn: "https://radio.example/")
    let quiet = Station(name: "Able", url: "https://radio.example/quiet", format: "AAC", bitrate: 48, foundOn: "")
    let addressed = Station(name: "Example", url: "https://radio.example:9020/stream?token=fixture",
                            format: "MP3", bitrate: 128, foundOn: "https://radio.example/list", listeners: 0)
    let detail = DetailRow.rows(for: addressed, selectedCount: 2)
    try require(detail.first?.value == addressed.url && detail.first?.address?.absoluteString == addressed.url &&
                detail.first?.server?.absoluteString == "https://radio.example:9020/" &&
                detail[0].enterCommand == .search && detail[1].enterCommand == .search &&
                detail[1].address?.absoluteString == addressed.foundOn && detail[1].server == nil &&
                detail[2].address == nil && detail.contains { $0.kind == .listeners && $0.value == "0" } &&
                detail.last?.text == "Selection: Details for the first of 2 streams" &&
                DetailRow.rows(for: nil, selectedCount: 0).isEmpty, "Detail actions target the raw address and its exact server")
    let hls = try JSONDecoder().decode(Station.self, from: Data(#"{"name":"Live","url":"https://radio.example:9020/hls/live?token=fixture","format":"HLS","foundOn":"https://radio.example:9020/hls/live?token=fixture"}"#.utf8))
    let direct = DetailRow.rows(for: hls, selectedCount: 2)
    try require(LastResults.read(from: defaults) == nil, "No saved results on first launch")
    let safe = Station(name: "Able", url: "https://radio.example:9020/quiet", format: "AAC", bitrate: 48,
                       foundOn: "https://radio.example:9020/list", listeners: 0)
    let stable = Station(name: "Lyd FM", url: "https://radio.example/stream", format: "MP3", bitrate: 320,
                         foundOn: "https://radio.example/")
    let local = Station(name: "Local", url: "http://127.0.0.1/stream", format: "MP3", bitrate: 320, foundOn: stable.foundOn)
    let temporary = Station(name: "Live", url: "https://radio.example/opaque-session/playlist.m3u8", format: "HLS", bitrate: nil,
                            foundOn: stable.foundOn)
    let view = ResultView(format: "AAC", order: .listenersDescending)
    guard let saved = LastResults.capture(input: "radio.example:9020", mode: .passive,
                                         stations: [addressed, local, temporary, stable, safe, safe], view: view,
                                         selection: safe.url) else { throw UpdateFailure.invalid("No restorable streams") }
    try require(saved.stations == [stable, safe], "Only reusable streams are remembered")
    saved.save(to: defaults)
    let restored = LastResults.read(from: defaults)
    try require(restored?.input == "radio.example:9020" && restored?.mode == .passive &&
                restored?.view.format == "AAC" && restored?.view.order == .listenersDescending &&
                restored?.view.apply(restored?.stations ?? []) == [safe] && restored?.selection == safe.url,
                "Reopening preserves the query, method, format, sort and selection")
    try require(LastResults.capture(input: hls.url, mode: .passive, stations: [safe], view: view, selection: nil) == nil &&
                LastResults.capture(input: stable.foundOn, mode: .passive, stations: [], view: view, selection: nil) == nil &&
                LastResults.read(from: defaults)?.stations == [stable, safe], "Unsafe and empty scans leave the snapshot intact")
    LastResults.clear(in: defaults)
    try require(LastResults.read(from: defaults) == nil, "New Search forgets the snapshot")
    defaults.set(Data("{broken".utf8), forKey: "LastResults")
    try require(LastResults.read(from: defaults) == nil, "Malformed state is ignored")
    LastResults.clear(in: defaults)
    let serverRoot = "https://radio.example:9020/"
    let rooted = Station(name: "Root", url: serverRoot, format: "AAC", bitrate: nil, foundOn: serverRoot)
    let guides = detail + direct + DetailRow.rows(for: rooted, selectedCount: 1)
    for item in guides {
        let lines = ContextGuide.detail(item, canSearch: true).components(separatedBy: "\n")
        try require(lines.count > 3 && !lines[0].isEmpty && lines[1].isEmpty &&
                    lines.contains { $0.hasPrefix("Return:") } == (item.enterCommand != nil) &&
                    lines.contains { $0.hasPrefix("Shift+Return:") } == (item.kind == .streamAddress) &&
                    lines.contains("F4: Review the full line."), "F1 commands match the focused row")
    }
    try require(ContextGuide.detail(direct[0], canSearch: true).contains("Return: Open Save Stream Playlist") &&
                ContextGuide.detail(direct[1], canSearch: true).contains("Return: Scan the server root") &&
                ContextGuide.detail(detail[1], canSearch: true).contains("Return: Search the source page.") &&
                !ContextGuide.detail(direct[0], canSearch: false).contains("\nReturn:") &&
                !ContextGuide.streams(selected: 2).contains("\nReturn:") && ContextGuide.streams(selected: 1).contains("\nReturn:") &&
                ContextGuide.reviewText.contains("Command+C: Copy the selection.") &&
                ContextGuide.reviewClose.hasPrefix("Close returns"), "Distinct help for HLS, source, Streams and review controls")
    try require(direct[0].enterCommand == .savePlaylist && direct[0].playlistStation == hls &&
                direct[1].enterCommand == .scanServer && direct[1].server?.absoluteString == serverRoot &&
                DetailRow.rows(for: rooted, selectedCount: 1)[1].enterCommand == nil,
                "Detail Return saves HLS or scans the repeated stream origin without looping")
    let hlsPlaylist = try Playlist.data(direct[0].playlistStation.map { [$0] } ?? [])
    try require(String(decoding: hlsPlaylist, as: UTF8.self) == "#EXTM3U\r\n#EXTINF:-1,Live\r\n\(hls.url)\r\n",
                "An HLS detail saves one link, not an HLS manifest")
    if let event = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: .shift, timestamp: 0,
                                   windowNumber: 0, context: nil, characters: "\r", charactersIgnoringModifiers: "\r",
                                   isARepeat: false, keyCode: UInt16(kVK_Return)),
       let review = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0,
                                    windowNumber: 0, context: nil, characters: functionFour, charactersIgnoringModifiers: functionFour,
                                    isARepeat: false, keyCode: UInt16(kVK_F4)) {
        let detailTable = DetailTable()
        detailTable.configure()
        detailTable.showRows(direct)
        var scanned: [String] = []
        var reviewed: String?
        detailTable.onCommand = { command, row in
            if command == .scanServer, let server = row.server { scanned.append(server.absoluteString) }
            if command == .review { reviewed = row.text }
        }
        detailTable.keyDown(with: event)
        try require(scanned == [serverRoot], "Shift+Return scans an HLS Stream address, rather than saving it")
        detailTable.selectRowIndexes(IndexSet(integer: 1), byExtendingSelection: false)
        detailTable.keyDown(with: event)
        detailTable.selectRowIndexes(IndexSet(integer: 0), byExtendingSelection: false)
        detailTable.canSearch = false
        detailTable.keyDown(with: event)
        try require(scanned == [serverRoot], "Shift+Return does not scan Found on or interrupt a busy search")
        detailTable.keyDown(with: review)
        try require(reviewed == direct[0].text && reviewed?.contains(hls.url) == true,
                    "F4 reviews the full detail line, even when a new scan is unavailable")
        let streams = StreamTable()
        streams.addTableColumn(NSTableColumn(identifier: NSUserInterfaceItemIdentifier("Test stream")))
        streams.dataSource = detailTable
        streams.reloadData()
        streams.selectRowIndexes(IndexSet(integer: 0), byExtendingSelection: false)
        var opened = false
        var requestedReview = false
        streams.openPlayer = { opened = true }
        streams.reviewLine = { requestedReview = true }
        streams.keyDown(with: event)
        try require(opened, "Shift+Return in Streams still saves and opens the playlist")
        streams.keyDown(with: review)
        try require(requestedReview, "F4 on a stream requests line review")
    } else {
        throw UpdateFailure.invalid("Could not create the keyboard test events")
    }
    let unknown = Station(name: "Local", url: "https://radio.example/local", format: "MP3", bitrate: nil, foundOn: "")
    let mixed = [unknown, row, quiet]
    try require(ResultView.formats(mixed) == ["AAC", "MP3"] && ResultView.formats([quiet]) == ["AAC"],
                "Format choices are derived from the current results")
    try require(ResultView(format: "AAC").apply(mixed) == [quiet] && ResultView().apply(mixed) == mixed &&
                ResultView(order: .bitrateDescending).apply(mixed) == [row, quiet, unknown] &&
                ResultView(order: .bitrateAscending).apply(mixed) == [quiet, row, unknown], "Filter and bitrate order")
    try require(ResultView.find(in: mixed, after: -1, query: "LYD E", backwards: false) == 1 &&
                ResultView.find(in: mixed, after: 1, query: "MP3", backwards: false) == 0 &&
                ResultView.find(in: mixed, after: 0, query: "MP3", backwards: true) == 1 &&
                ResultView.find(in: [quiet], after: -1, query: "MP3", backwards: false) == nil, "Find wraps within the visible set")
    let zero = try JSONDecoder().decode(Station.self, from: Data(#"{"name":"Zero","url":"https://radio.example/zero","format":"MP3","foundOn":"","listeners":0}"#.utf8))
    let busy = Station(name: "Busy", url: "https://radio.example/busy", format: "MP3", bitrate: nil, foundOn: "", listeners: 12)
    try require(zero.listLabel(named: "Zero") == "Zero, MP3, 0 listeners" &&
                ResultView(order: .listenersDescending).apply([row, zero, busy]) == [busy, zero, row] &&
                ResultView(order: .listenersAscending).apply([row, zero, busy]) == [zero, busy, row], "Listener counts including zero, with unknown last")
    let choice = ResultView(format: "MP3", order: .listenersDescending)
    let retained = choice.retainingAvailable(in: [zero, quiet])
    let dropped = choice.retainingAvailable(in: [quiet])
    try require(retained.format == "MP3" && retained.order == .listenersDescending &&
                dropped.format == nil && dropped.order == .search, "Refresh retains only available choices")
    let expected = "#EXTM3U\r\n#EXTINF:-1,Lyd \u{e9} FM\r\nhttps://radio.example/stream\r\n"
    let first = try Playlist.saveNew([row], in: root)
    let second = try Playlist.saveNew([row], in: root)
    let firstBytes = try Data(contentsOf: first)
    try require(first != second && firstBytes == Data(expected.utf8) && second.lastPathComponent.contains(" (2)."),
                "UTF-8 export without overwrite")
    let bad = Station(name: "Bad", url: "file:///tmp/private", format: "MP3", bitrate: nil, foundOn: "")
    try require(StreamPreview.address(row)?.absoluteString == row.url && StreamPreview.address(bad) == nil,
                "Preview passes only a network address and needs no playlist")
    do {
        _ = try Playlist.data([row, bad])
        throw UpdateFailure.invalid("Accepted a file URL")
    } catch StreamError.playlist {}
    let selection = (1...288).map { index in
        Station(name: "Station \(index)", url: "https://radio.example/\(index)", format: row.format,
                bitrate: row.bitrate, foundOn: row.foundOn)
    }
    let batch = try Playlist.saveNew(selection, in: root)
    let batchText = try String(contentsOf: batch, encoding: .utf8)
    try require(batch.lastPathComponent == "Streams.m3u8" &&
                batchText.components(separatedBy: "\r\n").filter { $0.hasPrefix("https://") } == selection.map(\.url),
                "One playlist contains all 288 selected results in their order")
    let fastPlay = root.appendingPathComponent("FastPlay.app")
    let vlc = root.appendingPathComponent("VLC.app")
    func installed(_ id: String) -> URL? {
        id == MediaPlayerChoice.fastPlay.bundleIdentifier ? fastPlay : id == MediaPlayerChoice.vlc.bundleIdentifier ? vlc : nil
    }
    try require(StreamPreview.target(choice: .automatic, associated: fastPlay,
                    associatedIdentifier: MediaPlayerChoice.fastPlay.bundleIdentifier, installed: installed)?.0 == .fastPlay &&
                StreamPreview.target(choice: .vlc, associated: fastPlay,
                    associatedIdentifier: MediaPlayerChoice.fastPlay.bundleIdentifier, installed: installed)?.1 == vlc,
                "FastPlay association and explicit VLC selection use different native targets")
    let temp = root.appendingPathComponent("handoffs", isDirectory: true)
    let tempFirst = try PlaybackHandoff.stage(row, in: temp)
    let tempSecond = try PlaybackHandoff.stage(row, in: temp)
    let beforeCleanup = try Data(contentsOf: tempFirst)
    try require(tempFirst != tempSecond && beforeCleanup == Data(expected.utf8), "Two distinct FastPlay handoffs contain one correct stream each")
    let extra = temp.appendingPathComponent("unrelated.m3u8")
    try Data(expected.utf8).write(to: extra)
    let remaining = try PlaybackHandoff.prune(in: temp, now: Date().addingTimeInterval(PlaybackHandoff.retention + 5))
    try require(remaining == 0 && FileManager.default.fileExists(atPath: extra.path) &&
                !FileManager.default.fileExists(atPath: tempFirst.path) &&
                !FileManager.default.fileExists(atPath: tempSecond.path), "Expired handoffs are removed without touching unrelated playlists")
    try require(row.listLabel(named: "Test Station") == "Test Station, MP3, 320 kbps" &&
                bad.listLabel(named: "No Bitrate") == "No Bitrate, MP3", "Station, codec and available bitrate share one result")

    let json = #"{"tag_name":"v1.0.1","draft":false,"prerelease":false,"assets":[{"name":"StreamSift-1.0.1-macOS.zip","size":1000,"browser_download_url":"https://github.com/OnjLouis/StreamSift/releases/download/v1.0.1/StreamSift-1.0.1-macOS.zip"}]}"#
    let release = try JSONDecoder().decode(Updater.GitHubRelease.self, from: Data(json.utf8))
    let newer = try Updater.candidate(release, installed: "1.0.0")
    let stale = try Updater.candidate(release, installed: "1.0.1")
    try require(newer?.version == "1.0.1" && stale == nil, "Only a newer matching Mac update is offered")
    try require(Updater.isAssetHost(newer!.url) &&
                !Updater.isAssetHost(URL(string: "https://user@github.com/OnjLouis/StreamSift/releases/download/v1.0.1/file.zip")!) &&
                !Updater.isAssetHost(URL(string: "https://github.com/elsewhere/releases/download/v1.0/file.zip")!),
                "Update redirects stay on the release hosts")
    for listing in ["../escape", "StreamSift.app/Contents/MacOS/StreamSift"] {
        var rejected = false
        do { try Updater.validateEntries(listing, "") }
        catch { rejected = true }
        try require(rejected, "Rejected an unexpected or incomplete app archive")
    }
    let response = #"{"stations":[],"requests":5,"unreachable":3,"limitReached":true}"#
    let empty = try JSONDecoder().decode(SearchResult.self, from: Data(response.utf8))
    try require(empty.summary == "No live streams verified. Checked 5 addresses. 3 unavailable. Search limit reached.", "Scanner reply")
    var output = ScannerOutput(fallback: true)
    let prefix = output.receive(Data("--bro".utf8))
    let progress = output.receive(Data("wser-json\r\n--directory-json\n".utf8) + Data(response.utf8))
    try require(prefix.isEmpty && progress == [.browser, .directory] && !output.invalid && output.payload == Data(response.utf8),
                "Read split fallback progress without losing the final scanner reply")
    _ = output.receive(Data("\n".utf8))
    try require(output.invalid, "Reject an unexpected third progress line")
    let fallbackReply = #"{"stations":[],"requests":3,"unreachable":0,"limitReached":false,"method":"--directory-json","attempts":3,"failedMethods":1}"#
    let result = try JSONDecoder().decode(SearchResult.self, from: Data(fallbackReply.utf8))
    try require(result.method == .directory && result.summary.hasSuffix("Tried 3 methods. Methods that could not finish: 1."),
                "Fallback results identify the method and attempt counts")
}
