using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ferrita.Controls.FerritaPreferencesControl.Services;
using Ferrita.Rendering;
using Ferrita.Services.Localization;
using Ferrita.Services.ShellIntegration;
using Ferrita.Services.Daemon;
using Ferrita.Windows;
using Ferrita.Tools;
using Ferrita.Services.ChatSession;

namespace Ferrita
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private bool _isShuttingDown;
        private MainWindow? _mainWindow;
        private bool _mainWindowShown;
        private ComputerUseWindow? _computerUseWindow;

        public bool IsShuttingDown => _isShuttingDown;

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // 设置为显式关闭模式，防止窗口关闭后应用自动退出（daemon 模式需要）
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // 1. 单实例检查与参数转发
            if (!FerritaDaemonService.Instance.CheckSingleInstanceAndNotify(e.Args))
            {
                Shutdown();
                return;
            }

            // 监听后续实例发送的 IPC 参数唤醒请求
            FerritaDaemonService.Instance.OnMessageReceived += OnIpcMessageReceived;

            // 2. 初始化系统托盘图标
            TrayIconService.Instance.Initialize(
                onOpenOrFocusRequested: () => WakeUpMainWindow(),
                onShutdownRequested: () => ForceShutdown()
            );

            // 启动本地后台记忆提取队列
            BackgroundMemoryQueue.Instance.Start();
            ScheduledTasksDaemonService.Instance.Start();

            LocalizationRuntime.Instance.ApplyConfiguredLanguage();
            FerritaPreferencesRegistration.EnsureRegistered();
            Ferrita.Services.Notifications.NotificationService.Instance.ClearTransient();

            ComputerUseSessionManager.SessionStarted += OnComputerUseSessionStarted;
            ComputerUseSessionManager.SessionEnded += OnComputerUseSessionEnded;

            // 3. 处理命令行参数
            var shellChatStartupContext = ShellIntegrationCommandLine.ParseShellChatStartup(e.Args);
            if (shellChatStartupContext != null)
            {
                if (ShouldAggregateShellStartup(shellChatStartupContext))
                {
                    _ = ShowShellChatWindowAfterStartupAggregationAsync(shellChatStartupContext, isStartup: true);
                }
                else
                {
                    ShowShellChatWindow(shellChatStartupContext);
                }
                return;
            }

            _ = ShellIntegrationRuntime.Instance.ApplyConfiguredRegistration();

            bool isDaemonStartup = IsDaemonOnlyStartup(e.Args);
            SplashWindow? splashWindow = null;
            if (!isDaemonStartup)
            {
                splashWindow = new SplashWindow();
                splashWindow.Show();
            }

            DirectXResourcePreloader.PreloadAll();

            // 初始化主窗口，若为 daemon 静默启动则保持隐藏
            _mainWindow = new MainWindow();
            MainWindow = _mainWindow;

            if (!isDaemonStartup)
            {
                Action showMainWindow = () =>
                {
                    if (_mainWindowShown)
                    {
                        return;
                    }

                    _mainWindowShown = true;

                    Dispatcher.Invoke(() =>
                    {
                        _mainWindow.Show();
                        _mainWindow.Activate();
                        splashWindow?.Close();
                    });
                };

                System.Threading.Tasks.Task.Delay(500).ContinueWith(_ => showMainWindow());
            }
        }

        private void OnIpcMessageReceived(string[] args)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var shellChatStartupContext = ShellIntegrationCommandLine.ParseShellChatStartup(args);
                if (shellChatStartupContext != null)
                {
                    if (ShouldAggregateShellStartup(shellChatStartupContext))
                    {
                        _ = ShowShellChatWindowAfterStartupAggregationAsync(shellChatStartupContext, isStartup: false);
                    }
                    else
                    {
                        ShowShellChatWindow(shellChatStartupContext);
                    }
                }
                else
                {
                    WakeUpMainWindow();
                }
            }));
        }

        public void WakeUpMainWindow()
        {
            Dispatcher.Invoke(() =>
            {
                DirectXResourcePreloader.PreloadAll();

                if (_mainWindow == null)
                {
                    _mainWindow = new MainWindow();
                    MainWindow = _mainWindow;
                }

                _mainWindowShown = true;
                _mainWindow.Show();
                if (_mainWindow.WindowState == WindowState.Minimized)
                {
                    _mainWindow.WindowState = WindowState.Normal;
                }
                _mainWindow.Activate();
            });
        }

        public void ForceShutdown()
        {
            if (_isShuttingDown)
            {
                // 重复调用时直接强制退出
                Environment.Exit(0);
                return;
            }

            _isShuttingDown = true;

            // 启动一个后台线程作为最终安全网：无论清理是否完成，在限定时间后强制终止进程。
            // 这确保了托盘图标的"关闭"操作是至高无上的——即使有后台任务在运行，也一定会退出。
            var forceExitThread = new Thread(() =>
            {
                Thread.Sleep(3000);
                Environment.Exit(0);
            })
            {
                IsBackground = true,
                Name = "ForceExitWatchdog"
            };
            forceExitThread.Start();

            // 尽力清理各后台服务（不阻塞等待，每个单独 try/catch 避免连锁失败）
            try { TrayIconService.Instance.Dispose(); } catch { }
            try { FerritaDaemonService.Instance.Dispose(); } catch { }
            try { BackgroundMemoryQueue.Instance.Dispose(); } catch { }
            try { ScheduledTasksDaemonService.Instance.Dispose(); } catch { }

            // 在 UI 线程上执行 WPF 的正常关闭流程
            try
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        Shutdown();
                    }
                    catch
                    {
                        Environment.Exit(0);
                    }
                });
            }
            catch
            {
                // Dispatcher 调用失败（例如应用已在关闭），直接强制退出
                Environment.Exit(0);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            ComputerUseSessionManager.SessionStarted -= OnComputerUseSessionStarted;
            ComputerUseSessionManager.SessionEnded -= OnComputerUseSessionEnded;

            TrayIconService.Instance.Dispose();
            FerritaDaemonService.Instance.Dispose();
            BackgroundMemoryQueue.Instance.Dispose();
            ScheduledTasksDaemonService.Instance.Dispose();
            base.OnExit(e);
        }

        private static void ShowShellChatWindow(ShellChatStartupContext startupContext)
        {
            var shellWindow = new ShellChatWindow(startupContext);
            Current.MainWindow = shellWindow;
            shellWindow.Show();
            shellWindow.Activate();
        }

        private static bool ShouldAggregateShellStartup(ShellChatStartupContext startupContext)
        {
            return startupContext.SelectedPaths.Any(path =>
                !string.IsNullOrWhiteSpace(path) &&
                !Directory.Exists(path.Trim()));
        }

        private async Task ShowShellChatWindowAfterStartupAggregationAsync(
            ShellChatStartupContext startupContext,
            bool isStartup)
        {
            var aggregatedContext = await ShellChatStartupCoordinator
                .TryAggregateOrForwardAsync(startupContext)
                .ConfigureAwait(true);
            if (aggregatedContext == null)
            {
                if (isStartup)
                {
                    Shutdown();
                }
                return;
            }

            ShowShellChatWindow(aggregatedContext);
        }

        private static bool IsDaemonOnlyStartup(string[] args)
        {
            return args.Any(arg =>
                string.Equals(arg, "--daemon", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--daemon-only", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--skylifter-only", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "/daemon", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "/daemon-only", StringComparison.OrdinalIgnoreCase));
        }

        private void OnComputerUseSessionStarted(object? sender, string sessionId)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var session = ComputerUseSessionManager.GetSession(sessionId);
                    if (session == null) return;

                    var chatSession = new ChatSessionRepository().LoadBySessionId(session.ChatSessionId);
                    if (chatSession == null) return;

                    if (_computerUseWindow != null)
                    {
                        try
                        {
                            _computerUseWindow.Close();
                        }
                        catch { }
                        _computerUseWindow = null;
                    }

                    _computerUseWindow = new ComputerUseWindow(chatSession);
                    _computerUseWindow.Show();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error starting ComputerUseWindow: {ex}");
                }
            }));
        }

        private void OnComputerUseSessionEnded(object? sender, string sessionId)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_computerUseWindow != null)
                    {
                        _computerUseWindow.CloseWithAnimation();
                        _computerUseWindow = null;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error ending ComputerUseWindow: {ex}");
                }
            }));
        }
    }
}
