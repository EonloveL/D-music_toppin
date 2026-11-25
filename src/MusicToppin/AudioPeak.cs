using System.Runtime.InteropServices;

namespace MusicToppin;

/// <summary>
/// 默认播放设备的电平峰值：用来判断「系统现在到底有没有在出声」。
/// 窗口标题通道读不到播放状态，用它兜底区分「正在播放 / 已暂停」。
/// </summary>
internal static class AudioPeak
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int dwClsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        int OpenPropertyStore(int stgmAccess, out IntPtr properties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        int GetPeakValue(out float peak);
        int GetMeteringChannelCount(out int count);
        int GetChannelsPeakValues(int channelCount, [Out] float[] peaks);
        int QueryHardwareSupport(out int mask);
    }

    private static readonly Guid IID_IAudioMeterInformation = new Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064");

    private static readonly object Gate = new object();
    private static IAudioMeterInformation _meter;
    private static DateTime _meterCreatedUtc = DateTime.MinValue;
    private static int _failures;

    /// <summary>返回默认播放设备的峰值（0~1），失败返回 -1。COM 对象缓存复用，避免每 500ms 重新激活。</summary>
    public static float DevicePeak()
    {
        lock (Gate)
        {
            try
            {
                if (_meter == null || (DateTime.UtcNow - _meterCreatedUtc).TotalSeconds > 30)
                {
                    _meter = CreateMeter();
                    _meterCreatedUtc = DateTime.UtcNow;
                    if (_meter == null) return -1f;
                }

                float peak;
                if (_meter.GetPeakValue(out peak) != 0)
                {
                    _meter = null;
                    return -1f;
                }
                _failures = 0;
                return peak;
            }
            catch
            {
                _meter = null;
                _failures++;
                return -1f;
            }
        }
    }

    private static IAudioMeterInformation CreateMeter()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            IMMDevice device;
            if (enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 0 /*eConsole*/, out device) != 0 || device == null)
                return null;

            var iid = IID_IAudioMeterInformation;
            object meterObject;
            if (device.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out meterObject) != 0 || meterObject == null)
                return null;

            return (IAudioMeterInformation)meterObject;
        }
        catch
        {
            return null;
        }
    }
}
