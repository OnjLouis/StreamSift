import Foundation

struct FavouriteImport: Decodable {
    let stations: [Station]
    let skipped: Int

    func summary(added: Int) -> String {
        let missed = skipped + stations.count - added
        return "Added \(added) \(added == 1 ? "favourite" : "favourites")." +
            (missed > 0 ? " Skipped \(missed) \(missed == 1 ? "entry" : "entries")." : "")
    }
}

enum FavouriteError: LocalizedError {
    case full, invalidPlaylist

    var errorDescription: String? {
        switch self {
        case .full: return "Favourites is full."
        case .invalidPlaylist: return "Choose a UTF-8 M3U8 playlist with station or file addresses."
        }
    }
}

enum Favourites {
    private static let key = "Favourites"
    private static let maxEntries = 512
    private static let maxBytes = 2 * 1024 * 1024

    static func canSave(_ station: Station) -> Bool {
        station.isHls != true && station.isVideo != true && station.format.uppercased() != "HLS" &&
            LastResults.safeText(station.name, limit: 512) && LastResults.safeText(station.format, limit: 32) &&
            LastResults.stableAddress(station.url) &&
            URL(string: station.url)?.pathExtension.lowercased() != "m3u8"
    }

    static func merge(_ existing: [Station], with incoming: [Station]) -> [Station] {
        var seen = Set<URL>()
        var merged: [Station] = []
        for var row in existing + incoming {
            guard canSave(row), let url = StreamPreview.address(row), seen.insert(url).inserted else { continue }
            if !LastResults.stableAddress(row.foundOn) { row.foundOn = row.url }
            row.scanTitle = nil
            merged.append(row)
            if merged.count == maxEntries { break }
        }
        return merged
    }

    static func read(from defaults: UserDefaults) -> [Station] {
        guard let data = defaults.data(forKey: key), data.count <= maxBytes,
              let saved = try? JSONDecoder().decode([Station].self, from: data) else { return [] }
        return merge([], with: saved)
    }

    static func save(_ stations: [Station], to defaults: UserDefaults) throws {
        let data = try JSONEncoder().encode(stations)
        guard data.count <= maxBytes else { throw FavouriteError.full }
        defaults.set(data, forKey: key)
    }

    static func random(in stations: [Station], avoiding last: String?) -> Station? {
        let alternatives = stations.filter { $0.url != last }
        return (alternatives.isEmpty ? stations : alternatives).randomElement()
    }

    static func importPlaylist(_ url: URL) throws -> FavouriteImport {
        let input = try FileHandle(forReadingFrom: url)
        defer { try? input.close() }
        let data = try convert("--import-favourites", input: input)
        return try JSONDecoder().decode(FavouriteImport.self, from: data)
    }

    static func exportPlaylist(_ stations: [Station], to url: URL) throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("StreamSift-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false,
                                                attributes: [.posixPermissions: 0o700])
        defer { try? FileManager.default.removeItem(at: directory) }
        let source = directory.appendingPathComponent("favourites.json")
        try JSONEncoder().encode(stations).write(to: source)
        let input = try FileHandle(forReadingFrom: source)
        defer { try? input.close() }
        let playlist = try convert("--export-favourites", input: input)
        try playlist.write(to: url, options: .atomic)
    }

    private static func convert(_ operation: String, input: FileHandle) throws -> Data {
        guard let scanner = Bundle.main.url(forAuxiliaryExecutable: "Scanner") else { throw FavouriteError.invalidPlaylist }
        let output = Pipe()
        let process = Process()
        process.executableURL = scanner
        process.arguments = [operation]
        process.standardInput = input
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        try process.run()
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else { throw FavouriteError.invalidPlaylist }
        return data
    }
}
