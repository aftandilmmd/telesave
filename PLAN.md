# TeleSave — macOS Menü Çubuğu Uygulaması: Plan

> Çalışma adı: **TeleSave** (değiştirilebilir).
> Amaç: Menü çubuğunda (taskbar) yaşayan, sürükle-bırak ile dosyaları kullanıcının **kendi Telegram hesabındaki Saved Messages**'a yükleyen uygulama.
> Kapsam: Şimdilik yalnızca macOS. Windows/Linux ileride.

> **Durum (2026-11-16):** Faz 0–6 uygulandı. Plandan sapmalar:
> - XcodeGen yerine **SwiftPM** + `scripts/build-app.sh` (XcodeGen kurulu değildi).
> - `TDLibKit` yerine doğrudan **`TDLibFramework` + TDLib JSON arayüzü**: daha hafif, ve Windows/Linux'ta da aynı arayüz kullanılacak.
> - Min. macOS **15** (hazır TDLib binary'si macOS 15 için derlenmiş).
> - api_id/api_hash gömülmek zorunda değil: ilk açılışta kullanıcı girer, Keychain'de saklanır. İsteğe bağlı `Config/Secrets.env` ile build'e gömülür.
> - Çıkış yapınca TDLib kendi veritabanını siler; Keychain'deki DB anahtarı yeniden kullanılır.
> - TDLib `use_message_database = false`: yarım kalan gönderimleri TDLib kendiliğinden tekrar göndermez, kuyruk yönetir (çift gönderim önlenir).

---

## 1. Kritik teknik gerçekler (karar öncesi)

| Konu | Gerçek | Sonuç |
|---|---|---|
| Bot API | Bot, kullanıcının Saved Messages'ına yazamaz. Bot sadece kendi chat'lerine mesaj atar. Limit 50 MB. | **Bot API kullanılamaz.** |
| MTProto (kullanıcı hesabı) | Saved Messages = kullanıcının kendisiyle olan özel chat'i. Kullanıcı olarak oturum açmak gerekir. | **TDLib** (Telegram'ın resmi client kütüphanesi) ile kullanıcı oturumu. |
| API anahtarı | `api_id` + `api_hash` gerekir, my.telegram.org'dan alınır. | Uygulamaya gömülür (obfuscate edilir), repo'ya düz yazılmaz. |
| Dosya boyutu | Normal hesap 2 GB, Premium 4 GB / dosya. | Bırakmadan önce boyut kontrolü, `isPremium`'a göre limit. |
| Rate limit | Çok hızlı gönderim `FLOOD_WAIT_X` döndürür. | Kuyruk + eşzamanlılık sınırı (2–3) + backoff. |

## 2. Teknoloji seçimi

**Öneri: Native Swift (SwiftUI + AppKit) + TDLib (`TDLibKit` SPM paketi, hazır xcframework).**

Gerekçe:
- macOS'ta dosyayı **doğrudan menü çubuğu ikonunun üzerine bırakmak** yalnızca AppKit (`NSStatusItem` + `NSDraggingDestination`) ile düzgün yapılır. Tauri'nin tray API'si drop desteklemez; Electron'da sadece macOS'a özel `drop-files` var.
- TDLib C++ ve platformlar arası. Windows/Linux'a geçince **aynı TDLib JSON arayüzü** kullanılır; Core katmanındaki kuyruk/durum makinesi tasarımı aynen taşınır, sadece UI yeniden yazılır.
- Hafif, hızlı açılış, düşük RAM (Electron ~150 MB+ yerine ~40–60 MB).

Alternatif (cross-platform öncelikliyse): **Tauri 2 (Rust) + TDLib/grammers**. Tek kod tabanı, ama menü çubuğu ikonuna drop için yine özel Objective-C eklentisi gerekir. Şimdilik önerilmez.

| Bileşen | Seçim |
|---|---|
| Dil / UI | Swift 5.9+, SwiftUI (popover içi), AppKit (status item, drag) |
| Telegram | TDLib via `TDLibKit` (sürüm sabitlenir) |
| Proje | XcodeGen `project.yml` (Holo ile aynı düzen) |
| Min. macOS | 15.0 (TDLib binary macOS 15 için derlenmiş) |
| Kalıcılık | Kuyruk: JSON / SwiftData (Application Support). TDLib DB: kendi dizini |
| Gizli bilgi | TDLib DB şifreleme anahtarı → Keychain |
| Dağıtım | Developer ID + notarization (App Store değil, v1) |

## 3. Mimari

