import AppKit

@MainActor
enum TrayIconRenderer {
    static func image(_ indicator: TrayIndicator, style: TrayIconStyle) -> NSImage {
        let size = NSSize(width: style == .percentage ? 28 : 22, height: 22)
        let image = NSImage(size: size, flipped: false) { bounds in
            let unavailablePie = style == .pie && indicator.percent == nil
            let badges = indicator.isPartial || indicator.isOverAllocation
            NSColor.black.set()
            if style == .percentage {
                let text = indicator.numericText as NSString
                let font = NSFont.monospacedDigitSystemFont(ofSize: badges ? 12 : 15, weight: .semibold)
                let attributes: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: NSColor.black]
                let measured = text.size(withAttributes: attributes)
                text.draw(at: NSPoint(x: (bounds.width - measured.width) / 2,
                                     y: badges ? 8 : (bounds.height - measured.height) / 2),
                          withAttributes: attributes)
            } else {
                let circle = NSRect(x: 3, y: 3, width: 16, height: 16)
                let outline = NSBezierPath(ovalIn: circle)
                outline.lineWidth = 1.5
                outline.stroke()
                if let percent = indicator.percent {
                    let fraction = min(max(percent / 100, 0), 1)
                    if fraction >= 1 { outline.fill() }
                    else if fraction > 0 {
                        let slice = NSBezierPath()
                        slice.move(to: NSPoint(x: 11, y: 11))
                        slice.line(to: NSPoint(x: 11, y: 19))
                        slice.appendArc(withCenter: NSPoint(x: 11, y: 11), radius: 8,
                                        startAngle: 90, endAngle: 90 - fraction * 360, clockwise: true)
                        slice.close()
                        slice.fill()
                    }
                }
            }
            if indicator.percent != nil {
                if indicator.isOverAllocation { badge("+", at: 0, width: bounds.width) }
            }
            if unavailablePie || (indicator.percent != nil && indicator.isPartial) {
                badge("!", at: bounds.width - 8, width: bounds.width)
            }
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = indicator.details
        return image
    }

    private static func badge(_ text: String, at x: CGFloat, width: CGFloat) {
        let rect = NSRect(x: x, y: 0, width: min(8, width - x), height: 10)
        rect.fill(using: .clear)
        (text as NSString).draw(in: rect, withAttributes: [
            .font: NSFont.boldSystemFont(ofSize: 10), .foregroundColor: NSColor.black
        ])
    }
}
