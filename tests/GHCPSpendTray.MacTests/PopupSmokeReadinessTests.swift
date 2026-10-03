import CoreGraphics
import Foundation

enum PopupSmokeReadinessTests {
    static func run() throws {
        func check(_ condition: Bool, _ message: String) throws {
            if !condition { throw AppError.message(message) }
        }
        func fixture(height: CGFloat = 225, gap: CGFloat = 5.5, x: CGFloat = 900) -> PopupSmokeGeometry {
            PopupSmokeGeometry(anchor: CGRect(x: 1100, y: 900, width: 24, height: 24),
                popup: CGRect(x: x, y: 900 - gap - height - 18, width: 400, height: height + 18),
                screen: CGRect(x: 0, y: 0, width: 1440, height: 924),
                visibleScreen: CGRect(x: 0, y: 30, width: 1440, height: 870),
                contentBounds: CGRect(x: 0, y: 0, width: 400, height: height),
                contentSize: CGSize(width: 400, height: height),
                preferredContentSize: CGSize(width: 400, height: height))
        }
        func observe(_ geometry: PopupSmokeGeometry, _ readiness: inout PopupSmokeReadiness,
                     at elapsed: Duration, failure: String? = nil) -> PopupSmokeReadiness.Result {
            readiness.observe(frames: geometry.stabilityFrames, failure: failure ?? geometry.failure, elapsed: elapsed)
        }

        for height: CGFloat in [225, 600, 270, 645, 450, 710] {
            let geometry = fixture(height: height)
            try check(geometry.failure == nil, "Empty/populated, notice and example geometry must remain attached.")
            var readiness = PopupSmokeReadiness()
            try check(observe(geometry, &readiness, at: .zero) == .waiting, "Initial presentation needs stability.")
            try check(observe(geometry, &readiness, at: .milliseconds(250)) == .waiting,
                      "One fixed 250 ms delay is not a stability proof.")
            try check(observe(geometry, &readiness, at: .milliseconds(300)) == .ready,
                      "Stable, correctly sized empty/populated content becomes ready.")
        }
        let empty = fixture()
        let populated = fixture(height: 600)
        let detached = fixture(gap: 536)
        try check(detached.failure?.contains("536.0-point") == true, "Original CI gap remains a strict failure.")
        var transient = PopupSmokeReadiness()
        try check(observe(detached, &transient, at: .zero) == .waiting, "Transient bad placement can settle.")
        try check(observe(detached, &transient, at: .milliseconds(250)) == .waiting, "Slow placement is not ready.")
        try check(observe(populated, &transient, at: .seconds(1)) == .waiting, "Correct placement starts stability.")
        try check(observe(populated, &transient, at: .milliseconds(1300)) == .ready,
                  "Late correct placement is accepted without reopening/reanchoring the popup.")
        var permanent = PopupSmokeReadiness()
        for milliseconds in stride(from: 0, to: 5000, by: 100) {
            try check(observe(detached, &permanent, at: .milliseconds(milliseconds)) == .waiting,
                      "Stable detached geometry never counts as ready.")
        }
        try check(observe(detached, &permanent, at: .seconds(5)) == .timedOut &&
                  permanent.reason.contains("detached"), "Permanent detachment fails at the bounded deadline.")

        var reset = PopupSmokeReadiness()
        _ = observe(empty, &reset, at: .zero)
        _ = observe(empty, &reset, at: .milliseconds(250), failure: "Popup is not visible.")
        try check(observe(empty, &reset, at: .milliseconds(300)) == .waiting, "Visibility loss resets stability.")
        try check(observe(empty, &reset, at: .milliseconds(600)) == .ready, "Visibility recovery must settle again.")
        var missing = PopupSmokeReadiness()
        try check(missing.observe(frames: [], failure: "Anchor window missing.", elapsed: .zero) == .waiting,
                  "Missing launch windows wait rather than throwing at an arbitrary delay.")
        try check(missing.observe(frames: [], failure: "Anchor window missing.", elapsed: .seconds(5)) == .timedOut,
                  "Missing windows do not wait indefinitely.")
        var changed = PopupSmokeReadiness()
        _ = observe(empty, &changed, at: .zero)
        try check(observe(fixture(height: 270), &changed, at: .milliseconds(250)) == .waiting,
                  "Live growth restarts the stability interval.")
        try check(observe(fixture(height: 270), &changed, at: .milliseconds(550)) == .ready, "Live growth settles.")
        try check(observe(empty, &changed, at: .milliseconds(600)) == .waiting, "Live shrinkage restarts stability.")
        try check(observe(empty, &changed, at: .milliseconds(900)) == .ready, "Live shrinkage settles.")
        var expected = PopupSmokeReadiness()
        _ = observe(empty, &expected, at: .zero, failure: "Expected example content has not appeared.")
        try check(observe(empty, &expected, at: .seconds(5),
                          failure: "Expected example content has not appeared.") == .timedOut,
                  "Unchanged valid geometry cannot hide a missing content update.")
        var oscillating = PopupSmokeReadiness()
        for milliseconds in stride(from: 0, through: 5000, by: 100) {
            let geometry = fixture(gap: milliseconds % 200 == 0 ? 5.5 : 7)
            let result = observe(geometry, &oscillating, at: .milliseconds(milliseconds))
            try check(result == (milliseconds == 5000 ? .timedOut : .waiting),
                      "Endless geometry changes cannot extend the deadline.")
        }
        var late = PopupSmokeReadiness()
        _ = observe(empty, &late, at: .milliseconds(4800))
        try check(observe(empty, &late, at: .milliseconds(5100)) == .timedOut,
                  "A stability interval completed after the deadline cannot pass.")
        var drift = PopupSmokeReadiness()
        _ = observe(empty, &drift, at: .zero)
        try check(observe(fixture(gap: 5.9), &drift, at: .milliseconds(100)) == .waiting,
                  "Subpixel noise can stay within the stability candidate.")
        try check(observe(fixture(gap: 6.3), &drift, at: .milliseconds(200)) == .waiting,
                  "Accumulated movement is compared with the candidate, not just the previous sample.")
        try check(observe(fixture(gap: 6.7), &drift, at: .milliseconds(300)) == .waiting,
                  "Slow drift cannot falsely complete the original stability interval.")
        try check(observe(fixture(gap: 6.3), &drift, at: .milliseconds(500)) == .ready,
                  "Subpixel noise around a new stable candidate is accepted.")

        for gap: CGFloat in [-8, 8] {
            try check(fixture(gap: gap).failure == nil, "Original eight-point attachment limit is inclusive.")
        }
        for gap: CGFloat in [-8.01, 8.01, 95.5, 536] {
            try check(fixture(gap: gap).failure != nil, "No relaxation of strict vertical attachment.")
        }
        try check(fixture(x: 100).failure?.contains("horizontally") == true,
                  "Matching vertical edges cannot hide a horizontally detached popup.")
        try check(fixture(height: 910).failure?.contains("cannot accommodate") == true,
                  "Constrained screens fail explicitly rather than accepting an unrelated popup edge.")
        try check(fixture(height: .nan).failure != nil, "Nonfinite geometry cannot pass.")
        let mismatchedSize = PopupSmokeGeometry(anchor: empty.anchor, popup: empty.popup, screen: empty.screen,
            visibleScreen: empty.visibleScreen, contentBounds: empty.contentBounds,
            contentSize: CGSize(width: 400, height: 320), preferredContentSize: empty.preferredContentSize)
        try check(mismatchedSize.failure?.contains("not converged") == true,
                  "The original default-height mismatch must not count as settled content.")
        let mismatchedPreferredSize = PopupSmokeGeometry(anchor: empty.anchor, popup: empty.popup, screen: empty.screen,
            visibleScreen: empty.visibleScreen, contentBounds: empty.contentBounds, contentSize: empty.contentSize,
            preferredContentSize: CGSize(width: 400, height: 270))
        try check(mismatchedPreferredSize.failure?.contains("not converged") == true,
                  "A pending preferred-size update must not count as settled content.")
        let offsetScreen = CGRect(x: -1440, y: -924, width: 1440, height: 924)
        let offset = PopupSmokeGeometry(anchor: empty.anchor.offsetBy(dx: -1440, dy: -924),
            popup: empty.popup.offsetBy(dx: -1440, dy: -924), screen: offsetScreen,
            visibleScreen: empty.visibleScreen.offsetBy(dx: -1440, dy: -924),
            contentBounds: empty.contentBounds, contentSize: empty.contentSize, preferredContentSize: empty.preferredContentSize)
        try check(offset.failure == nil, "Secondary-screen negative origins use screen coordinates, not fixed offsets.")
        let offscreen = PopupSmokeGeometry(anchor: empty.anchor, popup: empty.popup.offsetBy(dx: 400, dy: 0),
            screen: empty.screen, visibleScreen: empty.visibleScreen, contentBounds: empty.contentBounds,
            contentSize: empty.contentSize, preferredContentSize: empty.preferredContentSize)
        try check(offscreen.failure != nil, "Off-screen popup geometry cannot pass.")
        try check(PopupSmokeGeometry.anchorFailure(.zero, screen: empty.screen) != nil,
                  "A zero-size status button is not ready for show.")
        try check(PopupSmokeGeometry.anchorFailure(empty.anchor.offsetBy(dx: 1440, dy: 0), screen: empty.screen) != nil,
                  "A menu-bar item not yet placed on its screen is not ready for show.")
        print("PASS: popup geometry, bounded readiness, transient/permanent detachment and live content stability.")
    }
}
