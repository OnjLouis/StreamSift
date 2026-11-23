import Foundation

enum MediaPlayerChoice: String, CaseIterable {
    case automatic, vlc, fastPlay
    var title: String {
        switch self {
        case .automatic: return "Automatic"
        case .vlc: return "VLC"
        case .fastPlay: return "FastPlay"
        }
    }

    var bundleIdentifier: String? {
        switch self {
        case .automatic: return nil
        case .vlc: return "org.videolan.vlc"
        case .fastPlay: return "com.fastplay.macos"
        }
    }
}

enum StreamPreview {
    static func target(choice: MediaPlayerChoice, associated: URL?, associatedIdentifier: String?,
                       installed: (String) -> URL?) -> (player: MediaPlayerChoice?, app: URL, needsPlaylist: Bool)? {
        let supported = [MediaPlayerChoice.vlc, .fastPlay]
        var fallback: (player: MediaPlayerChoice?, app: URL, needsPlaylist: Bool)?
        if let associated, associated.isFileURL, associated.pathExtension == "app", associatedIdentifier != "me.onj.streamsift" {
            let preferred = supported.first { $0.bundleIdentifier == associatedIdentifier }
            fallback = (preferred, associated, preferred != .vlc)
            if choice == .automatic || choice == preferred { return fallback }
        }
        for player in (choice == .automatic ? supported : [choice]) {
            if let id = player.bundleIdentifier, let path = installed(id) { return (player, path, player == .fastPlay) }
        }
        return fallback
    }

    static func address(_ station: Station) -> URL? { address(station.url) }

    static func address(_ text: String) -> URL? {
        guard let parts = URLComponents(string: text), ["http", "https"].contains(parts.scheme ?? ""),
              let host = parts.host, !host.isEmpty, parts.user == nil, parts.password == nil else { return nil }
        return parts.url
    }
}

struct Station: Codable, Equatable {
    let name: String
    let url: String
    let format: String
    let bitrate: Int?
    let foundOn: String
    var listeners: Int?
    var isVideo: Bool?

    func listLabel(named displayName: String) -> String {
        [displayName, format, bitrate.map { "\($0) kbps" }, listeners.map(ResultView.listenerLabel)]
            .compactMap { $0 }.joined(separator: ", ")
    }
}

struct SearchResult: Decodable {
    let entryWasWebsite: Bool?
    let stations: [Station]
    let requests: Int
    let unreachable: Int
    let limitReached: Bool
    let method: SearchMode?
    let attempts: Int?
    let failedMethods: Int?

    var summary: String {
        let count = stations.count
        let caption = count == 0 ? "No live streams verified." : (count == 1 ? "1 stream found." : "\(count) streams found.")
        return caption + " Checked \(requests) \(requests == 1 ? "address" : "addresses")." +
            (unreachable > 0 ? " \(unreachable) unavailable." : "") + (limitReached ? " Search limit reached." : "") +
            ((attempts ?? 0) > 1 ? " Tried \(attempts ?? 0) methods." : "") +
            ((failedMethods ?? 0) > 0 ? " Methods that could not finish: \(failedMethods ?? 0)." : "")
    }
}

enum SearchCompletionCue {
    case success, failure

    static var failureData: Data? {
        Bundle.main.object(forInfoDictionaryKey: "StreamSiftFailureSound") as? Data
    }

    static func select(verifiedStreams: Int, enabled: Bool, cancelled: Bool) -> Self? {
        guard enabled, !cancelled else { return nil }
        return verifiedStreams > 0 ? .success : .failure
    }
}

enum StreamError: LocalizedError {
    case invalidAddress, browserAddress, search, browserRequired, browserSearch, directorySearch, playlist, tooManyNames, tooManyHandoffs

