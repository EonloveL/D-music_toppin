namespace MusicToppin;

internal enum QueueRejectReason
{
    None = 0,
    Empty,
    Duplicate,
    UserCooldown,
    SongCooldown,
    Full,
    InsufficientLevel
}

internal sealed class QueueResult
{
    public bool Accepted;
    public QueueRejectReason Reason;
    public string Message;
    public QueueItem Item;
    public int Position;
}

/// <summary>队列里的一首歌。</summary>
internal sealed class QueueItem
{
    public string Id = Guid.NewGuid().ToString("N").Substring(0, 8);
    public SongRequest Request;
    public DateTime AddedAtUtc = DateTime.UtcNow;

    // 解析后填充
    public string Title = "";
    public string Artist = "";
    public string ResolvedUrl = "";
    public TimeSpan Duration = TimeSpan.Zero;
    public bool Resolved;

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title)) return Title + (string.IsNullOrWhiteSpace(Artist) ? "" : " - " + Artist);
            return Request != null ? Request.Query : "（未知）";
        }
    }

    public string Requester
    {
        get { return Request != null ? Request.Requester : ""; }
    }
}

/// <summary>
/// 点歌队列核心：去重、用户冷却、歌曲冷却、长度上限。
/// 纯逻辑、无网络无 UI，可独立测试（V2 的 S1 切片）。
/// </summary>
internal sealed class RequestQueue
{
    private readonly List<QueueItem> _items = new List<QueueItem>();
    private readonly Dictionary<string, DateTime> _songSeenUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _userLastUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