```
┌──────────────────── TeleSave.app (LSUIElement, Dock ikonu yok) ────────────────────┐
│                                                                                    │
│  StatusItemController (AppKit)          PopoverView (SwiftUI)                      │
│   - menü çubuğu ikonu                    - drop alanı                              │
│   - ikon üstüne drop                     - kuyruk listesi, ilerleme, iptal/tekrar  │
│   - durum rozeti (yükleniyor/hata)       - giriş ekranı (QR / telefon)             │
│            │                             - ayarlar                                 │
│            └───────────────┬──────────────────┘                                    │
│                            ▼                                                       │
│                     AppModel (@Observable, MainActor)                              │
│                            │                                                       │
├────────────────────────────┼───────────────────────────────────────────────────────┤
│  TeleSaveCore (framework, UI'dan bağımsız, test edilebilir)                        │
│                            ▼                                                       │
│   UploadQueue (actor)  ── kuyruk, eşzamanlılık, retry, FLOOD_WAIT backoff          │
│   DropIntake           ── URL → UploadItem (boyut, tip, klasör→zip, doğrulama)     │
│   AuthStateMachine     ── TDLib authorizationState → UI durumları                  │
│   TelegramClient (protocol) ◄── TDLibTelegramClient (gerçek) / FakeTelegramClient  │
│   QueueStore           ── kalıcı kuyruk (yeniden açılışta devam)                   │
└────────────────────────────────────────────────────────────────────────────────────┘
```

`TelegramClient` protokolü sayesinde Core, sahte istemciyle unit-test edilir; ileride Windows/Linux portunda aynı arayüz korunur.

### Klasör yapısı

```
TeleSave/
├── PLAN.md
├── project.yml
├── Config/
│   ├── TeleSave-Info.plist
│   ├── TeleSave.entitlements
│   └── Secrets.xcconfig          (gitignore: TG_API_ID, TG_API_HASH)
├── Sources/
│   ├── TeleSaveApp/
│   │   ├── TeleSaveApp.swift      (@main, NSApplicationDelegateAdaptor)
│   │   ├── StatusItemController.swift
│   │   ├── StatusItemDropView.swift
│   │   ├── PopoverView.swift
│   │   ├── LoginView.swift
│   │   ├── QueueView.swift
│   │   ├── SettingsView.swift
│   │   └── AppModel.swift
│   └── TeleSaveCore/
│       ├── TelegramClient.swift
│       ├── TDLibTelegramClient.swift
│       ├── AuthStateMachine.swift
│       ├── UploadItem.swift
│       ├── UploadQueue.swift
│       ├── DropIntake.swift
│       └── QueueStore.swift
└── Tests/TeleSaveCoreTests/
```

## 4. Telegram akışları (TDLib)

### 4.1 Başlatma
1. `setTdlibParameters`: `database_directory` = `~/Library/Application Support/TeleSave/tdlib`, `files_directory`, `use_message_database = true`, `api_id`, `api_hash`, `system_language_code`, `device_model = "Mac"`, `application_version`.
2. `database_encryption_key` → ilk açılışta rastgele üret, Keychain'e yaz.
3. TDLib log seviyesini 1'e çek, log dosyası Application Support'ta.

### 4.2 Giriş (Auth)
`updateAuthorizationState` durumlarına göre:

| TDLib durumu | UI |
|---|---|
| `authorizationStateWaitPhoneNumber` | İki seçenek: **QR ile giriş** (önerilen, `requestQrCodeAuthentication`) veya telefon numarası |
| `authorizationStateWaitOtherDeviceConfirmation(link)` | QR kodu göster (`tg://login?token=…`), telefondan taranır |
| `authorizationStateWaitCode` | Kod girişi (Telegram uygulamasına gelir) |
| `authorizationStateWaitPassword` | 2FA şifresi (ipucu gösterilir) |
| `authorizationStateReady` | Ana ekran |
| `authorizationStateLoggingOut` / `Closed` | Giriş ekranına dön |

Çıkış: `logOut` + yerel TDLib DB ve Keychain anahtarı silinir.

### 4.3 Saved Messages'a gönderme
1. `getMe` → `user.id`, `is_premium`.
2. `createPrivateChat(userId: me.id)` → Saved Messages `chatId` (önbelleğe al).
3. Her dosya için `sendMessage(chatId, inputMessageContent: inputMessageDocument(document: inputFileLocal(path), caption))`.
   - Varsayılan **belge olarak** gönder → orijinal kalite, sıkıştırma yok.
   - Ayar: "Fotoğraf/videoları medya olarak gönder" (`inputMessagePhoto` / `inputMessageVideo`, sıkıştırılır).
