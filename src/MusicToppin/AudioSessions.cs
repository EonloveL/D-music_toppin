using System.Runtime.InteropServices;

namespace MusicToppin;

/// <summary>
/// 按进程查询音频会话状态（Core Audio）。
/// 端点级峰值无法区分是哪个进程在出声，多播放器/游戏音效会误判，所以这里用
/// IAudioSessionControl2.GetProcessId 精确匹配窗口播放器的进程。
/// </summary>
internal static class AudioSessions
{
    public enum Activity
    {
        Unknown = 0,
        Active = 1,     // 正在出声
        Inactive = 2    // 有会话但静音/暂停
    }

    private const int DEVICE_STATE_ACTIVE = 1;
    private const int CLSCTX_ALL = 23;

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out int count);
        int Item(int index, out IMMDevice device);
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

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int GetAudioSessionControl(ref Guid audioSessionGuid, int streamFlags, out IntPtr sessionControl);
        int GetSimpleAudioVolume(ref Guid audioSessionGuid, int streamFlags, out IntPtr audioVolume);
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
        int RegisterSessionNotification(IntPtr notification);
        int UnregisterSessionNotification(IntPtr notification);
        int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
        int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int sessionCount);
        int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        int GetState(out int state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
        int GetGroupingParam(out Guid groupingParam);
        int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        int RegisterAudioSessionNotification(IntPtr notification);
        int UnregisterAudioSessionNotification(IntPtr notification);
    }

    /// <summary>IAudioSessionControl2：在 IAudioSessionControl 之后追加 5 个方法。</summary>
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        int GetState(out int state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
        int GetGroupingParam(out Guid groupingParam);
        int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        int RegisterAudioSessionNotification(IntPtr notification);
        int UnregisterAudioSessionNotification(IntPtr notification);
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);
        int GetProcessId(out uint processId);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool optOut);
    }

    private static readonly Guid IID_IAudioSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    // 结果缓存：每 2 秒最多枚举一次，避免 500ms 轮询带来大量 COM 调用
    private static readonly object CacheGate = new object();
    private static int _cachePid = -1;
    private static Activity _cacheValue = Activity.Unknown;
    private static DateTime _cacheUtc = DateTime.MinValue;

    /// <summary>查某个进程是否在出声。返回 Unknown 表示查不出来（不要当成“正在播放”）。</summary>
    public static Activity GetProcessActivity(int pid)
    {
        if (pid <= 0) return Activity.Unknown;

        lock (CacheGate)
        {
            var fresh = (DateTime.UtcNow - _cacheUtc).TotalMilliseconds < 2000;
            if (fresh && _cachePid == pid) return _cacheValue;

            var value = Query(pid);
            _cachePid = pid;
            _cacheValue = value;
            _cacheUtc = DateTime.UtcNow;
            return value;
        }
    }

    private static Activity Query(int pid)
    {
        IMMDeviceCollection devices = null;
        object enumeratorObject = null;
        try
        {
            enumeratorObject = new MMDeviceEnumeratorComObject();
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            if (enumerator.EnumAudioEndpoints(0 /*eRender*/, DEVICE_STATE_ACTIVE, out devices) != 0 || devices == null)
                return Activity.Unknown;

            int deviceCount;
            if (devices.GetCount(out deviceCount) != 0) return Activity.Unknown;

            var found = Activity.Unknown;
            for (var d = 0; d < deviceCount; d++)
            {
                IMMDevice device = null;
                object managerObject = null;
                IAudioSessionEnumerator sessions = null;
                try
                {
                    if (devices.Item(d, out device) != 0 || device == null) continue;

                    var iid = IID_IAudioSessionManager2;
                    if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out managerObject) != 0 || managerObject == null)
                        continue;

                    var manager = (IAudioSessionManager2)managerObject;
                    if (manager.GetSessionEnumerator(out sessions) != 0 || sessions == null) continue;

                    int sessionCount;
                    if (sessions.GetCount(out sessionCount) != 0) continue;

                    for (var i = 0; i < sessionCount; i++)
                    {
                        IAudioSessionControl control = null;
                        try
                        {
                            if (sessions.GetSession(i, out control) != 0 || control == null) continue;

                            IAudioSessionControl2 control2;
                            try { control2 = (IAudioSessionControl2)control; }
                            catch { continue; }

                            uint sessionPid;
                            if (control2.GetProcessId(out sessionPid) != 0) continue;
                            if (sessionPid != (uint)pid) continue;

                            int state;
                            if (control2.GetState(out state) != 0) continue;

                            if (state == 1) return Activity.Active;
                            found = Activity.Inactive;
                        }
                        finally
                        {
                            Release(control);
                        }
                    }
                }
                catch
                {
                    // 单个设备失败不影响其他设备
                }
                finally
                {
                    Release(sessions);
                    Release(managerObject);
                    Release(device);
                }
            }

            return found;
        }
        catch
        {
            return Activity.Unknown;
        }
        finally
        {
            Release(devices);
            Release(enumeratorObject);
        }
    }

    private static void Release(object comObject)
    {
        if (comObject == null) return;
        try
        {
            if (Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
        }
        catch { }
    }
}
