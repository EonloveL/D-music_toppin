namespace MusicToppin;

/// <summary>
/// 双通道「正在播放」采集：
///   ① SMTC —— 浏览器 / Spotify / Apple Music / 部分播放器，有进度、有播放状态
///   ② 窗口标题 + 按进程音频会话 —— 网易云、QQ音乐等不注册 SMTC 的桌面播放器
///
/// 选源带滞回：已激活的源只要还在就保持，避免在「暂停的 SMTC」和「在放歌的窗口播放器」
/// 之间来回跳，把历史记录和「已播 N 首」写脏。
/// </summary>
internal sealed class NowPlayingService : IDisposable
{
    /// <summary>一首歌至少连续播放这么久，才记为「播完」，避免连点切歌也算播完。</summary>
    private static readonly TimeSpan MinPlayedToCount = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan PollHeartbeatTimeout = TimeSpan.FromSeconds(5);

    private enum SourceKind
    {
        None = 0,
        Smtc = 1,
        Window = 2
    }

    private readonly SmtcReader _smtc = new SmtcReader();
    private readonly System.Threading.Timer _timer;

    private int _busy;
    private DateTime _pollStartedUtc = DateTime.MinValue;
    private volatile bool _disposed;

    private string _lastTitle = "";
    private string _lastArtist = "";
    private DateTime _titleSinceUtc = DateTime.UtcNow;
    private readonly List<string[]> _recent = new List<string[]>();
    private int _playedCount;

    private SourceKind _activeKind = SourceKind.None;
    private SourceKind _candidateKind = SourceKind.None;
    private int _candidateHits;

    public event Action<NowPlaying> Updated;

    /// <summary>最近一次读数来自哪个通道（排错用）。</summary>
    public string LastChannel { get; private set; } = "";

    public NowPlayingService()
    {
        try
        {
            TrackHistory.Load();
            var titles = new string[NowPlaying.HistorySlots];
            var artists = new string[NowPlaying.HistorySlots];
            var count = TrackHistory.Snapshot(titles, artists);
            for (var i = 0; i < count; i++) _recent.Add(new[] { titles[i], artists[i] });
        }
        catch (Exception ex)
        {
            Logger.Write("history load: " + ex.Message);
        }

        _timer = new System.Threading.Timer(_ => Poll(), null, 200, 500);
    }

    private async void Poll()
    {
        if (_disposed) return;

        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            // 上一轮卡住（SMTC 提供方挂起）时强制复位，避免 HUD 永久冻结
            if (_pollStartedUtc != DateTime.MinValue && DateTime.UtcNow - _pollStartedUtc > PollHeartbeatTimeout)
            {
                Logger.Write("poll watchdog: force reset");
                _smtc.Reset();
                Interlocked.Exchange(ref _busy, 0);
            }
            return;
        }

