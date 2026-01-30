using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WIMISODriverInjector;

/// <summary>
/// Win32 file/folder dialogs - work without package identity.
/// Uses GetOpenFileName/GetSaveFileName and IFileOpenDialog (folder only).
/// </summary>
internal static class NativeFileDialog
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int flagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetOpenFileName(ref OPENFILENAME ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetSaveFileName(ref OPENFILENAME ofn);

    private const int OFN_PATHMUSTEXIST = 0x800;
    private const int OFN_FILEMUSTEXIST = 0x1000;
    private const int MAX_PATH = 260;

    /// <summary>Show open file dialog. filter: "Display|pattern" e.g. "ISO/WIM (*.iso;*.wim)|*.iso;*.wim"</summary>
    public static string? PickOpenFile(IntPtr ownerHwnd, string title, string filter)
    {
        var filterStr = filter.Replace("|", "\0") + "\0\0";
        var file = new StringBuilder(MAX_PATH * 4);
        var ofn = new OPENFILENAME
        {
            lStructSize = Marshal.SizeOf<OPENFILENAME>(),
            hwndOwner = ownerHwnd,
            lpstrFilter = filterStr,
            nFilterIndex = 1,
            lpstrFile = Marshal.AllocCoTaskMem((MAX_PATH * 4 + 1) * 2),
            nMaxFile = MAX_PATH * 4 + 1,
            lpstrTitle = title,
            Flags = OFN_PATHMUSTEXIST | OFN_FILEMUSTEXIST
        };
        try
        {
            if (!GetOpenFileName(ref ofn)) return null;
            return Marshal.PtrToStringUni(ofn.lpstrFile);
        }
        finally
        {
            if (ofn.lpstrFile != IntPtr.Zero)
                Marshal.FreeCoTaskMem(ofn.lpstrFile);
        }
    }

    /// <summary>Show save file dialog.</summary>
    public static string? PickSaveFile(IntPtr ownerHwnd, string title, string defaultFileName, string defaultExt, string filter)
    {
        var filterStr = filter.Replace("|", "\0") + "\0\0";
        var fileBuf = new byte[(MAX_PATH * 4 + 1) * 2];
        var defaultBytes = Encoding.Unicode.GetBytes(defaultFileName + "\0");
        Array.Copy(defaultBytes, fileBuf, Math.Min(defaultBytes.Length, fileBuf.Length - 2));
        var lpstrFile = Marshal.AllocCoTaskMem(fileBuf.Length);
        try
        {
            Marshal.Copy(fileBuf, 0, lpstrFile, fileBuf.Length);
            var ofn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                hwndOwner = ownerHwnd,
                lpstrFilter = filterStr,
                nFilterIndex = 1,
                lpstrFile = lpstrFile,
                nMaxFile = MAX_PATH * 4 + 1,
                lpstrTitle = title,
                lpstrDefExt = defaultExt,
                Flags = OFN_PATHMUSTEXIST
            };
            if (!GetSaveFileName(ref ofn)) return null;
            return Marshal.PtrToStringUni(ofn.lpstrFile);
        }
        finally
        {
            if (lpstrFile != IntPtr.Zero)
                Marshal.FreeCoTaskMem(lpstrFile);
        }
    }

    /// <summary>Show folder picker using IFileOpenDialog with FOS_PICKFOLDERS.</summary>
    public static string? PickFolder(IntPtr ownerHwnd, string title)
    {
        try
        {
            var clsid = new Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
            var type = Type.GetTypeFromCLSID(clsid);
            if (type == null) return null;
            var dialog = Activator.CreateInstance(type);
            if (dialog == null) return null;
            var fd = (IFileDialog)dialog;
            fd.SetOptions(0x20); // FOS_PICKFOLDERS
            fd.SetTitle(title);
            if (fd.Show(ownerHwnd) != 0) return null;
            fd.GetResult(out var item);
            if (item == null) return null;
            item.GetDisplayName(unchecked((int)0x80058000), out var path); // SIGDN_FILESYSPATH
            return path;
        }
        catch
        {
            return null;
        }
    }

    [ComImport]
    [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr hwndOwner);
        [PreserveSig] int SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        [PreserveSig] int SetFileTypeIndex(uint iFileType);
        [PreserveSig] int GetFileTypeIndex(out uint piFileType);
        [PreserveSig] int Advise(IntPtr pfde, out uint pdwCookie);
        [PreserveSig] int Unadvise(uint dwCookie);
        [PreserveSig] int SetOptions(uint fos);
        [PreserveSig] int GetOptions(out uint pfos);
        void SetDefaultFolder(IntPtr psi);
        void SetFolder(IntPtr psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IntPtr psi, int alignment);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(int sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
