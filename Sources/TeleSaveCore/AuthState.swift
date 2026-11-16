import Foundation

/// UI-facing view of TDLib's `AuthorizationState`.
public enum AuthState: Equatable, Sendable {
    case initializing
    case waitPhoneNumber
    case waitQRConfirmation(link: String)
    case waitCode(phoneNumber: String, delivery: CodeDelivery)
    case waitPassword(hint: String)
    case waitRegistration
    case ready
    case loggingOut
    case closing
    case closed

    public enum CodeDelivery: Equatable, Sendable {
        case telegramApp
        case sms
        case call
        case other
    }

    public var isReady: Bool { self == .ready }

    /// Maps a TDLib JSON `authorizationState*` object. Returns nil for states the UI never shows
    /// (`waitTdlibParameters`, which the client answers itself) or unknown types.
    public init?(tdObject: [String: Any]) {
        guard let type = tdObject["@type"] as? String else { return nil }
        switch type {
        case "authorizationStateWaitPhoneNumber":
            self = .waitPhoneNumber
        case "authorizationStateWaitOtherDeviceConfirmation":
            self = .waitQRConfirmation(link: tdObject["link"] as? String ?? "")
        case "authorizationStateWaitCode":
            let info = tdObject["code_info"] as? [String: Any] ?? [:]
            let phone = info["phone_number"] as? String ?? ""
            let codeType = (info["type"] as? [String: Any])?["@type"] as? String ?? ""
            let delivery: CodeDelivery
            switch codeType {
            case "authenticationCodeTypeTelegramMessage": delivery = .telegramApp
            case "authenticationCodeTypeSms", "authenticationCodeTypeSmsWord", "authenticationCodeTypeSmsPhrase": delivery = .sms
            case "authenticationCodeTypeCall", "authenticationCodeTypeFlashCall", "authenticationCodeTypeMissedCall": delivery = .call
            default: delivery = .other
            }
            self = .waitCode(phoneNumber: phone, delivery: delivery)
        case "authorizationStateWaitPassword":
            self = .waitPassword(hint: tdObject["password_hint"] as? String ?? "")
        case "authorizationStateWaitRegistration":
            self = .waitRegistration
        case "authorizationStateReady":
            self = .ready
        case "authorizationStateLoggingOut":
            self = .loggingOut
        case "authorizationStateClosing":
            self = .closing
        case "authorizationStateClosed":
            self = .closed
        default:
            return nil
        }
    }
}

extension ConnectionState {
    public init?(tdObject: [String: Any]) {
        switch tdObject["@type"] as? String {
        case "connectionStateWaitingForNetwork": self = .waitingForNetwork
        case "connectionStateConnectingToProxy": self = .connectingToProxy
        case "connectionStateConnecting": self = .connecting
        case "connectionStateUpdating": self = .updating
        case "connectionStateReady": self = .ready
        default: return nil
        }
    }
}

/// Human-readable Turkish messages for common TDLib auth errors.
public enum AuthErrorText {
    public static func describe(_ error: Error) -> String {
        guard let tg = error as? TelegramError else { return error.localizedDescription }
        switch tg.message {
        case "PHONE_NUMBER_INVALID": return L("Telefon numarası geçersiz. Ülke koduyla girin (ör. +90…).")
        case "PHONE_NUMBER_BANNED": return L("Bu numara Telegram tarafından engellenmiş.")
        case "PHONE_CODE_INVALID": return L("Kod hatalı.")
        case "PHONE_CODE_EXPIRED": return L("Kodun süresi doldu. Tekrar deneyin.")
        case "PASSWORD_HASH_INVALID": return L("İki adımlı doğrulama şifresi hatalı.")
        case "API_ID_INVALID", "API_ID_PUBLISHED_FLOOD": return L("API kimliği geçersiz. Ayarlardan api_id / api_hash kontrol edin.")
        default:
            if case .flood(let s) = tg.kind { return L("Çok fazla deneme. %@ saniye sonra tekrar deneyin.", String(s)) }
            return tg.message
        }
    }
}
