using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Ferrita.Services.ChatSession;
using FlaUI.UIA3;
using FlaUI.Core.AutomationElements;

namespace Ferrita.Tools
{
    public class ComputerUseSession
    {
        public string SessionId { get; set; } = string.Empty;
        public string ChatSessionId { get; set; } = string.Empty;
        public string? OwnerAgentId { get; set; }
        public string? OwnerAgentName { get; set; }
        public string SessionFolderPath { get; set; } = string.Empty;
        public Screen Screen { get; set; } = null!;
        public string Resolution { get; set; } = "XGA";
        public int ScreenshotCounter { get; set; } = 1;
        public double Scale { get; set; } = 1.0;
        public int OffsetX { get; set; } = 0;
        public int OffsetY { get; set; } = 0;
        public bool IsAssistiveEnabled { get; set; } = false;
        public ConcurrentDictionary<int, Rectangle> LatestElements { get; } = new();
    }

    public static class ComputerUseSessionManager
    {
        private static readonly ConcurrentDictionary<string, ComputerUseSession> s_sessions = new(StringComparer.OrdinalIgnoreCase);
        private static string? s_activeSessionId;

        public static event EventHandler<string>? SessionStarted;
        public static event EventHandler<string>? SessionEnded;

        private static HOOKPROC? _keyboardProc;
        private static HOOKPROC? _mouseProc;
        private static HHOOK _keyboardHookId;
        private static HHOOK _mouseHookId;
        private static readonly object _hookLock = new();

        private static Thread? s_hookThread;
        private static uint s_hookThreadId;
        private static readonly ManualResetEventSlim s_hookStartedBarrier = new(false);

        public static string? ActiveSessionId => s_activeSessionId;

        public static ComputerUseSession? GetSession(string sessionId)
        {
            return s_sessions.TryGetValue(sessionId, out var session) ? session : null;
        }

