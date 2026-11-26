using System;

namespace MusicToppin.Tests
{
    /// <summary>网易云取流（S3）的解析部分：搜索 / 详情 / player url。纯离线。</summary>
    internal static class NeteaseTests
    {
        public static void Run()
        {
            TestRun.Section("网易云解析");

            var searchJson =
                "{\"result\":{\"songs\":[" +
                "{\"id\":1330348068,\"name\":\"起风了\",\"duration\":325868,\"fee\":8," +
                "\"artists\":[{\"name\":\"冯沁苑(买辣椒也用券)\",\"id\":12085562}]," +
                "\"album\":{\"name\":\"起风了\",\"id\":74715426},\"status\":0}," +
                "{\"id\":185809,\"name\":\"彩虹\",\"duration\":263784,\"fee\":0," +
                "\"artists\":[{\"name\":\"周杰伦\",\"id\":6452},{\"name\":\"袁咏琳\"}],\"status\":0}" +
                "]},\"code\":200}";

            var songs = Netease.ParseSearch(searchJson);
            TestRun.Check(songs.Count == 2, "搜索：两条候选");
            TestRun.Check(songs[0].Id == 1330348068 && songs[0].Name == "起风了", "搜索：id/歌名");
            TestRun.Check(songs[0].Artist == "冯沁苑(买辣椒也用券)", "搜索：歌手");
            TestRun.Check(Math.Abs(songs[0].Duration.TotalSeconds - 325.868) < 0.01, "搜索：时长（毫秒→TimeSpan）");
            TestRun.Check(songs[1].Artist == "周杰伦 / 袁咏琳", "搜索：多歌手拼接");

            TestRun.Check(Netease.ParseSearch("{\"result\":{\"songs\":[]}}").Count == 0, "搜索：空结果");
            TestRun.Check(Netease.ParseSearch("{\"code\":-460}").Count == 0, "搜索：错误码");
            TestRun.Check(Netease.ParseSearch(null).Count == 0, "搜索：null");
            TestRun.Check(Netease.ParseSearch("not json").Count == 0, "搜索：坏 JSON");

            var detailJson =
                "{\"songs\":[{\"name\":\"起风了\",\"id\":1330348068,\"duration\":325868,\"fee\":8," +
                "\"artists\":[{\"name\":\"冯沁苑(买辣椒也用券)\"}]}],\"code\":200}";
            var detail = Netease.ParseDetail(detailJson);
            TestRun.Check(detail != null && detail.Id == 1330348068 && detail.Name == "起风了", "详情：单曲");
            TestRun.Check(Netease.ParseDetail("{\"songs\":[]}") == null, "详情：空");

            var urlJson =
                "{\"data\":[{\"id\":1330348068,\"url\":\"http://m801.music.126.net/a.mp3\",\"br\":320000,\"code\":200," +
                "\"type\":\"mp3\",\"size\":13037236}],\"code\":200}";
            TestRun.Check(Netease.ParsePlayerUrl(urlJson) == "http://m801.music.126.net/a.mp3", "取流：320k url");

            var vipJson =
                "{\"data\":[{\"id\":185809,\"url\":null,\"br\":0,\"code\":404,\"size\":0}],\"code\":200}";
            TestRun.Check(Netease.ParsePlayerUrl(vipJson) == null, "取流：无版权 code=404 → null");

            TestRun.Check(Netease.ParsePlayerUrl("{\"data\":[]}") == null, "取流：空 data");
            TestRun.Check(Netease.ParsePlayerUrl(null) == null, "取流：null");

            var playlistJson =
                "{\"playlist\":{\"name\":\"默认歌单\",\"tracks\":[" +
                "{\"id\":1330348068,\"name\":\"起风了\",\"duration\":325868,\"artists\":[{\"name\":\"买辣椒也用券\"}]}," +
                "{\"id\":185809,\"name\":\"彩虹\",\"duration\":263784,\"artists\":[{\"name\":\"周杰伦\"}]}" +
                "]}}";
            var playlist = Netease.ParsePlaylist(playlistJson, "12345");
            TestRun.Check(playlist != null && playlist.Name == "默认歌单", "歌单：名称");
            TestRun.Check(playlist != null && playlist.Tracks.Count == 2, "歌单：曲目数");
            TestRun.Check(playlist != null && playlist.Tracks[0].Title == "起风了" && playlist.Tracks[0].Id == "1330348068", "歌单：曲目/ID");
            TestRun.Check(playlist != null && playlist.Tracks[1].Artist == "周杰伦", "歌单：歌手");
            TestRun.Check(Netease.ParsePlaylist("{\"playlist\":{\"tracks\":[]}}", "1") == null, "歌单：空");
            TestRun.Check(Netease.ParsePlaylist(null, "1") == null, "歌单：null");
        }
    }
}