    var errorDescription: String? {
        switch self {
        case .invalidAddress: return "Enter a public HTTP or HTTPS address, station name or call sign."
        case .browserAddress: return "Enter a public HTTP or HTTPS address."
        case .search: return "Search could not finish. Try the station's player page."
        case .browserRequired: return "Find with Browser requires Chrome or Microsoft Edge."
        case .browserSearch: return "Browser-assisted search could not finish."
        case .directorySearch: return "Public directory lookup could not finish."
        case .playlist: return "The stream does not have a public HTTP or HTTPS address."
        case .tooManyNames: return "There are too many playlists with that name in the selected folder."
        case .tooManyHandoffs: return "Too many playlists were sent recently. Try again in two minutes."
        }
    }
}

enum SearchMode: String, Codable {
    case passive = "--json", browser = "--browser-json", directory = "--directory-json"

    var starting: String {
        switch self {
        case .passive: return "Finding streams..."
        case .browser: return "Searching with a temporary browser..."
        case .directory: return "Checking public radio directories..."
        }
    }

    var failure: StreamError {
        switch self {
        case .passive: return .search
        case .browser: return .browserSearch
        case .directory: return .directorySearch
        }
    }

    var resultPrefix: String {
        switch self {
        case .passive: return ""
        case .browser: return "Browser-assisted search: "
        case .directory: return "Public directory: "
        }
    }
}

struct ScannerOutput {
    private static let maxBytes = 8 * 1024 * 1024
    private static let maxProgressLines = 2
    let fallback: Bool
    private(set) var payload = Data()
    private(set) var invalid = false
    private var progressCount = 0

    mutating func receive(_ data: Data) -> [SearchMode] {
        guard !invalid, data.count <= Self.maxBytes - payload.count else { invalid = true; return [] }
        payload.append(data)
        guard fallback else { return [] }
        var progress: [SearchMode] = []
        while let newline = payload.firstIndex(of: 10) {
            let line = String(decoding: payload[..<newline], as: UTF8.self).trimmingCharacters(in: .newlines)
            guard progressCount < Self.maxProgressLines, let next = SearchMode(rawValue: line) else {
                invalid = true
                break
            }
            progressCount += 1
            progress.append(next)
            payload.removeSubrange(...newline)
        }
        return progress
    }
}

final class ScanJob {
    private let process = Process()

    func start(_ address: String, mode: SearchMode, fallback: Bool, recentSearches: [String], onFallback: @escaping (SearchMode) -> Void,
               completed: @escaping (Result<SearchResult, Error>) -> Void) throws {
        guard !address.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              address.utf16.count <= 4096, !address.contains("\0"), !address.contains(where: \.isNewline)
        else { throw StreamError.invalidAddress }
        guard let scanner = Bundle.main.url(forAuxiliaryExecutable: "Scanner") else { throw StreamError.search }
        let output = Pipe()
        let input = Pipe()
        process.executableURL = scanner
        process.arguments = [mode.rawValue] + (fallback ? ["--fallback"] : [])
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        process.standardInput = input
        try process.run()
        let recent = mode == .passive ? recentSearches.filter { $0.utf16.count <= 1024 && !$0.contains(where: \.isNewline) }.prefix(10) : []
        let request = ([address] + recent).joined(separator: "\n")
        do {
            try input.fileHandleForWriting.write(contentsOf: Data(request.utf8))
            try input.fileHandleForWriting.close()
        } catch {
            try? input.fileHandleForWriting.close()
            cancel()
            throw error
        }
        DispatchQueue.global(qos: .userInitiated).async { [process] in
            var reply = ScannerOutput(fallback: fallback)
            while true {
                let data = output.fileHandleForReading.availableData
                if data.isEmpty { break }
                for next in reply.receive(data) { DispatchQueue.main.async { onFallback(next) } }
            }
            process.waitUntilExit()
            let result = Result {
                if process.terminationStatus == 3 {
                    if mode == .browser { throw StreamError.browserAddress }
                    throw StreamError.invalidAddress
                }
                if process.terminationStatus == 4 { throw StreamError.browserRequired }
                guard process.terminationStatus == 0, !reply.invalid else {
                    throw mode.failure
                }
                return try JSONDecoder().decode(SearchResult.self, from: reply.payload)
            }
            DispatchQueue.main.async { completed(result) }
        }
    }

