import Foundation

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
    try require(prefs.sounds && !prefs.startupUpdates && prefs.folder == Preferences.defaultFolder, "Initial settings")
    prefs.sounds = false
    prefs.startupUpdates = true
    prefs.customFolder = root.path
    prefs.save(to: defaults)
    try require(prefs == Preferences(reading: defaults), "Settings round trip")
    try require(Preferences.validFolder("path/to/folder") == nil &&
                Preferences.validFolder("/tmp/\nfolder") == nil, "Invalid paths")
    try require(Playlist.stem("...") == "Stream" && Playlist.stem("a/b:c") == "a_b_c", "Playlist filenames")
    let row = Station(name: "Lyd \u{e9}\nFM", url: "https://radio.example/stream", format: "MP3", bitrate: 320,
                      foundOn: "https://radio.example/")
    let expected = "#EXTM3U\r\n#EXTINF:-1,Lyd \u{e9} FM\r\nhttps://radio.example/stream\r\n"
    let first = try Playlist.saveNew(row, in: root)
    let second = try Playlist.saveNew(row, in: root)
    let firstBytes = try Data(contentsOf: first)
    try require(first != second && firstBytes == Data(expected.utf8) && second.lastPathComponent.contains(" (2)."),
                "UTF-8 export without overwrite")
    let bad = Station(name: "Bad", url: "file:///tmp/private", format: "MP3", bitrate: nil, foundOn: "")
    do {
        _ = try Playlist.data(bad)
        throw UpdateFailure.invalid("Accepted a file URL")
    } catch StreamError.playlist {}

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
}
