import Foundation

struct Station: Decodable {
    let name: String
    let url: String
    let format: String
    let bitrate: Int?
    let foundOn: String

    func listLabel(named displayName: String) -> String {
        [displayName, format, bitrate.map { "\($0) kbps" }].compactMap { $0 }.joined(separator: ", ")
    }
}

struct SearchResult: Decodable {
    let stations: [Station]
    let requests: Int
    let unreachable: Int
    let limitReached: Bool

    var summary: String {
        let count = stations.count
        let caption = count == 0 ? "No live streams verified." : (count == 1 ? "1 stream found." : "\(count) streams found.")
        return caption + " Checked \(requests) \(requests == 1 ? "address" : "addresses")." +
            (unreachable > 0 ? " \(unreachable) unavailable." : "") + (limitReached ? " Search limit reached." : "")
    }
}

enum StreamError: LocalizedError {
    case invalidAddress, search, browserRequired, browserSearch, directorySearch, playlist, tooManyNames

    var errorDescription: String? {
        switch self {
        case .invalidAddress: return "Enter a public HTTP or HTTPS address."
        case .search: return "Search could not finish. Try the station's player page."
        case .browserRequired: return "Find with Browser requires Chrome or Microsoft Edge."
        case .browserSearch: return "Browser-assisted search could not finish."
        case .directorySearch: return "Public directory lookup could not finish."
        case .playlist: return "The stream does not have a public HTTP or HTTPS address."
        case .tooManyNames: return "There are too many playlists with that name in the selected folder."
        }
    }
}

enum SearchMode: String {
    case passive = "--json", browser = "--browser-json", directory = "--directory-json"

    var starting: String {
        switch self {
        case .passive: return "Searching..."
        case .browser: return "Searching with a temporary browser..."
        case .directory: return "Checking the public radio directory..."
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

final class ScanJob {
    private let process = Process()

    func start(_ address: String, mode: SearchMode = .passive,
               completed: @escaping (Result<SearchResult, Error>) -> Void) throws {
        guard !address.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              address.utf16.count <= 4096, !address.contains("\0") else { throw StreamError.invalidAddress }
        guard let scanner = Bundle.main.url(forAuxiliaryExecutable: "Scanner") else { throw StreamError.search }
        let output = Pipe()
        let input = Pipe()
        process.executableURL = scanner
        process.arguments = [mode.rawValue]
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        process.standardInput = input
        try process.run()
        // The helper reads at most 4097 UTF-16 units and does not start the scan until stdin closes.
        do {
            try input.fileHandleForWriting.write(contentsOf: Data(address.utf8))
            try input.fileHandleForWriting.close()
        } catch {
            try? input.fileHandleForWriting.close()
            cancel()
            throw error
        }
        DispatchQueue.global(qos: .userInitiated).async { [process] in
            let data = output.fileHandleForReading.readDataToEndOfFile()
            process.waitUntilExit()
            let result = Result {
                if process.terminationStatus == 3 { throw StreamError.invalidAddress }
                if process.terminationStatus == 4 { throw StreamError.browserRequired }
                guard process.terminationStatus == 0, data.count <= 8 * 1024 * 1024 else {
                    throw mode.failure
                }
                return try JSONDecoder().decode(SearchResult.self, from: data)
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

    static func data(_ station: Station) throws -> Data {
        guard !station.url.contains(where: \.isNewline),
              let address = URLComponents(string: station.url), ["http", "https"].contains(address.scheme ?? ""),
              address.host != nil, address.user == nil, address.password == nil else { throw StreamError.playlist }
        let name = String(station.name.prefix(160).map { character in
            character.isNewline || character.unicodeScalars.contains { CharacterSet.controlCharacters.contains($0) } ? " " : character
        })
        return Data("#EXTM3U\r\n#EXTINF:-1,\(name)\r\n\(station.url)\r\n".utf8)
    }

    static func saveNew(_ station: Station, in folder: URL) throws -> URL {
        let data = try data(station)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let name = stem(station.name)
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
    var startupUpdates = false
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
        startupUpdates = defaults.bool(forKey: "CheckUpdatesAtStartup")
        customFolder = Self.validFolder(defaults.string(forKey: "PlaylistFolder") ?? "")
    }

    func save(to defaults: UserDefaults) {
        defaults.set(sounds, forKey: "SearchSounds")
        defaults.set(startupUpdates, forKey: "CheckUpdatesAtStartup")
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
