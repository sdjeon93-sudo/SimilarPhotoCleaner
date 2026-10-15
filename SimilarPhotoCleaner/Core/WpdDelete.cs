using System.Runtime.InteropServices;

namespace SimilarPhotoCleaner.Core;

/// <summary>
/// 휴대용 장치(WPD) API로 휴대폰 파일을 '객체 ID'로 한꺼번에 지운다.
/// MediaDevices의 DeleteFile(경로)는 파일마다 폴더를 처음부터 훑어 찾느라 사진이 수천 장인 폴더에서 장당 1~2초가 걸린다.
/// 폴더 목록을 한 번 읽어 얻은 ID로 지우면 수백 장도 몇 초면 끝난다.
/// GUID는 Windows 레지스트리(HKCR\CLSID, HKCR\Interface)에서 확인했다.
/// </summary>
internal static class WpdDelete
{
    /// <summary>객체들을 지우고, 객체마다 성공 여부를 돌려준다.</summary>
    public static Dictionary<string, bool> Delete(string pnpDeviceId, IReadOnlyList<string> objectIds)
    {
        var result = objectIds.ToDictionary(id => id, _ => false);
        if (objectIds.Count == 0) return result;

        var device = (IPortableDevice)new PortableDeviceFTM();
        var clientInfo = (IPortableDeviceValues)new PortableDeviceValues();
        device.Open(pnpDeviceId, clientInfo);
        try
        {
            device.Content(out var content);
            var ids = (IPortableDevicePropVariantCollection)new PortableDevicePropVariantCollection();
            foreach (var id in objectIds)
            {
                var pv = new PropVariant { vt = VT_LPWSTR, p = Marshal.StringToCoTaskMemUni(id) };
                try { ids.Add(ref pv); }
                finally { PropVariantClear(ref pv); }
            }

            int hr = content.Delete(0 /* PORTABLE_DEVICE_DELETE_NO_RECURSION */, ids, out var results);
            if (hr == 0)
            {
                foreach (var id in objectIds) result[id] = true;
            }
            else if (results != null)
            {
                // 일부만 지워졌으면 객체마다 HRESULT(VT_ERROR)가 순서대로 들어 있다.
                results.GetCount(out uint count);
                for (uint i = 0; i < count && i < objectIds.Count; i++)
                {
                    var pv = new PropVariant();
                    results.GetAt(i, ref pv);
                    result[objectIds[(int)i]] = pv.vt == VT_ERROR && pv.scode == 0;
                    PropVariantClear(ref pv);
                }
            }
        }
        finally
        {
            device.Close();
        }
        return result;
    }

    private const ushort VT_ERROR = 10;
    private const ushort VT_LPWSTR = 31;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;
        [FieldOffset(8)] public int scode;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [ComImport, Guid("F7C0039A-4762-488A-B4B3-760EF9A1BA9B")]
    private class PortableDeviceFTM { }

    [ComImport, Guid("0C15D503-D017-47CE-9016-7B3F978721CC")]
    private class PortableDeviceValues { }

    [ComImport, Guid("08A99E2F-6D6D-4B80-AF5A-BAF2BCBE4CB9")]
    private class PortableDevicePropVariantCollection { }

    // 메서드를 부르지 않으므로 vtable은 비워 둔다(인터페이스 포인터만 넘긴다).
    [ComImport, Guid("6848F6F2-3155-4F86-B6F5-263EEEAB3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceValues { }

    [ComImport, Guid("625E2DF8-6392-4CF0-9AD1-3CFA5F17775C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDevice
    {
        void Open([MarshalAs(UnmanagedType.LPWStr)] string pnpDeviceId, IPortableDeviceValues clientInfo);
        void SendCommand(uint flags, IntPtr parameters, out IntPtr results);
        void Content(out IPortableDeviceContent content);
        void Capabilities(out IntPtr capabilities);
        void Cancel();
        void Close();
    }

    [ComImport, Guid("6A96ED84-7C73-4480-9938-BF5AF477D426"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceContent
    {
        void EnumObjects(uint flags, [MarshalAs(UnmanagedType.LPWStr)] string parentId, IntPtr filter, out IntPtr enumIds);
        void Properties(out IntPtr properties);
        void Transfer(out IntPtr resources);
        void CreateObjectWithPropertiesOnly(IntPtr values, out IntPtr objectId);
        void CreateObjectWithPropertiesAndData(IntPtr values, out IntPtr data, ref uint optimalWriteBufferSize, out IntPtr cookie);
        [PreserveSig]
        int Delete(uint options, IPortableDevicePropVariantCollection objectIds, out IPortableDevicePropVariantCollection? results);
    }

    [ComImport, Guid("89B2E422-4F1B-4316-BCEF-A44AFEA83EB3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDevicePropVariantCollection
    {
        void GetCount(out uint count);
        void GetAt(uint index, ref PropVariant value);
        void Add(ref PropVariant value);
    }
}
