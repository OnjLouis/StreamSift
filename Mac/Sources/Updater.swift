import AppKit
import Foundation

enum UpdateFailure: LocalizedError {
    case invalid(String)
    var errorDescription: String? { if case let .invalid(message) = self { return message }; return nil }
}

enum Updater {
    static let app = "StreamSift.app"
    static let bundleID = "me.onj.streamsift"
    static let repository = "OnjLouis/StreamSift"
    static let requirement = "=anchor apple generic and certificate leaf[field.1.2.840.113635.100.6.1.13] exists and certificate leaf[subject.OU] = \"83NN3HS237\" and identifier \"\(bundleID)\""
    private static let limit = 32 * 1024 * 1024
    private static let extractLimit = 48 * 1024 * 1024

    struct Release {
        let version: String
        let notes: String
        let url: URL
    }
    struct GitHubRelease: Decodable {
        struct Asset: Decodable {
            let name: String
            let size: Int
            let browser_download_url: URL
        }
        let tag_name: String
        let draft: Bool
        let prerelease: Bool
        let body: String?
        let assets: [Asset]
    }

    static func parts(_ value: String) -> [Int]? {
        let pieces = value.split(separator: ".", omittingEmptySubsequences: false)
        let numbers = pieces.compactMap { Int($0) }
        guard pieces.count == 3, numbers.count == 3, numbers.allSatisfy({ (0...999_999).contains($0) }),
              numbers.map(String.init).joined(separator: ".") == value else { return nil }
        return numbers
    }

    static func candidate(_ release: GitHubRelease, installed: String) throws -> Release? {
        guard !release.draft, !release.prerelease, release.tag_name.hasPrefix("v"),
              let current = parts(installed) else { throw UpdateFailure.invalid("The release metadata is invalid.") }
        let version = String(release.tag_name.dropFirst())
        guard let offered = parts(version) else { throw UpdateFailure.invalid("The release version is invalid.") }
        guard current.lexicographicallyPrecedes(offered) else { return nil }
        let name = "StreamSift-\(version)-macOS.zip"
        let expected = URL(string: "https://github.com/\(repository)/releases/download/v\(version)/\(name)")!
        let matches = release.assets.filter { $0.name == name }
        guard matches.count == 1, let asset = matches.first, (1...limit).contains(asset.size),
              asset.browser_download_url == expected else {
            throw UpdateFailure.invalid("The release has no matching StreamSift Mac update.")
        }
        return Release(version: version, notes: release.body ?? "", url: expected)
    }

    static func latest() async throws -> Release? {
        let url = URL(string: "https://api.github.com/repos/\(repository)/releases/latest")!
        let data = try await fetch(url, maxBytes: 2 * 1024 * 1024, asset: false)
        let current = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
        return try candidate(JSONDecoder().decode(GitHubRelease.self, from: data), installed: current)
    }

    @MainActor static func install(_ release: Release) async throws {
        let target = URL(fileURLWithPath: "/Applications/\(app)")
        guard Bundle.main.bundleURL.resolvingSymlinksInPath().path == target.path,
              FileManager.default.isWritableFile(atPath: target.deletingLastPathComponent().path) else {
            throw UpdateFailure.invalid("Open StreamSift from Applications before updating. If Applications is not writable, install the new version manually.")
        }
        let staging = FileManager.default.temporaryDirectory.appendingPathComponent("StreamSift-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: staging, withIntermediateDirectories: false,
                                                attributes: [.posixPermissions: 0o700])
        var handedOff = false
        defer { if !handedOff { try? FileManager.default.removeItem(at: staging) } }
        let zip = staging.appendingPathComponent("update.zip")
        try await fetch(release.url, maxBytes: limit, asset: true).write(to: zip)
        let stageApp = try await Task.detached { try prepare(zip: zip, staging: staging, version: release.version) }.value
        let helper = staging.appendingPathComponent("install.zsh")
        try installer(stageApp: stageApp, target: target, staging: staging,
                      pid: ProcessInfo.processInfo.processIdentifier).write(to: helper, atomically: true, encoding: .utf8)
        let worker = Process()
        worker.executableURL = URL(fileURLWithPath: "/bin/zsh")
        worker.arguments = [helper.path]
        try worker.run()
        handedOff = true
        NSApp.terminate(nil)
    }

