namespace MusicToppin;

/// <summary>解析出来的可播放条目。各平台 resolver 的统一产物。</summary>
internal sealed class ResolvedSong
{
    public string Title = "";
    public string Artist = "";
    public string AudioUrl = "";
    public string BackupUrl = "";
    public TimeSpan Duration = TimeSpan.Zero;
    public string SourceLabel = "B站";
    public string PageUrl = "";
}
