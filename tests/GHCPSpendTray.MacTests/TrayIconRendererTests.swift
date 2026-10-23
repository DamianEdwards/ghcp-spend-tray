import AppKit

@MainActor
enum TrayIconRendererTests {
    static func run() throws {
        func check(_ condition: Bool, _ message: String) throws {
            if !condition { throw AppError.message(message) }
        }
        func indicator(_ percent: Double?, partial: Bool = false, selected: Int = 1,
                       unlimited: Bool = false) -> TrayIndicator {
            TrayIndicator(accountKey: nil, name: "Synthetic", percent: percent,
                includedAccounts: percent == nil && !unlimited ? 0 : 1, selectedAccounts: selected,
                details: percent == nil ? "Synthetic usage unavailable." : "Synthetic allocation usage.",
                tooltip: "Synthetic tooltip", isPartial: partial, isOverAllocation: (percent ?? 0) > 100,
                valueText: unlimited ? "Unlimited allocation" : percent.map { "\($0)%" } ?? "Unavailable",
                numericText: unlimited ? "\u{221e}" : percent.map { String(Int($0)) } ?? "?",
                isUnlimited: unlimited)
        }
        let unavailable = TrayIconRenderer.image(.unavailable, style: .pie)
        let zero = TrayIconRenderer.image(indicator(0), style: .pie)
        let partialZero = TrayIconRenderer.image(indicator(0, partial: true), style: .pie)
        let question = TrayIconRenderer.image(.unavailable, style: .percentage)
        let full = TrayIconRenderer.image(indicator(100), style: .pie)
        let over = indicator(105, partial: true, selected: 2)
        let overPie = TrayIconRenderer.image(over, style: .pie)
        let overNumber = TrayIconRenderer.image(over, style: .percentage)
        let selectedUnavailable = TrayIconRenderer.image(indicator(nil, partial: true, selected: 2), style: .pie)
        let infinity = TrayIconRenderer.image(indicator(nil, unlimited: true), style: .pie)
        let infinityNumber = TrayIconRenderer.image(indicator(nil, unlimited: true), style: .percentage)
        let partialInfinity = TrayIconRenderer.image(indicator(nil, partial: true, selected: 2, unlimited: true), style: .pie)

        try check(unavailable.size == NSSize(width: 22, height: 22) &&
                  question.size == NSSize(width: 28, height: 22), "Native icon sizes are preserved.")
        try check(unavailable.accessibilityDescription == TrayIndicator.unavailable.details &&
                  selectedUnavailable.accessibilityDescription == "Synthetic usage unavailable.",
                  "Unavailable usage keeps its description instead of claiming zero consumption.")
        for scale in [1, 2] {
            let missing = try pixels(unavailable, scale: scale)
            let empty = try pixels(zero, scale: scale)
            let warning = try pixels(partialZero, scale: scale)
            let numeric = try pixels(question, scale: scale)
            try check(pixels(infinity, scale: scale) != missing &&
                      pixels(infinityNumber, scale: scale) != numeric &&
                      pixels(infinity, scale: scale) != empty,
                      "Unlimited uses infinity, never unavailable or zero, in both styles at \(scale)x.")
            try check(pixels(infinity, scale: scale) != pixels(partialInfinity, scale: scale),
                      "Partial unlimited roll-ups retain the warning badge at \(scale)x.")
            try check(missing == warning, "Unavailable pie is an empty outline with the circular ! warning badge, at \(scale)x.")
            try check(missing != empty, "Unavailable pie must be distinguishable from genuine 0% usage, at \(scale)x.")
            try check(missing == pixels(selectedUnavailable, scale: scale),
                      "Startup, empty selection and selected-but-unavailable accounts use the same warning pie, at \(scale)x.")
            try check(numeric != pixels(TrayIconRenderer.image(indicator(0), style: .percentage), scale: scale),
                      "Percentage mode retains ? rather than substituting zero, at \(scale)x.")
            try check(pixels(overPie, scale: scale) != pixels(overNumber, scale: scale),
                      "Pie and percentage styles remain distinct, at \(scale)x.")
            let filled = try pixels(full, scale: scale)
            let center = 11 * scale * 22 * scale + 11 * scale
            try check(missing[center] == 0 && empty[center] == 0 && filled[center] > 0,
                      "Unavailable and 0% pies are unfilled; 100% pies are filled, at \(scale)x.")
            let badgeStart = 11 * scale
            let width = 22 * scale
            for y in 0..<(22 * scale) {
                for x in 0..<badgeStart {
                    try check(missing[y * width + x] == empty[y * width + x],
                              "Unavailable pie changes only the warning corner, at \(scale)x.")
                }
            }
            func alpha(_ x: Int, _ y: Int) -> UInt8 {
                missing[(22 * scale - 1 - y * scale) * width + x * scale]
            }
            try check(alpha(17, 9) > 0 && alpha(13, 5) > 0 && alpha(21, 5) > 0,
                      "Warning badge has a circular border, at \(scale)x.")
            try check(alpha(17, 6) > 0 && alpha(17, 2) > 0 && alpha(15, 5) == 0,
                      "Compact bold exclamation has a stem, dot and transparent interior, at \(scale)x.")
            for image in [unavailable, zero, question, full, overPie, overNumber, selectedUnavailable,
                          infinity, infinityNumber, partialInfinity] {
                let alpha = try pixels(image, scale: scale)
                try check(image.isTemplate, "Menu-bar graphics adapt to light/dark native appearance.")
                try check(alpha.contains { $0 > 0 } && alpha.contains(0),
                          "Tray graphics contain a visible glyph and transparent background, at \(scale)x.")
                try check(alpha.contains { $0 > 0 && $0 < 255 }, "Tray graphics remain antialiased, at \(scale)x.")
                try check(alpha[0] == 0, "The icon does not add an opaque background, at \(scale)x.")
            }
        }
        print("PASS: macOS pie/percentage graphics, unavailable warning versus zero usage, badges and 1x/2x template rendering.")
    }

    private static func pixels(_ image: NSImage, scale: Int) throws -> [UInt8] {
        let width = Int(image.size.width) * scale
        let height = Int(image.size.height) * scale
        guard let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height,
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0),
              let context = NSGraphicsContext(bitmapImageRep: bitmap) else {
            throw AppError.message("Could not allocate synthetic tray pixels.")
        }
        NSGraphicsContext.saveGraphicsState()
        defer { NSGraphicsContext.restoreGraphicsState() }
        NSGraphicsContext.current = context
        context.cgContext.scaleBy(x: CGFloat(scale), y: CGFloat(scale))
        image.draw(in: NSRect(origin: .zero, size: image.size), from: .zero, operation: .copy, fraction: 1)
        var result: [UInt8] = []
        for y in 0..<height {
            for x in 0..<width {
                guard let color = bitmap.colorAt(x: x, y: y) else {
                    throw AppError.message("Could not read synthetic tray pixels.")
                }
                result.append(UInt8((color.alphaComponent * 255).rounded()))
            }
        }
        return result
    }
}
