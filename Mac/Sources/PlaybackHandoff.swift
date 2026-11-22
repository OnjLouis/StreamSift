import Foundation

enum PlaybackHandoff {
    static let retention: TimeInterval = 120
    private static let maxPending = 24
    static var directory: URL {
        FileManager.default.temporaryDirectory.appendingPathComponent("StreamSift-FastPlay", isDirectory: true)
    }

    static func stage(_ station: Station, in folder: URL = directory) throws -> URL {
        let data = try Playlist.data([station])
        let io = FileManager.default
        if !io.fileExists(atPath: folder.path) {
            try io.createDirectory(at: folder, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        }
        guard try prune(in: folder) < maxPending else { throw StreamError.tooManyHandoffs }
        let file = folder.appendingPathComponent("handoff-" + UUID().uuidString + ".m3u8")
        try data.write(to: file, options: .withoutOverwriting)
        return file
    }

    @discardableResult static func prune(in folder: URL = directory, now: Date = Date()) throws -> Int {
        let io = FileManager.default
        guard io.fileExists(atPath: folder.path) else { return 0 }
        let root = try folder.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
        guard root.isDirectory == true && root.isSymbolicLink == false else { throw StreamError.playlist }
        let keys: Set<URLResourceKey> = [.isRegularFileKey, .isSymbolicLinkKey, .contentModificationDateKey]
        let files = try io.contentsOfDirectory(at: folder, includingPropertiesForKeys: Array(keys), options: [.skipsHiddenFiles])
        var remaining = 0
        for file in files {
            let name = file.deletingPathExtension().lastPathComponent
            guard file.pathExtension == "m3u8", name.hasPrefix("handoff-"),
                  UUID(uuidString: String(name.dropFirst(8))) != nil,
                  let attributes = try? file.resourceValues(forKeys: keys),
                  attributes.isRegularFile == true, attributes.isSymbolicLink == false else { continue }
            if let modified = attributes.contentModificationDate, now.timeIntervalSince(modified) >= retention {
                do { try io.removeItem(at: file); continue } catch {}
            }
            remaining += 1
        }
        return remaining
    }
}
