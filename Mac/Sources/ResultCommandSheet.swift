import AppKit

@MainActor final class ResultCommandSheet: NSObject {
    private let panel: KeyboardWindow
    private let first: NSControl

    init(title: String, action: String, fields: [(name: String, input: NSControl, hint: String)],
         manual: @escaping () -> Void) {
        first = fields[0].input
        panel = KeyboardWindow(contentRect: NSRect(x: 0, y: 0, width: 460, height: fields.count == 1 ? 100 : 142),
                               styleMask: [.titled], backing: .buffered, defer: false)
        super.init()
        panel.title = title
        panel.isReleasedWhenClosed = false
        let confirm = NSButton(title: action, target: self, action: #selector(apply))
        let cancel = NSButton(title: "Cancel", target: self, action: #selector(dismiss))
        confirm.keyEquivalent = "\r"
        cancel.keyEquivalent = "\u{1b}"
        let rows = fields.map { field in
            label(field.name, for: field.input, hint: field.hint)
            let caption = NSTextField(labelWithString: field.name + ":")
            caption.setAccessibilityServesAsTitleForUIElements([field.input])
            let row = NSStackView(views: [caption, field.input])
            row.alignment = .centerY
            row.spacing = 10
            caption.widthAnchor.constraint(equalToConstant: 90).isActive = true
            return row
        }
        let buttons = NSStackView(views: [confirm, cancel])
        let content = NSStackView(views: rows + [buttons])
        content.orientation = .vertical
        content.alignment = .trailing
        content.spacing = 10
        content.edgeInsets = NSEdgeInsets(top: 12, left: 14, bottom: 12, right: 14)
        panel.contentView = content
        for row in rows { row.widthAnchor.constraint(equalTo: content.widthAnchor, constant: -28).isActive = true }
        let order: [NSView] = fields.map(\.input) + [confirm, cancel]
        for (index, control) in order.enumerated() { control.nextKeyView = order[(index + 1) % order.count] }
        panel.keyOrder = { order }
        panel.cancel = { [weak self] in self?.dismiss(); return true }
        panel.help = { [weak panel, weak first] in
            guard let panel else { return }
            let focus = panel.focusedView
            let hint = focus?.accessibilityHelp() ?? first?.accessibilityHelp() ?? ""
            HelpPanel(title: focus?.accessibilityLabel() ?? title, text: hint, manual: manual).present(on: panel)
        }
    }

    func present(on parent: NSWindow, accepted: @escaping () -> Void) {
        parent.beginSheet(panel) { [self] response in
            panel.orderOut(nil)
            if response == .OK { accepted() }
        }
        panel.makeFirstResponder(first)
        (first as? NSTextField)?.selectText(nil)
    }

    @objc private func apply() { panel.sheetParent?.endSheet(panel, returnCode: .OK) }
    @objc private func dismiss() { panel.sheetParent?.endSheet(panel, returnCode: .cancel) }
}
