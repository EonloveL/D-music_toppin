namespace MusicToppin;

/// <summary>当前正在播放的曲目快照 + 已播完的最近几首（用于下方灰卡堆叠）。</summary>
internal struct NowPlaying
{
    public const int HistorySlots = 3;

    public bool HasSession;
    public string Title;
    public string Artist;
    public TimeSpan Position;
    public TimeSpan Duration;
    public bool Playing;

    /// <summary>是否知道进度/时长。窗口标题通道读不到时长，为 false。</summary>
    public bool HasTimeline;

    /// <summary>已播完的曲目，索引 0 = 最近一首。</summary>
    public string[] PrevTitles;
    public string[] PrevArtists;
    public int PrevCount;

    public int PlayedCount;

    /// <summary>队列里的下一首（只有点歌播放时有值）。</summary>
    public string NextTitle;
    public string NextArtist;

    /// <summary>队列里还排着几首（含下一首）；0 = 没有队列。</summary>
    public int QueueCount;

    /// <summary>本应用回退到上一首时为 true，环状动画反向滚动。</summary>
    public bool RingReverse;

    public NowPlaying(bool hasSession, string title, string artist, TimeSpan position, TimeSpan duration,
        bool playing, bool hasTimeline, string[] prevTitles, string[] prevArtists, int prevCount, int playedCount)
    {
        HasSession = hasSession;
        Title = title;
        Artist = artist;
        Position = position;
        Duration = duration;
        Playing = playing;
        HasTimeline = hasTimeline;
        PrevTitles = prevTitles ?? new string[HistorySlots];
        PrevArtists = prevArtists ?? new string[HistorySlots];
        PrevCount = prevCount;
        PlayedCount = playedCount;
    }

    public static NowPlaying Empty
    {
        get
        {
            return new NowPlaying(false, "", "", TimeSpan.Zero, TimeSpan.Zero, false, false,
                new string[HistorySlots], new string[HistorySlots], 0, 0);
        }
    }
}
