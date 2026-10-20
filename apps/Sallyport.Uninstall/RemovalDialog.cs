using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sallyport.Setup;

namespace Sallyport.Uninstall;

/// <summary>
/// The removal in Windows' own task dialog, by its flat API (the uninstaller is compiled ahead of time, where Windows
/// Forms is not), three pages in one window as setup's removal shows them: what it removes, with whether the profiles
/// and settings go too; each step as it is done; then the end, and what was left. The work runs on the thread pool and
/// the page reads it on the dialog's own timer, so nothing of the window is touched from another thread.
/// </summary>
internal static unsafe partial class RemovalDialog
{
    private const int RemoveButton = 100;
    private const int IdCancel = 2, IdClose = 8;
    // The dialog's own buttons, in the window's language: Windows' common ones are in Windows' own.
    private static (int, string) CancelButton => (IdCancel, L.T("Cancel"));
    private static (int, string) CloseButton => (IdClose, L.T("Close"));
    private const int AllowCancellation = 0x8, ShowProgressBar = 0x200, CallbackTimer = 0x800, SizeToContent = 0x1000000;
    private const uint Created = 0, Navigated = 1, ButtonClicked = 2, Timer = 4, VerificationClicked = 8;
    private const uint NavigatePage = 0x400 + 101, SetProgressPosition = 0x400 + 106, SetElementText = 0x400 + 108, EnableButton = 0x400 + 111;
    private const int MainInstruction = 3;
    private const int OkResult = 0, KeepOpen = 1;
    // TD_WARNING_ICON and TD_ERROR_ICON: MAKEINTRESOURCE(-1) and (-2), a WORD each. Wider, the dialog reads a string's
    // address there (measured: an access violation).
    private static readonly IntPtr WarningIcon = 0xFFFF, ErrorIcon = 0xFFFE;

