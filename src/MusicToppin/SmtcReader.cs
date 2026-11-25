using Windows.Media.Control;

namespace MusicToppin;

/// <summary>
/// 通道 1：Windows 系统级「正在播放」（SMTC）。
/// 只覆盖实现了 SMTC 的应用：浏览器、Spotify、Apple Music、部分播放器。
/// </summary>
internal sealed class SmtcReader : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager _manager;
    private GlobalSystemMediaTransportControlsSession _session;
    private string _title = "";
    private string _artist = "";
    private bool _propsDirty = true;
    private DateTime _propsRetryAfterUtc = DateTime.MinValue;
    private volatile bool _disposed;

    public bool HasSession
    {
        get { return _session != null; }
    }

    /// <summary>清掉缓存的 manager/session，下一拍重新 RequestAsync（看门狗用）。</summary>
    public void Reset()
    {
        Detach();
        _manager = null;
        _propsDirty = true;
        _propsRetryAfterUtc = DateTime.MinValue;
    }

    public async Task<NowPlaying?> ReadAsync()
    {
        if (_disposed) return null;

        try
        {
            if (_manager == null)
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (_manager == null || _disposed) return null;
            }

            var session = PickSession();
            if (session == null)
            {
                Detach();
                return null;
            }

            if (!ReferenceEquals(session, _session))
            {
                Attach(session);
            }

            // 元数据失败过就退避一会儿，别每 500ms 打一次
            if (_propsDirty && DateTime.UtcNow >= _propsRetryAfterUtc)
            {
                await RefreshPropertiesAsync(session);
                if (_disposed) return null;
            }

            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            var duration = timeline.EndTime - timeline.StartTime;
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

            var position = timeline.Position;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (duration > TimeSpan.Zero && position > duration) position = duration;

            var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            return new NowPlaying(
                true, _title, _artist, position, duration, playing, duration > TimeSpan.Zero,
                new string[NowPlaying.HistorySlots], new string[NowPlaying.HistorySlots], 0, 0);
        }
        catch (Exception ex)
        {
            Logger.Write("smtc read: " + ex.GetType().Name + " " + ex.Message);
            Detach();
            return null;
        }
    }

    /// <summary>优先「当前会话」，其次任意正在播放的会话；单个会话异常不影响其他会话。</summary>
    private GlobalSystemMediaTransportControlsSession PickSession()
    {
        if (_manager == null) return null;

        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
        try { sessions = _manager.GetSessions(); }
        catch { return null; }

        if (sessions == null || sessions.Count == 0) return null;

        GlobalSystemMediaTransportControlsSession current = null;
        try { current = _manager.GetCurrentSession(); }
        catch { }

        if (current != null && IsPlaying(current)) return current;

        // 当前会话没在播（或取不到）时，退而求其次找正在播放的会话
        for (var i = 0; i < sessions.Count; i++)
        {
            var candidate = sessions[i];
            if (candidate == null) continue;
            if (ReferenceEquals(candidate, current)) continue;
            if (IsPlaying(candidate)) return candidate;
        }

        // 都没在播：保持当前会话，否则用第一个（HUD 显示“已暂停”而不是空白）
        if (current != null) return current;
        return sessions[0];
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo().PlaybackStatus ==
                   GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            return false;
        }
    }

    private void Attach(GlobalSystemMediaTransportControlsSession session)
    {
        Detach();
        _session = session;
        _title = "";
        _artist = "";
        _propsDirty = true;
        _propsRetryAfterUtc = DateTime.MinValue;
        try
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        }
        catch { }
    }

    private void Detach()
    {
        var session = _session;
        _session = null;
        if (session == null) return;
        try
        {
            session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        }
        catch { }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        _propsDirty = true;
    }

    private async Task RefreshPropertiesAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (_disposed) return;

            var title = props == null ? "" : (props.Title ?? "").Trim();
            var artist = props == null ? "" : (props.Artist ?? "").Trim();
            if (artist.Length == 0 && props != null) artist = (props.AlbumArtist ?? "").Trim();

            if (title.Length == 0)
            {
                // 没拿到标题（广告/加载/无元数据）：保持脏标记，1.5 秒后重试
                _propsDirty = true;
                _propsRetryAfterUtc = DateTime.UtcNow.AddMilliseconds(1500);
                return;
            }

            // Apple Music 会把「歌手 — 专辑」拼进 Artist 字段（专辑字段却是空的），这里剥掉
            var dash = artist.IndexOf(" — ", StringComparison.Ordinal);
            if (dash > 0)
            {
                var left = artist.Substring(0, dash).Trim();
                var right = artist.Substring(dash + 3).Trim();
                if (left.Length > 0 && right.Length > 0 &&
                    right.StartsWith(title, StringComparison.OrdinalIgnoreCase))
                {
                    artist = left;
                }
            }

            _title = title;
            _artist = artist;
            _propsDirty = false;
            _propsRetryAfterUtc = DateTime.MinValue;
        }
        catch
        {
            _propsDirty = true;
            _propsRetryAfterUtc = DateTime.UtcNow.AddMilliseconds(2000);
        }
    }

    public async Task<bool> SkipNextAsync()
    {
        var session = _session;
        if (session == null) return false;
        try
        {
            var ok = await session.TrySkipNextAsync();
            _propsDirty = true;
            return ok;
        }
        catch { return false; }
    }

    public async Task<bool> SkipPreviousAsync()
    {
        var session = _session;
        if (session == null) return false;
        try
        {
            var ok = await session.TrySkipPreviousAsync();
            _propsDirty = true;
            return ok;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _disposed = true;
        Detach();
    }
}
