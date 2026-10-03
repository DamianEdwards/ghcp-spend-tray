import CoreGraphics
import Foundation

struct PopupSmokeGeometry {
    static let anchorTolerance: CGFloat = 8
    let anchor: CGRect
    let popup: CGRect
    let screen: CGRect
    let visibleScreen: CGRect
    let contentBounds: CGRect
    let windowContentBounds: CGRect
    let preferredContentSize: CGSize

    static func anchorFailure(_ anchor: CGRect, screen: CGRect) -> String? {
        guard usable(anchor), usable(screen) else { return "Anchor or screen geometry is empty or nonfinite." }
        guard screen.insetBy(dx: -1, dy: -1).contains(anchor) else {
            return "Menu-bar anchor is outside its screen."
        }
        return nil
    }

    var failure: String? {
        if let failure = Self.anchorFailure(anchor, screen: screen) { return failure }
        guard Self.usable(popup), Self.usable(contentBounds), Self.usable(windowContentBounds), Self.usable(visibleScreen) else {
            return "Popup, content or visible-screen geometry is empty or nonfinite."
        }
        guard Self.sameSize(contentBounds.size, windowContentBounds.size),
              Self.sameSize(contentBounds.size, preferredContentSize) else {
            return "Popup hosting view, window content and hosting-controller preferred size have not converged."
        }
        // A menu-bar popup should fit below its button; never accept a different edge
        // or a screen-clamped, detached window as evidence of correct anchoring.
        guard popup.height <= anchor.minY - screen.minY + Self.anchorTolerance else {
            return "Screen cannot accommodate the popup below its menu-bar anchor."
        }
        guard abs(gap) <= Self.anchorTolerance else {
            return "Menu-bar popup is detached: \(gap)-point vertical gap (limit \(Self.anchorTolerance))."
        }
        guard anchor.midX >= popup.minX - Self.anchorTolerance,
              anchor.midX <= popup.maxX + Self.anchorTolerance else {
            return "Menu-bar popup is horizontally detached from its anchor."
        }
        guard screen.insetBy(dx: -Self.anchorTolerance, dy: -Self.anchorTolerance).contains(popup) else {
            return "Menu-bar popup extends outside its anchor screen."
        }
        return nil
    }

    var gap: CGFloat { anchor.minY - popup.maxY }

    var stabilityFrames: [CGRect] {
        [anchor, popup, screen, visibleScreen, contentBounds, windowContentBounds,
         CGRect(origin: .zero, size: preferredContentSize)]
    }

    private static func usable(_ rect: CGRect) -> Bool {
        rect.origin.x.isFinite && rect.origin.y.isFinite && rect.width.isFinite && rect.height.isFinite &&
            rect.width > 0 && rect.height > 0
    }

    private static func sameSize(_ lhs: CGSize, _ rhs: CGSize) -> Bool {
        rhs.width.isFinite && rhs.height.isFinite &&
            abs(lhs.width - rhs.width) <= 1 && abs(lhs.height - rhs.height) <= 1
    }
}

struct PopupSmokeReadiness {
    enum Result { case waiting, ready, timedOut }

    let timeout: Duration = .seconds(5)
    let stableFor: Duration = .milliseconds(300)
    private var candidate: [CGRect]?
    private var stableSince: Duration?
    private(set) var reason = "No geometry samples."

    mutating func observe(frames: [CGRect], failure: String?, elapsed: Duration) -> Result {
        if let failure {
            reason = failure
            candidate = nil
            stableSince = nil
        } else if frames.isEmpty {
            reason = "No geometry samples."
            candidate = nil
            stableSince = nil
        } else if let candidate, Self.sameFrames(candidate, frames), let stableSince {
            reason = "Geometry has not remained stable for \(stableFor)."
            if elapsed < timeout && elapsed - stableSince >= stableFor { return .ready }
        } else {
            reason = "Geometry is still changing."
            candidate = frames
            stableSince = elapsed
        }
        return elapsed >= timeout ? .timedOut : .waiting
    }

    private static func sameFrames(_ lhs: [CGRect], _ rhs: [CGRect]) -> Bool {
        lhs.count == rhs.count && zip(lhs, rhs).allSatisfy { a, b in
            abs(a.minX - b.minX) <= 0.5 && abs(a.minY - b.minY) <= 0.5 &&
                abs(a.width - b.width) <= 0.5 && abs(a.height - b.height) <= 0.5
        }
    }
}