4. Çoklu bırakma: ayara bağlı olarak `sendMessageAlbum` (en fazla 10 öğe/grup) veya tek tek.
5. Takip:
   - `updateFile` → `file.remote.uploadedSize / file.size` = ilerleme.
   - `updateMessageSendSucceeded` → tamamlandı (yeni `messageId` sakla).
   - `updateMessageSendFailed` → hata kodu; `FLOOD_WAIT_X` ise X saniye bekle, yeniden dene.
6. İptal: `cancelPreliminaryUploadFile` / `deleteMessages(revoke: true)` gönderim sürmekteyse.

Caption: varsayılan boş; ayar ile dosya adı, tarih ya da sabit etiket (ör. `#telesave`).

## 5. Sürükle-bırak

### 5.1 Menü çubuğu ikonuna bırakma
- `NSStatusItem.button` içine şeffaf `StatusItemDropView` (NSView, `NSDraggingDestination`) eklenir, `registerForDraggedTypes([.fileURL])`.
- `draggingEntered` → ikon vurgulanır (dolu ikon / renk), `.copy` döner.
- `performDragOperation` → `NSPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true])` → `DropIntake`.
- Bırakma sonrası popover kısa süre açılır, kuyruğu gösterir (ayar ile kapatılabilir).

### 5.2 Popover içine bırakma
- SwiftUI `.dropDestination(for: URL.self)` ile büyük drop alanı.
- "Dosya seç…" butonu (`NSOpenPanel`, çoklu seçim) — sürükleyemeyenler için.

### 5.3 DropIntake kuralları
- Dosya → doğrudan kuyruğa.
- Klasör → v1: geçici dizinde `.zip`'e sıkıştır (`NSFileCoordinator` `.forUploading` seçeneği zip üretir), sonra yükle; tamamlanınca geçici zip silinir.
- 0 bayt, okunamayan, limit üstü dosyalar → reddet, sebebini göster.
- Aynı dosya kısa süre içinde ikinci kez bırakılırsa uyar (çift gönderim önleme).

## 6. Kuyruk ve kalıcılık

`UploadItem`: `id`, `sourceURL`, `bookmark` (security-scoped), `displayName`, `size`, `state` (`queued | uploading(progress) | done(messageId) | failed(reason) | cancelled`), `createdAt`, `attempts`.

- `UploadQueue` actor: en fazla 2 eşzamanlı yükleme (ayar 1–4).
- Retry: ağ hatası → üstel backoff (2s, 4s, 8s… max 5 deneme). `FLOOD_WAIT` → belirtilen süre.
- Ağ yokken (`NWPathMonitor`) kuyruk duraklar, ağ gelince devam.
- `QueueStore`: kuyruk değişince JSON'a yaz. Uygulama yeniden açılınca bitmemiş öğeler `queued`'a döner ve bookmark'tan dosyaya yeniden erişilir.
- Tamamlanan öğeler 24 saat / son 50 öğe kadar listede kalır.

## 7. Arayüz

**Menü çubuğu ikonu durumları:** boşta · drag üstünde (vurgulu) · yükleniyor (ilerleme halkası veya animasyon) · hata (kırmızı nokta) · oturum yok (soluk).

**Popover (≈360×480):**
1. Üst: hesap avatarı + ad, ⚙︎ ayarlar.
2. Drop alanı: "Dosyaları buraya bırak → Saved Messages".
3. Kuyruk: dosya adı, boyut, ilerleme çubuğu, hız/kalan süre, iptal / tekrar dene / "Telegram'da aç" (`tg://openmessage?user_id=…` veya Telegram.app).
4. Alt: "Tamamlananları temizle".

**Sağ tık menüsü (ikon):** Dosya seç…, Ayarlar, Çıkış yap, Uygulamadan çık.

**Ayarlar:** Girişte başlat (`SMAppService.mainApp`), medyayı sıkıştırarak gönder, albüm olarak grupla, caption şablonu, eşzamanlılık, bildirimler, bırakınca popover aç.

**Bildirimler:** `UserNotifications` — "3 dosya Saved Messages'a yüklendi" / hata.

## 8. Güvenlik ve izinler

- `Info.plist`: `LSUIElement = true`, `LSMultipleInstancesProhibited = true`.
- Sandbox: **açık** (`com.apple.security.app-sandbox`), `network.client`, `files.user-selected.read-only`, `files.bookmarks.app-scope`. Bırakılan dosyalar sandbox'ta okunabilir; yeniden açılışta erişim için security-scoped bookmark saklanır.
- Hardened Runtime açık, Developer ID ile imza + `notarytool`.
- `api_id/api_hash`: `Secrets.xcconfig` (gitignore) → build-time Info.plist anahtarı → basit XOR obfuscation. (Tam gizlilik mümkün değil; bu Telegram istemcilerinde standarttır.)
- TDLib DB şifreli (Keychain anahtarı). Uygulama hiçbir veriyi Telegram dışında bir sunucuya göndermez.
- Telefon numarası/kod log'lanmaz.

