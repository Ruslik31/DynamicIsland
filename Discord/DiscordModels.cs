namespace DynamicIsland.Discord;

public sealed record DiscordUser(string Id, string Name, string? Avatar);

public sealed record VoiceChannel(string Id, string Name, string? GuildId, string? GuildName, int Type);

public sealed record VoiceMember(
    string Id, string Name, string? Avatar, bool Self,
    bool SelfMute, bool SelfDeaf, bool Mute, bool Deaf,
    bool Stream, bool Video, bool Speaking,
    double? Volume, bool LocalMute);

public sealed record VoiceState(
    VoiceChannel? Channel, IReadOnlyList<VoiceMember> Members, bool SelfMute, bool SelfDeaf);

public sealed record MessageAuthor(string Id, string Name, string? Avatar, bool Bot);

public sealed record MessageAttachment(string Name, string Type);

/// <summary>Reason: dm, mention, role, everyone или all.</summary>
public sealed record ChatMessage(
    string Id, string Reason, bool Muted,
    string ChannelId, string? GuildId, string ChannelName, string? GuildName, bool IsDM,
    MessageAuthor Author, string Text,
    IReadOnlyList<MessageAttachment> Attachments, int Stickers, int Embeds,
    string? Timestamp, bool WindowFocused, string? CurrentChannelId)
{
    /// <summary>Ссылка, которую открывает Discord и переходит в нужный канал.</summary>
    public string DeepLink => $"discord://-/channels/{GuildId ?? "@me"}/{ChannelId}";
}

public sealed record HelloInfo(string Plugin, string? Discord, IReadOnlyDictionary<string, bool> Caps);
