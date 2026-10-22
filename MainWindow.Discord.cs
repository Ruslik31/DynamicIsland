using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicIsland.Discord;
using static DynamicIsland.Motion;

namespace DynamicIsland;

public partial class MainWindow
{
    // Аватарка почти во всю высоту пилюли (34); по 6 с краёв оставляют место значку на правом нижнем краю последней
    const double VoiceAvatarSize = 32, VoiceOverflowWidth = 26;
    const double VoiceWidthPadding = 12;
    const double VoiceRowHeight = 38, VoiceRowAvatar = 32, VoiceMoreHeight = 24;
    const double VoiceBigChrome = 132;
    const int VoiceBigRows = 6, VolumeStepPercent = 5;
    const double MutedDiscAlpha = 0.25, PlainDiscAlpha = 0.18;
    static readonly Regex ChatLink = new(@"^discord://-/channels/(\d+|@me)/\d+$", RegexOptions.Compiled);

    readonly SolidColorBrush _muteDisc = new(Colors.White.WithAlpha(PlainDiscAlpha));
    readonly SolidColorBrush _deafenDisc = new(Colors.White.WithAlpha(PlainDiscAlpha));
    readonly Dictionary<string, Avatar> _compactAvatars = [];
    readonly Dictionary<string, Avatar> _bigAvatars = [];
    readonly Dictionary<string, TextBlock> _rowVolumes = [];
    double _voiceWidth = 64;
    string _compactKey = "";
    int _shownVoiceSeconds = -1;
    Action? _messageOpen;

    bool ShowsVoice => Settings.DiscordVoice && _discord.InCall;

    void PrepareDiscord()
    {
        VoiceMuteDisc.Fill = _muteDisc;
        VoiceDeafenDisc.Fill = _deafenDisc;
        _discord.LinkChanged += OnDiscordLink;
        _discord.VoiceChanged += OnDiscordVoice;
        _discord.SpeakingChanged += OnDiscordSpeaking;
        _discord.MemberMoved += OnDiscordMember;
        _discord.MessageReceived += OnDiscordMessage;
        Application.Current.Exit += (_, _) => _discord.Dispose();
    }