        _pollStartedUtc = DateTime.UtcNow;
        try
        {
            var smtc = await _smtc.ReadAsync();
            if (_disposed) return;

            // SMTC 正在放歌时就不必每拍都去枚举窗口 + 音频会话（那是这条通道最贵的部分）
            var needWindow = smtc == null || !smtc.Value.Playing || _activeKind == SourceKind.Window;
            var window = needWindow ? ReadFromWindow() : null;
            var value = Pick(smtc, window);

            if (value == null)
            {
                Publish(NowPlaying.Empty);
                return;
            }

            UpdateHistory(value.Value.Title, value.Value.Artist);

            var titles = new string[NowPlaying.HistorySlots];
            var artists = new string[NowPlaying.HistorySlots];
            for (var i = 0; i < _recent.Count && i < NowPlaying.HistorySlots; i++)
            {
                titles[i] = _recent[i][0];
                artists[i] = _recent[i][1];
            }

            var np = value.Value;
            np.PrevTitles = titles;
            np.PrevArtists = artists;
            np.PrevCount = Math.Min(_recent.Count, NowPlaying.HistorySlots);
            np.PlayedCount = _playedCount;
            Publish(np);
        }
        catch (Exception ex)
        {
            Logger.Write("poll: " + ex.GetType().Name + " " + ex.Message);
            Publish(NowPlaying.Empty);
        }
        finally
        {
            _pollStartedUtc = DateTime.MinValue;
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>带滞回的选源：当前源仍可用就不换；要换必须连续两拍都指向新源。</summary>
    private NowPlaying? Pick(NowPlaying? smtc, NowPlaying? window)
    {
        var smtcAlive = smtc != null && smtc.Value.Title.Length > 0;
        var windowAlive = window != null && window.Value.Title.Length > 0;

        var smtcPlaying = smtcAlive && smtc.Value.Playing;
        var windowPlaying = windowAlive && window.Value.Playing;

        // 1) 当前源还能用（还活着），优先保持
        if (_activeKind == SourceKind.Smtc && smtcAlive && (smtcPlaying || !windowPlaying))
        {
            LastChannel = "SMTC";
            return smtc;
        }
        if (_activeKind == SourceKind.Window && windowAlive && (windowPlaying || !smtcPlaying))
        {
            LastChannel = "窗口:" + _activePidLabel;
            return window;
        }

        // 2) 需要换源：挑一个候选
        SourceKind candidate;
        if (smtcPlaying) candidate = SourceKind.Smtc;
        else if (windowPlaying) candidate = SourceKind.Window;
        else if (smtcAlive) candidate = SourceKind.Smtc;
        else if (windowAlive) candidate = SourceKind.Window;
        else candidate = SourceKind.None;

        if (candidate == SourceKind.None)
        {
            _activeKind = SourceKind.None;
            _candidateKind = SourceKind.None;
            _candidateHits = 0;
            return null;
        }

        // 当前源彻底消失时才允许立即切换，否则连续两拍确认
        var currentGone =
            (_activeKind == SourceKind.Smtc && !smtcAlive) ||
            (_activeKind == SourceKind.Window && !windowAlive) ||
            _activeKind == SourceKind.None;

        if (candidate == _activeKind)
        {
            _candidateHits = 0;
        }
        else
        {
            if (candidate == _candidateKind) _candidateHits++;
            else { _candidateKind = candidate; _candidateHits = 1; }
        }

        if (candidate != _activeKind && !currentGone && _candidateHits < 2)
        {
            // 还在确认期：保持当前源（若它已不可用就落到候选）
            if (smtcAlive && _activeKind == SourceKind.Smtc) return smtc;
            if (windowAlive && _activeKind == SourceKind.Window) return window;
        }

        _activeKind = candidate;
        _candidateHits = 0;
        _candidateKind = SourceKind.None;

        if (candidate == SourceKind.Smtc)
        {
            LastChannel = "SMTC";
            return smtc;
        }

        LastChannel = "窗口:" + _activePidLabel;
        return window;
    }

    private string _activePidLabel = "";

    private NowPlaying? ReadFromWindow()
    {
        string title, artist, process;
        int pid;
        bool visible;
        if (!WindowPlayers.TryRead(out title, out artist, out process, out pid, out visible)) return null;

        _activePidLabel = process + "/" + pid;

        // 按进程查音频会话：只有它真的在出声才算「正在播放」
        var activity = AudioSessions.GetProcessActivity(pid);
        bool playing;
        if (activity == AudioSessions.Activity.Active) playing = true;
        else if (activity == AudioSessions.Activity.Inactive) playing = false;
        else
        {
            // 查不到就退回端点峰值，但只在有声音时判为播放（静音不误判成播放）
            var peak = AudioPeak.DevicePeak();
            playing = peak > 0.0008f;
        }

        // 躲到托盘的窗口：如果已经不在出声，就别再挂着一张“已暂停”的旧卡片
        if (!visible && !playing) return null;

        return new NowPlaying(true, title, artist, TimeSpan.Zero, TimeSpan.Zero, playing, false,
            new string[NowPlaying.HistorySlots], new string[NowPlaying.HistorySlots], 0, 0);
    }

    private void UpdateHistory(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        if (string.Equals(title, _lastTitle, StringComparison.Ordinal) &&
            string.Equals(artist, _lastArtist, StringComparison.Ordinal)) return;

        if (_lastTitle.Length > 0)
        {
            var played = DateTime.UtcNow - _titleSinceUtc;
            if (played >= MinPlayedToCount)
            {
                var duplicate = _recent.Count > 0 && string.Equals(_recent[0][0], _lastTitle, StringComparison.Ordinal);
                if (duplicate)
                {
                    _recent[0] = new[] { _lastTitle, _lastArtist };
                }
                else
                {
                    _recent.Insert(0, new[] { _lastTitle, _lastArtist });
                }
                while (_recent.Count > NowPlaying.HistorySlots) _recent.RemoveAt(_recent.Count - 1);
                _playedCount++;
                TrackHistory.Push(_lastTitle, _lastArtist);
            }
        }

        _lastTitle = title;
        _lastArtist = artist ?? "";
        _titleSinceUtc = DateTime.UtcNow;
    }

    private void Publish(NowPlaying np)
    {
        var handler = Updated;
        if (handler == null) return;
        try { handler(np); }
        catch (Exception ex) { Logger.Write("publish: " + ex.Message); }
    }

    public async Task<bool> SkipNextAsync()
    {
        if (_activeKind == SourceKind.Smtc)
        {
            var ok = await _smtc.SkipNextAsync();
            if (ok) return true;
        }

        return WindowPlayers.Next();
    }

    public async Task<bool> SkipPreviousAsync()
    {
        if (_activeKind == SourceKind.Smtc)
        {
            var ok = await _smtc.SkipPreviousAsync();
            if (ok) return true;
        }

        return WindowPlayers.Previous();
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        _smtc.Dispose();
    }
}
