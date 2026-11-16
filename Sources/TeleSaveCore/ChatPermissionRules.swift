import Foundation

/// Decides from TDLib's `ChatMemberStatus` whether the user may send files to a group or channel.
public enum ChatPermissionRules {
    /// - status: the user's `chatMemberStatus*` object in that chat.
    /// - isChannel: broadcast channel (only creator and admins with `can_post_messages` may post).
    /// - defaultPermission: the chat's default `permissions.can_send_documents` for ordinary members.
    public static func canSendFiles(status: [String: Any]?, isChannel: Bool, defaultPermission: Bool) -> Bool {
        switch status?["@type"] as? String {
        case "chatMemberStatusCreator":
            return true
        case "chatMemberStatusAdministrator":
            guard isChannel else { return true }
            return ((status?["rights"] as? [String: Any])?["can_post_messages"] as? Bool) ?? false
        case "chatMemberStatusMember":
            return !isChannel && defaultPermission
        case "chatMemberStatusRestricted":
            return !isChannel && (((status?["permissions"] as? [String: Any])?["can_send_documents"] as? Bool) ?? false)
        default:
            // Left, banned, or unknown.
            return false
        }
    }
}