        private static void HookThreadProc()
        {
            try
            {
                s_hookThreadId = PInvoke.GetCurrentThreadId();
                
                // 安装输入拦截钩子
                InstallHooks();
                
                // 标记已成功安装钩子并启动
                s_hookStartedBarrier.Set();

                // 启动后台线程的消息循环
                while (PInvoke.GetMessage(out var msg, default, 0, 0))
                {
                    PInvoke.TranslateMessage(msg);
                    PInvoke.DispatchMessage(msg);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in background hook thread: {ex}");
            }
            finally
            {
                // 安全卸载输入拦截钩子
                UninstallHooks();
                s_hookStartedBarrier.Reset();
            }
        }

        private static void StopHookThread()
        {
            lock (_hookLock)
            {
                if (s_hookThread != null)
                {
                    if (s_hookThreadId != 0)
                    {
                        // 投递 WM_QUIT (0x0012) 消息给后台钩子线程以结束其消息循环
                        PInvoke.PostThreadMessage(s_hookThreadId, 0x0012, default, default);
                    }

                    if (s_hookThread.IsAlive)
                    {
                        s_hookThread.Join(1000);
                    }

                    s_hookThread = null;
                    s_hookThreadId = 0;
                }
            }
        }

        public static ComputerUseSession StartSession(
            string chatSessionId,
            Screen screen,
            string resolution,
            string sessionFolderPath,
            bool isAssistiveEnabled,
            string? ownerAgentId,
            string? ownerAgentName)
        {
            lock (_hookLock)
            {
                // 如果已有活动会话，先进行清理
                if (!string.IsNullOrEmpty(s_activeSessionId))
                {
                    var activeSession = GetSession(s_activeSessionId);
                    if (activeSession != null)
                    {
                        if (activeSession.OwnerAgentId != ownerAgentId)
                        {
                            throw new InvalidOperationException($"无法启动 Computer Use 会话。代理“{activeSession.OwnerAgentName}”正在使用计算机。同时只允许一个 Computer Use 会话存在。");
                        }
                    }
                    EndSession(s_activeSessionId);
                }

                // 生成Session ID (4位字母数字)
                string sessionId = GenerateSessionId();
                while (s_sessions.ContainsKey(sessionId))
                {
                    sessionId = GenerateSessionId();
                }

                var session = new ComputerUseSession
                {
                    SessionId = sessionId,
                    ChatSessionId = chatSessionId,
                    OwnerAgentId = ownerAgentId,
                    OwnerAgentName = ownerAgentName,
                    Screen = screen,
                    Resolution = resolution,
                    SessionFolderPath = sessionFolderPath,
                    IsAssistiveEnabled = isAssistiveEnabled
                };

                InitializeSessionScaleAndOffset(session);
                s_sessions[sessionId] = session;
                s_activeSessionId = sessionId;

                // 启动后台钩子线程
                s_hookStartedBarrier.Reset();
                s_hookThread = new Thread(HookThreadProc)
                {
                    IsBackground = true,
                    Name = "ComputerUseHookThread"
                };
                s_hookThread.SetApartmentState(ApartmentState.STA);
                s_hookThread.Start();

                // 等待钩子线程成功启动并安装好钩子（最多等待2秒）
                s_hookStartedBarrier.Wait(2000);

                // 注册会话注册表变更事件以在执行终止时自动解锁
                ActiveChatSessionExecutionRegistry.Instance.Changed -= OnRegistryChanged;
                ActiveChatSessionExecutionRegistry.Instance.Changed += OnRegistryChanged;

                SessionStarted?.Invoke(null, sessionId);

                return session;
            }
        }

        public static void EndSession(string sessionId)
        {
            lock (_hookLock)
            {
                if (s_sessions.TryRemove(sessionId, out _))
                {
                    if (string.Equals(s_activeSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        s_activeSessionId = null;
                        ActiveChatSessionExecutionRegistry.Instance.Changed -= OnRegistryChanged;
                        
                        StopHookThread();

                        SessionEnded?.Invoke(null, sessionId);
                    }
                }
            }
        }

        public static void SwitchMonitor(string sessionId, Screen newScreen)
        {
            if (s_sessions.TryGetValue(sessionId, out var session))
            {
                session.Screen = newScreen;
                InitializeSessionScaleAndOffset(session);
            }
            else
            {
                throw new InvalidOperationException($"会话 {sessionId} 不存在。");
            }
        }

        public static void InitializeSessionScaleAndOffset(ComputerUseSession session)
        {
            var screen = session.Screen;
            int targetShortSide = session.Resolution.Equals("FHD", StringComparison.OrdinalIgnoreCase) ? 1080 : 768;
            int screenShortSide = Math.Min(screen.Bounds.Width, screen.Bounds.Height);

            double scale = (double)targetShortSide / screenShortSide;

            session.Scale = scale;
            session.OffsetX = 0;
            session.OffsetY = 0;
        }

        public static (int X, int Y) MapToActualScreen(string sessionId, int xpos, int ypos)
        {
            if (!s_sessions.TryGetValue(sessionId, out var session))
            {
                throw new InvalidOperationException($"会话 {sessionId} 不存在。");
            }

            var screen = session.Screen;
            double scale = session.Scale;

            double screenX = xpos / scale;
            double screenY = ypos / scale;

            // 限制到当前屏幕边界内
            screenX = Math.Max(0, Math.Min(screen.Bounds.Width - 1, screenX));
            screenY = Math.Max(0, Math.Min(screen.Bounds.Height - 1, screenY));

            int actualX = screen.Bounds.Left + (int)screenX;
            int actualY = screen.Bounds.Top + (int)screenY;

            return (actualX, actualY);
        }

        private static bool IsTargetElement(AutomationElement element)
        {
            try
            {
                // 1. 剔除不可见或不可用状态
                if (element.Properties.IsOffscreen.ValueOrDefault)
                {
                    return false;
                }
                if (!element.Properties.IsEnabled.ValueOrDefault)
                {
                    return false;
                }

                // 2. 获取控件类型
                var controlType = element.ControlType;

                // 3. 剔除明确为纯布局、容器或非点击目标的类型
                if (controlType == FlaUI.Core.Definitions.ControlType.Pane ||
                    controlType == FlaUI.Core.Definitions.ControlType.Window ||
                    controlType == FlaUI.Core.Definitions.ControlType.Group ||
                    controlType == FlaUI.Core.Definitions.ControlType.ScrollBar ||
                    controlType == FlaUI.Core.Definitions.ControlType.Custom ||
                    controlType == FlaUI.Core.Definitions.ControlType.Header ||
                    controlType == FlaUI.Core.Definitions.ControlType.HeaderItem ||
                    controlType == FlaUI.Core.Definitions.ControlType.MenuBar ||
                    controlType == FlaUI.Core.Definitions.ControlType.Menu ||
                    controlType == FlaUI.Core.Definitions.ControlType.ToolTip ||
                    controlType == FlaUI.Core.Definitions.ControlType.Separator ||
                    controlType == FlaUI.Core.Definitions.ControlType.TitleBar ||
                    controlType == FlaUI.Core.Definitions.ControlType.ProgressBar)
                {
                    return false;
                }

                // 4. 针对图片或文档类型，仅在支持交互模式时保留
                if (controlType == FlaUI.Core.Definitions.ControlType.Image ||
                    controlType == FlaUI.Core.Definitions.ControlType.Document)
                {
                    return IsInteractiveSafe(element);
                }

                // 5. 针对纯文本 Text 类型，仅在支持交互模式且名称非空时保留
                if (controlType == FlaUI.Core.Definitions.ControlType.Text)
                {
                    return IsInteractiveSafe(element) && !string.IsNullOrWhiteSpace(element.Name);
                }

                // 6. 其他常见可交互控件直接保留
                if (controlType == FlaUI.Core.Definitions.ControlType.Button ||
                    controlType == FlaUI.Core.Definitions.ControlType.CheckBox ||
                    controlType == FlaUI.Core.Definitions.ControlType.ComboBox ||
                    controlType == FlaUI.Core.Definitions.ControlType.Edit ||
                    controlType == FlaUI.Core.Definitions.ControlType.Hyperlink ||
                    controlType == FlaUI.Core.Definitions.ControlType.ListItem ||
                    controlType == FlaUI.Core.Definitions.ControlType.MenuItem ||
                    controlType == FlaUI.Core.Definitions.ControlType.RadioButton ||
                    controlType == FlaUI.Core.Definitions.ControlType.TabItem ||
                    controlType == FlaUI.Core.Definitions.ControlType.TreeItem ||
                    controlType == FlaUI.Core.Definitions.ControlType.Spinner ||
                    controlType == FlaUI.Core.Definitions.ControlType.Slider)
                {
                    return true;
                }

                // 7. 如果非上述类别，但明确是可键盘聚焦的也可以保留
                if (IsKeyboardFocusableSafe(element))
                {
                    return true;
                }
            }
            catch
            {
                // 忽略 COM / UIA 异常
            }
            return false;
        }

        private static bool IsInteractiveSafe(AutomationElement element)
        {
            try
            {
                var patterns = element.Patterns;
                return patterns.Invoke.IsSupported ||
                       patterns.Toggle.IsSupported ||
                       patterns.SelectionItem.IsSupported ||
                       patterns.Value.IsSupported;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsKeyboardFocusableSafe(AutomationElement element)
        {
            try
            {
                return element.Properties.IsKeyboardFocusable.ValueOrDefault;
            }
            catch
            {
                return false;
            }
        }

        private static Rectangle GetBoundingRectangleSafe(AutomationElement element)
        {
            try
            {
                return element.BoundingRectangle;
            }
            catch
            {
                return Rectangle.Empty;
            }
        }

        private static void TraverseElements(AutomationElement element, Rectangle screenBounds, List<AutomationElement> targetElements, int depth, CancellationToken cancellationToken)
        {
            if (targetElements.Count >= 500) return;
            if (depth > 12) return;
            if (cancellationToken.IsCancellationRequested) return;

            var rect = GetBoundingRectangleSafe(element);
            if (rect.Width <= 0 || rect.Height <= 0) return;
            if (!screenBounds.IntersectsWith(rect)) return;

            // 如果元素被判定为不可见/折叠/Offscreen，跳过遍历该节点及其子树
            try
            {
                if (element.Properties.IsOffscreen.ValueOrDefault)
                {
                    return;
                }
            }
            catch
            {
                // 忽略读取异常并继续
            }

            if (IsTargetElement(element))
            {
                targetElements.Add(element);
            }

            try
            {
                var children = element.FindAllChildren();
                if (children != null)
                {
                    foreach (var child in children)
                    {
                        if (cancellationToken.IsCancellationRequested) return;
                        TraverseElements(child, screenBounds, targetElements, depth + 1, cancellationToken);
                    }
                }
            }
            catch
            {
                // 忽略遍历子节点异常
            }
        }

        private static string GetNameSafe(AutomationElement element)
        {
            try
            {
                return element.Name ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetAutomationIdSafe(AutomationElement element)
        {
            try
            {
                return element.Properties.AutomationId.ValueOrDefault ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetControlTypeSafeString(AutomationElement element)
        {
            try
            {
                return element.ControlType.ToString();
            }
            catch
            {
                return "Unknown";
            }
        }

        private static string GetValueSafe(AutomationElement element)
        {
            try
            {
                if (element.Patterns.Value.IsSupported)
                {
                    return element.Patterns.Value.Pattern.Value.Value ?? string.Empty;
                }
            }
            catch
            {
                // 忽略获取属性值异常
            }
            return string.Empty;
        }

        public static string CaptureAndSaveScreenshot(string sessionId)
        {
            if (!s_sessions.TryGetValue(sessionId, out var session))
            {
                throw new InvalidOperationException($"会话 {sessionId} 不存在。");
            }

            var screen = session.Screen;
            var bounds = screen.Bounds;

            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
            }

            int targetWidth = Math.Max(1, (int)Math.Round(bounds.Width * session.Scale));
            int targetHeight = Math.Max(1, (int)Math.Round(bounds.Height * session.Scale));

            using var scaledBitmap = ScaleImage(bitmap, targetWidth, targetHeight);

            string filename = $"screenshot_{session.ScreenshotCounter:D4}.png";
            string fullPath = Path.Combine(session.SessionFolderPath, filename);
            scaledBitmap.Save(fullPath, ImageFormat.Png);

            if (!session.IsAssistiveEnabled)
            {
                session.ScreenshotCounter++;
                return $"<FerritaPreservedContent><Image Path=\"{System.Security.SecurityElement.Escape(fullPath)}\" /></FerritaPreservedContent>";
            }

            // 辅助模式：遍历并标注元素
            var targetElements = new List<AutomationElement>();
            try
            {
                using var cts = new CancellationTokenSource(3000);
                var token = cts.Token;

                using var automation = new UIA3Automation();
                var desktop = automation.GetDesktop();
                TraverseElements(desktop, bounds, targetElements, 0, token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FlaUI element traversal error: {ex}");
            }

            // 更新 session.LatestElements
            session.LatestElements.Clear();
            int id = 1;
            foreach (var el in targetElements)
            {
                var rect = GetBoundingRectangleSafe(el);
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                session.LatestElements[id] = rect;
                id++;
            }

            // 绘制标注图
            string labeledFilename = $"screenshot_{session.ScreenshotCounter:D4}_labeled.png";
            string labeledFullPath = Path.Combine(session.SessionFolderPath, labeledFilename);
            session.ScreenshotCounter++;

            using var labeledBitmap = new Bitmap(scaledBitmap);
            using (var g = Graphics.FromImage(labeledBitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                using var pen = new Pen(Color.FromArgb(255, 57, 255, 20), 2); // 霓虹绿
                using var brushBg = new SolidBrush(Color.FromArgb(255, 57, 255, 20));
                using var brushText = new SolidBrush(Color.Black);
                using var font = new Font(new FontFamily("Segoe UI"), 9f, System.Drawing.FontStyle.Bold);

                int drawId = 1;
                foreach (var el in targetElements)
                {
                    var rect = GetBoundingRectangleSafe(el);
                    if (rect.Width <= 0 || rect.Height <= 0) continue;

                    // 计算相对当前显示器的缩放坐标
                    int x1 = (int)Math.Round((rect.Left - bounds.Left) * session.Scale);
                    int y1 = (int)Math.Round((rect.Top - bounds.Top) * session.Scale);
                    int x2 = (int)Math.Round((rect.Right - bounds.Left) * session.Scale);
                    int y2 = (int)Math.Round((rect.Bottom - bounds.Top) * session.Scale);

                    // 限制在图表尺寸内
                    x1 = Math.Max(0, Math.Min(targetWidth - 1, x1));
                    y1 = Math.Max(0, Math.Min(targetHeight - 1, y1));
                    x2 = Math.Max(0, Math.Min(targetWidth - 1, x2));
                    y2 = Math.Max(0, Math.Min(targetHeight - 1, y2));

                    int w = x2 - x1;
                    int h = y2 - y1;
                    if (w <= 0 || h <= 0) continue;

                    g.DrawRectangle(pen, x1, y1, w, h);

                    string labelText = drawId.ToString();
                    var labelSize = g.MeasureString(labelText, font);
                    g.FillRectangle(brushBg, x1, y1, (int)labelSize.Width + 2, (int)labelSize.Height + 2);
                    g.DrawString(labelText, font, brushText, x1 + 1, y1 + 1);

                    drawId++;
                }
            }

            labeledBitmap.Save(labeledFullPath, ImageFormat.Png);

            // 生成 XML
            var xmlBuilder = new StringBuilder();
            xmlBuilder.AppendLine("<Elements>");
            int xmlId = 1;
            foreach (var el in targetElements)
            {
                var rect = GetBoundingRectangleSafe(el);
                if (rect.Width <= 0 || rect.Height <= 0) continue;

                int x1 = (int)Math.Round((rect.Left - bounds.Left) * session.Scale);
                int y1 = (int)Math.Round((rect.Top - bounds.Top) * session.Scale);
                int x2 = (int)Math.Round((rect.Right - bounds.Left) * session.Scale);
                int y2 = (int)Math.Round((rect.Bottom - bounds.Top) * session.Scale);

                string name = System.Security.SecurityElement.Escape(GetNameSafe(el));
                string controlType = System.Security.SecurityElement.Escape(GetControlTypeSafeString(el));
                string automationId = System.Security.SecurityElement.Escape(GetAutomationIdSafe(el));
                string val = System.Security.SecurityElement.Escape(GetValueSafe(el));

                xmlBuilder.AppendLine($"  <Element id=\"{xmlId}\">");
                xmlBuilder.AppendLine($"    <Id>{xmlId}</Id>");
                xmlBuilder.AppendLine($"    <ControlType>{controlType}</ControlType>");
                xmlBuilder.AppendLine($"    <Name>{name}</Name>");
                xmlBuilder.AppendLine($"    <AutomationId>{automationId}</AutomationId>");
                xmlBuilder.AppendLine($"    <Value>{val}</Value>");
                xmlBuilder.AppendLine($"    <BoundingRectangleX1>{x1}</BoundingRectangleX1>");
                xmlBuilder.AppendLine($"    <BoundingRectangleY1>{y1}</BoundingRectangleY1>");
                xmlBuilder.AppendLine($"    <BoundingRectangleX2>{x2}</BoundingRectangleX2>");
                xmlBuilder.AppendLine($"    <BoundingRectangleY2>{y2}</BoundingRectangleY2>");
                xmlBuilder.AppendLine("  </Element>");

                xmlId++;
            }
            xmlBuilder.AppendLine("</Elements>");

            string elementsXml = xmlBuilder.ToString();

            return $"{elementsXml}\n<FerritaPreservedContent><Image Path=\"{System.Security.SecurityElement.Escape(fullPath)}\" /></FerritaPreservedContent>\n<FerritaPreservedContent><Image Path=\"{System.Security.SecurityElement.Escape(labeledFullPath)}\" /></FerritaPreservedContent>";
        }

        public static Bitmap ScaleImage(Bitmap source, int targetWidth, int targetHeight)
        {
            var target = new Bitmap(targetWidth, targetHeight);
            using var g = Graphics.FromImage(target);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, new Rectangle(0, 0, targetWidth, targetHeight));

            return target;
        }

        private static string GenerateSessionId()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var random = new Random();
            return new string(Enumerable.Repeat(chars, 4)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }

        private static void OnRegistryChanged(object? sender, EventArgs e)
        {
            var activeId = s_activeSessionId;
            if (string.IsNullOrEmpty(activeId))
            {
                return;
            }

            var session = GetSession(activeId);
            if (session == null)
            {
                return;
            }

            var snapshot = ActiveChatSessionExecutionRegistry.Instance.GetSnapshot();
            bool isActive = snapshot.Any(s => string.Equals(s.SessionId, session.ChatSessionId, StringComparison.OrdinalIgnoreCase));
            if (!isActive)
            {
                // 当后台执行流程中止或完成时，立刻解锁输入
                StopHookThread();

                // 同时也结束 Computer Use 会话以自动关闭窗口等
                EndSession(activeId);
            }
        }

        private static void InstallHooks()
        {
            lock (_hookLock)
            {
                if (!_keyboardHookId.IsNull || !_mouseHookId.IsNull)
                {
                    return; // 已经安装了
                }

                _keyboardProc = HookCallbackKeyboard;
                _mouseProc = HookCallbackMouse;

                _keyboardHookId = PInvoke.SetWindowsHookEx(
                    WINDOWS_HOOK_ID.WH_KEYBOARD_LL,
                    _keyboardProc,
                    default(HINSTANCE),
                    0
                );

                _mouseHookId = PInvoke.SetWindowsHookEx(
                    WINDOWS_HOOK_ID.WH_MOUSE_LL,
                    _mouseProc,
                    default(HINSTANCE),
                    0
                );
            }
        }

        public static void UninstallHooks()
        {
            lock (_hookLock)
            {
                if (!_keyboardHookId.IsNull)
                {
                    PInvoke.UnhookWindowsHookEx(_keyboardHookId);
                    _keyboardHookId = default;
                }
                if (!_mouseHookId.IsNull)
                {
                    PInvoke.UnhookWindowsHookEx(_mouseHookId);
                    _mouseHookId = default;
                }

                _keyboardProc = null;
                _mouseProc = null;
            }
        }

        private static LRESULT HookCallbackKeyboard(int nCode, WPARAM wParam, LPARAM lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    var kbdStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    bool isInjected = ((uint)kbdStruct.flags & 0x10) != 0; // LLKHF_INJECTED = 0x10

                    // 检测 Ctrl+Esc (VK_ESCAPE = 0x1B, VK_CONTROL = 0x11)
                    if (kbdStruct.vkCode == 0x1B)
                    {
                        short ctrlState = PInvoke.GetAsyncKeyState(0x11);
                        bool isCtrlDown = (ctrlState & 0x8000) != 0;

                        if (isCtrlDown)
                        {
                            var sessId = s_activeSessionId;
                            if (!string.IsNullOrEmpty(sessId))
                            {
                                Task.Run(() =>
                                {
                                    ActiveChatSessionExecutionRegistry.Instance.Cancel(sessId);
                                });
                            }
                            
                            // 投递 WM_QUIT 消息到当前后台钩子线程的消息队列，使其安全退出并清理钩子
                            PInvoke.PostThreadMessage(PInvoke.GetCurrentThreadId(), 0x0012, default, default);
                            return new LRESULT(1); // 消费该事件，拦截
                        }
                    }

                    // 不再锁定用户物理输入，允许通过
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error in keyboard hook: {ex}");
                }
            }
            return PInvoke.CallNextHookEx(default, nCode, wParam, lParam);
        }

        private static LRESULT HookCallbackMouse(int nCode, WPARAM wParam, LPARAM lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    var mouseStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    bool isInjected = ((uint)mouseStruct.flags & 0x01) != 0; // LLMHF_INJECTED = 0x01

                    // 不再锁定用户物理输入，允许通过
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error in mouse hook: {ex}");
                }
            }
            return PInvoke.CallNextHookEx(default, nCode, wParam, lParam);
        }
    }
}
