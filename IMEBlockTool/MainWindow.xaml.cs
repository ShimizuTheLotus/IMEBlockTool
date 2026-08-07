using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace IMEBlockTool
{
    public sealed partial class MainWindow : Window
    {
        private readonly string _configPath;
        private readonly HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);

        // Event hook
        private IntPtr _foregroundHook = IntPtr.Zero;
        private IntPtr _focusHook = IntPtr.Zero;
        private WinEventDelegate? _foregroundDelegate;
        private WinEventDelegate? _focusDelegate;
        private readonly object _hookSync = new();

        private IntPtr _englishHkl = IntPtr.Zero;

        // Saved IME stat
        private IntPtr _savedHkl = IntPtr.Zero;
        private bool? _savedImeOpen = null;
        private bool _isInBlockedApp = false;

        // Last window stat
        private IntPtr _prevHwnd = IntPtr.Zero;
        private uint _prevThreadId = 0;
        private bool _prevWasBlocked = false;

        // Queue
        private readonly ConcurrentQueue<Action> _actionQueue = new();
private int _isProcessingQueue = 0;
        private readonly object _queueLock = new();


        private const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint EVENT_OBJECT_FOCUS = 0x8005;
        private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;

        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

        private const int OBJID_WINDOW = 0x0000;
        private const int OBJID_CLIENT = 0xFFFF;

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
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

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

        [DllImport("imm32.dll")]
        private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        public MainWindow()
        {
            this.InitializeComponent();

            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "blocked.txt");
            LoadConfig();
            this.Closed += OnClosed;
        }

        private void LoadConfig()
        {
            if (File.Exists(_configPath))
            {
                try
                {
                    var lines = File.ReadAllLines(_configPath)
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .Select(l => l.Trim());
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

        private void OnClosed(object sender, WindowEventArgs args)
        {
            StopWatcher();
        }

        private bool IsBlockedProcess(uint processId)
        {
            try
            {
                var proc = Process.GetProcessById((int)processId);
                var exe = proc.ProcessName + ".exe";
                return _blocked.Contains(exe);
            }
            catch
            {
                return false;
            }
        }

        private bool IsBlockedWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            uint processId = GetWindowThreadProcessId(hwnd, out _);
            return IsBlockedProcess(processId);
        }

        private IntPtr GetWindowLayout(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            uint threadId = GetWindowThreadProcessId(hwnd, out _);
            return GetKeyboardLayout(threadId);
        }

        private void SaveImeState(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            try
            {
                var layout = GetWindowLayout(hwnd);
                if (layout != IntPtr.Zero)
                {
                    _savedHkl = layout;
                    _savedImeOpen = null;

                    var hImc = ImmGetContext(hwnd);
                    if (hImc != IntPtr.Zero)
                    {
                        _savedImeOpen = ImmGetOpenStatus(hImc);
                        ImmReleaseContext(hwnd, hImc);
                    }
                }
            }
            catch (Exception ex)
            {
            }
        }

        private void RestoreImeState(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            if (_savedHkl == IntPtr.Zero) return;

            try
            {
                if (!IsWindow(hwnd))
                {
                    return;
                }

                SendMessage(hwnd, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, _savedHkl);

                if (_savedImeOpen != null)
                {
                    var hImc = ImmGetContext(hwnd);
                    if (hImc != IntPtr.Zero)
                    {
                        ImmSetOpenStatus(hImc, _savedImeOpen.Value);
                        ImmReleaseContext(hwnd, hImc);
                    }
                }
            }
            catch (Exception ex)
            {
            }
            finally
            {
                _savedHkl = IntPtr.Zero;
                _savedImeOpen = null;
            }
        }

        private void ForceEnglish(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            if (_englishHkl == IntPtr.Zero) return;

            try
            {
                SendMessage(hwnd, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, _englishHkl);
            }
            catch (Exception ex)
            {

            }
        }

        private void EnqueueAction(Action action)
        {
            if (action == null) return;

            _actionQueue.Enqueue(action);
            ProcessQueue();
        }

        private async void ProcessQueue()
        {
            if (Interlocked.CompareExchange(ref _isProcessingQueue, 1, 0) == 1)
                return;

            try
            {
                var actions = new List<Action>();
                while (_actionQueue.TryDequeue(out Action action))
                {
                    actions.Add(action);
                }

                if (actions.Count == 0) return;

                var lastAction = actions.Last();
                await Task.Run(() => lastAction());
            }
            finally
            {
                Interlocked.Exchange(ref _isProcessingQueue, 0);

                if (!_actionQueue.IsEmpty)
                {
                    ProcessQueue();
                }
            }
        }

        private void StartWatcher()
        {
            lock (_hookSync)
            {
                if (_foregroundHook != IntPtr.Zero && _focusHook != IntPtr.Zero)
                    return;

                try { _englishHkl = LoadKeyboardLayout("00000409", 0); } catch { _englishHkl = IntPtr.Zero; }

                _foregroundDelegate = ForegroundWinEventProc;
                _foregroundHook = SetWinEventHook(
                    EVENT_SYSTEM_FOREGROUND,
                    EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero,
                    _foregroundDelegate,
                    0, 0,
                    WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS
                );

                _focusDelegate = FocusWinEventProc;
                _focusHook = SetWinEventHook(
                    EVENT_OBJECT_FOCUS,
                    EVENT_OBJECT_FOCUS,
                    IntPtr.Zero,
                    _focusDelegate,
                    0, 0,
                    WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS
                );

                _savedHkl = IntPtr.Zero;
                _savedImeOpen = null;
                _isInBlockedApp = false;
                _prevHwnd = IntPtr.Zero;
                _prevThreadId = 0;
                _prevWasBlocked = false;
            }
        }

        private void StopWatcher()
        {
            lock (_hookSync)
            {
                if (_foregroundHook != IntPtr.Zero)
                {
                    UnhookWinEvent(_foregroundHook);
                    _foregroundHook = IntPtr.Zero;
                }

                if (_focusHook != IntPtr.Zero)
                {
                    UnhookWinEvent(_focusHook);
                    _focusHook = IntPtr.Zero;
                }

                _foregroundDelegate = null;
                _focusDelegate = null;

                _savedHkl = IntPtr.Zero;
                _savedImeOpen = null;
                _isInBlockedApp = false;

                // Empty queue
                while (_actionQueue.TryDequeue(out _)) { }
            }
        }

        private void ForegroundWinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (hwnd == IntPtr.Zero) return;

            EnqueueAction(() => HandleForegroundChange(hwnd));
        }

        private void FocusWinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (hwnd == IntPtr.Zero) return;

            EnqueueAction(() =>
            {
                try
                {
                    if (!_isInBlockedApp) return;
                    if (!IsWindow(hwnd)) return;

                    if (IsBlockedWindow(hwnd))
                    {
                        ForceEnglish(hwnd);
                    }
                }
                catch (Exception ex)
                {
                }
            });
        }


        private void HandleForegroundChange(IntPtr hwnd)
        {
            try
            {
                lock (_hookSync)
                {
                    if (!IsWindow(hwnd)) return;

                    uint threadId = GetWindowThreadProcessId(hwnd, out uint processId);
                    bool isBlocked = IsBlockedProcess(processId);

                    if (isBlocked && !_isInBlockedApp)
                    {
                        if (_prevHwnd != IntPtr.Zero && !_prevWasBlocked)
                        {
                            SaveImeState(_prevHwnd);
                        }

                        ForceEnglish(hwnd);
                        _isInBlockedApp = true;
                    }

                    if (!isBlocked && _isInBlockedApp)
                    {
                        RestoreImeState(hwnd);
                        _isInBlockedApp = false;
                    }

                    if (isBlocked && _isInBlockedApp)
                    {
                        ForceEnglish(hwnd);
                    }

                    if (!isBlocked && !_isInBlockedApp && _savedHkl != IntPtr.Zero)
                    {
                        _savedHkl = IntPtr.Zero;
                        _savedImeOpen = null;
                    }

                    _prevHwnd = hwnd;
                    _prevThreadId = threadId;
                    _prevWasBlocked = isBlocked;
                }
            }
            catch (Exception ex)
            {
            }
        }
    }
}