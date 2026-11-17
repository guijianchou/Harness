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

        var window = new Harness.MainWindow();
        window.LoseTray();
        Check(!window.Hidden && window.Shown == 1, "Lost tray restores hidden main window");
        window.Hide();
        Check(!window.Hidden && window.Saved == 0, "Missing tray cannot hide main window");
        window.Hidden = true;
        window.Closing = true;
        window.LoseTray();
        Check(window.Shown == 1, "Closing window is not resurrected by recovery");
        Console.WriteLine($"Tray recovery regression passed: {checks} checks; Windows shell unchanged.");
    }
}

namespace Harness.Services
{
    internal sealed partial class TrayIconService
    {
        private bool _isDisposed;
        private bool _isIconAdded = true;
        private readonly uint _taskbarCreatedMessage = 0xC001;
        private readonly IntPtr _messageWindowHandle = new(1);
        private readonly IntPtr _iconHandle = new(2);
        private readonly FakeLogger _logger = new();
        private const uint TrayCallbackMessage = 0x8001;
        private const uint NotifyIconVersion4 = 4;
        public bool IsAvailable => _isIconAdded;
        public bool Disposed { set => _isDisposed = value; }
        public bool FailAdd;
        public bool FailVersion;
        public List<uint> Calls { get; } = [];
        public event Action? AvailabilityLost;
        public void RestartTaskbar() => OnWindowMessage(_messageWindowHandle, _taskbarCreatedMessage, IntPtr.Zero, IntPtr.Zero);
        private bool ShellNotifyIcon(uint message, ref NotifyIconData data)
        {
            Calls.Add(message);
            return !(message == 0 && FailAdd || message == 4 && FailVersion);
        }
        private static NotifyIconData CreateNotifyIconData(uint flags) => new();
        private static void HandleTrayCallback(uint message) { }
        private static uint LowWord(IntPtr value) => 0;
        private static IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam) => IntPtr.Zero;
        private struct NotifyIconData { public uint uTimeoutOrVersion; }
        private static class NotifyIconMessage { public const uint Add = 0, Delete = 2, SetVersion = 4; }
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
    internal static class App { public static FakeLogger Logger { get; } = new(); }
    internal sealed partial class MainWindow
    {
        private bool _isClosing;
        private bool _isWindowHidden = true;
        private TrayIconService? _trayIconService;
        private FakeDispatcher DispatcherQueue { get; } = new();
        public int Shown;
        public int Saved;
        public bool Hidden { get => _isWindowHidden; set => _isWindowHidden = value; }
        public bool Closing { set => _isClosing = value; }
        public void LoseTray() => OnTrayAvailabilityLost();
        public void Hide() => HideMainWindowToTray();
        private void SaveWindowBounds() => Saved++;
        private void OnWindowHidden() { }
        private void ShowMainWindowFromTray() { _isWindowHidden = false; Shown++; }
        private sealed class FakeDispatcher
        {
            public bool TryEnqueue(Action action) { action(); return true; }
        }
    }
}
