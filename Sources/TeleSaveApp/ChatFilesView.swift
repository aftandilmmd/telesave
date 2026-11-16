import SwiftUI
import TeleSaveCore

/// Every file in the destination chat, with server-side search and type filters. Uploads that are
/// still in progress (or failed) are pinned on top, since they are not in the chat yet.
struct ChatFilesView: View {
    @Bindable var model: AppModel
    @State private var query = ""
    @State private var filter: ChatFileFilter = .all

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                TextField(L("Dosya ara"), text: $query)
                    .textFieldStyle(.roundedBorder)
                Picker("", selection: $filter) {
                    Text(L("Tümü")).tag(ChatFileFilter.all)
                    Text(L("Belge")).tag(ChatFileFilter.documents)
                    Text(L("Resim")).tag(ChatFileFilter.images)
                    Text(L("Video")).tag(ChatFileFilter.videos)
                    Text(L("Ses")).tag(ChatFileFilter.audio)
                }
                .labelsHidden()
                .fixedSize()
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            Divider()
            content
        }
        // Debounced: wait for typing to pause before asking the server.
        .task(id: "\(model.me?.id ?? 0)|\(query)|\(filter.rawValue)|\(model.destination.chatId ?? 0)|\(model.destination.forumTopicId ?? 0)") {
            if !query.isEmpty { try? await Task.sleep(nanoseconds: 300_000_000) }
            guard !Task.isCancelled else { return }
            await model.reloadChatFiles(query: query.trimmingCharacters(in: .whitespaces), filter: filter)
        }
    }

    /// Queue items not (yet) visible in the chat list: in progress, failed, or just finished and
    /// waiting for the list refresh. Only for the chat being shown.
    private var pendingUploads: [UploadItem] {
        let listed = Set(model.chatFiles.map(\.messageId))
        let shown = model.destination.isSavedMessages ? nil : model.destination
        return model.items.reversed().filter { item in
            guard item.destination == shown else { return false }
            switch item.state {
            case .queued, .uploading, .failed:
                return true
            case .done(let messageId):
                return !listed.contains(messageId) && (item.finishedAt ?? .distantPast) > Date().addingTimeInterval(-60)
            case .cancelled:
                return false
            }
        }
    }

    @ViewBuilder private var content: some View {
        let pending = pendingUploads
        if pending.isEmpty, let error = model.chatFilesError, model.chatFiles.isEmpty {
            placeholder(error, symbol: "exclamationmark.triangle")
        } else if pending.isEmpty && model.chatFiles.isEmpty && model.chatFilesLoading {
            VStack { Spacer(); ProgressView().controlSize(.small); Spacer() }.frame(maxWidth: .infinity)
        } else if pending.isEmpty && model.chatFiles.isEmpty && !model.chatFilesComplete {
            // Scanned a while without a match, but older messages remain.
            VStack(spacing: 8) {
                Spacer()
                Text(L("Henüz eşleşme yok")).foregroundStyle(.secondary)
                Button(L("Aramaya devam et")) { Task { await model.loadMoreChatFiles() } }
                Spacer()
            }
            .frame(maxWidth: .infinity)
        } else if pending.isEmpty && model.chatFiles.isEmpty {
            placeholder(query.isEmpty ? L("Bu sohbette dosya yok") : L("Sonuç yok"), symbol: "doc")
        } else {
            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(pending) { item in
                        QueueRow(item: item, model: model)
                        Divider().padding(.leading, 60)
                    }
                    ForEach(model.chatFiles) { file in
                        ChatFileRow(file: file, model: model)
                            .onAppear {
                                if file.id == model.chatFiles.last?.id {
                                    Task { await model.loadMoreChatFiles() }
                                }
                            }
                        Divider().padding(.leading, 60)
                    }
                    if model.chatFilesLoading {
                        ProgressView().controlSize(.small).padding(10)
                    }
                }
            }
        }
    }

    private func placeholder(_ text: String, symbol: String) -> some View {
        VStack(spacing: 6) {
            Spacer()
            Image(systemName: symbol).font(.title2).foregroundStyle(.tertiary)
            Text(text).foregroundStyle(.secondary).multilineTextAlignment(.center)
            Spacer()
        }
        .padding(.horizontal, 20)
        .frame(maxWidth: .infinity)
    }
}

struct ChatFileRow: View {
    let file: ChatFile
    @Bindable var model: AppModel
    @State private var confirmingDelete = false

    var body: some View {
        HStack(spacing: 10) {
            ChatFileThumbnail(file: file, model: model)
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 4) {
                    Text(file.fileName)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    if model.isFromTeleSave(file) {
                        Image(systemName: "paperplane.fill")
                            .font(.system(size: 9))
                            .foregroundStyle(Color.accentColor)
                            .help(L("Bu Mac'ten TeleSave ile yüklendi"))
                            .accessibilityLabel(L("TeleSave ile yüklendi"))
                    }
                }
                Text("\(Format.bytes(file.size)) · \(Format.uploadDate(file.date))")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            .help(file.caption.isEmpty ? file.fileName : "\(file.fileName)\n\(file.caption)")
            Spacer(minLength: 4)
            actions
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    @ViewBuilder private var actions: some View {
        if model.busyChatFiles.contains(file.id) {
            ProgressView().controlSize(.small).frame(width: 20, height: 20)
        } else if confirmingDelete {
            Button(L("Sil"), role: .destructive) {
                confirmingDelete = false
                model.delete(file)
            }
            .controlSize(.small)
            .tint(.red)
            .help(L("Mesajı Telegram'dan herkes için sil"))
            IconButton(symbol: "xmark", help: L("Vazgeç")) { confirmingDelete = false }
        } else {
            IconButton(symbol: "arrow.down.circle", help: L("İndir (İndirilenler klasörüne)")) { model.download(file) }
            if model.canDelete(file) {
                IconButton(symbol: "trash", help: L("Telegram'dan sil")) { confirmingDelete = true }
            }
        }
    }
}

/// Instant blurry minithumbnail, replaced by the real thumbnail once downloaded; type icon otherwise.
struct ChatFileThumbnail: View {
    let file: ChatFile
    @Bindable var model: AppModel
    @State private var image: NSImage?

    var body: some View {
        Group {
            if let image {
                Image(nsImage: image).resizable().scaledToFill()
            } else if let data = file.minithumbnail, let mini = NSImage(data: data) {
                Image(nsImage: mini).resizable().scaledToFill().blur(radius: 1)
            } else {
                Image(nsImage: ThumbnailCache.typeIcon(for: file.fileName)).resizable().scaledToFit()
            }
        }
        .frame(width: 36, height: 36)
        .clipShape(RoundedRectangle(cornerRadius: 6))
        .overlay(alignment: .bottomTrailing) {
            if file.kind == .video || file.kind == .animation {
                Image(systemName: "play.fill")
                    .font(.system(size: 9))
                    .foregroundStyle(.white)
                    .shadow(radius: 1)
                    .padding(3)
            }
        }
        .task(id: file.id) {
            guard image == nil, file.thumbnailFileId != nil,
                  let path = await model.thumbnailPath(for: file) else { return }
            image = NSImage(contentsOfFile: path)
        }
        .accessibilityHidden(true)
    }
}