    func cancel() { if process.isRunning { process.terminate() } }
}

enum Playlist {
    static func stem(_ name: String) -> String {
        let value = String(name.prefix(96).map { character in
            character == "/" || character == ":" || character == "\\" || character.unicodeScalars.contains {
                CharacterSet.controlCharacters.contains($0)
            } ? "_" : character
        }).trimmingCharacters(in: CharacterSet(charactersIn: " ."))
        return value.isEmpty ? "Stream" : value
    }

    static func stem(_ stations: [Station]) -> String {
        stations.count == 1 ? stem(stations[0].name) : "Streams"
    }

    static func data(_ stations: [Station]) throws -> Data {
        guard !stations.isEmpty else { throw StreamError.playlist }
        var text = "#EXTM3U\r\n"
        for station in stations {
            guard !station.url.contains(where: \.isNewline), let address = StreamPreview.address(station) else {
                throw StreamError.playlist
            }
            let name = String(station.name.prefix(160).map { character in
                character.isNewline || character.unicodeScalars.contains { CharacterSet.controlCharacters.contains($0) } ? " " : character
            }).trimmingCharacters(in: .whitespaces)
            text += "#EXTINF:-1,\(name)\r\n\(address.absoluteString)\r\n"
        }
        return Data(text.utf8)
    }

    static func saveNew(_ stations: [Station], in folder: URL) throws -> URL {
        let data = try data(stations)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let name = stem(stations)
        for index in 1...100 {
            let file = folder.appendingPathComponent(index == 1 ? "\(name).m3u8" : "\(name) (\(index)).m3u8")
            do {
                try data.write(to: file, options: .withoutOverwriting)
                return file
            } catch let error as NSError where error.domain == NSCocoaErrorDomain &&
                error.code == CocoaError.fileWriteFileExists.rawValue { continue }
        }
        throw StreamError.tooManyNames
    }
}

struct Preferences: Equatable {
    var sounds = true
    var startupUpdates = true
    var automaticFallback = false
    var mediaPlayer = MediaPlayerChoice.automatic
    var customFolder: String?

    static var defaultFolder: URL {
        let music = FileManager.default.urls(for: .musicDirectory, in: .userDomainMask).first ??
            FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Music")
        return music.appendingPathComponent("StreamSift", isDirectory: true)
    }

    var folder: URL {
        customFolder.map { URL(fileURLWithPath: $0, isDirectory: true) } ?? Self.defaultFolder
    }

    init() {}

    init(reading defaults: UserDefaults) {
        sounds = (defaults.object(forKey: "SearchSounds") as? Bool) ?? true
        startupUpdates = (defaults.object(forKey: "CheckUpdatesAtStartup") as? Bool) ?? true
        automaticFallback = defaults.bool(forKey: "AutomaticFallback")
        mediaPlayer = MediaPlayerChoice(rawValue: defaults.string(forKey: "MediaPlayer") ?? "") ?? .automatic
        customFolder = Self.validFolder(defaults.string(forKey: "PlaylistFolder") ?? "")
    }

    func save(to defaults: UserDefaults) {
        defaults.set(sounds, forKey: "SearchSounds")
        defaults.set(startupUpdates, forKey: "CheckUpdatesAtStartup")
        defaults.set(automaticFallback, forKey: "AutomaticFallback")
        defaults.set(mediaPlayer.rawValue, forKey: "MediaPlayer")
        defaults.set(customFolder, forKey: "PlaylistFolder")
    }

    static func validFolder(_ value: String) -> String? {
        let path = (value.trimmingCharacters(in: .whitespacesAndNewlines) as NSString).expandingTildeInPath
        guard path.hasPrefix("/"), !path.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) })
        else { return nil }
        let normalized = URL(fileURLWithPath: path, isDirectory: true).standardizedFileURL.path
        return normalized == defaultFolder.path ? nil : normalized
    }
}
