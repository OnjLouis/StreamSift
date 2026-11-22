import Foundation

struct SearchHistory {
    static let capacity = 10
    private static let storeKey = "RecentSuccessfulSearches"
    private(set) var entries: [String] = []

    init(reading defaults: UserDefaults) {
        var seen = Set<String>()
        for raw in (defaults.stringArray(forKey: Self.storeKey) ?? []).prefix(2 * Self.capacity) {
            let value = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            guard let key = Self.key(value), seen.insert(key).inserted else { continue }
            entries.append(value)
            if entries.count == Self.capacity { break }
        }
    }

    mutating func remember(_ input: String, verifiedStreams: Int, in defaults: UserDefaults) -> Bool {
        guard verifiedStreams > 0 else { return false }
        let value = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let key = Self.key(value), entries.first != value else { return false }
        entries.removeAll { Self.key($0) == key }
        entries.insert(value, at: 0)
        if entries.count > Self.capacity { entries.removeLast() }
        defaults.set(entries, forKey: Self.storeKey)
        return true
    }

    mutating func clear(in defaults: UserDefaults) {
        defaults.removeObject(forKey: Self.storeKey)
        entries.removeAll()
    }

    static func key(_ value: String) -> String? {
        guard !value.isEmpty, value.utf16.count <= 1024,
              !value.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains),
              !value.contains(where: { "?#@\\".contains($0) }) else { return nil }
        let explicit = value.contains("://")
        if var parts = URLComponents(string: explicit ? value : "https://" + value),
           let scheme = parts.scheme?.lowercased(), scheme == "https" || scheme == "http",
           let host = parts.host?.lowercased(), host.contains(".") || host.contains(":"),
           parts.user == nil, parts.password == nil {
            parts.scheme = scheme
            parts.host = host
            if parts.path.isEmpty { parts.path = "/" }
            return parts.string.map { (explicit ? "url:" : "bare:") + $0 }
        }
        guard !explicit, (2...96).contains(value.count),
              value.contains(where: { $0.isLetter || $0.isNumber }),
              value.allSatisfy({ $0.isLetter || $0.isNumber || $0.isWhitespace || ".-'&".contains($0) }) else { return nil }
        return "name:" + value.uppercased()
    }
}
