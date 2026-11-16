import SwiftUI
import TeleSaveCore

struct PopoverView: View {
    @Bindable var model: AppModel
    @State private var page: Page = .main

    enum Page {
        case main
        case settings
        case destination
    }

    var body: some View {
        Group {
            switch model.phase {
            case .needsCredentials:
                CredentialsView(model: model)
            case .running:
                if page == .settings {
                    SettingsView(model: model, onClose: { page = .main })
                } else if !model.canUpload {
                    LoginView(model: model, onSettings: { page = .settings })
                } else if page == .destination {
                    DestinationPicker(model: model, onClose: { page = .main })
                } else {
                    MainView(model: model, onSettings: { page = .settings }, onPickDestination: { page = .destination })
                }
            }
        }
        // Rebuild everything when the language changes; page state above is kept.
        .id(model.settings.language)
        .frame(width: 360, height: 480)
    }
}

// MARK: - Main

struct MainView: View {
    @Bindable var model: AppModel
    var onSettings: () -> Void
    var onPickDestination: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            RemoteBanner(model: model)
                .padding([.horizontal, .top], 12)
            DropZone(model: model)
                .padding(12)
            if let notice = model.notice {
                NoticeBanner(text: notice) { model.notice = nil }
                    .padding(.horizontal, 12)
                    .padding(.bottom, 8)
            }
            Divider()
            // One list: the whole chat (setting on) or only TeleSave's uploads (setting off).
            if model.settings.showChatFiles {
                ChatFilesView(model: model)
                if model.bytesPerSecond > 0 {
                    Divider()
                    footer
                }
            } else {
                QueueList(model: model)
                Divider()
                footer
            }
        }
    }

    private var header: some View {
        HStack(spacing: 10) {
            Avatar(name: model.me?.displayName ?? "?", image: model.avatar)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 5) {
                    ConnectionDot(state: model.connection, networkAvailable: model.networkAvailable)
                    Text(model.me?.displayName ?? "")
                        .font(.headline)
                        .lineLimit(1)
                }
                DestinationLine(model: model, onPick: onPickDestination)
            }
            Spacer()
            Button { model.openInTelegram() } label: {
                Image(systemName: "bubble.left.and.text.bubble.right")
            }
            .buttonStyle(.borderless)
            .help(L("Kayıtlı Mesajları Telegram'da aç"))
            Button(action: onSettings) {
                Image(systemName: "gearshape")
            }
            .buttonStyle(.borderless)
            .help(L("Ayarlar"))
        }
        .padding(12)
    }

    private var footer: some View {
        HStack {
            if model.bytesPerSecond > 0 {
                Text(Format.speed(model.bytesPerSecond) + (model.remainingSeconds.map { " · " + L("%@ kaldı", Format.duration($0)) } ?? ""))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
            }
            Spacer()
            if !model.settings.showChatFiles {
                Button(L("Tamamlananları temizle"), action: model.clearFinished)
                    .buttonStyle(.borderless)
                    .font(.caption)
                    .disabled(!model.items.contains { if case .done = $0.state { return true }; return $0.state == .cancelled })
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }
}

struct DropZone: View {
    @Bindable var model: AppModel
    @State private var targeted = false

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: targeted ? "tray.and.arrow.down.fill" : "tray.and.arrow.down")
                .font(.system(size: 20, weight: .light))
                .foregroundStyle(targeted ? Color.accentColor : .secondary)
            VStack(alignment: .leading, spacing: 1) {
                Text(L("Dosyaları buraya bırakın"))
                    .font(.callout.weight(.medium))
                Text(L("tıklayın ya da ⌘V"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity)
        .frame(height: 56)
        .background(
            RoundedRectangle(cornerRadius: 12)
                .fill(targeted ? Color.accentColor.opacity(0.12) : Color.secondary.opacity(0.06))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(targeted ? Color.accentColor : Color.secondary.opacity(0.35), style: StrokeStyle(lineWidth: 1.5, dash: [6, 4]))
        )
        .contentShape(Rectangle())
        .onTapGesture(perform: model.pickFiles)
        .dropDestination(for: URL.self) { urls, _ in
            model.handleDrop(urls.filter(\.isFileURL))
            return true
        } isTargeted: { targeted = $0 }
        .animation(.easeOut(duration: 0.15), value: targeted)
        .accessibilityAddTraits(.isButton)
        .accessibilityLabel(L("Dosya bırakma alanı"))
    }
}

struct QueueList: View {
    @Bindable var model: AppModel

    var body: some View {
        if model.items.isEmpty {
            VStack(spacing: 4) {
                Spacer()
                Text(L("Henüz yükleme yok"))
                    .foregroundStyle(.secondary)
                Spacer()
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(model.items.reversed()) { item in
                        QueueRow(item: item, model: model)
                        Divider().padding(.leading, 60)
                    }
                }
            }
        }
    }
}

