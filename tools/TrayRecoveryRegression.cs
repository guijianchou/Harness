// Copyright (c) Lanstack @openclaw. All rights reserved.
// Platform substitutes exercise the authored recovery and visibility methods.

using Harness.Services;

public static class TrayRecoveryRegression
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) { throw new InvalidOperationException(name); }
            checks++;
            Console.WriteLine("PASS " + name);
        }

        var tray = new TrayIconService();
        var lost = 0;
        tray.AvailabilityLost += () => lost++;
        tray.RestartTaskbar();
        Check(tray.IsAvailable && tray.Calls.SequenceEqual(new uint[] { 0, 4 }), "Explorer restart adds icon and restores callback version");
        Check(lost == 0, "Successful recovery does not restore hidden window");
        tray.Calls.Clear();
        tray.FailAdd = true;
        tray.RestartTaskbar();
        Check(!tray.IsAvailable && lost == 1, "Failed re-add clears availability and signals fallback");
        tray.Calls.Clear();
        tray.FailAdd = false;
        tray.FailVersion = true;
        tray.RestartTaskbar();
        Check(!tray.IsAvailable && tray.Calls.SequenceEqual(new uint[] { 0, 4, 2 }) && lost == 2, "Version failure removes unusable icon and signals fallback");
        tray.Calls.Clear();
        tray.FailVersion = false;
        tray.RestartTaskbar();
        Check(tray.IsAvailable, "Subsequent taskbar recovery can succeed");
        tray.Calls.Clear();
        tray.Disposed = true;
        tray.RestartTaskbar();
        Check(tray.Calls.Count == 0 && lost == 2, "Disposed service ignores recovery broadcasts");
        Check(!tray.EnsureAvailable() && tray.Calls.Count == 0, "Disposed service cannot report an old registration as available");

        var recovered = new TrayIconService();
        recovered.UpdateStatus("RUNNING");
        Check(recovered.IsAvailable && recovered.Calls.SequenceEqual(new uint[] { 1 }), "Status update confirms the existing shell registration");
        recovered.Calls.Clear();
        recovered.FailModify = true;
        recovered.UpdateStatus("COMPLETED");
        Check(recovered.IsAvailable && recovered.Calls.SequenceEqual(new uint[] { 1, 0, 4 }), "Lost registration is re-added when a status update fails");
        Check(recovered.LastTip == "COMPLETED", "Restored tray registration keeps the current status");
        recovered.Calls.Clear();
        recovered.FailAdd = true;
        var failedUpdates = 0;
        recovered.AvailabilityLost += () => failedUpdates++;
        recovered.UpdateStatus("WAIT");
        Check(!recovered.IsAvailable && failedUpdates == 1 && recovered.Calls.SequenceEqual(new uint[] { 1, 0 }),
            "Failed status recovery clears stale availability and announces the missing icon");
        recovered.Calls.Clear();
        recovered.FailModify = false;
        recovered.FailAdd = false;
        Check(recovered.EnsureAvailable() && recovered.Calls.SequenceEqual(new uint[] { 0, 4 }),
            "A later close attempt can recover a previously unavailable registration");
        var reentrant = new TrayIconService { FailModify = true, FailAdd = true };
        var reentrantEvents = 0;
        reentrant.AvailabilityLost += () => { reentrantEvents++; reentrant.UpdateStatus("RESTORING"); };
        Check(!reentrant.EnsureAvailable() && reentrantEvents == 1 && reentrant.Calls.SequenceEqual(new uint[] { 1, 0 }),
            "A synchronous restore status update cannot recursively retry tray registration");

        var window = new Harness.MainWindow();
        window.LoseTray();
        Check(!window.Hidden && window.Shown == 1, "Lost tray restores hidden main window");
        window.Hide();
        Check(!window.Hidden && window.Saved == 0, "Missing tray cannot hide main window");
        window.Hidden = true;
        window.Closing = true;
        window.LoseTray();
        Check(window.Shown == 1, "Closing window is not resurrected by recovery");

        var close = new Harness.MainWindow { Hidden = false };
        Harness.App.Configuration.Settings.SystemTray = true;
        Check(close.RequestClose() && !close.Hidden && close.Saved == 0 && close.DeferredExitChecks == 0,
            "Close-to-tray with no tray service keeps the visible window open instead of exiting");
        Check(close.WarningCount == 1, "A failed close-to-tray attempt explains why the main window remains open");
        var closeTray = new TrayIconService { FailModify = true, FailAdd = true };
        close.AttachTray(closeTray);
        Check(close.RequestClose() && !close.Hidden && close.Saved == 0 && !closeTray.IsAvailable,
            "A stale available flag cannot hide the window when shell recovery fails");
        closeTray.FailModify = false;
        closeTray.FailAdd = false;
        Check(close.RequestClose() && close.Hidden && close.Saved == 1,
            "Close-to-tray hides only after the shell accepts a live registration");
        closeTray.FailModify = true;
        closeTray.FailAdd = true;
        closeTray.UpdateStatus("WAIT");
        Check(!close.Hidden && close.Shown == 1, "A hidden window is restored when its status update detects a lost icon");
        Check(close.WarningCount == 3, "Restoring a hidden window also reports tray unavailability");
        Harness.App.Configuration.Settings.SystemTray = false;
        Check(!close.RequestClose() && close.DeferredExitChecks == 1, "Disabling close-to-tray retains the normal exit path");
        Harness.App.Configuration.Settings.SystemTray = true;
        close.RequestExplicitExit();
        Check(!close.RequestClose() && close.DeferredExitChecks == 2, "Explicit Exit remains available when tray registration is broken");
        var saving = new Harness.MainWindow { DeferExit = true, Hidden = false };
        saving.RequestExplicitExit();
        Check(saving.RequestClose() && saving.DeferredExitChecks == 1 && !saving.Hidden, "Explicit exit still waits for a pending settings save");
        Console.WriteLine($"Tray recovery regression passed: {checks} checks; Windows shell unchanged.");
    }
}

