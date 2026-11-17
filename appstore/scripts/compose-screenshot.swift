import AppKit

// Mac App Store screenshot (2880×1800): headline and subline on the left, the app panel on the right.
//   swift compose-screenshot.swift <panel.png> <headline> <subline> <out.png>
let args = CommandLine.arguments
let panelImage = NSImage(contentsOfFile: args[1])!
let headline = args[2], subline = args[3]
let out = URL(fileURLWithPath: args[4])

let W = 2880, H = 1800
// Opaque RGB canvas (App Store screenshots must not have an alpha channel).
let cg = CGContext(data: nil, width: W, height: H, bitsPerComponent: 8, bytesPerRow: 0,
                   space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)!
NSGraphicsContext.saveGraphicsState()
let ctx = NSGraphicsContext(cgContext: cg, flipped: false)
NSGraphicsContext.current = ctx
ctx.imageInterpolation = .high
let canvas = NSRect(x: 0, y: 0, width: W, height: H)
NSGradient(colors: [NSColor(calibratedRed: 0.13, green: 0.62, blue: 0.93, alpha: 1),
                    NSColor(calibratedRed: 0.36, green: 0.36, blue: 0.90, alpha: 1),
                    NSColor(calibratedRed: 0.55, green: 0.30, blue: 0.85, alpha: 1)])!.draw(in: canvas, angle: -35)
for (center, radius, alpha) in [(NSPoint(x: 500, y: 1600), 1100.0, 0.25), (NSPoint(x: 2600, y: 200), 1000.0, 0.2)] {
    NSGradient(colors: [NSColor(white: 1, alpha: alpha), NSColor(white: 1, alpha: 0)])!
        .draw(fromCenter: center, radius: 0, toCenter: center, radius: radius, options: [])
}

// Panel: 1.5× the 2× render (720×960 → 1080×1440), rounded, with shadow.
let rect = NSRect(x: 1600, y: 180, width: 1080, height: 1440)
let path = NSBezierPath(roundedRect: rect, xRadius: 40, yRadius: 40)
NSGraphicsContext.saveGraphicsState()
let shadow = NSShadow()
shadow.shadowColor = NSColor(calibratedRed: 0.05, green: 0.05, blue: 0.2, alpha: 0.45)
shadow.shadowBlurRadius = 90
shadow.shadowOffset = NSSize(width: 0, height: -40)
shadow.set()
NSColor.white.setFill()
path.fill()
NSGraphicsContext.restoreGraphicsState()
NSGraphicsContext.saveGraphicsState()
path.addClip()
panelImage.draw(in: rect, from: .zero, operation: .sourceOver, fraction: 1)
NSGraphicsContext.restoreGraphicsState()

// Text block, vertically centred on the left.
let style = NSMutableParagraphStyle()
style.lineSpacing = 6
let title = NSAttributedString(string: headline, attributes: [
    .font: NSFont.systemFont(ofSize: 124, weight: .bold), .foregroundColor: NSColor.white, .paragraphStyle: style])
let sub = NSAttributedString(string: subline, attributes: [
    .font: NSFont.systemFont(ofSize: 60, weight: .medium), .foregroundColor: NSColor(white: 1, alpha: 0.88), .paragraphStyle: style])
let textWidth: CGFloat = 1280
let titleBox = title.boundingRect(with: NSSize(width: textWidth, height: 1000), options: [.usesLineFragmentOrigin])
let subBox = sub.boundingRect(with: NSSize(width: textWidth, height: 600), options: [.usesLineFragmentOrigin])
let total = titleBox.height + 50 + subBox.height
let top = CGFloat(H) / 2 + total / 2
title.draw(with: NSRect(x: 200, y: top - titleBox.height, width: textWidth, height: titleBox.height), options: [.usesLineFragmentOrigin])
sub.draw(with: NSRect(x: 200, y: top - titleBox.height - 50 - subBox.height, width: textWidth, height: subBox.height), options: [.usesLineFragmentOrigin])

NSGraphicsContext.restoreGraphicsState()
let rep = NSBitmapImageRep(cgImage: cg.makeImage()!)
try! rep.representation(using: .png, properties: [:])!.write(to: out)
