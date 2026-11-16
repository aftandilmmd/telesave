using System.Globalization;
using System.Text.Json.Nodes;
using TeleSave.Core;

namespace TeleSave.TdLib;

/// <summary>
/// ITelegramClient backed by TDLib's JSON interface. Logs in as the user (MTProto), so it can write to the
/// user's own Saved Messages, which the Bot API cannot. Mirrors the macOS TDLibTelegramClient.
/// </summary>
public sealed class TdLibTelegramClient : ITelegramClient
{
    public sealed record Configuration(
        string DatabaseDirectory, string FilesDirectory, string? LogFile, ApiCredentials Credentials,
        byte[] EncryptionKey, string ApplicationVersion, bool UseTestDc = false);

    private readonly Configuration _config;
    private readonly Action<TelegramEvent> _onEvent;
    private readonly object _gate = new();
    private TdJsonClient? _client;
    private long? _savedMessagesChatId;
    private bool _restartOnClose;
    private ManualResetEventSlim? _closed;

    public TdLibTelegramClient(Configuration configuration, Action<TelegramEvent> onEvent)
    {
        _config = configuration;
        _onEvent = onEvent;
    }

    // MARK: Lifecycle

    public void Start()
    {
        TdJsonClient.Execute(new JsonObject { ["@type"] = "setLogVerbosityLevel", ["new_verbosity_level"] = 1 });
        if (_config.LogFile is { } log)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            TdJsonClient.Execute(new JsonObject
            {
                ["@type"] = "setLogStream",
                ["log_stream"] = new JsonObject { ["@type"] = "logStreamFile", ["path"] = log, ["max_file_size"] = 5 * 1024 * 1024, ["redirect_stderr"] = false }
            });
        }
        MakeClient();
    }

    public void Close()
    {
        TdJsonClient? current;
        var closed = new ManualResetEventSlim();
        lock (_gate)
        {
            _closed = closed;
            _restartOnClose = false;
            current = _client;
        }
        if (current is null) return;
        current.Send(new JsonObject { ["@type"] = "close" });
        closed.Wait(TimeSpan.FromSeconds(3));
    }

    public void Dispose() => Close();

    private void MakeClient()
    {
        var client = new TdJsonClient(HandleUpdate);
        lock (_gate)
        {
            _client = client;
            _savedMessagesChatId = null;
        }
        // TDLib only starts emitting updates after the first request.
        client.Send(new JsonObject { ["@type"] = "getOption", ["name"] = "version" });
    }

    private Task<JsonObject> RequestAsync(JsonObject request)
    {
        TdJsonClient? client;
        lock (_gate) client = _client;
        return client is null ? Task.FromException<JsonObject>(new TelegramError(0, "Telegram client not started")) : client.RequestAsync(request);
    }

    // MARK: Updates

    private void HandleUpdate(JsonObject update)
    {
        switch (update["@type"]?.GetValue<string>())
        {
            case "updateAuthorizationState":
                var state = update["authorization_state"] as JsonObject;
                var type = state?["@type"]?.GetValue<string>();
                if (type == "authorizationStateWaitTdlibParameters") { SendParameters(); return; }
                if (type == "authorizationStateClosed") HandleClosed();
                if (AuthState.Parse(state) is { } mapped) _onEvent(new TelegramEvent.Auth(mapped));
                break;
            case "updateConnectionState":
                if (ConnectionStateParser.Parse(update["state"] as JsonObject) is { } connection) _onEvent(new TelegramEvent.Connection(connection));
                break;
            case "updateFile":
                if (update["file"] is not JsonObject file || Int(file["id"]) is not { } id) return;
                var rawSize = Long(file["size"]) ?? 0;
                var size = rawSize > 0 ? rawSize : Long(file["expected_size"]) ?? 0;
                var uploaded = Long((file["remote"] as JsonObject)?["uploaded_size"]) ?? 0;
                _onEvent(new TelegramEvent.FileProgress(id, uploaded, size));
                break;
            case "updateMessageSendSucceeded":
                if (Long(update["old_message_id"]) is { } oldId && Long((update["message"] as JsonObject)?["id"]) is { } newId)
                    _onEvent(new TelegramEvent.SendSucceeded(oldId, newId));
                break;
            case "updateMessageSendFailed":
                if (Long(update["old_message_id"]) is not { } failedId) return;
                var error = update["error"] as JsonObject;
                _onEvent(new TelegramEvent.SendFailed(failedId, new TelegramError(Int(error?["code"]) ?? 0, error?["message"]?.GetValue<string>() ?? "Send failed")));
                break;
        }
    }

    private void SendParameters()
    {
        Directory.CreateDirectory(_config.DatabaseDirectory);
        Directory.CreateDirectory(_config.FilesDirectory);
        var parameters = new JsonObject
        {
            ["@type"] = "setTdlibParameters",
            ["use_test_dc"] = _config.UseTestDc,
            ["database_directory"] = _config.DatabaseDirectory,
            ["files_directory"] = _config.FilesDirectory,
            ["database_encryption_key"] = Convert.ToBase64String(_config.EncryptionKey),
            // File DB lets TDLib resume partial uploads; message DB stays off so TDLib never re-sends
            // pending messages on its own after a restart — the upload queue owns retries.
            ["use_file_database"] = true,
            ["use_chat_info_database"] = true,
            ["use_message_database"] = false,
            ["use_secret_chats"] = false,
            ["api_id"] = _config.Credentials.ApiId,
            ["api_hash"] = _config.Credentials.ApiHash,
            ["system_language_code"] = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            ["device_model"] = "PC",
            ["system_version"] = Environment.OSVersion.VersionString,
            ["application_version"] = _config.ApplicationVersion
        };
        _ = RequestAsync(parameters).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private void HandleClosed()
    {
        TdJsonClient? old;
        bool restart;
        ManualResetEventSlim? closed;
        lock (_gate)
        {
            old = _client;
            restart = _restartOnClose;
            _restartOnClose = false;
            closed = _closed;
            _closed = null;
            _client = null;
        }
        old?.FailPending(new TelegramError(0, "Telegram client closed"));
        old?.Dispose();
        closed?.Set();
        if (restart) MakeClient();
    }

    // MARK: Auth

    public Task RequestQrCodeAsync() => RequestAsync(new JsonObject { ["@type"] = "requestQrCodeAuthentication", ["other_user_ids"] = new JsonArray() });

    public Task SubmitPhoneNumberAsync(string phoneNumber) => RequestAsync(new JsonObject
    {
        ["@type"] = "setAuthenticationPhoneNumber",
        ["phone_number"] = phoneNumber,
        ["settings"] = new JsonObject { ["@type"] = "phoneNumberAuthenticationSettings" }
    });

    public Task SubmitCodeAsync(string code) => RequestAsync(new JsonObject { ["@type"] = "checkAuthenticationCode", ["code"] = code });

    public Task SubmitPasswordAsync(string password) => RequestAsync(new JsonObject { ["@type"] = "checkAuthenticationPassword", ["password"] = password });

    public Task LogOutAsync()
    {
        lock (_gate) _restartOnClose = true;
        // logOut destroys the local database; TDLib then closes and a fresh instance is started.
        return RequestAsync(new JsonObject { ["@type"] = "logOut" });
    }

    public async Task<TelegramUser> FetchMeAsync()
    {
        var user = await RequestAsync(new JsonObject { ["@type"] = "getMe" }).ConfigureAwait(false);
        var usernames = (user["usernames"] as JsonObject)?["active_usernames"] as JsonArray;
        var photo = ((user["profile_photo"] as JsonObject)?["small"] as JsonObject)?["id"];
        return new TelegramUser(
            Long(user["id"]) ?? 0,
            user["first_name"]?.GetValue<string>() ?? "",
            user["last_name"]?.GetValue<string>() ?? "",
            usernames?.FirstOrDefault()?.GetValue<string>(),
            user["is_premium"]?.GetValue<bool>() ?? false,
            Int(photo));
    }

    // MARK: Sending

    private async Task<long> SavedMessagesChatAsync()
    {
        lock (_gate) if (_savedMessagesChatId is { } cached) return cached;
        var me = await FetchMeAsync().ConfigureAwait(false);
        // Saved Messages is the private chat with yourself.
        var chat = await RequestAsync(new JsonObject { ["@type"] = "createPrivateChat", ["user_id"] = me.Id, ["force"] = false }).ConfigureAwait(false);
        var id = Long(chat["id"]) ?? throw new TelegramError(500, "Saved Messages chat not found");
        lock (_gate) _savedMessagesChatId = id;
        return id;
    }

    private async Task<long> ResolveChatAsync(UploadDestination? destination) =>
        destination?.ChatId ?? await SavedMessagesChatAsync().ConfigureAwait(false);

    private static JsonObject? TopicOf(UploadDestination? destination) =>
        destination?.ForumTopicId is { } topic ? new JsonObject { ["@type"] = "messageTopicForum", ["forum_topic_id"] = topic } : null;

    public async Task<IReadOnlyList<SentMessage>> SendAsync(IReadOnlyList<OutgoingFile> files, UploadDestination? destination)
    {
        if (files.Count == 0) return [];
        var chatId = await ResolveChatAsync(destination).ConfigureAwait(false);
        var request = new JsonObject { ["chat_id"] = chatId };
        if (TopicOf(destination) is { } topic) request["topic_id"] = topic;
        List<JsonObject> messages;
        if (files.Count == 1)
        {
            request["@type"] = "sendMessage";
            request["input_message_content"] = Content(files[0]);
            messages = [await RequestAsync(request).ConfigureAwait(false)];
        }
        else
        {
            request["@type"] = "sendMessageAlbum";
            request["input_message_contents"] = new JsonArray(files.Select(f => (JsonNode)Content(f)).ToArray());
            var result = await RequestAsync(request).ConfigureAwait(false);
            messages = (result["messages"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        }
        if (messages.Count != files.Count) throw new TelegramError(500, $"Unexpected TDLib response: {messages.Count} messages for {files.Count} files");
        return messages.Zip(files).Select(p => new SentMessage(Long(p.First["id"]) ?? 0, FileIdIn(p.First, p.Second.Path))).ToList();
    }

    public async Task CancelAsync(IReadOnlyList<long> messageIds, UploadDestination? destination)
    {
        try { await DeleteMessagesAsync(messageIds, destination).ConfigureAwait(false); }
        catch (Exception) { /* deleting a pending message cancels its upload; best effort */ }
    }

    public async Task DeleteMessagesAsync(IReadOnlyList<long> messageIds, UploadDestination? destination)
    {
        var chatId = await ResolveChatAsync(destination).ConfigureAwait(false);
        await RequestAsync(new JsonObject
        {
            ["@type"] = "deleteMessages", ["chat_id"] = chatId, ["message_ids"] = new JsonArray(messageIds.Select(i => (JsonNode)i).ToArray()), ["revoke"] = true
        }).ConfigureAwait(false);
    }

    /// <summary>TDLib 1.8.5x+ wraps the file in inputDocument / inputPhoto / inputVideo.</summary>
    internal static JsonObject Content(OutgoingFile file)
    {
        var input = new JsonObject { ["@type"] = "inputFileLocal", ["path"] = file.Path };
        var caption = new JsonObject { ["@type"] = "formattedText", ["text"] = file.Caption, ["entities"] = new JsonArray() };
        var dims = file.Dimensions;
        return file.Kind switch
        {
            SendKind.Photo => new JsonObject
            {
                ["@type"] = "inputMessagePhoto",
                ["photo"] = new JsonObject
                {
                    ["@type"] = "inputPhoto", ["photo"] = input, ["added_sticker_file_ids"] = new JsonArray(),
                    ["width"] = dims?.Width ?? 0, ["height"] = dims?.Height ?? 0
                },
                ["caption"] = caption
            },
            SendKind.Video => new JsonObject
            {
                ["@type"] = "inputMessageVideo",
                ["video"] = new JsonObject
                {
                    ["@type"] = "inputVideo", ["video"] = input, ["added_sticker_file_ids"] = new JsonArray(),
                    ["duration"] = dims?.Duration ?? 0, ["width"] = dims?.Width ?? 0, ["height"] = dims?.Height ?? 0, ["supports_streaming"] = true
                },
                ["caption"] = caption
            },
            _ => new JsonObject
            {
                ["@type"] = "inputMessageDocument",
                ["document"] = new JsonObject { ["@type"] = "inputDocument", ["document"] = input, ["disable_content_type_detection"] = true },
                ["caption"] = caption
            }
        };
    }

    /// <summary>Id of the uploading file inside a message, preferring the one with our local path.</summary>
    internal static int? FileIdIn(JsonObject message, string path)
    {
        var files = new List<JsonObject>();
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (obj["@type"]?.GetValue<string>() == "file") files.Add(obj);
                    foreach (var (_, value) in obj) Walk(value);
                    break;
                case JsonArray array:
                    foreach (var item in array) Walk(item);
                    break;
            }
        }
        Walk(message["content"]);
        var match = files.FirstOrDefault(f => (f["local"] as JsonObject)?["path"]?.GetValue<string>() == path) ?? files.FirstOrDefault();
        return Int(match?["id"]);
    }

    // MARK: Chats

    public async Task<IReadOnlyList<ChatSummary>> LoadChatsAsync(int limit)
    {
        var main = new JsonObject { ["@type"] = "chatListMain" };
        // loadChats pulls the list from the server; it answers 404 once everything is loaded.
        try { await RequestAsync(new JsonObject { ["@type"] = "loadChats", ["chat_list"] = main.DeepClone(), ["limit"] = limit }).ConfigureAwait(false); }
        catch (TelegramError e) when (e.Code == 404) { }
        var result = await RequestAsync(new JsonObject { ["@type"] = "getChats", ["chat_list"] = main.DeepClone(), ["limit"] = limit }).ConfigureAwait(false);
        var ids = (result["chat_ids"] as JsonArray)?.Select(n => Long(n) ?? 0).ToList() ?? [];
        var myId = (await FetchMeAsync().ConfigureAwait(false)).Id;
        var chats = new List<ChatSummary>();
        foreach (var id in ids)
        {
            try
            {
                var chat = await RequestAsync(new JsonObject { ["@type"] = "getChat", ["chat_id"] = id }).ConfigureAwait(false);
                if (await SummarizeAsync(chat, myId).ConfigureAwait(false) is { } summary) chats.Add(summary);
            }
            catch (TelegramError) { }
        }
        return chats;
    }

    private async Task<ChatSummary?> SummarizeAsync(JsonObject chat, long myId)
    {
        if (Long(chat["id"]) is not { } id || chat["type"] is not JsonObject type) return null;
        var title = chat["title"]?.GetValue<string>() ?? "";
        switch (type["@type"]?.GetValue<string>())
        {
            case "chatTypePrivate":
                var userId = Long(type["user_id"]) ?? 0;
                if (userId == myId) return new ChatSummary(id, "Saved Messages", ChatKind.SavedMessages, false, null);
                JsonObject? user = null;
                try { user = await RequestAsync(new JsonObject { ["@type"] = "getUser", ["user_id"] = userId }).ConfigureAwait(false); } catch (TelegramError) { }
                var isBot = (user?["type"] as JsonObject)?["@type"]?.GetValue<string>() == "userTypeBot";
                var username = ((user?["usernames"] as JsonObject)?["active_usernames"] as JsonArray)?.FirstOrDefault()?.GetValue<string>();
                return new ChatSummary(id, title, isBot ? ChatKind.Bot : ChatKind.User, false, username);
            case "chatTypeBasicGroup":
                return new ChatSummary(id, title, ChatKind.Group, false, null);
            case "chatTypeSupergroup":
                var isChannel = type["is_channel"]?.GetValue<bool>() ?? false;
                JsonObject? group = null;
                try { group = await RequestAsync(new JsonObject { ["@type"] = "getSupergroup", ["supergroup_id"] = Long(type["supergroup_id"]) ?? 0 }).ConfigureAwait(false); } catch (TelegramError) { }
                var groupName = ((group?["usernames"] as JsonObject)?["active_usernames"] as JsonArray)?.FirstOrDefault()?.GetValue<string>();
                return new ChatSummary(id, title, isChannel ? ChatKind.Channel : ChatKind.Group, group?["is_forum"]?.GetValue<bool>() ?? false, groupName);
            default:
                // Secret chats need separate encryption handling; not offered as destinations.
                return null;
        }
    }

    public async Task<bool> CanSendFilesAsync(long chatId)
    {
        var chat = await RequestAsync(new JsonObject { ["@type"] = "getChat", ["chat_id"] = chatId }).ConfigureAwait(false);
        if (chat["type"] is not JsonObject type) return false;
        var defaults = (chat["permissions"] as JsonObject)?["can_send_documents"]?.GetValue<bool>() ?? false;
        switch (type["@type"]?.GetValue<string>())
        {
            case "chatTypePrivate":
                return true;
            case "chatTypeBasicGroup":
                var basic = await RequestAsync(new JsonObject { ["@type"] = "getBasicGroup", ["basic_group_id"] = Long(type["basic_group_id"]) ?? 0 }).ConfigureAwait(false);
                return ChatPermissionRules.CanSendFiles(basic["status"] as JsonObject, false, defaults);
            case "chatTypeSupergroup":
                var super = await RequestAsync(new JsonObject { ["@type"] = "getSupergroup", ["supergroup_id"] = Long(type["supergroup_id"]) ?? 0 }).ConfigureAwait(false);
                return ChatPermissionRules.CanSendFiles(super["status"] as JsonObject, type["is_channel"]?.GetValue<bool>() ?? false, defaults);
            default:
                return false;
        }
    }

    public async Task<IReadOnlyList<ForumTopicSummary>> ForumTopicsAsync(long chatId)
    {
        var result = await RequestAsync(new JsonObject
        {
            ["@type"] = "getForumTopics", ["chat_id"] = chatId, ["query"] = "",
            ["offset_date"] = 0, ["offset_message_id"] = 0, ["offset_forum_topic_id"] = 0, ["limit"] = 100
        }).ConfigureAwait(false);
        return (result["topics"] as JsonArray)?.OfType<JsonObject>()
            .Select(t => t["info"] as JsonObject)
            .Where(i => i is not null && Int(i["forum_topic_id"]) is not null)
            .Select(i => new ForumTopicSummary(Int(i!["forum_topic_id"])!.Value, i["name"]?.GetValue<string>() ?? "",
                i["is_general"]?.GetValue<bool>() ?? false, i["is_closed"]?.GetValue<bool>() ?? false))
            .ToList() ?? [];
    }

    // MARK: Files

    public async Task<string> DownloadFileAsync(int fileId)
    {
        var file = await RequestAsync(new JsonObject
        {
            ["@type"] = "downloadFile", ["file_id"] = fileId, ["priority"] = 1, ["offset"] = 0, ["limit"] = 0, ["synchronous"] = true
        }).ConfigureAwait(false);
        var path = (file["local"] as JsonObject)?["path"]?.GetValue<string>();
        return string.IsNullOrEmpty(path) ? throw new TelegramError(404, "File not downloaded") : path;
    }

    public async Task<string> DownloadMessageFileAsync(long messageId, UploadDestination? destination)
    {
        var chatId = await ResolveChatAsync(destination).ConfigureAwait(false);
        var message = await RequestAsync(new JsonObject { ["@type"] = "getMessage", ["chat_id"] = chatId, ["message_id"] = messageId }).ConfigureAwait(false);
        var fileId = ChatFileParser.Parse(message)?.FileId ?? FileIdIn(message, "") ?? throw new TelegramError(404, "No file in message");
        return await DownloadFileAsync(fileId).ConfigureAwait(false);
    }

    public async Task<ChatFilePage> SearchChatFilesAsync(UploadDestination? destination, string query, ChatFileFilter filter, ChatFileCursor? cursor, int limit)
    {
        var chatId = await ResolveChatAsync(destination).ConfigureAwait(false);
        var topic = TopicOf(destination);
        return await ChatFileSearch.PageAsync(filter, query, cursor, limit, async (tdFilter, from, pageLimit) =>
        {
            var request = new JsonObject
            {
                ["@type"] = "searchChatMessages",
                ["chat_id"] = chatId,
                // Matching happens locally (file names, accent-insensitive), so the server lists everything.
                ["query"] = "",
                ["from_message_id"] = from,
                ["offset"] = 0,
                ["limit"] = pageLimit,
                ["filter"] = new JsonObject { ["@type"] = tdFilter }
            };
            if (topic is not null) request["topic_id"] = topic.DeepClone();
            var result = await RequestAsync(request).ConfigureAwait(false);
            var files = (result["messages"] as JsonArray)?.OfType<JsonObject>().Select(ChatFileParser.Parse).OfType<ChatFile>().ToList() ?? [];
            return (files, Long(result["next_from_message_id"]) ?? 0);
        }).ConfigureAwait(false);
    }

    // MARK: JSON helpers

    internal static long? Long(JsonNode? node) => ChatFileParser.Int64(node);
    internal static int? Int(JsonNode? node) => Long(node) is { } l ? (int)l : null;
}
