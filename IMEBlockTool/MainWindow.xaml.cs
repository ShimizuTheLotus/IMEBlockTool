using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace IMEBlockTool
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private readonly string _configPath;
        private readonly HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _cts;
        private IntPtr _englishHkl = IntPtr.Zero;
        // single-level saved layout (the layout that was active before switching into a blocked app)
        private uint? _savedThreadId = null;
        private IntPtr _savedHwnd = IntPtr.Zero;
        private IntPtr _savedHkl = IntPtr.Zero;
        private bool? _savedImeOpen = null;

        // UI elements are defined in generated partial class from XAML


        private const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint Flags);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("imm32.dll")]
        private static extern IntPtr ImmGetContext(IntPtr hWnd);

        [DllImport("imm32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ImmGetOpenStatus(IntPtr hIMC);

        [DllImport("imm32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ImmSetOpenStatus(IntPtr hIMC, bool fOpen);

        [DllImport("imm32.dll")]
        private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

        public MainWindow()
        {
            this.InitializeComponent();



            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "blocked.txt");
            LoadConfig();
        }

        private void LoadConfig()
        {
            if (File.Exists(_configPath))
            {
                try
                {
                    var lines = File.ReadAllLines(_configPath).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim());
                    foreach (var l in lines)
                    {
                        var n = NormalizeName(l);
                        if (_blocked.Add(n))
                            ListBlocked.Items.Add(n);
                    }
                }
                catch { }
            }
        }

        private void SaveConfig()
        {
            try
            {
                File.WriteAllLines(_configPath, _blocked);
            }
            catch { }
        }

        private static string NormalizeName(string name)
        {
            name = name.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return name;
            return name + ".exe";
        }

        private void ToggleBlock_Checked(object sender, RoutedEventArgs e)
        {
            ToggleBlock.Content = "On";
            StartWatcher();
        }

        private void ToggleBlock_Unchecked(object sender, RoutedEventArgs e)
        {
            ToggleBlock.Content = "Off";
            StopWatcher();
        }

        private void StartWatcher()
        {
            if (_cts != null)
                return;
            _cts = new CancellationTokenSource();
            // Load English (US) layout handle
            try { _englishHkl = LoadKeyboardLayout("00000409", 0); } catch { _englishHkl = IntPtr.Zero; }
            Task.Run(() => WatchForeground(_cts.Token));
        }

        private void StopWatcher()
        {
            try
            {
                _cts?.Cancel();
            }
            catch { }
            _cts = null;
        }

        private async Task WatchForeground(CancellationToken token)
        {
            IntPtr prevHwnd = IntPtr.Zero;
            uint prevThreadId = 0;
            bool prevWasBlocked = false;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var hwnd = GetForegroundWindow();
                    if (hwnd != IntPtr.Zero && hwnd != prevHwnd)
                    {
                        var threadId = GetWindowThreadProcessId(hwnd, out uint processId);
                        bool isBlocked = false;

                        try
                        {
                            var proc = Process.GetProcessById((int)processId);
                            var exe = proc.ProcessName + ".exe";
                            isBlocked = _blocked.Contains(exe);
                        }
                        catch { }

                        if (isBlocked)
                        {
                            // Save the layout of the application we came from (so we can restore it when leaving the blocked app)
                            try
                            {
                                if (prevHwnd != IntPtr.Zero && prevThreadId != 0 && _savedThreadId == null)
                                {
                                    var cur = GetKeyboardLayout(prevThreadId);
                                    if (cur != IntPtr.Zero)
                                    {
                                        _savedThreadId = prevThreadId;
                                        _savedHwnd = prevHwnd;
                                        _savedHkl = cur;
                                        // also save IME open/closed status for the previous window
                                        try
                                        {
                                            var hImc = ImmGetContext(prevHwnd);
                                            if (hImc != IntPtr.Zero)
                                            {
                                                _savedImeOpen = ImmGetOpenStatus(hImc);
                                                ImmReleaseContext(prevHwnd, hImc);
                                            }
                                        }
                                        catch { _savedImeOpen = null; }
                                    }
                                }
                            }
                            catch { }

                            // switch blocked app to English
                            if (_englishHkl == IntPtr.Zero)
                                _englishHkl = LoadKeyboardLayout("00000409", 0);
                            if (_englishHkl != IntPtr.Zero)
                                SendMessage(hwnd, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, _englishHkl);
                        }

                        // If we left a blocked window, restore previously saved layout (if any)
                        if (!isBlocked && prevWasBlocked)
                        {
                            try
                            {
                                if (_savedThreadId != null && _savedHwnd != IntPtr.Zero && _savedHkl != IntPtr.Zero)
                                {
                                    SendMessage(_savedHwnd, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, _savedHkl);
                                    // restore IME open status if we saved it and the window is still valid
                                    try
                                    {
                                        if (_savedImeOpen != null && IsWindow(_savedHwnd))
                                        {
                                            var hImcRestore = ImmGetContext(_savedHwnd);
                                            if (hImcRestore != IntPtr.Zero)
                                            {
                                                ImmSetOpenStatus(hImcRestore, _savedImeOpen.Value);
                                                ImmReleaseContext(_savedHwnd, hImcRestore);
                                            }
                                        }
                                    }
                                    catch { }
                                }
                            }
                            catch { }
                            finally
                            {
                                _savedThreadId = null;
                                _savedHwnd = IntPtr.Zero;
                                _savedHkl = IntPtr.Zero;
                                _savedImeOpen = null;
                            }
                        }

                        prevHwnd = hwnd;
                        prevThreadId = threadId;
                        prevWasBlocked = isBlocked;
                    }
                }
                catch { }

                try { await Task.Delay(500, token); } catch { break; }
            }
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var txt = TxtNew.Text?.Trim();
            if (string.IsNullOrEmpty(txt))
                return;
            var n = NormalizeName(txt);
            if (_blocked.Add(n))
                ListBlocked.Items.Add(n);
            TxtNew.Text = string.Empty;
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (ListBlocked.SelectedItem is string s)
            {
                if (_blocked.Remove(s))
                    ListBlocked.Items.Remove(s);
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            SaveConfig();
        }
    }
}
