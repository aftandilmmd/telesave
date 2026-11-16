import SwiftUI
import TeleSaveCore

/// Upload destination under the account name; click to change. Anything other than Saved Messages
/// is shown in orange so it is never missed.
struct DestinationLine: View {
    @Bindable var model: AppModel
    var onPick: () -> Void

    var body: some View {
        let destination = model.destination
        let isSaved = destination.isSavedMessages
        HStack(spacing: 4) {
            Button(action: onPick) {
                HStack(spacing: 3) {
                    Image(systemName: isSaved ? "bookmark.fill" : "arrow.turn.down.right")
                        .font(.system(size: 9, weight: .semibold))
                    Text(destination.displayTitle)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Image(systemName: "chevron.down")
                        .font(.system(size: 8, weight: .semibold))
                }
                .font(.caption.weight(isSaved ? .regular : .semibold))
                .foregroundStyle(isSaved ? Color.secondary : Color.orange)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(L("Yükleme hedefi: %@. Değiştirmek için tıklayın.", destination.displayTitle))
            .accessibilityLabel(L("Yükleme hedefi: %@. Değiştir", destination.displayTitle))

            if !isSaved {
                Button {
                    model.select(.savedMessages)
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .font(.system(size: 10))
                        .foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
                .help(L("Kayıtlı Mesajlara dön"))
            }
        }
    }
}

struct DestinationPicker: View {
    @Bindable var model: AppModel
    var onClose: () -> Void

    @State private var query = ""
    @State private var forumChat: ChatSummary?
    @State private var topics: [ForumTopicSummary] = []
    @State private var checkingChatId: Int64?

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            if let error = model.destinationError {
                NoticeBanner(text: error) { model.destinationError = nil }
                    .padding(12)
            }
            if let forumChat {
                topicList(for: forumChat)
            } else {
                chatList
            }
        }
        .task {
            model.destinationError = nil
            if model.chats.isEmpty { await model.refreshChats() }
        }
    }

    private var header: some View {
        HStack {
            Button {
                if forumChat != nil { forumChat = nil } else { onClose() }
            } label: {
                Label(L("Geri"), systemImage: "chevron.left")
            }
            .buttonStyle(.borderless)
            Spacer()
            Text(forumChat?.title ?? L("Yükleme hedefi"))
                .font(.headline)
                .lineLimit(1)
            Spacer()
            Button {
                Task { await model.refreshChats() }
            } label: {
                Image(systemName: "arrow.clockwise")
            }
            .buttonStyle(.borderless)
            .help(L("Sohbetleri yenile"))
            .disabled(model.isLoadingChats || forumChat != nil)
        }
        .padding(12)
    }

    private var filteredChats: [ChatSummary] {
        let q = query.trimmingCharacters(in: .whitespaces)
        guard !q.isEmpty else { return model.chats }
        return model.chats.filter {
            $0.title.localizedCaseInsensitiveContains(q) || ($0.username?.localizedCaseInsensitiveContains(q) ?? false)
        }
    }

    private var chatList: some View {
        VStack(spacing: 0) {
            TextField(L("Sohbet ara"), text: $query)
                .textFieldStyle(.roundedBorder)
                .padding(.horizontal, 12)
                .padding(.top, 10)
                .padding(.bottom, 6)
            List {
                Section {
                    row(icon: "bookmark.fill", title: L("Saved Messages"), subtitle: L("Kendi bulut sohbetiniz"),
                        selected: model.destination.isSavedMessages) {
                        model.select(.savedMessages)
                        onClose()
                    }
                }
                if query.isEmpty && !model.recentDestinations.isEmpty {
                    Section(L("Son kullanılanlar")) {
                        ForEach(model.recentDestinations, id: \.self) { recent in
                            row(icon: "clock.arrow.circlepath", title: recent.title, subtitle: nil,
                                selected: recent == model.destination) {
                                model.select(recent)
                                onClose()
                            }
                        }
                    }
                }
                Section(L("Sohbetler")) {
                    if model.isLoadingChats && model.chats.isEmpty {
                        HStack { Spacer(); ProgressView().controlSize(.small); Spacer() }
                    } else if filteredChats.isEmpty {
                        Text(L("Sohbet bulunamadı")).foregroundStyle(.secondary)
                    }
                    ForEach(filteredChats) { chat in
                        row(icon: icon(for: chat), title: chat.title, subtitle: subtitle(for: chat),
                            selected: model.destination.chatId == chat.id && model.destination.forumTopicId == nil,
                            busy: checkingChatId == chat.id) {
                            open(chat)
                        }
                    }
                }
            }
            .listStyle(.inset)
        }
    }

    private func topicList(for chat: ChatSummary) -> some View {
        List {
            if topics.isEmpty {
                HStack { Spacer(); ProgressView().controlSize(.small); Spacer() }
            }
            ForEach(topics) { topic in
                row(icon: topic.isGeneral ? "number" : "text.bubble", title: topic.name, subtitle: nil,
                    selected: model.destination.chatId == chat.id && model.destination.forumTopicId == topic.id) {
                    Task {
                        // The General topic is the chat itself; no topic id needed.
                        if await model.choose(chat, topic: topic.isGeneral ? nil : topic) { onClose() }
                    }
                }
            }
        }
        .listStyle(.inset)
        .task(id: chat.id) {
            topics = await model.forumTopics(for: chat)
        }
    }

    private func open(_ chat: ChatSummary) {
        if chat.isForum {
            topics = []
            forumChat = chat
            return
        }
        checkingChatId = chat.id
        Task {
            let ok = await model.choose(chat)
            checkingChatId = nil
            if ok { onClose() }
        }
    }

    private func row(icon: String, title: String, subtitle: String?, selected: Bool, busy: Bool = false,
                     action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: icon)
                    .frame(width: 20)
                    .foregroundStyle(Color.accentColor)
                VStack(alignment: .leading, spacing: 1) {
                    Text(title).lineLimit(1)
                    if let subtitle {
                        Text(subtitle).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                }
                Spacer()
                if busy {
                    ProgressView().controlSize(.small)
                } else if selected {
                    Image(systemName: "checkmark").foregroundStyle(Color.accentColor)
                }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    private func icon(for chat: ChatSummary) -> String {
        switch chat.kind {
        case .savedMessages: return "bookmark.fill"
        case .user: return "person.fill"
        case .bot: return "cpu"
        case .group: return chat.isForum ? "rectangle.3.group.bubble" : "person.2.fill"
        case .channel: return "megaphone.fill"
        }
    }

    private func subtitle(for chat: ChatSummary) -> String {
        var parts: [String] = []
        switch chat.kind {
        case .savedMessages: parts.append(L("Saved Messages"))
        case .user: parts.append(L("Kişi"))
        case .bot: parts.append("Bot")
        case .group: parts.append(chat.isForum ? L("Forum grubu") : L("Grup"))
        case .channel: parts.append(L("Kanal"))
        }
        if let username = chat.username { parts.append("@\(username)") }
        return parts.joined(separator: " · ")
    }
}