    public int MaxSize { get; set; } = 20;
    public TimeSpan UserCooldown { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan SongCooldown { get; set; } = TimeSpan.FromMinutes(5);
    public DateTime? CurrentSinceUtc { get; private set; }

    public event Action Changed;

    public QueueItem Current { get; private set; }

    public int Count
    {
        get { return _items.Count; }
    }

    public IReadOnlyList<QueueItem> Items
    {
        get { return _items; }
    }

    private static string UserKey(SongRequest request)
    {
        if (request == null) return "";
        if (request.RequesterUid > 0) return "u:" + request.RequesterUid;
        return string.IsNullOrEmpty(request.Requester) ? "" : "n:" + request.Requester;
    }

    public QueueResult Enqueue(SongRequest request, bool ignoreLimits = false)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Query))
        {
            return Reject(QueueRejectReason.Empty, "点歌内容为空");
        }

        var now = DateTime.UtcNow;
        var userKey = UserKey(request);

        // 同一用户在冷却时间内只能点一次（按 uid，同名观众不互相影响、改昵称也绕不过）
        if (!ignoreLimits && UserCooldown > TimeSpan.Zero && userKey.Length > 0)
        {
            DateTime last;
            if (_userLastUtc.TryGetValue(userKey, out last) && now - last < UserCooldown)
            {
                var wait = (int)Math.Ceiling((UserCooldown - (now - last)).TotalSeconds);
                return Reject(QueueRejectReason.UserCooldown, request.Requester + " 还需等待 " + wait + " 秒");
            }
        }

        // 同一首歌在冷却时间内不能重复点（含队列里已有的）
        var key = request.DedupeKey;
        if (!ignoreLimits && SongCooldown > TimeSpan.Zero)
        {
            DateTime seen;
            if (_songSeenUtc.TryGetValue(key, out seen) && now - seen < SongCooldown)
            {
                return Reject(QueueRejectReason.SongCooldown, "这首歌刚点过，换一首吧");
            }

            foreach (var existing in _items)
            {
                if (existing.Request != null && string.Equals(existing.Request.DedupeKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    return Reject(QueueRejectReason.Duplicate, "队列里已经有这首歌了");
                }
            }

            // 正在播的这首也要算：解析后 Source 会从 Auto 改成实际平台，
            // 只用 _items 会漏掉「Auto 点歌 → 已解析为网易云」的重复点歌。
            if (Current != null && Current.Request != null &&
                string.Equals(Current.Request.DedupeKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return Reject(QueueRejectReason.Duplicate, "这首歌正在播放");
            }
        }

        if (_items.Count >= MaxSize)
        {
            return Reject(QueueRejectReason.Full, "队列已满（" + MaxSize + " 首）");
        }

        var item = new QueueItem { Request = request };
        _items.Add(item);

        if (!ignoreLimits)
        {
            _songSeenUtc[key] = now;
            if (userKey.Length > 0) _userLastUtc[userKey] = now;
        }

        RaiseChanged();

        return new QueueResult
        {
            Accepted = true,
            Reason = QueueRejectReason.None,
            Message = "已加入队列（第 " + _items.Count + " 位）",
            Item = item,
            Position = _items.Count
        };
    }

    /// <summary>取下一首（不出队）：给解析器预取用。</summary>
    public QueueItem PeekNext()
    {
        return _items.Count > 0 ? _items[0] : null;
    }

    /// <summary>取下一首并出队，成为当前播放项。</summary>
    public QueueItem TakeNext()
    {
        if (_items.Count == 0) return null;

        var item = _items[0];
        _items.RemoveAt(0);
        Current = item;
        CurrentSinceUtc = DateTime.UtcNow;
        RaiseChanged();
        return item;
    }

    /// <summary>播放结束/切歌后清空当前项。</summary>
    public void ClearCurrent()
    {
        Current = null;
        CurrentSinceUtc = null;
        RaiseChanged();
    }

    /// <summary>回退到队列里已经播放过的一首：恢复 Current 用于去重，不重新入队。</summary>
    public void RestoreCurrent(QueueItem item)
    {
        Current = item;
        CurrentSinceUtc = item == null ? (DateTime?)null : DateTime.UtcNow;
        RaiseChanged();
    }

    /// <summary>按队列序号（1 起，和托盘列表一致）移除还没轮到的歌；成功返回被移除的项。</summary>
    public QueueItem RemoveAt(int oneBasedPosition)
    {
        if (oneBasedPosition < 1 || oneBasedPosition > _items.Count) return null;

        var item = _items[oneBasedPosition - 1];
        _items.RemoveAt(oneBasedPosition - 1);
        RaiseChanged();
        return item;
    }

    /// <summary>移除某个观众还没轮到的第一首（按 uid，避免同名观众误删）。</summary>
    public QueueItem RemoveByRequesterUid(long uid)
    {
        if (uid <= 0) return null;

        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].Request != null && _items[i].Request.RequesterUid == uid)
            {
                var item = _items[i];
                _items.RemoveAt(i);
                RaiseChanged();
                return item;
            }
        }
        return null;
    }

    /// <summary>按歌名/关键词找唯一匹配的待播项（不删除）；matches 告诉你命中了几首。</summary>
    public QueueItem FindByKeyword(string keyword, out int matches)
    {
        matches = 0;
        QueueItem found = null;

        if (string.IsNullOrWhiteSpace(keyword)) return null;

        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var name = item.DisplayName ?? "";
            var query = item.Request != null ? item.Request.Query : "";

            if (name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                query.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                matches++;
                if (found == null) found = item;
            }
        }

        return matches == 1 ? found : null;
    }

    /// <summary>按歌名/关键词找唯一匹配的待播项并移除；matches 告诉你命中了几首。</summary>
    public QueueItem RemoveByKeyword(string keyword, out int matches)
    {
        var found = FindByKeyword(keyword, out matches);
        if (found == null) return null;

        _items.Remove(found);
        RaiseChanged();
        return found;
    }

    /// <summary>把自己的点歌撤掉（还没轮到的话）。</summary>
    public bool RemoveByRequester(string requester)
    {
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_items[i].Requester, requester, StringComparison.Ordinal))
            {
                _items.RemoveAt(i);
                RaiseChanged();
                return true;
            }
        }
        return false;
    }

    public bool Remove(string id)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (string.Equals(_items[i].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                _items.RemoveAt(i);
                RaiseChanged();
                return true;
            }
        }
        return false;
    }

    public void Clear()
    {
        _items.Clear();
        RaiseChanged();
    }

    /// <summary>顶歌：把某一首挪到最前面。</summary>
    public bool Promote(string id)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (!string.Equals(_items[i].Id, id, StringComparison.OrdinalIgnoreCase)) continue;
            if (i == 0) return true;
            var item = _items[i];
            _items.RemoveAt(i);
            _items.Insert(0, item);
            RaiseChanged();
            return true;
        }
        return false;
    }

    /// <summary>清理过期的冷却记录，避免字典无限增长（定时调用）。</summary>
    public void TrimCooldowns()
    {
        var now = DateTime.UtcNow;

        var staleSongs = new List<string>();
        foreach (var pair in _songSeenUtc)
        {
            if (now - pair.Value > SongCooldown) staleSongs.Add(pair.Key);
        }
        foreach (var key in staleSongs) _songSeenUtc.Remove(key);

        var staleUsers = new List<string>();
        foreach (var pair in _userLastUtc)
        {
            if (now - pair.Value > UserCooldown) staleUsers.Add(pair.Key);
        }
        foreach (var user in staleUsers) _userLastUtc.Remove(user);
    }

    private QueueResult Reject(QueueRejectReason reason, string message)
    {
        return new QueueResult { Accepted = false, Reason = reason, Message = message };
    }

    private void RaiseChanged()
    {
        var handler = Changed;
        if (handler == null) return;
        try { handler(); }
        catch (Exception ex) { Logger.Write("queue changed: " + ex.Message); }
    }
}
