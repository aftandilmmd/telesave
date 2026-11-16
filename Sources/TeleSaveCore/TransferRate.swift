import Foundation

/// Smoothed upload speed and ETA computed from periodic snapshots of the queue.
public struct TransferRate: Sendable {
    public private(set) var bytesPerSecond: Double = 0
    private var lastBytes: Double?
    private var lastTime: Date?
    private let smoothing: Double

    public init(smoothing: Double = 0.3) {
        self.smoothing = smoothing
    }

    /// `uploadedBytes` is the total uploaded across in-flight items at `time`.
    public mutating func sample(uploadedBytes: Double, at time: Date) {
        guard let previousBytes = lastBytes, let previousTime = lastTime else {
            lastBytes = uploadedBytes
            lastTime = time
            return
        }
        let dt = time.timeIntervalSince(previousTime)
        // Keep the baseline until enough time has passed for a meaningful sample.
        guard dt >= 0.2 else { return }
        lastBytes = uploadedBytes
        lastTime = time
        let delta = uploadedBytes - previousBytes
        // A job finishing drops its bytes from the in-flight sum; ignore that negative step.
        guard delta >= 0 else { return }
        let instant = delta / dt
        bytesPerSecond = bytesPerSecond == 0 ? instant : bytesPerSecond * (1 - smoothing) + instant * smoothing
    }

    public mutating func reset() {
        bytesPerSecond = 0
        lastBytes = nil
        lastTime = nil
    }

    public func eta(remainingBytes: Double) -> TimeInterval? {
        guard bytesPerSecond > 1 else { return nil }
        return remainingBytes / bytesPerSecond
    }
}
