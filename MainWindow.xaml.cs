using System.IO;
using System.Windows.Controls;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using GpuVramMonitor.Models;
using GpuVramMonitor.Services;

namespace GpuVramMonitor;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    // Win32 API — reliably brings window to front (bypasses focus-stealing prevention)
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    private readonly DispatcherTimer _timer;
    private bool _isPaused;
    private bool _isRefreshing;
    private bool _hasLoaded;
    private bool _settingsApplied;
    private double _vramTotalMB;
    private double _vramUsedMB;
    private string _gpuName = "";
    private int _processCount;
    private string _lastRefreshTime = "";
    private AppSettings _settings;

    // ─── Bound Properties ────────────────────────────────────────────

    public string GpuName
    {
        get => _gpuName;
        set { _gpuName = value; OnPropertyChanged(); }
    }

    public double VramUsedMB
    {
        get => _vramUsedMB;
        set { _vramUsedMB = value; OnPropertyChanged(); OnPropertyChanged(nameof(VramPercent)); }
    }

    public double VramTotalMB
    {
        get => _vramTotalMB;
        set { _vramTotalMB = value; OnPropertyChanged(); OnPropertyChanged(nameof(VramPercent)); }
    }

    public double VramPercent => VramTotalMB > 0
        ? Math.Round(VramUsedMB / VramTotalMB * 100, 1)
        : 0;

    public int ProcessCount
    {
        get => _processCount;
        set { _processCount = value; OnPropertyChanged(); }
    }

    public string LastRefreshTime
    {
        get => _lastRefreshTime;
        set { _lastRefreshTime = value; OnPropertyChanged(); }
    }

    // ─── Constructor ─────────────────────────────────────────────────

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        // Load settings
        _settings = SettingsService.Load();

        try
        {
            GpuName = VramMonitorService.GetGpuName();
            VramTotalMB = VramMonitorService.GetTotalVramCapacity();
        }
        catch
        {
            GpuName = "Unknown GPU";
        }

        if (VramTotalMB <= 0)
            VramTotalMB = 16384;

        // Apply UI state from settings
        StartOnBootCheck.IsChecked = _settings.StartOnBoot;
        MinimizeToTrayCheck.IsChecked = _settings.MinimizeToTrayOnMinimize;
        MinimizeOnCloseCheck.IsChecked = _settings.MinimizeToTrayOnClose;

        // Auto-start registry
        if (_settings.StartOnBoot)
            SettingsService.ApplyAutoStart(true);

        // Tray icon click
        TrayIcon.TrayMouseDoubleClick += TrayIcon_DoubleClick;

        // Auto-refresh timer
        _timer = new DispatcherTimer();
        _timer.Tick += TimerTick;
        _timer.Interval = TimeSpan.FromSeconds(2);

        // Mark as ready — prevents ExcludeDwm_Changed from firing a refresh
        // before VRAM capacity is detected (happens during InitializeComponent
        // when XAML sets IsChecked="True" on the ExcludeDwm checkbox).
        _settingsApplied = true;

        Dispatcher.BeginInvoke(() => TriggerRefresh());
        _timer.Start();
    }

    // ─── Window State Management ─────────────────────────────────────

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;

        // Win32 SetForegroundWindow reliably bypasses focus-stealing prevention,
        // while WPF's Show()/WindowState handle the rendering correctly.
        var hWnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        SetForegroundWindow(hWnd);
    }

    private void TrayIcon_DoubleClick(object sender, RoutedEventArgs e)
    {
        ShowWindow();
    }

    private void TrayIcon_RightClick(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)Resources["TrayContextMenu"];
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        TrayIcon.Dispose();
        Application.Current.Shutdown();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        if (WindowState == WindowState.Minimized && _settings.MinimizeToTrayOnMinimize)
        {
            Hide();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            WindowState = WindowState.Minimized;
            return;
        }

        // Clean up tray icon on actual exit
        TrayIcon.Dispose();
        base.OnClosing(e);
    }

    // ─── Settings ────────────────────────────────────────────────────

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.Visibility = SettingsPopup.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void SaveSettings()
    {
        _settings.StartOnBoot = StartOnBootCheck.IsChecked ?? false;
        _settings.MinimizeToTrayOnMinimize = MinimizeToTrayCheck.IsChecked ?? false;
        _settings.MinimizeToTrayOnClose = MinimizeOnCloseCheck.IsChecked ?? false;
        SettingsService.Save(_settings);
    }

    private void SettingsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        // Apply auto-start immediately on user toggle
        if (sender == StartOnBootCheck)
            SettingsService.ApplyAutoStart(StartOnBootCheck.IsChecked ?? false);
    }

    // ─── Refresh ─────────────────────────────────────────────────────

    private async void TimerTick(object? sender, EventArgs e)
    {
        await RefreshDataAsync();
    }

    private void TriggerRefresh()
    {
        _ = RefreshDataAsync();
    }

    private async Task RefreshDataAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;

        try
        {
            bool excludeDwm = ExcludeDwmCheck.IsChecked ?? true;

            var (processes, adapters) = await Task.Run(() =>
            {
                var procs = VramMonitorService.GetProcessVramUsage(excludeDwm, _vramTotalMB);
                var adaps = VramMonitorService.GetAdapterVramUsage();
                return (procs, adaps);
            });

            // Preserve selection across refresh
            int? selectedPid = (ProcessGrid.SelectedItem as GpuProcessInfo)?.PID;

            ProcessGrid.ItemsSource = processes;
            ProcessCount = processes.Count;

            // Re-select the previously selected process if still in the list
            if (selectedPid.HasValue)
            {
                foreach (var item in processes)
                {
                    if (item.PID == selectedPid.Value)
                    {
                        ProcessGrid.SelectedItem = item;
                        break;
                    }
                }
            }

            // First load: swap loading overlay → DataGrid
            if (!_hasLoaded)
            {
                _hasLoaded = true;
                LoadingOverlay.Visibility = Visibility.Collapsed;
                ProcessGrid.Visibility = Visibility.Visible;
            }

            if (adapters.Count > 0)
                VramUsedMB = adapters[0].usedMB;

            // Update top VRAM bar
            var parent = VramBar.Parent as FrameworkElement;
            if (parent != null && parent.ActualWidth > 0)
            {
                double pct = VramTotalMB > 0 ? VramUsedMB / VramTotalMB : 0;
                VramBar.Width = Math.Max(0, parent.ActualWidth * pct);
            }

            VramHeaderText.Text = $"VRAM: {VramUsedMB:F0} / {VramTotalMB:F0} MB  ({VramPercent:F1}%)";
            LastRefreshTime = DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Refresh error: {ex.Message}");
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    // ─── Event Handlers ──────────────────────────────────────────────

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _isPaused = !_isPaused;
        if (_isPaused)
        {
            _timer.Stop();
            PauseButton.Content = "▶ Resume";
        }
        else
        {
            _timer.Start();
            PauseButton.Content = "⏸ Pause";
        }
    }

    private void RefreshNowButton_Click(object sender, RoutedEventArgs e)
    {
        TriggerRefresh();
    }

    private void RefreshSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_timer is null) return;
        _timer.Interval = TimeSpan.FromSeconds(e.NewValue);
    }

    private void ExcludeDwm_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingsApplied) return;
        TriggerRefresh();
    }

    // ─── Process Selection Actions ───────────────────────────────────

    private void ProcessGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProcessGrid.SelectedItem is GpuProcessInfo proc)
        {
            SelectedProcessText.Text = $"{proc.ProcessName} (PID {proc.PID})";
            ProcessActionBar.Visibility = Visibility.Visible;
        }
        else
        {
            ProcessActionBar.Visibility = Visibility.Collapsed;
        }
    }

    private void FindProcess_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessGrid.SelectedItem is not GpuProcessInfo proc) return;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(proc.PID);
            string? path = null;

            try { path = process.MainModule?.FileName; }
            catch { /* Access denied for system processes */ }

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else
            {
                MessageBox.Show($"Cannot locate file for '{proc.ProcessName}'. The process may be a system process.", "File Not Found",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (ArgumentException)
        {
            MessageBox.Show($"Process '{proc.ProcessName}' (PID {proc.PID}) is no longer running.", "Process Exited",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void KillProcess_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessGrid.SelectedItem is not GpuProcessInfo proc) return;

        var result = MessageBox.Show(
            $"End task '{proc.ProcessName}' (PID {proc.PID})?\n\nThis will forcefully terminate the process.",
            "Confirm End Task",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(proc.PID);
            process.Kill();
            // Wait briefly then refresh the list
            await Task.Delay(200);
            TriggerRefresh();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show($"Cannot end task '{proc.ProcessName}'. Access denied.", "Access Denied",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (ArgumentException)
        {
            MessageBox.Show($"Process '{proc.ProcessName}' has already exited.", "Process Exited",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (InvalidOperationException)
        {
            MessageBox.Show($"Process '{proc.ProcessName}' has already exited or cannot be terminated.", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ─── INotifyPropertyChanged ──────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