## 9. Fazlar ve doğrulama kriterleri

| Faz | İş | Doğrulama |
|---|---|---|
| **0. Hazırlık** | my.telegram.org'da app oluştur (api_id/hash). XcodeGen proje iskeleti, `TDLibKit` SPM, `Secrets.xcconfig`. | `xcodebuild build` geçer; app açılınca menü çubuğunda ikon görünür, Dock'ta görünmez. |
| **1. TDLib + Auth** | `TDLibTelegramClient`, `AuthStateMachine`, QR + telefon/kod/2FA giriş ekranları, Keychain anahtar. | Gerçek hesapla QR ve telefonla giriş; yeniden açılışta oturum korunuyor; çıkış yapınca DB siliniyor. `AuthStateMachine` unit testleri (sahte durum dizileri). |
| **2. Tek dosya yükleme** | `getMe` → Saved Messages chat, `sendMessage(inputMessageDocument)`, ilerleme, başarı/hata. Popover'da "Dosya seç". | 1 KB, 50 MB, 1 GB dosya Saved Messages'ta görünür, orijinal boyut/ad korunur. İlerleme %0→100 akar. |
| **3. Sürükle-bırak** | `StatusItemDropView`, popover drop alanı, `DropIntake` (klasör→zip, reddetme kuralları). | Finder'dan ikona ve popover'a çoklu dosya + klasör bırakma çalışır; limit üstü dosya net mesajla reddedilir. `DropIntake` unit testleri. |
| **4. Kuyruk** | `UploadQueue` actor, eşzamanlılık, iptal, retry, FLOOD_WAIT, `NWPathMonitor`, `QueueStore`. | 20 dosya bırak → en fazla 2 paralel. Wi-Fi kapat/aç → devam eder. Yükleme sırasında uygulamayı kapat/aç → kuyruk devam eder. Sahte client ile retry/backoff testleri. |
| **5. Cilalama** | İkon durumları, bildirimler, ayarlar, girişte başlat, albüm/medya seçenekleri, caption şablonu. | Elle kontrol listesi (Bölüm 7'deki her öğe). |
| **6. Dağıtım** | Developer ID imza, notarization, DMG, (ops.) Sparkle ile otomatik güncelleme. | Temiz bir Mac'te DMG'den kurulum, Gatekeeper uyarısız açılır. |

## 10. Riskler

| Risk | Etki | Önlem |
|---|---|---|
| TDLib binary boyutu (~30–60 MB) | Büyük indirme | Kabul edilebilir; sadece arm64+x86_64 slice, strip. |
| FLOOD_WAIT / hesap kısıtlaması | Yükleme duraklar | Düşük eşzamanlılık, backoff; toplu spam benzeri davranış yok. |
| `api_id` kötüye kullanım / sızıntı | Anahtar iptali | Obfuscation, gerekirse yeni anahtar ile güncelleme. |
| TDLibKit sürüm uyumsuzluğu | Build kırılır | Sürüm sabitle, güncellemeleri ayrı yap. |
| Sandbox'ta bırakılan dosyaya sonradan erişim | Yeniden açılışta yükleme başarısız | Bırakma anında security-scoped bookmark oluştur. |
| Büyük dosyada uyku modu | Yükleme kesilir | Yükleme sürerken `ProcessInfo.beginActivity(.idleSystemSleepDisabled)`. |

## 11. v1 dışı (sonra)

- Finder **Share Extension** / Quick Action: "Saved Messages'a gönder".
- Global kısayol: panodaki dosya/görsel/metni gönder.
- Ekran görüntüsü al → doğrudan gönder.
- Birden fazla hesap.
- Hedef seçimi (Saved Messages dışında belirli chat/kanal, Saved Messages topic'leri).
- Windows (tray + pencere drop) ve Linux portu: TDLib aynı, Core tasarımı aynı, UI yeniden.

## 12. Açık sorular

1. Uygulama adı / bundle id (`com.???.telesave`)?
2. App Store hedefi var mı? (Varsa sandbox/inceleme kuralları sıkılaşır; TDLib App Store'da kullanılabiliyor ama inceleme riski var.)
3. Klasörler zip mi olsun, yoksa içindeki dosyalar tek tek mi gitsin?
4. Fotoğraf/video varsayılanı: belge (orijinal) mi, medya (sıkıştırılmış) mı?
