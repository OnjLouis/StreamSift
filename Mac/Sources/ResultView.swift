import Foundation

enum ResultOrder: Int, CaseIterable {
    case search, nameAscending, nameDescending, bitrateDescending, bitrateAscending
    case listenersDescending, listenersAscending
    case dateModifiedDescending, dateModifiedAscending, fileSizeDescending, fileSizeAscending

    func label(tracks: Bool) -> String {
        switch self {
        case .search: return tracks ? "Listed order" : "Search order"
        case .nameAscending: return tracks ? "Track name, A to Z" : "Station name, A to Z"
        case .nameDescending: return tracks ? "Track name, Z to A" : "Station name, Z to A"
        case .bitrateDescending: return "Bitrate, highest first"
        case .bitrateAscending: return "Bitrate, lowest first"
        case .listenersDescending: return "Listeners, most first"
        case .listenersAscending: return "Listeners, fewest first"
        case .dateModifiedDescending: return "Date modified, newest first"
        case .dateModifiedAscending: return "Date modified, oldest first"
        case .fileSizeDescending: return "File size, largest first"
        case .fileSizeAscending: return "File size, smallest first"
        }
    }
}

struct ResultView {
    var format: String?
    var order = ResultOrder.search

    static func forSearch(fileIndex: Bool, preferListedOrder: Bool = false) -> ResultView {
        ResultView(order: fileIndex && !preferListedOrder ? .nameAscending : .search)
    }

    static func formats(_ stations: [Station]) -> [String] {
        var seen = Set<String>()
        return stations.map(\.format).filter { seen.insert($0.uppercased()).inserted }
            .sorted { $0.localizedCaseInsensitiveCompare($1) == .orderedAscending }
    }

    static func availableOrders(_ stations: [Station]) -> [ResultOrder] {
        var orders: [ResultOrder] = [.search, .nameAscending, .nameDescending]
        if stations.contains(where: { $0.isTrack == true }) {
            if stations.contains(where: { $0.dateModified != nil }) { orders += [.dateModifiedDescending, .dateModifiedAscending] }
            if stations.contains(where: { $0.fileSizeBytes != nil }) { orders += [.fileSizeDescending, .fileSizeAscending] }
        } else {
            orders += [.bitrateDescending, .bitrateAscending]
            if stations.contains(where: { $0.listeners != nil }) { orders += [.listenersDescending, .listenersAscending] }
        }
        return orders
    }

    func retainingAvailable(in stations: [Station], fileIndex: Bool = false) -> ResultView {
        var next = self
        if let format, !stations.contains(where: { $0.format.caseInsensitiveCompare(format) == .orderedSame }) {
            next.format = nil
        }
        if !Self.availableOrders(stations).contains(order) {
            next.order = Self.forSearch(fileIndex: fileIndex).order
        }
        return next
    }

    func apply(_ stations: [Station]) -> [Station] {
        let visible = stations.filter { station in format.map { $0.caseInsensitiveCompare(station.format) == .orderedSame } ?? true }
        switch order {
        case .search: return visible
        case .nameAscending, .nameDescending:
            let tracks = stations.contains { $0.isTrack == true }
            let direction: ComparisonResult = order == .nameAscending ? .orderedAscending : .orderedDescending
            return visible.sorted { left, right in
                if tracks {
                    return left.name.compare(right.name, options: [.caseInsensitive, .numeric], locale: .current) == direction
                }
                return left.name.localizedCaseInsensitiveCompare(right.name) == direction
            }
        case .bitrateDescending, .bitrateAscending:
            return sorted(by: \.bitrate, descending: order == .bitrateDescending)
        case .listenersDescending, .listenersAscending:
            return sorted(by: \.listeners, descending: order == .listenersDescending)
        case .dateModifiedDescending, .dateModifiedAscending:
            return sorted(by: \.dateModified, descending: order == .dateModifiedDescending)
        case .fileSizeDescending, .fileSizeAscending:
            return sorted(by: \.fileSizeBytes, descending: order == .fileSizeDescending)
        }

        func sorted<Value: Comparable>(by key: KeyPath<Station, Value?>, descending: Bool) -> [Station] {
            return visible.sorted { left, right in
                guard let a = left[keyPath: key] else { return false }
                guard let b = right[keyPath: key] else { return true }
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
                          row.listeners.map(listenerLabel) ?? "", row.scanTitle ?? ""]
            if values.contains(where: { $0.range(of: query, options: [.caseInsensitive, .diacriticInsensitive]) != nil }) {
                return index
            }
        }
        return nil
    }

    static func listenerLabel(_ count: Int) -> String { count == 1 ? "1 listener" : "\(count) listeners" }
}
