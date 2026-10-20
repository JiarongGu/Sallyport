using System.Runtime.InteropServices;

/// <summary>A shortcut (.lnk) through the shell's own <c>IShellLinkW</c> and <c>IPersistFile</c>: COM the base library
/// reaches without a package (rule 4). Both declared here: the release is trimmed, and with the base library's own
/// <c>ComTypes.IPersistFile</c> the save did nothing and said nothing (measured: setup said it added the shortcuts, and
/// none was written) — this assembly is copied whole.</summary>
static class ShellLink
{
    public static void Create(string path, string target, string arguments, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Made anew, so that it is there afterwards says it was written: a save that does nothing is a failure, not done.
        File.Delete(path);
        var link = (IShellLinkW)new CShellLink();
        try
        {
            link.SetPath(target);
            link.SetArguments(arguments);
            link.SetDescription(description);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
        if (!File.Exists(path)) throw new IOException($"The shortcut {Path.GetFileName(path)} was not written.");
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink;

    // IPersist's GetClassID, then IPersistFile's own, in their vtable's order.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int size, IntPtr findData, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