namespace Harness.Services
{
    internal sealed partial class TrayIconService
    {
        private bool _isDisposed;
        private bool _isIconAdded = true;
        private bool _isEnsuringAvailability;
        private readonly uint _taskbarCreatedMessage = 0xC001;
        private readonly IntPtr _messageWindowHandle = new(1);
        private readonly IntPtr _iconHandle = new(2);
        private readonly FakeLogger _logger = new();
        private string _statusText = "WAIT";
        private const uint TrayCallbackMessage = 0x8001;
        private const uint NotifyIconVersion4 = 4;
        public bool IsAvailable => _isIconAdded;
        public bool Disposed { set => _isDisposed = value; }
        public bool FailAdd;
        public bool FailVersion;
        public bool FailModify;
        public string? LastTip;
        public List<uint> Calls { get; } = [];
        public event Action? AvailabilityLost;
        public void RestartTaskbar() => OnWindowMessage(_messageWindowHandle, _taskbarCreatedMessage, IntPtr.Zero, IntPtr.Zero);
        private bool ShellNotifyIcon(uint message, ref NotifyIconData data)
        {
            Calls.Add(message);
            LastTip = data.Tip;
            return !(message == 0 && FailAdd || message == 4 && FailVersion || message == 1 && FailModify);
        }
        private NotifyIconData CreateNotifyIconData(uint flags) => new() { Tip = _statusText };
        private static void HandleTrayCallback(uint message) { }
        private static uint LowWord(IntPtr value) => 0;
        private static IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam) => IntPtr.Zero;
        private struct NotifyIconData { public uint uTimeoutOrVersion; public string? Tip; }
        private static class NotifyIconMessage { public const uint Add = 0, Modify = 1, Delete = 2, SetVersion = 4; }
        private static class NotifyIconFlags { public const uint Message = 1, Icon = 2, Tip = 4; }
    }

    internal sealed class FakeLogger
    {
        public void Warning(string text) { }
        public void Info(string text) { }
    }

    internal static class WindowFrameHelper
    {
        public static bool IsWindowMinimized(Harness.MainWindow window) => false;
        public static void HideWindow(Harness.MainWindow window) { }
    }
}

namespace Harness
{
    internal static class App
    {
        public static FakeLogger Logger { get; } = new();
        public static FakeConfiguration Configuration { get; } = new();
    }
    internal sealed class FakeConfiguration { public FakeSettings Settings { get; } = new(); }
    internal sealed class FakeSettings { public bool SystemTray { get; set; } }
    internal sealed partial class MainWindow
    {
        private bool _isClosing;
        private bool _isWindowHidden = true;
        private TrayIconService? _trayIconService;
        private readonly TrayClosePolicy _trayClosePolicy = new();
        private FakeViewModel ViewModel { get; } = new();
        private FakeDispatcher DispatcherQueue { get; } = new();
        public int Shown;
        public int Saved;
        public int DeferredExitChecks;
        public bool DeferExit;
        public int WarningCount => ViewModel.WarningCount;
        public bool Hidden { get => _isWindowHidden; set => _isWindowHidden = value; }
        public bool Closing { set => _isClosing = value; }
        public void LoseTray() => OnTrayAvailabilityLost();
        public void Hide() => HideMainWindowToTray();
        public void AttachTray(TrayIconService tray)
        {
            _trayIconService = tray;
            tray.AvailabilityLost += OnTrayAvailabilityLost;
        }
        public bool RequestClose()
        {
            var args = new Microsoft.UI.Windowing.AppWindowClosingEventArgs();
            OnAppWindowClosing(new Microsoft.UI.Windowing.AppWindow(), args);
            return args.Cancel;
        }
        public void RequestExplicitExit() => _trayClosePolicy.RequestExit();
        private bool DeferExitUntilSettingsSaved() { DeferredExitChecks++; return DeferExit; }
        private void SaveWindowBounds() => Saved++;
        private void OnWindowHidden() { }
        private void ShowMainWindowFromTray() { _isWindowHidden = false; Shown++; }
        private sealed class FakeDispatcher
        {
            public bool TryEnqueue(Action action) { action(); return true; }
        }
        private sealed class FakeViewModel
        {
            public int WarningCount;
            public void ShowShellWarning(string message) => WarningCount++;
        }
    }
}

namespace Harness.Helpers
{
    internal static class StringResources { public static string Get(string key) => key; }
}

namespace Microsoft.UI.Windowing
{
    internal sealed class AppWindow { }
    internal sealed class AppWindowClosingEventArgs { public bool Cancel { get; set; } }
}
