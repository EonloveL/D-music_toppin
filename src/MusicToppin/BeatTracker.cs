using System;
using System.Collections.Generic;

namespace MusicToppin;

/// <summary>
/// 从系统输出电平做节拍跟踪：检测瞬态鼓点、估计 BPM。
/// 不抓音频采样、不 OCR；数据来自 AudioPeak 的峰值表，开销很小。
/// </summary>
internal sealed class BeatTracker : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private readonly List<double> _intervals = new List<double>();
    private float _baseline = 0.05f;
    private DateTime _lastOnsetUtc = DateTime.MinValue;
    private DateTime _lastBeatUtc = DateTime.MinValue;
    private double _bpm;
    private int _beatCount;
    private bool _enabled;

    /// <summary>检测到一拍（用于让快照及时带上最新 beatEpoch）。</summary>
    public event Action Beat;

    /// <summary>估计出的 BPM；0 = 还没测出来。</summary>
    public int Bpm
    {
        get { return _bpm > 0 ? (int)Math.Round(_bpm) : 0; }
    }

    /// <summary>最近一次鼓点的时间（UTC 毫秒），前端据此做本地节拍对齐。</summary>
    public long BeatEpochMs
    {
        get { return _lastBeatUtc == DateTime.MinValue ? 0 : new DateTimeOffset(_lastBeatUtc).ToUnixTimeMilliseconds(); }
    }

    public int BeatCount
    {
        get { return _beatCount; }
    }

    public bool Enabled
    {
        get { return _enabled; }
        set
        {
            _enabled = value;
            if (value) _timer.Start();
            else
            {
                _timer.Stop();
                _bpm = 0;
                _intervals.Clear();
            }
        }
    }

    public BeatTracker()
    {
        _timer = new System.Windows.Forms.Timer { Interval = 60 };
        _timer.Tick += (_, _) => Tick();
    }

    public void Reset()
    {
        _intervals.Clear();
        _bpm = 0;
        _lastOnsetUtc = DateTime.MinValue;
        _lastBeatUtc = DateTime.MinValue;
        _baseline = 0.05f;
    }

    private void Tick()
    {
        if (!_enabled) return;

        var peak = AudioPeak.DevicePeak();
        if (peak < 0) return;

        _baseline = _baseline * 0.90f + peak * 0.10f;

        // 瞬态检测：明显高于平均电平才算鼓点；180ms 内不重复计拍
        var threshold = Math.Max(0.05f, _baseline * 1.45f);
        var now = DateTime.UtcNow;
        var sinceLast = (now - _lastOnsetUtc).TotalMilliseconds;
        if (peak < threshold || sinceLast < 180) return;

        if (_lastOnsetUtc != DateTime.MinValue && sinceLast <= 1200)
        {
            _intervals.Add(sinceLast);
            if (_intervals.Count > 8) _intervals.RemoveAt(0);

            var sorted = new List<double>(_intervals);
            sorted.Sort();
            var median = sorted[sorted.Count / 2];
            if (median > 0)
            {
                var candidate = 60000.0 / median;
                // 只接受常见歌曲速度，避免把碎拍/环境声算进去
                if (candidate >= 55 && candidate <= 190)
                    _bpm = _bpm <= 0 ? candidate : _bpm * 0.65 + candidate * 0.35;
            }
        }

        _lastOnsetUtc = now;
        _lastBeatUtc = now;
        _beatCount++;
        var beatHandler = Beat;
        if (beatHandler != null) { try { beatHandler(); } catch { } }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
