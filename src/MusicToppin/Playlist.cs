namespace MusicToppin;

/// <summary>默认歌单来源：二选一，互不混用。</summary>
internal enum PlaylistSource
{
    None = 0,
    Netease = 1,
    QQMusic = 2
}

/// <summary>默认歌单里的一首（只存元信息，播放时再取直链）。</summary>
internal sealed class PlaylistTrack
{
    public PlaylistSource Source;
    public string Id = "";
    public string Title = "";
    public string Artist = "";
    public TimeSpan Duration = TimeSpan.Zero;
    public string DisplayName
    {
        get { return Title + (string.IsNullOrWhiteSpace(Artist) ? "" : " - " + Artist); }
    }
}

internal sealed class PlaylistData
{
    public PlaylistSource Source;
    public string Id = "";
    public string Name = "";
    public List<PlaylistTrack> Tracks = new List<PlaylistTrack>();
}

/// <summary>默认歌单：按平台取歌单列表，按需取每首直链；失败只影响单首。</summary>
internal static class DefaultPlaylist
{
    public static PlaylistData Load(PlaylistSource source, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        switch (source)
        {
            case PlaylistSource.Netease: return Netease.LoadPlaylist(id);
            case PlaylistSource.QQMusic: return QQMusic.LoadPlaylist(id);
            default: return null;
        }
    }

    public static ResolvedSong Resolve(PlaylistTrack track)
    {
        if (track == null || string.IsNullOrWhiteSpace(track.Id)) return null;

        switch (track.Source)
        {
            case PlaylistSource.Netease:
                long neteaseId;
                if (!long.TryParse(track.Id, out neteaseId)) return null;
                return Netease.ResolveById(neteaseId, track.Title, track.Artist, track.Duration);

            case PlaylistSource.QQMusic:
                return QQMusic.ResolveByMid(track.Id, track.Title, track.Artist, track.Duration);

            default:
                return null;
        }
    }
}