    void StartDiscord()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (args.Contains("--demo")) _discord.StartDemo(args.Contains("--demo-message"));
        else _discord.Start();
        SyncVoice();
        SyncDiscordTexts();
        SyncDiscordSwitches(false);
    }

    // ---------- связь с плагином ----------

    void OnDiscordLink()
    {
        SyncDiscordTexts();
        if (!Settings.DiscordLink) return;
        if (_discord.Linked) Notify(Glyph.Check, _green, "Discord", "Плагин подключён");
        else Notify(Glyph.Cross, _dim, "Discord", "Плагин отключился");
    }

    void SyncDiscordTexts()
    {
        bool linked = _discord.Linked;
        MenuDiscord.Text = _discord.InCall ? "в звонке" : linked ? "подключён" : "ждёт плагин";
        DiscordDot.Fill = linked ? _green : (Brush)FindResource("Faint");
        SyncPluginRow();
        if (!linked)
        {
            DiscordStatus.Text = !_discord.Started ? "Не удалось открыть локальный порт" : PluginInstaller.Check() switch
            {
                PluginInstaller.Stage.NoBetterDiscord => "Нужен BetterDiscord (betterdiscord.app)",
                PluginInstaller.Stage.Missing => "Плагин не установлен",
                _ => "Жду плагин: запустите Discord",
            };
            return;
        }
        string update = _discord.PluginVersion != PluginInstaller.BundledVersion ? " · устарел" : "";
        DiscordStatus.Text = "Подключён · плагин v" + _discord.PluginVersion + update;
    }

    void SyncPluginRow()
    {
        (DiscordPluginLabel.Text, DiscordPluginState.Text) = PluginInstaller.Check() switch
        {
            PluginInstaller.Stage.NoBetterDiscord => ("Плагин BetterDiscord", "нет BetterDiscord"),
            PluginInstaller.Stage.Missing => ("Установить плагин", ""),
            PluginInstaller.Stage.Outdated => ("Обновить плагин", "v" + PluginInstaller.BundledVersion),
            _ => ("Плагин установлен", "v" + PluginInstaller.BundledVersion),
        };
    }

    void DiscordPlugin_Click(object sender, RoutedEventArgs e)
    {
        if (PluginInstaller.Check() == PluginInstaller.Stage.NoBetterDiscord)
        {
            DiscordStatus.Text = "BetterDiscord не найден (betterdiscord.app)";
            return;
        }
        bool done = PluginInstaller.Install();
        SyncPluginRow();
        DiscordStatus.Text = done ? "Плагин на месте. Включите его: Настройки BetterDiscord → Плагины" : "Не удалось записать плагин";
    }

    // ---------- звонок ----------

    void OnDiscordVoice()
    {
        if (!_discord.InCall && _panel == Panel.Voice) _panel = Panel.None;
        SyncVoice();
        SyncDiscordTexts();
        UpdateView();
        UpdateTargets();
    }

    void SyncVoice()
    {
        SyncVoiceCompact();
        SyncVoiceBig();
        _shownVoiceSeconds = -1;
        UpdateVoiceTime();
    }

    void OnDiscordSpeaking(string userId, bool on)
    {
        bool lit = on && Settings.DiscordSpeaking;
        if (_compactAvatars.TryGetValue(userId, out Avatar? small)) small.Speaking = lit;
        if (_bigAvatars.TryGetValue(userId, out Avatar? big)) big.Speaking = lit;
        if (on && !_compactAvatars.ContainsKey(userId)) SyncVoiceCompact();
    }

    void OnDiscordMember(VoiceMember member, bool joined)
    {
        if (!Settings.DiscordMoves) return;
        if (joined) Notify(Glyph.Plus, _green, member.Name, "Зашёл в канал");
        else Notify(Glyph.Minus, _dim, member.Name, "Вышел из канала");
    }

    void SyncVoiceCompact()
    {
        VoiceMember[] members = [.. _discord.Voice.Members];
        VoiceMember? me = members.FirstOrDefault(member => member.Self);
        VoiceMember[] others = [.. members.Where(member => !member.Self)];
        // «Аватарок в острове» считает и мою: остальные места занимают другие участники, моя всегда крайняя справа
        VoiceMember[] shown = ChooseShown(others, Math.Max(Settings.DiscordAvatarCount - (me is null ? 0 : 1), 0));
        int extra = others.Length - shown.Length;
        VoiceMember[] strip = me is null ? shown : [.. shown, me];

        string key = string.Join(',', strip.Select(member => member.Id + "|" + member.Avatar)) + "+" + extra;
        if (key != _compactKey)
        {
            _compactKey = key;
            VoiceAvatars.Children.Clear();
            _compactAvatars.Clear();
            foreach (VoiceMember member in shown) AddCompactAvatar(member);
            if (extra > 0)
            {
                VoiceAvatars.Children.Add(new TextBlock
                {
                    Text = "+" + extra, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = _dim,
                    Width = VoiceOverflowWidth, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                });
            }
            if (me is not null) AddCompactAvatar(me);

            double width = Math.Max(PillShapes[View.Voice].Width, VoiceWidthPadding + strip.Length * VoiceAvatarSize + (extra > 0 ? VoiceOverflowWidth : 0));
            if (width != _voiceWidth)
            {
                _voiceWidth = width;
                if (_view == View.Voice) _morphing = true;
            }
        }

        foreach (VoiceMember member in strip)
        {
            if (!_compactAvatars.TryGetValue(member.Id, out Avatar? avatar)) continue;
            avatar.Speaking = Settings.DiscordSpeaking && _discord.IsSpeaking(member.Id);
            avatar.Status = StatusOf(member);
        }
    }

    void AddCompactAvatar(VoiceMember member)
    {
        var avatar = new Avatar { Width = VoiceAvatarSize, Height = VoiceAvatarSize, Source = member.Avatar, Label = member.Name };
        _compactAvatars[member.Id] = avatar;
        VoiceAvatars.Children.Add(avatar);
    }

    /// <summary>Свой микрофон и наушники берём у клиента: он меняется раньше, чем приходит состояние в канале.</summary>
    AvatarStatus StatusOf(VoiceMember member)
    {
        bool deaf = member.Self ? _discord.Voice.SelfDeaf : member.SelfDeaf || member.Deaf;
        bool muted = member.Self ? _discord.Voice.SelfMute : member.SelfMute || member.Mute;
        return deaf ? AvatarStatus.Deafened : muted ? AvatarStatus.Muted : AvatarStatus.None;
    }

    VoiceMember[] ChooseShown(VoiceMember[] others, int limit)
    {
        List<VoiceMember> shown = [.. others.Take(limit)];
        foreach (VoiceMember talker in others.Skip(limit).Where(member => _discord.IsSpeaking(member.Id)))
        {
            int slot = shown.FindLastIndex(member => !_discord.IsSpeaking(member.Id));
            if (slot < 0) break;
            shown[slot] = talker;
        }
        return [.. shown];
    }

    void SyncVoiceBig()
    {
        VoiceState voice = _discord.Voice;
        VoiceChannel? channel = voice.Channel;
        VoiceChannelName.Text = channel?.Name is { Length: > 0 } name ? name : "Звонок";
        VoiceGuildName.Text = channel?.GuildName ?? "Личный звонок";

        VoiceMembers.Children.Clear();
        _bigAvatars.Clear();
        _rowVolumes.Clear();
        VoiceMember[] ordered = [.. voice.Members.OrderByDescending(member => member.Self).ThenBy(member => member.Name, StringComparer.CurrentCultureIgnoreCase)];
        foreach (VoiceMember member in ordered.Take(VoiceBigRows)) VoiceMembers.Children.Add(CreateMemberRow(member));
        int more = ordered.Length - VoiceBigRows;
        if (more > 0)
        {
            VoiceMembers.Children.Add(new TextBlock
            {
                Text = "и ещё " + more, FontSize = 12, Foreground = _dim, Height = VoiceMoreHeight,
                Margin = new Thickness(10, 4, 0, 0),
            });
        }

        double rows = Math.Min(ordered.Length, VoiceBigRows) * VoiceRowHeight + (more > 0 ? VoiceMoreHeight : 0);
        double height = VoiceBigChrome + Math.Max(rows, VoiceRowHeight);
        if (height != VoiceBigView.Height)
        {
            VoiceBigView.Height = height;
            if (_view == View.VoiceBig) _morphing = true;
        }

        SetDisc(_muteDisc, voice.SelfMute || voice.SelfDeaf);
        SetDisc(_deafenDisc, voice.SelfDeaf);
        VoiceMuteIcon.Kind = voice.SelfMute || voice.SelfDeaf ? VoiceGlyph.MicOff : VoiceGlyph.Mic;
        VoiceMuteIcon.Fill = voice.SelfMute || voice.SelfDeaf ? _red : Brushes.White;
        VoiceDeafenIcon.Kind = voice.SelfDeaf ? VoiceGlyph.HeadsetOff : VoiceGlyph.Headset;
        VoiceDeafenIcon.Fill = voice.SelfDeaf ? _red : Brushes.White;
    }

    static void SetDisc(SolidColorBrush disc, bool active)
    {
        Color to = active ? Color.FromRgb(0xFF, 0x45, 0x3A).WithAlpha(MutedDiscAlpha) : Colors.White.WithAlpha(PlainDiscAlpha);
        disc.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, Ms(220)));
    }

    Grid CreateMemberRow(VoiceMember member)
    {
        var row = new Grid { Height = VoiceRowHeight, Background = Brushes.Transparent, Tag = member };
        var avatar = new Avatar
        {
            Width = VoiceRowAvatar, Height = VoiceRowAvatar, Source = member.Avatar, Label = member.Name,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0), Status = StatusOf(member),
            Speaking = Settings.DiscordSpeaking && _discord.IsSpeaking(member.Id),
        };
        _bigAvatars[member.Id] = avatar;
        row.Children.Add(avatar);

        row.Children.Add(new TextBlock
        {
            Text = member.Self ? member.Name + " (вы)" : member.Name, FontSize = 13.5, FontWeight = FontWeights.Medium,
            Margin = new Thickness(52, 0, 96, 0), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var volume = new TextBlock
        {
            FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 76, 0),
        };
        _rowVolumes[member.Id] = volume;
        row.Children.Add(volume);

        var status = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
        };
        if (member.Stream) status.Children.Add(Chip("LIVE", _red));
        if (member.Video) status.Children.Add(StatusIcon(VoiceGlyph.Screen, _dim));
        row.Children.Add(status);
        return row;
    }

    static VoiceIcon StatusIcon(VoiceGlyph kind, Brush fill) =>
        new() { Kind = kind, Fill = fill, Width = 16, Height = 16, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    static Border Chip(string text, Brush tint) => new()
    {
        Background = tint, CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Brushes.Black },
    };

    void UpdateVoiceTime()
    {
        if (!_discord.InCall || !VoiceBigView.IsVisible) return;
        int seconds = (int)(DateTime.Now - _discord.JoinedAt).TotalSeconds;
        if (seconds == _shownVoiceSeconds) return;
        _shownVoiceSeconds = seconds;
        VoiceTime.Text = DescribeElapsed(TimeSpan.FromSeconds(Math.Max(seconds, 0)));
    }

    bool AdjustMemberVolume(int step)
    {
        foreach (UIElement child in VoiceMembers.Children)
        {
            if (child is not Grid { Tag: VoiceMember member } row || !row.IsMouseOver || member.Self) continue;

            int now = (int)Math.Round((member.Volume ?? 100) / VolumeStepPercent) * VolumeStepPercent;
            int next = Math.Clamp(now + step * VolumeStepPercent, 0, 200);
            _discord.SetUserVolume(member.Id, next);
            if (_rowVolumes.TryGetValue(member.Id, out TextBlock? label))
            {
                label.Text = next + "%";
                var show = new DoubleAnimationUsingKeyFrames { Duration = Ms(1500) };
                show.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100))));
                show.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100))));
                show.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1500))));
                label.BeginAnimation(OpacityProperty, show);
            }
            return true;
        }
        return false;
    }

    void VoiceMute_Click(object sender, RoutedEventArgs e) => _discord.ToggleMute();

    void VoiceDeafen_Click(object sender, RoutedEventArgs e) => _discord.ToggleDeafen();

    void VoiceLeave_Click(object sender, RoutedEventArgs e)
    {
        _discord.Leave();
        SelectPanel(Panel.None);
        UpdateView();
    }

    // ---------- сообщения ----------

    void OnDiscordMessage(ChatMessage message)
    {
        if (!ShouldShow(message)) return;

        MessageAvatar.Source = message.Author.Avatar;
        MessageAvatar.Label = message.Author.Name;
        MessageAuthor.Text = message.Author.Name;
        MessageWhere.Text = Where(message);
        MessageText.Text = Settings.DiscordPreview ? Describe(message) : "Новое сообщение";
        _messageOpen = () => OpenChat(message.DeepLink);

        bool shown = _view == View.Message;
        ShowTransient(View.Message, Settings.DiscordMessageSeconds);
        if (shown) FadeIn(MessageView);
    }

    bool ShouldShow(ChatMessage message)
    {
        if (_doNotDisturb == true) return false;
        bool wanted = message.Reason switch
        {
            "dm" => Settings.DiscordDm,
            "mention" or "role" => Settings.DiscordMention,
            "everyone" => Settings.DiscordEveryone,
            _ => true,
        };
        if (!wanted) return false;
        return !(Settings.DiscordQuietFocused && message.WindowFocused && message.CurrentChannelId == message.ChannelId);
    }

    static string Where(ChatMessage message)
    {
        if (message.IsDM) return "";
        string channel = message.ChannelName.Length > 0 ? "#" + message.ChannelName : "";
        return message.GuildName is { Length: > 0 } guild ? (channel.Length > 0 ? channel + " · " : "") + guild : channel;
    }

    static string Describe(ChatMessage message)
    {
        if (message.Text.Trim().Length > 0) return message.Text.Trim();
        if (message.Attachments.Count > 0)
        {
            string first = message.Attachments[0].Name;
            return message.Attachments.Count > 1 ? $"Вложения: {message.Attachments.Count}" : "Вложение: " + first;
        }
        if (message.Stickers > 0) return "Стикер";
        return message.Embeds > 0 ? "Ссылка" : "Сообщение";
    }

    static void OpenChat(string link)
    {
        if (!ChatLink.IsMatch(link)) return;
        try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { App.Log(ex); }
    }

    // ---------- меню и настройки ----------

    void DiscordRow_Click(object sender, RoutedEventArgs e)
    {
        SyncDiscordTexts();
        ShowPanel(Panel.Discord);
    }

    void DiscordBack_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.Menu);

    void DiscordVoiceRow_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.DiscordVoice);

    void DiscordVoiceBack_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.Discord);

    void DiscordMessagesRow_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.DiscordMessages);

    void DiscordMessagesBack_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.Discord);

    void SyncDiscordSwitches(bool animate)
    {
        DiscordLinkSwitch.Set(Settings.DiscordLink, animate);
        DiscordVoiceSwitch.Set(Settings.DiscordVoice, animate);
        DiscordSpeakingSwitch.Set(Settings.DiscordSpeaking, animate);
        DiscordMovesSwitch.Set(Settings.DiscordMoves, animate);
        DiscordInGamesSwitch.Set(Settings.DiscordInGames, animate);
        DiscordAvatarsSegments.Set(Settings.DiscordAvatars, animate);
        DiscordDmSwitch.Set(Settings.DiscordDm, animate);
        DiscordMentionSwitch.Set(Settings.DiscordMention, animate);
        DiscordEveryoneSwitch.Set(Settings.DiscordEveryone, animate);
        DiscordPreviewSwitch.Set(Settings.DiscordPreview, animate);
        DiscordQuietSwitch.Set(Settings.DiscordQuietFocused, animate);
        DiscordMessageTimeSegments.Set(Settings.DiscordMessageTime, animate);
    }

    void DiscordLink_Click(object sender, RoutedEventArgs e)
    {
        Settings.DiscordLink = !Settings.DiscordLink;
        UpdateSwitches(true);
    }

    void DiscordVoice_Click(object sender, RoutedEventArgs e)
    {
        Settings.DiscordVoice = !Settings.DiscordVoice;
        UpdateSwitches(true);
        UpdateView();
    }

    void DiscordSpeaking_Click(object sender, RoutedEventArgs e)
    {
        Settings.DiscordSpeaking = !Settings.DiscordSpeaking;
        UpdateSwitches(true);
        SyncVoice();
    }

    void DiscordMoves_Click(object sender, RoutedEventArgs e)
    {
        Settings.DiscordMoves = !Settings.DiscordMoves;
        UpdateSwitches(true);
    }

    void DiscordInGames_Click(object sender, RoutedEventArgs e)
    {
        Settings.DiscordInGames = !Settings.DiscordInGames;
        UpdateSwitches(true);
        CheckFullscreen();
    }

    void DiscordAvatars_Click(object sender, RoutedEventArgs e)
    {
        Settings.DiscordAvatars = DiscordAvatarsSegments.PickUnderPointer();
        UpdateSwitches(true);
        SyncVoiceCompact();
        UpdateTargets();
    }

    void DiscordDm_Click(object sender, RoutedEventArgs e)
    {
        Settings.DiscordDm = !Settings.DiscordDm;
        UpdateSwitches(true);
    }

    void DiscordMention_Click(object sender, RoutedEventArgs e)
    {
        Settings.DiscordMention = !Settings.DiscordMention;
        UpdateSwitches(true);
    }

    void DiscordEveryone_Click(object sender, RoutedEventArgs e)
    {
        Settings.DiscordEveryone = !Settings.DiscordEveryone;
        UpdateSwitches(true);
    }

    void DiscordPreview_Click(object sender, RoutedEventArgs e)
    {
        Settings.DiscordPreview = !Settings.DiscordPreview;
        UpdateSwitches(true);
    }

    void DiscordQuiet_Click(object sender, RoutedEventArgs e)
    {
        Settings.DiscordQuietFocused = !Settings.DiscordQuietFocused;
        UpdateSwitches(true);
    }

    void DiscordMessageTime_Click(object sender, RoutedEventArgs e)
    {
        Settings.DiscordMessageTime = DiscordMessageTimeSegments.PickUnderPointer();
        UpdateSwitches(true);
    }

    void Music_Click(object sender, RoutedEventArgs e)
    {
        Settings.Music = !Settings.Music;
        UpdateSwitches(true);
        TrackLyrics();
        SyncRim();
        UpdateView();
    }
}