struct QueueRow: View {
    let item: UploadItem
    @Bindable var model: AppModel
    @State private var confirmingDelete = false

    var body: some View {
        HStack(spacing: 10) {
            FileThumbnail(path: item.path)
                .overlay(alignment: .bottomTrailing) {
                    stateIcon
                        .font(.system(size: 13))
                        .offset(x: 4, y: 4)
                }
                .padding(.trailing, 2)
            VStack(alignment: .leading, spacing: 3) {
                Text(item.displayName)
                    .lineLimit(1)
                    .truncationMode(.middle)
                if case .uploading(let progress) = item.state {
                    ProgressView(value: progress)
                        .progressViewStyle(.linear)
                        .controlSize(.small)
                }
                Text(subtitle)
                    .font(.caption)
                    .foregroundStyle(isFailed ? Color.red : .secondary)
                    .lineLimit(2)
            }
            Spacer(minLength: 4)
            actions
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    private var destinationSuffix: String {
        item.destination.map { " → \($0.title)" } ?? ""
    }

    private var isFailed: Bool {
        if case .failed = item.state { return true }
        return false
    }

    private var subtitle: String {
        let size = Format.bytes(item.size)
        switch item.state {
        case .queued where item.destination != nil && item.notBefore == nil:
            return L("%@ · sırada", size) + destinationSuffix
        case .queued:
            if let next = item.notBefore, next > Date() { return L("%@ · yeniden denenecek", size) }
            return L("%@ · sırada", size)
        case .uploading(let p): return "\(size) · \(Format.percent(p))"
        case .done: return "\(size) · \(Format.uploadDate(item.finishedAt ?? item.createdAt))\(destinationSuffix)"
        case .failed(let reason): return "\(size) · \(reason)"
        case .cancelled: return L("%@ · iptal edildi", size)
        }
    }

    @ViewBuilder private var stateIcon: some View {
        switch item.state {
        case .queued: Image(systemName: "clock").foregroundStyle(.secondary)
        case .uploading: Image(systemName: "arrow.up.circle").foregroundStyle(Color.accentColor)
        case .done: Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
        case .failed: Image(systemName: "exclamationmark.circle.fill").foregroundStyle(.red)
        case .cancelled: Image(systemName: "xmark.circle").foregroundStyle(.secondary)
        }
    }

    @ViewBuilder private var actions: some View {
        switch item.state {
        case .queued, .uploading:
            IconButton(symbol: "xmark", help: L("İptal")) { model.cancel(item) }
        case .failed:
            IconButton(symbol: "arrow.clockwise", help: L("Tekrar dene")) { model.retry(item) }
            IconButton(symbol: "trash", help: L("Listeden kaldır")) { model.remove(item) }
        case .done:
            if model.busyItems.contains(item.id) {
                ProgressView().controlSize(.small).frame(width: 20, height: 20)
            } else if confirmingDelete {
                // Deleting is for everyone and cannot be undone: confirm inline.
                Button(L("Sil"), role: .destructive) {
                    confirmingDelete = false
                    model.deleteFromTelegram(item)
                }
                .controlSize(.small)
                .tint(.red)
                .help(L("Mesajı Telegram'dan herkes için sil"))
                IconButton(symbol: "xmark", help: L("Vazgeç")) { confirmingDelete = false }
            } else {
                IconButton(symbol: "arrow.down.circle", help: L("İndir (İndirilenler klasörüne)")) { model.download(item) }
                IconButton(symbol: "trash", help: L("Telegram'dan sil")) { confirmingDelete = true }
                IconButton(symbol: "arrow.up.forward.app", help: L("Telegram'da aç")) { model.openInTelegram(item.destination) }
            }
        case .cancelled:
            IconButton(symbol: "trash", help: L("Listeden kaldır")) { model.remove(item) }
        }
    }
}

// MARK: - Small components

struct IconButton: View {
    let symbol: String
    let help: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .frame(width: 20, height: 20)
        }
        .buttonStyle(.borderless)
        .help(help)
        .accessibilityLabel(help)
    }
}

struct Avatar: View {
    let name: String
    var image: NSImage?

