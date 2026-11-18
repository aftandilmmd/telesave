import AppKit
import CryptoKit
import TeleSaveCore
#if canImport(Sparkle)
import Sparkle
#endif

/// "Update" button: Sparkle downloads, installs and relaunches in place. When Sparkle cannot (not configured in this
/// build, no appcast for the release, app run from the disk image, any Sparkle error), the DMG is downloaded to
/// ~/Downloads and opened so the user can drag the new version over the old one.
@MainActor
@Observable
final class AppUpdater {
    enum Phase: Equatable {
        case idle
        case checking
        /// 0…1, nil while the size is unknown.
        case downloading(Double?)
        case installing
    }

    private(set) var phase: Phase = .idle
    var onNotice: ((String) -> Void)?

    #if canImport(Sparkle)
    private var updater: SPUUpdater?
    private var driver: SparkleDriver?
    /// Sparkle owns `phase` while true; the fallback owns it afterwards.
    private var sparkleActive = false
    #endif
    /// The release Sparkle is working on; the fallback downloads its DMG if Sparkle gives up.
    private var release: ReleaseInfo?

    func install(_ release: ReleaseInfo) {
        guard phase == .idle else { return }
        self.release = release
        #if canImport(Sparkle)
        if let updater = sparkleUpdater() {
            sparkleActive = true
            phase = .checking
            updater.checkForUpdates()
            return
        }
        #endif
        fallBack()
    }

    // MARK: - Fallback: DMG into ~/Downloads

    private func fallBack() {
        guard let release else { return }
        guard let dmg = release.dmg, let url = dmg.url else {
            phase = .idle
            NSWorkspace.shared.open(release.pageURL)
            return
        }
        phase = .downloading(nil)
        Task {
            defer { phase = .idle }
            do {
                let file = try await download(url, expectedSHA256: dmg.sha256)
                onNotice?(L("Güncelleme otomatik kurulamadı. Yeni sürüm İndirilenler klasörüne indirildi.") + "\n"
                          + L("TeleSave'i Uygulamalar klasörüne sürükleyip eskisinin yerine koyun, sonra yeniden açın."))
                NSWorkspace.shared.open(file)
            } catch {
                onNotice?(L("Güncelleme indirilemedi: %@", error.localizedDescription))
            }
        }
    }

    private func download(_ url: URL, expectedSHA256: String?) async throws -> URL {
        let delegate = DownloadProgress { [weak self] fraction in
            Task { @MainActor in
                if case .downloading = self?.phase { self?.phase = .downloading(fraction) }
            }
        }
        let (temporary, response) = try await URLSession.shared.download(from: url, delegate: delegate)
        if let status = (response as? HTTPURLResponse)?.statusCode, status != 200 {
            throw URLError(.badServerResponse)
        }
        if let expectedSHA256 {
            let actual = try await Task.detached { try Self.sha256(of: temporary) }.value
            guard actual == expectedSHA256 else {
                try? FileManager.default.removeItem(at: temporary)
                throw URLError(.cannotDecodeContentData)
            }
        }
        let downloads = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask)[0]
        let target = MessageFileLocator.uniqueURL(in: downloads, fileName: url.lastPathComponent)
        try FileManager.default.moveItem(at: temporary, to: target)
        return target
    }

    nonisolated private static func sha256(of file: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: file)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 1 << 20), !chunk.isEmpty { hasher.update(data: chunk) }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    // MARK: - Sparkle

    #if canImport(Sparkle)
    /// nil when Sparkle cannot install in place, so the fallback is used right away.
    private func sparkleUpdater() -> SPUUpdater? {
        if let updater { return updater }
        let info = Bundle.main.infoDictionary ?? [:]
        guard (info["SUFeedURL"] as? String)?.isEmpty == false,
              (info["SUPublicEDKey"] as? String)?.isEmpty == false else { return nil }
        // Opened straight from the disk image or quarantined (App Translocation): nothing to replace in place.
        let bundle = Bundle.main.bundleURL
        if bundle.path.contains("/AppTranslocation/")
            || (try? bundle.resourceValues(forKeys: [.volumeIsReadOnlyKey]).volumeIsReadOnly) == true {
            return nil
        }
        let driver = SparkleDriver(owner: self)
        let updater = SPUUpdater(hostBundle: .main, applicationBundle: .main, userDriver: driver, delegate: nil)
        do {
            try updater.start()
        } catch {
            NSLog("TeleSave: Sparkle could not start: \(error)")
            return nil
        }
        self.driver = driver
        self.updater = updater
        return updater
    }

    fileprivate func sparkleProgress(_ phase: Phase) {
        if sparkleActive { self.phase = phase }
    }

    /// Sparkle found nothing newer (no appcast in the release yet) or failed: hand over to the DMG download.
    fileprivate func sparkleGaveUp(_ error: Error) {
        NSLog("TeleSave: Sparkle update failed, falling back to the DMG: \(error)")
        guard sparkleActive else { return }
        sparkleActive = false
        fallBack()
    }

    fileprivate func sparkleDismissed() {
        guard sparkleActive else { return }
        sparkleActive = false
        phase = .idle
    }
    #endif
}

