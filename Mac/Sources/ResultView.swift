import Foundation

enum ResultOrder: Int, CaseIterable {
    case search, nameAscending, nameDescending, bitrateDescending, bitrateAscending
    case listenersDescending, listenersAscending

    var label: String {
        switch self {
        case .search: return "Search order"
        case .nameAscending: return "Station name, A to Z"
        case .nameDescending: return "Station name, Z to A"
        case .bitrateDescending: return "Bitrate, highest first"
        case .bitrateAscending: return "Bitrate, lowest first"
        case .listenersDescending: return "Listeners, most first"
        case .listenersAscending: return "Listeners, fewest first"
        }
    }
}

struct ResultView {
    var format: String?
    var order = ResultOrder.search

    static func formats(_ stations: [Station]) -> [String] {
        var seen = Set<String>()
        return stations.map(\.format).filter { seen.insert($0.uppercased()).inserted }
            .sorted { $0.localizedCaseInsensitiveCompare($1) == .orderedAscending }
    }

    func retainingAvailable(in stations: [Station]) -> ResultView {
        var next = self
        if let format, !stations.contains(where: { $0.format.caseInsensitiveCompare(format) == .orderedSame }) {
            next.format = nil
        }
        if (order == .listenersAscending || order == .listenersDescending) && !stations.contains(where: { $0.listeners != nil }) {
            next.order = .search
        }
        return next
    }

    func apply(_ stations: [Station]) -> [Station] {
        let visible = stations.filter { station in format.map { $0.caseInsensitiveCompare(station.format) == .orderedSame } ?? true }
        switch order {
        case .search: return visible
        case .nameAscending, .nameDescending:
            return visible.sorted { left, right in
                left.name.localizedCaseInsensitiveCompare(right.name) ==
                    (order == .nameAscending ? .orderedAscending : .orderedDescending)
            }
        case .bitrateDescending, .bitrateAscending, .listenersDescending, .listenersAscending:
            let listeners = order == .listenersDescending || order == .listenersAscending
            let descending = order == .bitrateDescending || order == .listenersDescending
            return visible.sorted { left, right in
                guard let a = listeners ? left.listeners : left.bitrate else { return false }
                guard let b = listeners ? right.listeners : right.bitrate else { return true }
                return descending ? a > b : a < b
            }
        }
    }

    static func find(in stations: [Station], after anchor: Int, query: String, backwards: Bool) -> Int? {
        let query = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !stations.isEmpty, !query.isEmpty else { return nil }
        let size = stations.count
        let start = stations.indices.contains(anchor) ? anchor : (backwards ? 0 : size - 1)
        for step in 1...size {
            let index = (start + (backwards ? -step : step) + size) % size
            let row = stations[index]
            let values = [row.name, row.format, row.url, row.bitrate.map { "\($0) kbps" } ?? "",
                          row.listeners.map(listenerLabel) ?? ""]
            if values.contains(where: { $0.range(of: query, options: [.caseInsensitive, .diacriticInsensitive]) != nil }) {
                return index
            }
        }
        return nil
    }

    static func listenerLabel(_ count: Int) -> String { count == 1 ? "1 listener" : "\(count) listeners" }
}