    // TASKDIALOGCONFIG and TASKDIALOG_BUTTON, packed to 1 as commctrl.h declares them (pshpack1.h).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Config
    {
        public uint Size;
        public IntPtr Parent, Instance;
        public int Flags, CommonButtons;
        public IntPtr Title, MainIcon, MainInstruction, Content;
        public uint ButtonCount;
        public IntPtr Buttons;
        public int DefaultButton;
        public uint RadioCount;
        public IntPtr Radios;
        public int DefaultRadio;
        public IntPtr Verification, Expanded, ExpandedControl, CollapsedControl, FooterIcon, Footer;
        public delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr, int> Callback;
        public IntPtr CallbackData;
        public uint Width;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Button
    {
        public int Id;
        public IntPtr Text;
    }

    [LibraryImport("comctl32.dll")]
    private static partial int TaskDialogIndirect(Config* config, int* button, int* radio, int* verified);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    private static partial IntPtr GetModuleHandle(IntPtr name);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW")]
    private static partial IntPtr LoadImage(IntPtr instance, IntPtr name, uint type, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    // Sallyport's mark on the dialog's title bar and taskbar button: the program's icon group (#32512, the compiler's).
    private static void Mark(IntPtr window)
    {
        const uint SetIcon = 0x80, Icon = 1;
        const int SmallIcon = 0, BigIcon = 1, SmallWidth = 49, BigWidth = 11;
        var module = GetModuleHandle(0);
        var small = LoadImage(module, 32512, Icon, GetSystemMetrics(SmallWidth), GetSystemMetrics(SmallWidth), 0);
        var big = LoadImage(module, 32512, Icon, GetSystemMetrics(BigWidth), GetSystemMetrics(BigWidth), 0);
        if (small != 0) SendMessage(window, SetIcon, SmallIcon, small);
        if (big != 0) SendMessage(window, SetIcon, BigIcon, big);
    }

    // One dialog per process: its state, reached from the callback Windows calls.
    private static Installer? _installer;
    private static bool _removeData;
    private static volatile SetupProgress? _progress;
    private static Task<IReadOnlyList<string>>? _work;
    private static string? _shownStep;
    private static bool _ended;
    private static readonly List<IntPtr> Kept = [];

    /// <summary>Shows the removal; its exit code: 0 removed (or not asked to), 1 failed.</summary>
    public static int Run(string folder)
    {
        _installer = new Installer(progress => _progress = progress, folder);
        var confirm = Page(L.T("Remove Sallyport from this computer"),
            L.T("Turns off every app's VPN, then removes the Sallyport service, its Start menu entry and its files from {0}.", folder),
            AllowCancellation | SizeToContent, [(RemoveButton, L.T("Remove")), CancelButton],
            verification: L.T("Also remove the VPN profiles and settings"),
            expanded: L.T("Sallyport's encrypted copies and its settings, kept in its folder for each Windows user. Left, installing Sallyport in the same folder again finds them. The .ovpn files you imported are not touched."));
        try
        {
            int pressed;
            var result = TaskDialogIndirect(confirm, &pressed, null, null);
            if (result != 0) return 1;
            return _work is { IsFaulted: true } ? 1 : 0;
        }
        finally
        {
            foreach (var kept in Kept) Marshal.FreeHGlobal(kept);
        }
    }

    /// <summary>One page that says why there is nothing to remove, and closes.</summary>
    public static void Tell(string instruction, string content)
    {
        try { TaskDialogIndirect(Page(instruction, content, SizeToContent | AllowCancellation, [CloseButton], icon: ErrorIcon), null, null, null); }
        finally { foreach (var kept in Kept) Marshal.FreeHGlobal(kept); }
    }

    // A page, its strings and itself in memory the dialog can keep until it closes (a navigation keeps the pointer).
    private static Config* Page(string instruction, string? content, int flags, (int Id, string Text)[] buttons,
        string? verification = null, string? expanded = null, IntPtr icon = default)
    {
        var page = (Config*)Keep(sizeof(Config));
        *page = default;
        page->Size = (uint)sizeof(Config);
        page->Flags = flags;
        page->Title = Text(L.T("Remove Sallyport"));
        page->MainIcon = icon;
        page->MainInstruction = Text(instruction);
        page->Content = content is null ? IntPtr.Zero : Text(content);
        page->Verification = verification is null ? IntPtr.Zero : Text(verification);
        page->Expanded = expanded is null ? IntPtr.Zero : Text(expanded);
        // The fold's own words, or Windows' ("See details") in Windows' language.
        if (expanded is not null) page->ExpandedControl = page->CollapsedControl = Text(L.T("What stays"));
        page->Callback = &Callback;
        if (buttons.Length > 0)
        {
            var list = (Button*)Keep(sizeof(Button) * buttons.Length);
            for (var i = 0; i < buttons.Length; i++) list[i] = new Button { Id = buttons[i].Id, Text = Text(buttons[i].Text) };
            page->Buttons = (IntPtr)list;
            page->ButtonCount = (uint)buttons.Length;
            // The safe one is what Enter presses: Cancel before anything is done, Close after.
            page->DefaultButton = buttons[^1].Id;
        }
        return page;
    }

    private static IntPtr Keep(int bytes)
    {
        var memory = Marshal.AllocHGlobal(bytes);
        Kept.Add(memory);
        return memory;
    }

    private static IntPtr Text(string text)
    {
        var memory = Marshal.StringToHGlobalUni(text);
        Kept.Add(memory);
        return memory;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Callback(IntPtr window, uint notification, IntPtr wParam, IntPtr lParam, IntPtr data)
    {
        switch (notification)
        {
            case Created:
                Mark(window);
                return OkResult;
            case VerificationClicked:
                _removeData = wParam != 0;
                return OkResult;
            case ButtonClicked when _work is null && (int)wParam == RemoveButton:
                // Gone ahead: the steps, on a page that cannot be closed until they are done.
                var installer = _installer!;
                var choices = new SetupChoices(Driver: false, RemoveData: _removeData);
                _work = Task.Run(() => installer.UninstallAsync(choices, CancellationToken.None));
                SendMessage(window, NavigatePage, 0, (IntPtr)Page(L.T("Closing Sallyport…"), null, ShowProgressBar | CallbackTimer | SizeToContent, [CloseButton]));
                return KeepOpen;
            case ButtonClicked when _work is { IsCompleted: false }:
                return KeepOpen;
            case Navigated when _work is { IsCompleted: false }:
                SendMessage(window, EnableButton, IdClose, 0);
                return OkResult;
            case Timer when _work is { } work:
                if (_progress is { } progress)
                {
                    SendMessage(window, SetProgressPosition, (IntPtr)(int)Math.Round(progress.Done * 100), 0);
                    if (progress.Text is { } step && step != _shownStep)
                    {
                        _shownStep = step;
                        SendMessage(window, SetElementText, MainInstruction, Text(step));
                    }
                }
                if (work.IsCompleted && !_ended)
                {
                    _ended = true;
                    SendMessage(window, NavigatePage, 0, (IntPtr)End(work));
                }
                return OkResult;
            default:
                return OkResult;
        }
    }

    // The end, as setup's removal says it: removed; removed with something left, and what; or not removed, and why.
    private static Config* End(Task<IReadOnlyList<string>> work)
    {
        if (work.IsFaulted)
            return Page(L.T("Sallyport was not removed"), work.Exception?.GetBaseException().Message, SizeToContent | AllowCancellation, [CloseButton], icon: ErrorIcon);
        var left = work.Result;
        return left.Count == 0
            ? Page(L.T("Sallyport is removed"), null, SizeToContent | AllowCancellation, [CloseButton])
            : Page(L.T("Sallyport is removed, with something left"), string.Join("\n", left), SizeToContent | AllowCancellation, [CloseButton], icon: WarningIcon);
    }
}