private final class DownloadProgress: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    private let onProgress: @Sendable (Double) -> Void
    private var observation: NSKeyValueObservation?

    init(onProgress: @escaping @Sendable (Double) -> Void) {
        self.onProgress = onProgress
    }

    func urlSession(_ session: URLSession, didCreateTask task: URLSessionTask) {
        let onProgress = onProgress
        observation = task.progress.observe(\.fractionCompleted) { progress, _ in onProgress(progress.fractionCompleted) }
    }
}

#if canImport(Sparkle)
/// Sparkle without its own windows: every step is answered automatically and shown in TeleSave's update banner.
/// Sparkle calls these on the main thread.
private final class SparkleDriver: NSObject, SPUUserDriver {
    private weak var owner: AppUpdater?
    private var expectedLength: UInt64 = 0
    private var receivedLength: UInt64 = 0

    init(owner: AppUpdater) {
        self.owner = owner
    }

    private func onMain(_ body: @MainActor (AppUpdater) -> Void) {
        MainActor.assumeIsolated {
            if let owner { body(owner) }
        }
    }

    func show(_ request: SPUUpdatePermissionRequest, reply: @escaping (SUUpdatePermissionResponse) -> Void) {
        // TeleSave checks GitHub itself; Sparkle only runs when the user clicks "Update".
        reply(SUUpdatePermissionResponse(automaticUpdateChecks: false, sendSystemProfile: false))
    }

    func showUserInitiatedUpdateCheck(cancellation: @escaping () -> Void) {
        onMain { $0.sparkleProgress(.checking) }
    }

    func showUpdateFound(with appcastItem: SUAppcastItem, state: SPUUserUpdateState, reply: @escaping (SPUUserUpdateChoice) -> Void) {
        reply(.install)
    }

    func showUpdateReleaseNotes(with downloadData: SPUDownloadData) {}

    func showUpdateReleaseNotesFailedToDownloadWithError(_ error: Error) {}

    func showUpdateNotFoundWithError(_ error: Error, acknowledgement: @escaping () -> Void) {
        // Before acknowledging: Sparkle dismisses the session from inside the acknowledgement.
        onMain { $0.sparkleGaveUp(error) }
        acknowledgement()
    }

    func showUpdaterError(_ error: Error, acknowledgement: @escaping () -> Void) {
        // Before acknowledging: Sparkle dismisses the session from inside the acknowledgement.
        onMain { $0.sparkleGaveUp(error) }
        acknowledgement()
    }

    func showDownloadInitiated(cancellation: @escaping () -> Void) {
        expectedLength = 0
        receivedLength = 0
        onMain { $0.sparkleProgress(.downloading(nil)) }
    }

    func showDownloadDidReceiveExpectedContentLength(_ expectedContentLength: UInt64) {
        expectedLength = expectedContentLength
    }

    func showDownloadDidReceiveData(ofLength length: UInt64) {
        receivedLength += length
        guard expectedLength > 0 else { return }
        let fraction = min(1, Double(receivedLength) / Double(expectedLength))
        onMain { $0.sparkleProgress(.downloading(fraction)) }
    }

    func showDownloadDidStartExtractingUpdate() {
        onMain { $0.sparkleProgress(.installing) }
    }

    func showExtractionReceivedProgress(_ progress: Double) {}

    func showReady(toInstallAndRelaunch reply: @escaping (SPUUserUpdateChoice) -> Void) {
        onMain { $0.sparkleProgress(.installing) }
        reply(.install)
    }

    func showInstallingUpdate(withApplicationTerminated applicationTerminated: Bool, retryTerminatingApplication: @escaping () -> Void) {
        onMain { $0.sparkleProgress(.installing) }
    }

    func showUpdateInstalledAndRelaunched(_ relaunched: Bool, acknowledgement: @escaping () -> Void) {
        acknowledgement()
    }

    func dismissUpdateInstallation() {
        onMain { $0.sparkleDismissed() }
    }
}
#endif
