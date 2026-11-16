import CoreImage.CIFilterBuiltins
import SwiftUI
import TeleSaveCore

struct LoginView: View {
    @Bindable var model: AppModel
    var onSettings: () -> Void

    @State private var phone = ""
    @State private var code = ""
    @State private var password = ""
    @State private var usePhoneInsteadOfQR = false

    var body: some View {
        VStack(spacing: 14) {
            HStack {
                Spacer()
                Button(action: onSettings) { Image(systemName: "gearshape") }
                    .buttonStyle(.borderless)
                    .help(L("Ayarlar"))
            }
            RemoteBanner(model: model)
            Image(systemName: "paperplane.circle.fill")
                .font(.system(size: 44))
                .foregroundStyle(Color.accentColor)
            content
            if let error = model.authError {
                Text(error)
                    .font(.caption)
                    .foregroundStyle(.red)
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer()
        }
        .padding(20)
        .disabled(model.isBusy)
    }

    @ViewBuilder private var content: some View {
        switch model.auth {
        case .initializing, .closing, .closed, .loggingOut:
            ProgressView(L("Telegram'a bağlanılıyor…"))
                .padding(.top, 20)

        case .ready:
            ProgressView(L("Hesap yükleniyor…"))
                .padding(.top, 20)

        case .waitPhoneNumber:
            Text(L("Telegram ile giriş yapın")).font(.title3.bold())
            Text(L("Dosyalar kendi hesabınızın Kayıtlı Mesajlar sohbetine yüklenir."))
                .font(.callout)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
            Button(action: model.requestQRCode) {
                Label(L("QR kod ile giriş"), systemImage: "qrcode")
                    .frame(maxWidth: .infinity)
            }
            .controlSize(.large)
            .buttonStyle(.borderedProminent)
            Text(L("veya")).font(.caption).foregroundStyle(.secondary)
            phoneForm

        case .waitQRConfirmation(let link):
            if usePhoneInsteadOfQR {
                phoneForm
                Button(L("QR koda dön")) { usePhoneInsteadOfQR = false }
                    .buttonStyle(.link)
            } else {
                Text(L("Telefonla tarayın")).font(.title3.bold())
                QRCodeImage(text: link)
                    .frame(width: 170, height: 170)
                Text(L("Telegram › Ayarlar › Cihazlar › Masaüstü Cihaz Bağla"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                Button(L("Telefon numarasıyla giriş")) { usePhoneInsteadOfQR = true }
                    .buttonStyle(.link)
            }

        case .waitCode(let number, let delivery):
            Text(L("Doğrulama kodu")).font(.title3.bold())
            Text(codeHint(number: number, delivery: delivery))
                .font(.callout)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
            TextField("12345", text: $code)
                .textFieldStyle(.roundedBorder)
                .multilineTextAlignment(.center)
                .font(.title3.monospacedDigit())
                .onSubmit { model.submitCode(code) }
            Button(L("Doğrula")) { model.submitCode(code) }
                .buttonStyle(.borderedProminent)
                .disabled(code.filter(\.isNumber).count < 5)
            backButton

        case .waitPassword(let hint):
            Text(L("İki adımlı doğrulama")).font(.title3.bold())
            SecureField(L("Şifre"), text: $password)
                .textFieldStyle(.roundedBorder)
                .onSubmit { model.submitPassword(password) }
            if !hint.isEmpty {
                Text(L("İpucu: %@", hint)).font(.caption).foregroundStyle(.secondary)
            }
            Button(L("Giriş yap")) { model.submitPassword(password) }
                .buttonStyle(.borderedProminent)
                .disabled(password.isEmpty)
            backButton

        case .waitRegistration:
            Text(L("Bu numarada Telegram hesabı yok.")).font(.headline)
            Text(L("Önce telefondaki Telegram uygulamasından kayıt olun, sonra tekrar deneyin."))
                .font(.callout)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
            backButton
        }
    }

    private var phoneForm: some View {
        VStack(spacing: 8) {
            TextField("+90 5xx xxx xx xx", text: $phone)
                .textFieldStyle(.roundedBorder)
                .onSubmit { model.submitPhone(phone) }
            Button(L("Kod gönder")) { model.submitPhone(phone) }
                .disabled(phone.filter(\.isNumber).count < 7)
        }
    }

    private var backButton: some View {
        // Logging out from a half-finished auth resets TDLib to the phone-number step.
        Button(L("Baştan başla"), action: model.logOut)
            .buttonStyle(.link)
            .font(.caption)
    }

    private func codeHint(number: String, delivery: AuthState.CodeDelivery) -> String {
        switch delivery {
        case .telegramApp: return L("Kod, %@ numaralı hesabın diğer Telegram oturumlarına gönderildi.", number)
        case .sms: return L("Kod %@ numarasına SMS ile gönderildi.", number)
        case .call: return L("Kod %@ numarasına arama ile iletilecek.", number)
        case .other: return L("%@ için gönderilen kodu girin.", number)
        }
    }
}

struct QRCodeImage: View {
    let text: String

    var body: some View {
        if let image = Self.render(text) {
            Image(nsImage: image)
                .interpolation(.none)
                .resizable()
                .scaledToFit()
                .padding(8)
                .background(RoundedRectangle(cornerRadius: 8).fill(.white))
        } else {
            ProgressView()
        }
    }

    static func render(_ text: String) -> NSImage? {
        guard !text.isEmpty else { return nil }
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(text.utf8)
        filter.correctionLevel = "M"
        guard let output = filter.outputImage?.transformed(by: CGAffineTransform(scaleX: 8, y: 8)) else { return nil }
        let rep = NSCIImageRep(ciImage: output)
        let image = NSImage(size: rep.size)
        image.addRepresentation(rep)
        return image
    }
}

struct CredentialsView: View {
    @Bindable var model: AppModel
    @State private var apiId = ""
    @State private var apiHash = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(L("Telegram API anahtarı")).font(.title3.bold())
            Text(L("TeleSave'in hesabınıza bağlanması için bir Telegram API anahtarı gerekiyor (tek seferlik):"))
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
            VStack(alignment: .leading, spacing: 4) {
                Text(L("1. my.telegram.org adresinde telefonunuzla giriş yapın."))
                Text(L("2. “API development tools” › yeni uygulama oluşturun."))
                Text(L("3. api_id ve api_hash değerlerini aşağıya yapıştırın."))
            }
            .font(.caption)
            .foregroundStyle(.secondary)
            Link(L("my.telegram.org'u aç"), destination: URL(string: "https://my.telegram.org/apps")!)
                .font(.callout)
            TextField(L("api_id (ör. 1234567)"), text: $apiId)
                .textFieldStyle(.roundedBorder)
            TextField(L("api_hash (32 karakter)"), text: $apiHash)
                .textFieldStyle(.roundedBorder)
                .font(.body.monospaced())
            if let error = model.authError {
                Text(error).font(.caption).foregroundStyle(.red)
            }
            HStack {
                Button(L("Çıkış")) { NSApp.terminate(nil) }
                Spacer()
                Button(L("Kaydet ve devam et")) { model.saveCredentials(apiId: apiId, apiHash: apiHash) }
                    .buttonStyle(.borderedProminent)
                    .disabled(apiId.isEmpty || apiHash.isEmpty)
            }
            Spacer()
            Text(L("Anahtar yalnızca bu Mac'in Keychain'inde saklanır."))
                .font(.caption2)
                .foregroundStyle(.secondary)
        }
        .padding(20)
    }
}