    private final class Redirects: NSObject, URLSessionTaskDelegate {
        let asset: Bool
        init(asset: Bool) { self.asset = asset }

        func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                        newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
            completionHandler(asset && request.url.map(Updater.isAssetHost) == true ? request : nil)
        }
    }

    static func isAssetHost(_ url: URL) -> Bool {
        guard url.scheme == "https", url.port == nil, url.user == nil, url.password == nil else { return false }
        return (url.host == "github.com" && url.path.hasPrefix("/\(repository)/releases/download/")) ||
            (url.host == "release-assets.githubusercontent.com" && url.path.hasPrefix("/github-production-release-asset"))
    }

    private static func fetch(_ url: URL, maxBytes: Int, asset: Bool) async throws -> Data {
        let config = URLSessionConfiguration.ephemeral
        config.httpShouldSetCookies = false
        config.timeoutIntervalForRequest = 25
        config.timeoutIntervalForResource = asset ? 100 : 30
        let session = URLSession(configuration: config, delegate: Redirects(asset: asset), delegateQueue: nil)
        defer { session.invalidateAndCancel() }
        var request = URLRequest(url: url)
        request.setValue("StreamSift updater", forHTTPHeaderField: "User-Agent")
        if !asset { request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept") }
        let (bytes, result) = try await session.bytes(for: request)
        guard let response = result as? HTTPURLResponse, response.statusCode == 200, let final = response.url,
              asset ? isAssetHost(final) : final == url, response.expectedContentLength <= Int64(maxBytes) else {
            throw UpdateFailure.invalid("The update could not be downloaded. Try again later or visit the GitHub project.")
        }
        var data = Data()
        if response.expectedContentLength > 0 { data.reserveCapacity(Int(response.expectedContentLength)) }
        for try await byte in bytes {
            guard data.count < maxBytes else { throw UpdateFailure.invalid("The update download is too large.") }
            data.append(byte)
        }
        return data
    }

    static func validateEntries(_ listing: String, _ details: String) throws {
        let entries = listing.split(separator: "\n").map(String.init)
        let allowed = Set([
            "\(app)/", "\(app)/Contents/", "\(app)/Contents/Info.plist",
            "\(app)/Contents/MacOS/", "\(app)/Contents/MacOS/StreamSift", "\(app)/Contents/MacOS/Scanner",
            "\(app)/Contents/Resources/", "\(app)/Contents/Resources/Manual.html",
            "\(app)/Contents/Resources/AngleSharp-LICENSE.txt", "\(app)/Contents/Resources/DotNet-LICENSE.txt",
            "\(app)/Contents/Resources/DotNet-ThirdPartyNotices.txt", "\(app)/Contents/Resources/search.wav",
            "\(app)/Contents/Resources/done.wav", "\(app)/Contents/_CodeSignature/",
            "\(app)/Contents/_CodeSignature/CodeResources", "\(app)/Contents/CodeResources"
        ])
        let required = allowed.filter { !$0.hasSuffix("/") && !$0.hasSuffix("/CodeResources") }
        guard !entries.isEmpty, Set(entries).count == entries.count, Set(entries).isSubset(of: allowed),
              Set(required).isSubset(of: Set(entries)), entries.contains("\(app)/Contents/_CodeSignature/CodeResources"),
              !details.split(separator: "\n").contains(where: { $0.hasPrefix("l") }) else {
            throw UpdateFailure.invalid("The update archive contains unexpected files.")
        }
    }

    static func prepare(zip: URL, staging: URL, version: String) throws -> URL {
        try validateEntries(run("/usr/bin/unzip", ["-Z", "-1", zip.path]), run("/usr/bin/zipinfo", ["-l", zip.path]))
        let totals = try run("/usr/bin/zipinfo", ["-t", zip.path])
        guard let match = totals.range(of: #"[0-9]+(?= bytes uncompressed)"#, options: .regularExpression),
              let size = Int(totals[match]), (1...extractLimit).contains(size) else {
            throw UpdateFailure.invalid("The update archive is too large or invalid.")
        }
        let extract = staging.appendingPathComponent("Extract")
        try FileManager.default.createDirectory(at: extract, withIntermediateDirectories: false)
        _ = try run("/usr/bin/ditto", ["-x", "-k", zip.path, extract.path])
        let candidate = extract.appendingPathComponent(app)
        try verify(candidate, version: version)
        return candidate
    }

    static func verify(_ candidate: URL, version: String, assess: Bool = true) throws {
        guard let bundle = Bundle(url: candidate), bundle.bundleIdentifier == bundleID,
              bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String == version else {
            throw UpdateFailure.invalid("The downloaded app has the wrong identity or version.")
        }
        _ = try run("/usr/bin/codesign", ["--verify", "--deep", "--strict", "-R", requirement, candidate.path])
        if assess { _ = try run("/usr/sbin/spctl", ["--assess", "--type", "execute", candidate.path]) }
        for executable in ["StreamSift", "Scanner"] {
            let output = try run("/usr/bin/lipo", ["-archs", candidate.appendingPathComponent("Contents/MacOS/\(executable)").path])
            guard Set(output.split(whereSeparator: \.isWhitespace)) == ["arm64", "x86_64"] else {
                throw UpdateFailure.invalid("The downloaded app is not universal.")
            }
        }
    }

    // Adapted from the ElevenLabs Mac installer: stage alongside the target, then switch and verify.
    static func installer(stageApp: URL, target: URL, staging: URL, pid: Int32, reopen: Bool = true) -> String {
        func quote(_ value: String) -> String { "'" + value.replacingOccurrences(of: "'", with: "'\\''") + "'" }
        let suffix = UUID().uuidString
        return """
        #!/bin/zsh
        set -eu
        target=\(quote(target.path))
        staged=\(quote(stageApp.path))
        staging=\(quote(staging.path))
        new=\(quote(target.path + ".new-" + suffix))
        old=\(quote(target.path + ".old-" + suffix))
        requirement=\(quote(requirement))
        committed=0
        failure() {
          local result_code=$?
          trap - EXIT
          if [[ "$committed" == 0 && -d "$old" ]]; then
            if [[ -e "$target" ]]; then /bin/mv -- "$target" "$new.rejected" || exit 1; fi
            /bin/mv -- "$old" "$target" || exit 1
            \(reopen ? "/usr/bin/open \"$target\" || true" : ":")
          elif [[ "$committed" == 0 && -d "$target" ]]; then
            \(reopen ? "if ! kill -0 \(pid) 2>/dev/null; then /usr/bin/open \"$target\" || true; fi" : ":")
          fi
          if [[ -d "$new" ]]; then /bin/rm -rf -- "$new"; fi
          if [[ -d "$new.rejected" ]]; then /bin/rm -rf -- "$new.rejected"; fi
          /bin/rm -rf -- "$staging"
          if [[ "$committed" == 0 && "$result_code" != 0 ]]; then
            \(reopen ? "/usr/bin/osascript -e 'display alert \"StreamSift update could not finish\" message \"The previous app was kept. Try again or install the new version manually.\" as critical' || true" : ":")
          fi
          exit "$result_code"
        }
        trap failure EXIT
        for i in {1..100}; do
          if ! kill -0 \(pid) 2>/dev/null; then break; fi
          /bin/sleep 0.2
        done
        if kill -0 \(pid) 2>/dev/null; then exit 1; fi
        [[ -d "$target" && ! -L "$target" && ! -e "$new" && ! -e "$old" ]]
        /usr/bin/codesign --verify --deep --strict -R "$requirement" "$target"
        /usr/bin/ditto "$staged" "$new"
        /usr/bin/codesign --verify --deep --strict -R "$requirement" "$new"
        /bin/mv -- "$target" "$old"
        /bin/mv -- "$new" "$target"
        /usr/bin/codesign --verify --deep --strict -R "$requirement" "$target"
        \(reopen ? "/usr/bin/open \"$target\"" : ":")
        committed=1
        /bin/rm -rf -- "$old"
        """
    }

    @discardableResult static func run(_ executable: String, _ arguments: [String]) throws -> String {
        let process = Process()
        let pipe = Pipe()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        process.environment = ProcessInfo.processInfo.environment.merging(["LC_ALL": "C"]) { _, new in new }
        process.standardOutput = pipe
        process.standardError = pipe
        try process.run()
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else {
            throw UpdateFailure.invalid("Update verification failed (\(URL(fileURLWithPath: executable).lastPathComponent)).")
        }
        return String(decoding: data, as: UTF8.self)
    }
}