    var body: some View {
        if let image {
            Image(nsImage: image)
                .resizable()
                .scaledToFill()
                .frame(width: 32, height: 32)
                .clipShape(Circle())
                .accessibilityLabel(name)
        } else {
            let initials = name.split(separator: " ").prefix(2).compactMap(\.first).map(String.init).joined()
            Circle()
                .fill(Color.accentColor.gradient)
                .frame(width: 32, height: 32)
                .overlay(Text(initials.isEmpty ? "?" : initials).font(.caption.bold()).foregroundStyle(.white))
                .accessibilityLabel(name)
        }
    }
}

/// Connection status as a dot before the account name; the text is in the tooltip.
struct ConnectionDot: View {
    let state: ConnectionState
    let networkAvailable: Bool

    var body: some View {
        let (text, color): (String, Color) = {
            if !networkAvailable { return (L("Ağ yok — yüklemeler duraklatıldı"), .orange) }
            switch state {
            case .ready: return (L("Telegram'a bağlı"), .green)
            case .updating: return (L("Güncelleniyor…"), .yellow)
            case .connecting, .connectingToProxy: return (L("Bağlanıyor…"), .orange)
            case .waitingForNetwork: return (L("Ağ bekleniyor…"), .orange)
            }
        }()
        Circle()
            .fill(color)
            .frame(width: 7, height: 7)
            .padding(3)
            .contentShape(Rectangle())
            .help(text)
            .accessibilityLabel(text)
    }
}

/// Status banners: no network, "update required", and remote announcements. Renders nothing when there is none.
struct RemoteBanner: View {
    @Bindable var model: AppModel

    var body: some View {
        VStack(spacing: 6) {
            if !model.networkAvailable {
                banner(L("İnternet bağlantısı yok. Yüklemeler duraklatıldı; bağlantı gelince kendiliğinden devam edecek."),
                       symbol: "wifi.slash", color: .orange)
            }
            if let update = model.availableUpdate {
                Button {
                    NSWorkspace.shared.open(update.pageURL)
                } label: {
                    banner(L("TeleSave %@ yayımlandı. İndirmek için tıklayın.", update.version),
                           symbol: "arrow.down.circle.fill", color: .accentColor)
                }
                .buttonStyle(.plain)
            }
            if model.updateRequired {
                banner(L("Bu TeleSave sürümü artık desteklenmiyor. Lütfen en yeni sürümü yükleyin."),
                       symbol: "arrow.down.circle.fill", color: .red)
            }
            if let message = model.remoteMessage {
                banner(message, symbol: "megaphone.fill", color: .accentColor)
            }
        }
    }

    private func banner(_ text: String, symbol: String, color: Color) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: symbol).foregroundStyle(color)
            Text(text)
                .font(.caption)
                .frame(maxWidth: .infinity, alignment: .leading)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(8)
        .background(RoundedRectangle(cornerRadius: 8).fill(color.opacity(0.12)))
    }
}

struct NoticeBanner: View {
    let text: String
    let onDismiss: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "info.circle.fill").foregroundStyle(.orange)
            Text(text)
                .font(.caption)
                .frame(maxWidth: .infinity, alignment: .leading)
                .fixedSize(horizontal: false, vertical: true)
            Button(action: onDismiss) { Image(systemName: "xmark").font(.caption) }
                .buttonStyle(.borderless)
        }
        .padding(8)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.orange.opacity(0.12)))
    }
}

enum Format {
    /// "2 Eyl 2026, 15:15" in the UI language.
    static func uploadDate(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.locale = L10n.locale
        formatter.dateFormat = "d MMM yyyy, HH:mm"
        return formatter.string(from: date)
    }

    /// "%42" in Turkish, "42%" in English, "42 %" in Russian.
    static func percent(_ fraction: Double) -> String {
        let formatter = NumberFormatter()
        formatter.numberStyle = .percent
        formatter.maximumFractionDigits = 0
        formatter.locale = L10n.locale
        return formatter.string(from: NSNumber(value: fraction)) ?? "\(Int(fraction * 100))%"
    }

    static func bytes(_ value: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: value, countStyle: .file)
    }

    static func speed(_ bytesPerSecond: Double) -> String {
        L("%@/sn", bytes(Int64(bytesPerSecond)))
    }

    static func duration(_ seconds: TimeInterval) -> String {
        let formatter = DateComponentsFormatter()
        var calendar = Calendar.current
        calendar.locale = L10n.locale
        formatter.calendar = calendar
        formatter.allowedUnits = seconds >= 3600 ? [.hour, .minute] : [.minute, .second]
        formatter.unitsStyle = .abbreviated
        return formatter.string(from: seconds) ?? ""
    }
}
