using System;

namespace MusicToppin.Tests
{
    /// <summary>QQ 音乐（S3）的搜索 / vkey / uin 解析。纯离线。</summary>
    internal static class QQMusicTests
    {
        public static void Run()
        {
            TestRun.Section("QQ 音乐解析");

            var searchJson =
                "{\"code\":0,\"data\":{\"song\":{\"curnum\":2,\"list\":[" +
                "{\"songmid\":\"0004jPDk2eB2dt\",\"songname\":\"起风了 (旧版)\",\"interval\":312," +
                "\"singer\":[{\"name\":\"买辣椒也用券\"}],\"albumname\":\"起风了\"}," +
                "{\"songmid\":\"002w57E00BGzXn\",\"songname\":\"起风了\",\"interval\":311," +
                "\"singer\":[{\"name\":\"周深\"},{\"name\":\"买辣椒也用券\"}]}" +
                "]}}}";

            var songs = QQMusic.ParseSearch(searchJson);
            TestRun.Check(songs.Count == 2, "搜索：两条候选");
            TestRun.Check(songs[0].Mid == "0004jPDk2eB2dt" && songs[0].Name == "起风了 (旧版)", "搜索：mid / 歌名");
            TestRun.Check(songs[0].Artist == "买辣椒也用券", "搜索：歌手");
            TestRun.Check(songs[1].Artist == "周深 / 买辣椒也用券", "搜索：多歌手拼接");
            TestRun.Check(Math.Abs(songs[0].Duration.TotalSeconds - 312) < 0.01, "搜索：时长（秒）");

            TestRun.Check(QQMusic.ParseSearch("{\"code\":0,\"data\":{}}").Count == 0, "搜索：空数据");
            TestRun.Check(QQMusic.ParseSearch(null).Count == 0, "搜索：null");
            TestRun.Check(QQMusic.ParseSearch("坏数据").Count == 0, "搜索：坏 JSON");

            var vkeyJson =
                "{\"code\":0,\"req_0\":{\"code\":0,\"data\":{" +
                "\"sip\":[\"https://isure.stream.qqmusic.qq.com/\"]," +
                "\"midurlinfo\":[{\"songmid\":\"0004jPDk2eB2dt\",\"filename\":\"C400xxx.m4a\"," +
                "\"purl\":\"M500xxx.mp3?vkey=ABC&guid=1\",\"result\":0}]}}}";

            TestRun.Check(
                QQMusic.ParseVkey(vkeyJson) == "https://isure.stream.qqmusic.qq.com/M500xxx.mp3?vkey=ABC&guid=1",
                "取流：sip + purl");

            var noSipJson =
                "{\"code\":0,\"req_0\":{\"code\":0,\"data\":{\"midurlinfo\":[{\"purl\":\"M500x.mp3\"}]}}}";
            TestRun.Check(QQMusic.ParseVkey(noSipJson) == "https://ws.stream.qqmusic.qq.com/M500x.mp3", "取流：默认 CDN 前缀");

            var noPurJson =
                "{\"code\":0,\"req_0\":{\"code\":0,\"data\":{\"sip\":[\"https://isure.stream.qqmusic.qq.com/\"]," +
                "\"midurlinfo\":[{\"purl\":\"\",\"result\":0}]}}}";
            TestRun.Check(QQMusic.ParseVkey(noPurJson) == null, "取流：空 purl → null");

            TestRun.Check(QQMusic.ParseVkey("{\"req_0\":{\"code\":1000}}") == null, "取流：未登录 code=1000 → null");

            TestRun.Check(QQMusic.NormalizeUin("o0123456789") == "0123456789", "uin：去掉 o 前缀");
            TestRun.Check(QQMusic.NormalizeUin("123456") == "123456", "uin：纯数字");
            TestRun.Check(QQMusic.NormalizeUin("o0abc") == "", "uin：非数字返回空");
            TestRun.Check(QQMusic.NormalizeUin("") == "", "uin：空串");

            var playlistJson =
                "{\"cdlist\":[{\"dissname\":\"默认歌单\",\"songlist\":[" +
                "{\"songmid\":\"0004jPDk2eB2dt\",\"songname\":\"起风了\",\"interval\":312,\"singer\":[{\"name\":\"买辣椒也用券\"}]}," +
                "{\"songmid\":\"002w57E00BGzXn\",\"songname\":\"群青\",\"interval\":264,\"singer\":[{\"name\":\"YOASOBI\"}]}" +
                "]}]}";
            var playlist = QQMusic.ParsePlaylist(playlistJson, "12345");
            TestRun.Check(playlist != null && playlist.Name == "默认歌单", "歌单：名称");
            TestRun.Check(playlist != null && playlist.Tracks.Count == 2, "歌单：曲目数");
            TestRun.Check(playlist != null && playlist.Tracks[0].Id == "0004jPDk2eB2dt" && playlist.Tracks[0].Title == "起风了", "歌单：曲目/ID");
            TestRun.Check(playlist != null && playlist.Tracks[1].Artist == "YOASOBI", "歌单：歌手");
            TestRun.Check(QQMusic.ParsePlaylist("{\"cdlist\":[{\"songlist\":[]}]}", "1") == null, "歌单：空");
            TestRun.Check(QQMusic.ParsePlaylist(null, "1") == null, "歌单：null");
        }
    }
}
