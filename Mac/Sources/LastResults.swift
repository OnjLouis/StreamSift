import Foundation

struct LastResults: Codable {
    private static let key = "LastResults"
    private static let maxStations = 512
    private static let maxBytes = 2 * 1024 * 1024
    let input: String
    let mode: SearchMode
    let stations: [Station]
    let format: String?
    let order: Int
    let selection: String?

    var view: ResultView { ResultView(format: format, order: ResultOrder(rawValue: order) ?? .search) }

    static func capture(input raw: String, mode: SearchMode, stations: [Station], view: ResultView,
                        selection: String?) -> LastResults? {
        let input = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let inputKey = SearchHistory.key(input),
              inputKey.hasPrefix("name:") || stableAddress(input.contains("://") ? input : "https://" + input)
        else { return nil }
        // An HLS server can embed a session identifier in an otherwise plain path.
        var seen = Set<String>()
        var safe: [Station] = []
        for station in stations {
            guard station.format != "HLS", station.isVideo != true, station.isTrack != true,
                  safeText(station.name, limit: 512), safeText(station.format, limit: 32),
                  stableAddress(station.url), stableAddress(station.foundOn), seen.insert(station.url).inserted else { continue }
            safe.append(station)
            if safe.count == maxStations { break }
        }
        guard !safe.isEmpty else { return nil }
        let restored = view.retainingAvailable(in: safe)
        return LastResults(input: input, mode: mode, stations: safe, format: restored.format,
                           order: restored.order.rawValue,
                           selection: restored.apply(safe).contains { $0.url == selection } ? selection : nil)
    }

    private static func safeText(_ text: String, limit: Int) -> Bool {
        !text.isEmpty && text.utf16.count <= limit &&
            !text.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains)
    }

    private static func stableAddress(_ text: String) -> Bool {
        guard safeText(text, limit: 2048), !text.contains(where: { "?#@\\".contains($0) }),
              let host = StreamPreview.address(text)?.host?.lowercased().trimmingCharacters(in: CharacterSet(charactersIn: ".")),
              let last = host.split(separator: ".").last, host.contains("."), !host.contains(":"),
              last.contains(where: { $0.isLetter }),
              ![".localhost", ".local", ".internal", ".home.arpa"].contains(where: { host.hasSuffix($0) })
        else { return false }
        return true
    }

    static func read(from defaults: UserDefaults) -> LastResults? {
        guard let data = defaults.data(forKey: key), data.count <= maxBytes,
              let saved = try? JSONDecoder().decode(Self.self, from: data) else { return nil }
        return capture(input: saved.input, mode: saved.mode, stations: saved.stations,
                       view: saved.view, selection: saved.selection)
    }

    func save(to defaults: UserDefaults) {
        if let data = try? JSONEncoder().encode(self), data.count <= Self.maxBytes {
            defaults.set(data, forKey: Self.key)
        }
    }

    static func clear(in defaults: UserDefaults) { defaults.removeObject(forKey: key) }
}
