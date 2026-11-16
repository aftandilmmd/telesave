using System.Text.Json.Nodes;

namespace TeleSave.Core;

/// <summary>UI-facing view of TDLib's AuthorizationState.</summary>
public abstract record AuthState
{
    public enum CodeDelivery { TelegramApp, Sms, Call, Other }

    public sealed record Initializing : AuthState;
    public sealed record WaitPhoneNumber : AuthState;
    public sealed record WaitQrConfirmation(string Link) : AuthState;
    public sealed record WaitCode(string PhoneNumber, CodeDelivery Delivery) : AuthState;
    public sealed record WaitPassword(string Hint) : AuthState;
    public sealed record WaitRegistration : AuthState;
    public sealed record Ready : AuthState;
    public sealed record LoggingOut : AuthState;
    public sealed record Closing : AuthState;
    public sealed record Closed : AuthState;

    public bool IsReady => this is Ready;

    /// <summary>Maps a TDLib authorizationState* object; null for waitTdlibParameters and unknown types.</summary>
    public static AuthState? Parse(JsonObject? obj)
    {
        switch (obj?["@type"]?.GetValue<string>())
        {
            case "authorizationStateWaitPhoneNumber": return new WaitPhoneNumber();
            case "authorizationStateWaitOtherDeviceConfirmation": return new WaitQrConfirmation(obj["link"]?.GetValue<string>() ?? "");
            case "authorizationStateWaitCode":
                var info = obj["code_info"] as JsonObject;
                var type = (info?["type"] as JsonObject)?["@type"]?.GetValue<string>() ?? "";
                var delivery = type switch
                {
                    "authenticationCodeTypeTelegramMessage" => CodeDelivery.TelegramApp,
                    "authenticationCodeTypeSms" or "authenticationCodeTypeSmsWord" or "authenticationCodeTypeSmsPhrase" => CodeDelivery.Sms,
                    "authenticationCodeTypeCall" or "authenticationCodeTypeFlashCall" or "authenticationCodeTypeMissedCall" => CodeDelivery.Call,
                    _ => CodeDelivery.Other
                };
                return new WaitCode(info?["phone_number"]?.GetValue<string>() ?? "", delivery);
            case "authorizationStateWaitPassword": return new WaitPassword(obj["password_hint"]?.GetValue<string>() ?? "");
            case "authorizationStateWaitRegistration": return new WaitRegistration();
            case "authorizationStateReady": return new Ready();
            case "authorizationStateLoggingOut": return new LoggingOut();
            case "authorizationStateClosing": return new Closing();
            case "authorizationStateClosed": return new Closed();
            default: return null;
        }
    }
}

public static class ConnectionStateParser
{
    public static ConnectionState? Parse(JsonObject? obj) => obj?["@type"]?.GetValue<string>() switch
    {
        "connectionStateWaitingForNetwork" => ConnectionState.WaitingForNetwork,
        "connectionStateConnectingToProxy" => ConnectionState.ConnectingToProxy,
        "connectionStateConnecting" => ConnectionState.Connecting,
        "connectionStateUpdating" => ConnectionState.Updating,
        "connectionStateReady" => ConnectionState.Ready,
        _ => null
    };
}

/// <summary>Human-readable messages for common TDLib auth errors.</summary>
public static class AuthErrorText
{
    public static string Describe(Exception error)
    {
        if (error is not TelegramError tg) return error.Message;
        return tg.Message switch
        {
            "PHONE_NUMBER_INVALID" => L10n.T("Telefon numarası geçersiz. Ülke koduyla girin (ör. +90…)."),
            "PHONE_NUMBER_BANNED" => L10n.T("Bu numara Telegram tarafından engellenmiş."),
            "PHONE_CODE_INVALID" => L10n.T("Kod hatalı."),
            "PHONE_CODE_EXPIRED" => L10n.T("Kodun süresi doldu. Tekrar deneyin."),
            "PASSWORD_HASH_INVALID" => L10n.T("İki adımlı doğrulama şifresi hatalı."),
            "API_ID_INVALID" or "API_ID_PUBLISHED_FLOOD" => L10n.T("API kimliği geçersiz. Ayarlardan api_id / api_hash kontrol edin."),
            _ => tg.Kind is TelegramError.FailureKind.Flood flood
                ? L10n.T("Çok fazla deneme. %@ saniye sonra tekrar deneyin.", flood.Seconds.ToString())
                : tg.Message
        };
    }
}
