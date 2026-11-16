import SwiftUI
import TeleSaveCore

struct SettingsView: View {
    @Bindable var model: AppModel
    var onClose: () -> Void

    @State private var launchAtLogin = LaunchAtLogin.isEnabled
    @State private var launchError: String?

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Button(action: onClose) {
                    Label(L("Geri"), systemImage: "chevron.left")
                }
                .buttonStyle(.borderless)
                Spacer()
                Text(L("Ayarlar")).font(.headline)
                Spacer()
                Color.clear.frame(width: 50, height: 1)
            }
            .padding(12)
            Divider()

            Form {
                Section(L("Gönderim")) {
                    Toggle(L("Fotoğraf/videoları medya olarak gönder"), isOn: $model.settings.sendMediaCompressed)
                        .help(L("Kapalıyken her şey belge olarak, orijinal kalitede gönderilir."))
                    Toggle(L("Birlikte bırakılanları albüm yap (en fazla 10)"), isOn: $model.settings.groupAsAlbum)
                    Picker(L("Klasörler"), selection: $model.settings.folderMode) {
                        Text(L("Zip olarak")).tag(FolderMode.zip)
                        Text(L("İçindeki dosyalar ayrı ayrı")).tag(FolderMode.expand)
                    }
                    Stepper(L("Aynı anda yükleme: %@", String(model.settings.maxConcurrent)), value: $model.settings.maxConcurrent, in: 1...4)
                    TextField(L("Açıklama şablonu"), text: $model.settings.captionTemplate, prompt: Text(L("ör. #telesave {filename}")))
                    Text(L("Değişkenler: {filename} {date} {size}"))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                Section(L("Uygulama")) {
                    Picker(L("Dil"), selection: $model.settings.language) {
                        Text(L("Sistem (%@)", L10n.languages.first { $0.code == L10n.systemLanguageCode }?.name ?? "English"))
                            .tag(L10n.systemCode)
                        ForEach(L10n.languages) { language in
                            Text(language.name).tag(language.code)
                        }
                    }
                    Toggle(L("Girişte başlat"), isOn: $launchAtLogin)
                        .onChange(of: launchAtLogin) { _, enabled in
                            do {
                                try LaunchAtLogin.set(enabled)
                                launchError = nil
                            } catch {
                                launchError = error.localizedDescription
                                launchAtLogin = LaunchAtLogin.isEnabled
                            }
                        }
                    if let launchError {
                        Text(launchError).font(.caption).foregroundStyle(.red)
                    }
                    Toggle(L("Sohbetteki tüm dosyaları göster"), isOn: $model.settings.showChatFiles)
                        .help(L("Açıkken listede seçili sohbetin tüm dosyaları görünür; kapalıyken sadece TeleSave ile yüklenenler."))
                    Toggle(L("Bildirim göster"), isOn: $model.settings.notificationsEnabled)
                    Toggle(L("İkona bırakınca pencereyi aç"), isOn: $model.settings.openPopoverOnDrop)
                }

                Section(L("Hesap")) {
                    if let me = model.me {
                        LabeledContent(L("Hesap"), value: me.displayName)
                        if let username = me.username { LabeledContent(L("Kullanıcı adı"), value: "@\(username)") }
                        LabeledContent(L("Dosya sınırı"), value: Format.bytes(me.maxUploadSize))
                    }
                    if model.auth.isReady {
                        Button(L("Telegram oturumunu kapat"), role: .destructive, action: model.logOut)
                    }
                    if !model.hasBundledCredentials {
                        Button(L("API anahtarını değiştir"), action: model.resetCredentials)
                    }
                }

                Section {
                    // <GitHub> TeleSave <version> [update] ↗ — opens the new release if there is one, else the repo.
                    // A plain button: Link would draw the text in the accent color, which is hard to read here.
                    Button {
                        NSWorkspace.shared.open(model.availableUpdate?.pageURL ?? UpdateChecker.repositoryURL)
                    } label: {
                        HStack(spacing: 8) {
                            Image(nsImage: GitHubMark.image)
                                .renderingMode(.template)
                                .resizable()
                                .frame(width: 16, height: 16)
                            Text("TeleSave")
                            Text(model.appVersion)
                                .foregroundStyle(.secondary)
                                .monospacedDigit()
                            if let update = model.availableUpdate {
                                Text(L("Güncelleme var: %@", update.version))
                                    .font(.caption.weight(.medium))
                                    .foregroundStyle(.white)
                                    .padding(.horizontal, 6)
                                    .padding(.vertical, 2)
                                    .background(Capsule().fill(Color.accentColor))
                            }
                            Spacer()
                            Image(systemName: "arrow.up.right")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                        .foregroundStyle(.primary)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .help(model.availableUpdate == nil ? "github.com/\(UpdateChecker.repository)" : L("Yeni sürümü indir"))
                }
            }
            .formStyle(.grouped)
        }
    }
}

/// GitHub mark (Octicons "mark-github"); SF Symbols has no brand logos.
enum GitHubMark {
    static let image: NSImage = {
        let svg = ##"<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" width="16" height="16"><path fill="#000" d="M8 0c4.42 0 8 3.58 8 8a8.013 8.013 0 0 1-5.45 7.59c-.4.08-.55-.17-.55-.38 0-.27.01-1.13.01-2.2 0-.75-.25-1.23-.54-1.48 1.78-.2 3.65-.88 3.65-3.95 0-.88-.31-1.59-.82-2.15.08-.2.36-1.02-.08-2.12 0 0-.67-.22-2.2.82-.64-.18-1.32-.27-2-.27-.68 0-1.36.09-2 .27-1.53-1.03-2.2-.82-2.2-.82-.44 1.1-.16 1.92-.08 2.12-.51.56-.82 1.28-.82 2.15 0 3.06 1.86 3.75 3.64 3.95-.23.2-.44.55-.51 1.07-.46.21-1.61.55-2.33-.66-.15-.24-.6-.83-1.23-.82-.67.01-.27.38.01.53.34.19.73.9.82 1.13.16.45.68 1.31 2.69.94 0 .67.01 1.3.01 1.49 0 .21-.15.45-.55.38A7.995 7.995 0 0 1 0 8c0-4.42 3.58-8 8-8Z"/></svg>"##
        let image = NSImage(data: Data(svg.utf8)) ?? NSImage(systemSymbolName: "link", accessibilityDescription: nil)!
        image.isTemplate = true
        return image
    }()
}
