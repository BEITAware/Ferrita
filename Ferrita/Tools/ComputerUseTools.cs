using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Ferrita.Controls.ChatSessionControl.Views;
using Ferrita.Services.FerritaTools;
using MouseButton = FlaUI.Core.Input.MouseButton;

namespace Ferrita.Tools
{
    // ==========================================
    // 1. PrepareForComputerUse
    // ==========================================
    public sealed class PrepareForComputerUseTool :
        IFerritaTool,
        IFerritaToolInvocationPresentationProvider,
        IFerritaToolPromptDescriptionProvider
    {
        public const string ToolName = "PrepareForComputerUse";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "准备进行计算机操作。截取所有显示器的完整截图以PreservedContent回填，同时返回当前操作系统版本以及正在运行的活跃进程列表。",
            "Device",
            [],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow,
            defaultToolKitKeys: ["computer"]);

        public FerritaToolDefinition Definition => s_definition;

        public string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "对所有显示器进行截图并以PreservedContent回填。同时向代理回填当前的操作系统版本以及活跃进程。";
        }

        public FrameworkElement? CreateInvocationPresentation(FerritaToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ToolInvocationCardFactory.CreateComputerUse(context, []);
        }

        public Task<FerritaToolResult> ExecuteAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tcs = new TaskCompletionSource<FerritaToolResult>();

            var thread = new Thread(() =>
            {
                try
                {
                    var screens = Screen.AllScreens;
                    var builder = new StringBuilder();

                    // 对所有屏幕进行截图
                    for (int i = 0; i < screens.Length; i++)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            tcs.TrySetCanceled(cancellationToken);
                            return;
                        }

                        var screen = screens[i];
                        var bounds = screen.Bounds;
                        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
                        using (var graphics = Graphics.FromImage(bitmap))
                        {
                            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
                        }

                        string tempFilePath = Path.Combine(Path.GetTempPath(), $"Prepare_Monitor_{i}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                        bitmap.Save(tempFilePath, ImageFormat.Png);

                        builder.AppendLine($"<FerritaPreservedContent><Image Path=\"{SecurityElement.Escape(tempFilePath)}\" /></FerritaPreservedContent>");
                    }

                    // 回填操作系统版本以及活跃进程
                    builder.AppendLine();
                    builder.AppendLine($"OS Version: {Environment.OSVersion}");
                    builder.AppendLine();
                    builder.AppendLine("Active Processes:");
                    builder.AppendLine(new string('-', 60));
                    builder.AppendLine($"{"PID",-10} {"Name",-35} {"Memory (MB)",12}");
                    builder.AppendLine(new string('-', 60));

                    var processes = Process.GetProcesses();
                    foreach (var p in processes.OrderBy(p => p.ProcessName).ThenBy(p => p.Id))
                    {
                        double memoryMb = 0;
                        try
                        {
                            memoryMb = p.WorkingSet64 / (1024.0 * 1024.0);
                        }
                        catch
                        {
                            // 忽略某些系统进程访问拒绝异常
                        }

                        string truncatedName = p.ProcessName.Length <= 33 ? p.ProcessName : p.ProcessName.Substring(0, 30) + "...";
                        builder.AppendLine($"{p.Id,-10} {truncatedName,-35} {memoryMb,12:F2}");
                    }

                    tcs.TrySetResult(FerritaToolResult.Success(builder.ToString().TrimEnd()));
                }
                catch (Exception ex)
                {
                    tcs.TrySetResult(FerritaToolResult.Failure($"准备计算机操作失败: {ex.Message}"));
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

            return tcs.Task;
        }
    }

    // ==========================================
    // 2. StartComputerUseSession
    // ==========================================
    public sealed class StartComputerUseSessionTool :
        IFerritaTool,
        IFerritaToolInvocationPresentationProvider,
        IFerritaToolPromptDescriptionProvider
    {
        public const string ToolName = "StartComputerUseSession";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "启动计算机操作会话。锁定宿主机物理输入设备（仅 Ctrl+Esc 可强制退出），创建会话截图存储文件夹并开启操作状态。说明：坐标原点 (0, 0) 位于屏幕左上角，所有操作坐标必须与返回的截图图像像素坐标一致。",
            "Device",
            [
                new FerritaToolParameterDefinition("Monitor", "显示器名称或索引（例如 0, 1 或 Primary）。多显示器时缺省则使用主显示器。", FerritaToolParameterType.String, isRequired: false),
                new FerritaToolParameterDefinition("Resolution", "会话截图的缩放清晰度，可选 'FHD' (1920x1080) 或 'XGA' (1024x768)。默认 XGA。", FerritaToolParameterType.String, isRequired: false, defaultValue: "XGA"),
                new FerritaToolParameterDefinition("Assistive", "保留参数，辅助标志。目前无实际逻辑作用。", FerritaToolParameterType.String, isRequired: false, defaultValue: "带辅助")
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.RequireConfirmation,
            defaultToolKitKeys: ["computer"]);

        public FerritaToolDefinition Definition => s_definition;

        public string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "启动一个新的计算机操作会话。锁定物理键盘鼠标输入，仅 ctrl+esc 键可终止。创建会话存储目录，并返回 Session ID 以及首张截图，并对坐标映射与原点系统作明确说明。";
        }

        public FrameworkElement? CreateInvocationPresentation(FerritaToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ToolInvocationCardFactory.CreateComputerUse(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Monitor", "Monitor", "Primary Screen"),
                    new ToolInvocationCardFieldDefinition("Resolution", "Resolution", "XGA"),
                    new ToolInvocationCardFieldDefinition("Assistive", "Assistive", "带辅助")
                ]);
        }

        public Task<FerritaToolResult> ExecuteAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var monitorArg = arguments.GetString("Monitor")?.Trim();
            var resolutionArg = arguments.GetString("Resolution")?.Trim() ?? "XGA";
            
            // 选择显示器
            Screen screen = Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault() ?? throw new InvalidOperationException("未检测到显示器。");
            if (!string.IsNullOrEmpty(monitorArg))
            {
                if (int.TryParse(monitorArg, out int index) && index >= 0 && index < Screen.AllScreens.Length)
                {
                    screen = Screen.AllScreens[index];
                }
                else
                {
                    var found = Screen.AllScreens.FirstOrDefault(s => s.DeviceName.Contains(monitorArg, StringComparison.OrdinalIgnoreCase));
                    if (found != null)
                    {
                        screen = found;
                    }
                }
            }

            // 获取会话资源文件夹
            string resourcesFolder;
            if (context.Properties.TryGetValue("resourcesFolderPath", out var path) && !string.IsNullOrWhiteSpace(path))
            {
                resourcesFolder = path;
            }
            else
            {
                resourcesFolder = context.WorkspacePath ?? AppContext.BaseDirectory;
            }

            string sessionFolderName = $"ComputerUse_{DateTime.Now:yyyyMMdd_HHmmss}";
            string sessionFolderPath = Path.Combine(resourcesFolder, sessionFolderName);
            Directory.CreateDirectory(sessionFolderPath);

            var assistiveArg = arguments.GetString("Assistive")?.Trim() ?? "带辅助";
            bool isAssistiveEnabled = !assistiveArg.Equals("无辅助", StringComparison.OrdinalIgnoreCase) &&
                                      !assistiveArg.Equals("false", StringComparison.OrdinalIgnoreCase) &&
                                      !assistiveArg.Equals("disabled", StringComparison.OrdinalIgnoreCase) &&
                                      !assistiveArg.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                                      !assistiveArg.Equals("关闭", StringComparison.OrdinalIgnoreCase);

            var chatSessionId = context.Properties.TryGetValue("sessionId", out var sid) ? sid : string.Empty;
            var ownerAgentId = context.CurrentAgent?.AgentId;
            var ownerAgentName = context.CurrentAgent?.DisplayNameOrFallback;

            try
            {
                // 开启会话并进行输入锁定
                var session = ComputerUseSessionManager.StartSession(
                    chatSessionId,
                    screen,
                    resolutionArg,
                    sessionFolderPath,
                    isAssistiveEnabled,
                    ownerAgentId,
                    ownerAgentName);
                
                // 截图
                var screenshotXml = ComputerUseSessionManager.CaptureAndSaveScreenshot(session.SessionId);

                // 向 LLM 明确说明本应用程序的坐标计算法和原点位置
                var returnText = $"{screenshotXml}\n" +
                                 $"Computer Use session started. Session ID: {session.SessionId}\n\n" +
                                 $"[COORDINATE SYSTEM INFO]\n" +
                                 $"- Origin (0, 0): Corresponds to the TOP-LEFT corner of the active screen (which is also the top-left of the returned screenshot image).\n" +
                                 $"- Target Coordinates: All coordinates (xpos, ypos) you submit to the other tools (Click, DoubleClick, Drag, MoveMouse, etc.) must match the exact pixel coordinates of the returned screenshot image.\n" +
                                 $"- Scaling: The screenshot is scaled from the actual screen so that its short side is {(session.Resolution.Equals("FHD", StringComparison.OrdinalIgnoreCase) ? 1080 : 768)} pixels. You do NOT need to calculate scale factors; just read the coordinates directly from the screenshot image you receive.";
                return Task.FromResult(FerritaToolResult.Success(returnText));
            }
            catch (InvalidOperationException ex)
            {
                return Task.FromResult(FerritaToolResult.Failure(ex.Message));
            }
        }
    }

    // ==========================================
    // 基类：提供 SessionId 校验及公共辅助逻辑
    // ==========================================
    public abstract class ComputerUseActionToolBase :
        IFerritaTool,
        IFerritaToolInvocationPresentationProvider,
        IFerritaToolPromptDescriptionProvider
    {
        public abstract FerritaToolDefinition Definition { get; }

        public abstract string GetPromptDescription(FerritaToolPromptDescriptionContext context);

        public abstract FrameworkElement? CreateInvocationPresentation(FerritaToolInvocationPresentationContext context);

        public abstract Task<FerritaToolResult> ExecuteActionAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            string sessionId,
            ComputerUseSession session,
            CancellationToken cancellationToken);

        public Task<FerritaToolResult> ExecuteAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sessionId = arguments.GetString("SessionId")?.Trim();
            if (string.IsNullOrEmpty(sessionId))
            {
                return Task.FromResult(FerritaToolResult.Failure("Session ID 不能为空。"));
            }

            var session = ComputerUseSessionManager.GetSession(sessionId);
            if (session == null)
            {
                return Task.FromResult(FerritaToolResult.Failure($"无效的 Session ID: {sessionId}。"));
            }

            // 安全策略校验：如果当前调用的 AgentId 和启动 Session 的 AgentId 不同，明确报错
            var currentAgentId = context.CurrentAgent?.AgentId;
            if (session.OwnerAgentId != currentAgentId)
            {
                return Task.FromResult(FerritaToolResult.Failure($"代理“{context.CurrentAgent?.DisplayNameOrFallback}”试图操作由代理“{session.OwnerAgentName}”启动的计算机会话。这被安全策略拒绝。"));
            }

            return ExecuteActionAsync(context, arguments, sessionId, session, cancellationToken);
        }

        protected static bool HasParameterValue(FerritaToolArguments arguments, string name)
        {
            var argVal = arguments.GetValue(name);
            return argVal != null && argVal.Value != null;
        }

        protected static VirtualKeyShort ParseKey(string keyName)
        {
            if (string.IsNullOrWhiteSpace(keyName))
            {
                throw new ArgumentException("Key name cannot be empty.");
            }

            string upperKey = keyName.Trim().ToUpperInvariant();

            // 常见别名映射
            if (upperKey == "CTRL" || upperKey == "CONTROL")
            {
                if (Enum.TryParse<VirtualKeyShort>("CONTROL", true, out var vk)) return vk;
                if (Enum.TryParse<VirtualKeyShort>("LCONTROL", true, out vk)) return vk;
            }
            if (upperKey == "ALT")
            {
                if (Enum.TryParse<VirtualKeyShort>("ALT", true, out var vk)) return vk;
                if (Enum.TryParse<VirtualKeyShort>("MENU", true, out vk)) return vk;
            }
            if (upperKey == "WIN" || upperKey == "LWIN" || upperKey == "SUPER" || upperKey == "COMMAND")
            {
                if (Enum.TryParse<VirtualKeyShort>("LWIN", true, out var vk)) return vk;
            }
            if (upperKey == "RWIN")
            {
                if (Enum.TryParse<VirtualKeyShort>("RWIN", true, out var vk)) return vk;
            }
            if (upperKey == "ENTER" || upperKey == "RETURN")
            {
                if (Enum.TryParse<VirtualKeyShort>("ENTER", true, out var vk)) return vk;
                if (Enum.TryParse<VirtualKeyShort>("RETURN", true, out vk)) return vk;
            }
            if (upperKey == "BACK" || upperKey == "BACKSPACE")
            {
                if (Enum.TryParse<VirtualKeyShort>("BACK", true, out var vk)) return vk;
            }
            if (upperKey == "ESC" || upperKey == "ESCAPE")
            {
                if (Enum.TryParse<VirtualKeyShort>("ESCAPE", true, out var vk)) return vk;
            }

            // 单个字母和数字的处理，例如 "a" -> "KEY_A", "1" -> "KEY_1"
            if (upperKey.Length == 1)
            {
                char c = upperKey[0];
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                {
                    string candidate = "KEY_" + c;
                    if (Enum.TryParse<VirtualKeyShort>(candidate, true, out var vk))
                    {
                        return vk;
                    }
                }
            }

            // 尝试直接解析
            if (Enum.TryParse<VirtualKeyShort>(upperKey, true, out var result))
            {
                return result;
            }

            // 尝试加 KEY_ 前缀解析（针对字母或数字等）
            if (Enum.TryParse<VirtualKeyShort>("KEY_" + upperKey, true, out result))
            {
                return result;
            }

            // 尝试加 VK_ 前缀解析
            if (Enum.TryParse<VirtualKeyShort>("VK_" + upperKey, true, out result))
            {
                return result;
            }

            throw new ArgumentException($"Unsupported or unknown key name: '{keyName}'");
        }
    }

    // ==========================================
    // 3. ComputerClick
    // ==========================================
    public sealed class ComputerClickTool : ComputerUseActionToolBase
    {
        public const string ToolName = "ComputerClick";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "在当前计算机操作会话的指定位置（坐标或元素标号）上模拟鼠标单击。",
            "Device",
            [
                new FerritaToolParameterDefinition("SessionId", "会话的唯一标识 ID。", FerritaToolParameterType.String, isRequired: true),
                new FerritaToolParameterDefinition("XPos", "点击位置的 X 坐标值。与 Element 互斥。", FerritaToolParameterType.Integer, isRequired: false),
                new FerritaToolParameterDefinition("YPos", "点击位置的 Y 坐标值。与 Element 互斥。", FerritaToolParameterType.Integer, isRequired: false),
                new FerritaToolParameterDefinition("Element", "点击的目标元素标号。与坐标参数互斥。", FerritaToolParameterType.Integer, isRequired: false)
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow,
            defaultToolKitKeys: ["computer"]);

        public override FerritaToolDefinition Definition => s_definition;

        public override string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "在指定的缩放坐标或元素标号上执行鼠标左键单击。";
        }

        public override FrameworkElement? CreateInvocationPresentation(FerritaToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ToolInvocationCardFactory.CreateComputerUse(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Session ID", "SessionId"),
                    new ToolInvocationCardFieldDefinition("X", "XPos"),
                    new ToolInvocationCardFieldDefinition("Y", "YPos"),
                    new ToolInvocationCardFieldDefinition("Element", "Element")
                ]);
        }

        public override async Task<FerritaToolResult> ExecuteActionAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            string sessionId,
            ComputerUseSession session,
            CancellationToken cancellationToken)
        {
            bool hasCoords = HasParameterValue(arguments, "XPos") && HasParameterValue(arguments, "YPos");
            bool hasElement = HasParameterValue(arguments, "Element");
            bool hasX = HasParameterValue(arguments, "XPos");
            bool hasY = HasParameterValue(arguments, "YPos");

            if ((hasX || hasY) && (!hasX || !hasY))
            {
                return FerritaToolResult.Failure("XPos和YPos坐标参数必须成对提供。");
            }

            if (hasCoords == hasElement)
            {
                return FerritaToolResult.Failure("必须且只能提供坐标参数(XPos, YPos)或元素参数(Element)之一。");
            }

            int actualX, actualY, xpos, ypos;

            if (hasElement)
            {
                int elementId = arguments.GetInteger("Element");
                if (!session.LatestElements.TryGetValue(elementId, out var rect))
                {
                    return FerritaToolResult.Failure($"未找到标号为 {elementId} 的元素。可能页面已刷新或该标号无效。");
                }
                actualX = (rect.Left + rect.Right) / 2;
                actualY = (rect.Top + rect.Bottom) / 2;
                xpos = (int)Math.Round(((rect.Left + rect.Right) / 2.0 - session.Screen.Bounds.Left) * session.Scale);
                ypos = (int)Math.Round(((rect.Top + rect.Bottom) / 2.0 - session.Screen.Bounds.Top) * session.Scale);
            }
            else
            {
                xpos = arguments.GetInteger("XPos");
                ypos = arguments.GetInteger("YPos");
                var (ax, ay) = ComputerUseSessionManager.MapToActualScreen(sessionId, xpos, ypos);
                actualX = ax;
                actualY = ay;
            }

            // 获取当前鼠标位置计算距离
            var currentPos = Mouse.Position;
            double distance = Math.Sqrt(Math.Pow(actualX - currentPos.X, 2) + Math.Pow(actualY - currentPos.Y, 2));
            var currentSpeed = Mouse.MovePixelsPerMillisecond;
            try
            {
                // 如果存在移动，调整移动速度以确保在 0.3s 内完成
                if (distance > 0)
                {
                    Mouse.MovePixelsPerMillisecond = Math.Max(5.0, distance / 300.0);
                }
                Mouse.MoveTo(new System.Drawing.Point(actualX, actualY));
            }
            finally
            {
                // 恢复默认速度
                Mouse.MovePixelsPerMillisecond = currentSpeed;
            }

            Mouse.Click(MouseButton.Left);
            
            // 操作完成后等待 0.5s 再进行截图
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);

            var screenshotXml = ComputerUseSessionManager.CaptureAndSaveScreenshot(sessionId);
            string locationMsg = hasElement ? $"element {arguments.GetInteger("Element")} center ({xpos}, {ypos})" : $"({xpos}, {ypos})";
            return FerritaToolResult.Success($"{screenshotXml}\nClicked at {locationMsg} [Screen: ({actualX}, {actualY})].");
        }
    }

    // ==========================================
    // 4. ComputerDoubleClick
    // ==========================================
    public sealed class ComputerDoubleClickTool : ComputerUseActionToolBase
    {
        public const string ToolName = "ComputerDoubleClick";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "在当前计算机操作会话的指定位置（坐标或元素标号）上模拟鼠标双击。",
            "Device",
            [
                new FerritaToolParameterDefinition("SessionId", "会话的唯一标识 ID。", FerritaToolParameterType.String, isRequired: true),
                new FerritaToolParameterDefinition("XPos", "双击位置的 X 坐标值。与 Element 互斥。", FerritaToolParameterType.Integer, isRequired: false),
                new FerritaToolParameterDefinition("YPos", "双击位置的 Y 坐标值。与 Element 互斥。", FerritaToolParameterType.Integer, isRequired: false),
                new FerritaToolParameterDefinition("Element", "双击的目标元素标号。与坐标参数互斥。", FerritaToolParameterType.Integer, isRequired: false)
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow,
            defaultToolKitKeys: ["computer"]);

        public override FerritaToolDefinition Definition => s_definition;

        public override string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "在指定的缩放坐标或元素标号上执行鼠标左键双击。";
        }

        public override FrameworkElement? CreateInvocationPresentation(FerritaToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ToolInvocationCardFactory.CreateComputerUse(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Session ID", "SessionId"),
                    new ToolInvocationCardFieldDefinition("X", "XPos"),
                    new ToolInvocationCardFieldDefinition("Y", "YPos"),
                    new ToolInvocationCardFieldDefinition("Element", "Element")
                ]);
        }

        public override async Task<FerritaToolResult> ExecuteActionAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            string sessionId,
            ComputerUseSession session,
            CancellationToken cancellationToken)
        {
            bool hasCoords = HasParameterValue(arguments, "XPos") && HasParameterValue(arguments, "YPos");
            bool hasElement = HasParameterValue(arguments, "Element");
            bool hasX = HasParameterValue(arguments, "XPos");
            bool hasY = HasParameterValue(arguments, "YPos");

            if ((hasX || hasY) && (!hasX || !hasY))
            {
                return FerritaToolResult.Failure("XPos和YPos坐标参数必须成对提供。");
            }

            if (hasCoords == hasElement)
            {
                return FerritaToolResult.Failure("必须且只能提供坐标参数(XPos, YPos)或元素参数(Element)之一。");
            }

            int actualX, actualY, xpos, ypos;

            if (hasElement)
            {
                int elementId = arguments.GetInteger("Element");
                if (!session.LatestElements.TryGetValue(elementId, out var rect))
                {
                    return FerritaToolResult.Failure($"未找到标号为 {elementId} 的元素。可能页面已刷新或该标号无效。");
                }
                actualX = (rect.Left + rect.Right) / 2;
                actualY = (rect.Top + rect.Bottom) / 2;
                xpos = (int)Math.Round(((rect.Left + rect.Right) / 2.0 - session.Screen.Bounds.Left) * session.Scale);
                ypos = (int)Math.Round(((rect.Top + rect.Bottom) / 2.0 - session.Screen.Bounds.Top) * session.Scale);
            }
            else
            {
                xpos = arguments.GetInteger("XPos");
                ypos = arguments.GetInteger("YPos");
                var (ax, ay) = ComputerUseSessionManager.MapToActualScreen(sessionId, xpos, ypos);
                actualX = ax;
                actualY = ay;
            }

            // 获取当前鼠标位置计算距离
            var currentPos = Mouse.Position;
            double distance = Math.Sqrt(Math.Pow(actualX - currentPos.X, 2) + Math.Pow(actualY - currentPos.Y, 2));
            var currentSpeed = Mouse.MovePixelsPerMillisecond;
            try
            {
                // 如果存在移动，调整移动速度以确保在 0.3s 内完成
                if (distance > 0)
                {
                    Mouse.MovePixelsPerMillisecond = Math.Max(5.0, distance / 300.0);
                }
                Mouse.MoveTo(new System.Drawing.Point(actualX, actualY));
            }
            finally
            {
                // 恢复默认速度
                Mouse.MovePixelsPerMillisecond = currentSpeed;
            }

            Mouse.DoubleClick(MouseButton.Left);
            
            // 操作完成后等待 0.5s 再进行截图
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);

            var screenshotXml = ComputerUseSessionManager.CaptureAndSaveScreenshot(sessionId);
            string locationMsg = hasElement ? $"element {arguments.GetInteger("Element")} center ({xpos}, {ypos})" : $"({xpos}, {ypos})";
            return FerritaToolResult.Success($"{screenshotXml}\nDouble clicked at {locationMsg} [Screen: ({actualX}, {actualY})].");
        }
    }

    // ==========================================
    // 5. SwitchMonitor
    // ==========================================
    public sealed class SwitchMonitorTool : ComputerUseActionToolBase
    {
        public const string ToolName = "SwitchMonitor";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "切换当前计算机操作会话监听绑定的监视器屏幕。",
            "Device",
            [
                new FerritaToolParameterDefinition("SessionId", "会话的唯一标识 ID。", FerritaToolParameterType.String, isRequired: true),
                new FerritaToolParameterDefinition("Monitor", "要切换到的目标监视器的索引或名称。", FerritaToolParameterType.String, isRequired: true)
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow,
            defaultToolKitKeys: ["computer"]);

        public override FerritaToolDefinition Definition => s_definition;

        public override string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "切换活跃的操作显示器，切换后自动截取并返回新显示器的首张截图。";
        }

        public override FrameworkElement? CreateInvocationPresentation(FerritaToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ToolInvocationCardFactory.CreateComputerUse(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Session ID", "SessionId"),
                    new ToolInvocationCardFieldDefinition("Monitor", "Monitor")
                ]);
        }

        public override Task<FerritaToolResult> ExecuteActionAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            string sessionId,
            ComputerUseSession session,
            CancellationToken cancellationToken)
        {
            var monitorArg = arguments.GetString("Monitor")?.Trim() ?? string.Empty;

            Screen? screen = null;
            if (int.TryParse(monitorArg, out int index) && index >= 0 && index < Screen.AllScreens.Length)
            {
                screen = Screen.AllScreens[index];
            }
            else
            {
                screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName.Contains(monitorArg, StringComparison.OrdinalIgnoreCase));
            }

            if (screen == null)
            {
                return Task.FromResult(FerritaToolResult.Failure($"无法找到指定的监视器: {monitorArg}。"));
            }

            ComputerUseSessionManager.SwitchMonitor(sessionId, screen);
            var screenshotXml = ComputerUseSessionManager.CaptureAndSaveScreenshot(sessionId);
            return Task.FromResult(FerritaToolResult.Success($"{screenshotXml}\nSwitched to monitor {monitorArg}."));
        }
    }

    // ==========================================
    // 6. ComputerDrag
    // ==========================================
    public sealed class ComputerDragTool : ComputerUseActionToolBase
    {
        public const string ToolName = "ComputerDrag";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "模拟在指定位置之间拖拽鼠标（坐标或元素标号）。",
            "Device",
            [
                new FerritaToolParameterDefinition("SessionId", "会话的唯一标识 ID。", FerritaToolParameterType.String, isRequired: true),
                new FerritaToolParameterDefinition("StartPosX", "起始点的 X 坐标值。与 StartElement 互斥。", FerritaToolParameterType.Integer, isRequired: false),
                new FerritaToolParameterDefinition("StartPosY", "起始点的 Y 坐标值。与 StartElement 互斥。", FerritaToolParameterType.Integer, isRequired: false),
                new FerritaToolParameterDefinition("StartElement", "起始目标元素标号。与起始坐标参数互斥。", FerritaToolParameterType.Integer, isRequired: false),
                new FerritaToolParameterDefinition("EndPosX", "结束点的 X 坐标值。与 EndElement 互斥。", FerritaToolParameterType.Integer, isRequired: false),
                new FerritaToolParameterDefinition("EndPosY", "结束点的 Y 坐标值。与 EndElement 互斥。", FerritaToolParameterType.Integer, isRequired: false),
                new FerritaToolParameterDefinition("EndElement", "结束目标元素标号。与结束坐标参数互斥。", FerritaToolParameterType.Integer, isRequired: false),
                new FerritaToolParameterDefinition("IfDither", "是否开启抖动平滑插值移动。默认不开启。", FerritaToolParameterType.Boolean, isRequired: false, defaultValue: "false")
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow,
            defaultToolKitKeys: ["computer"]);

        public override FerritaToolDefinition Definition => s_definition;

        public override string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "拖拽鼠标（左键按下并移动，最后释放），可选项 IfDither 决定移动是否呈线性抖动过渡。";
        }

        public override FrameworkElement? CreateInvocationPresentation(FerritaToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ToolInvocationCardFactory.CreateComputerUse(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Session ID", "SessionId"),
                    new ToolInvocationCardFieldDefinition("Start X", "StartPosX"),
                    new ToolInvocationCardFieldDefinition("Start Y", "StartPosY"),
                    new ToolInvocationCardFieldDefinition("Start Element", "StartElement"),
                    new ToolInvocationCardFieldDefinition("End X", "EndPosX"),
                    new ToolInvocationCardFieldDefinition("End Y", "EndPosY"),
                    new ToolInvocationCardFieldDefinition("End Element", "EndElement"),
                    new ToolInvocationCardFieldDefinition("If Dither", "IfDither", "false")
                ]);
        }

        public override async Task<FerritaToolResult> ExecuteActionAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            string sessionId,
            ComputerUseSession session,
            CancellationToken cancellationToken)
        {
            // Start point validation
            bool hasStartCoords = HasParameterValue(arguments, "StartPosX") && HasParameterValue(arguments, "StartPosY");
            bool hasStartElement = HasParameterValue(arguments, "StartElement");
            bool hasStartX = HasParameterValue(arguments, "StartPosX");
            bool hasStartY = HasParameterValue(arguments, "StartPosY");

            if ((hasStartX || hasStartY) && (!hasStartX || !hasStartY))
            {
                return FerritaToolResult.Failure("StartPosX和StartPosY坐标参数必须成对提供。");
            }
            if (hasStartCoords == hasStartElement)
            {
                return FerritaToolResult.Failure("起始点必须且只能提供坐标参数(StartPosX, StartPosY)或元素参数(StartElement)之一。");
            }

            // End point validation
            bool hasEndCoords = HasParameterValue(arguments, "EndPosX") && HasParameterValue(arguments, "EndPosY");
            bool hasEndElement = HasParameterValue(arguments, "EndElement");
            bool hasEndX = HasParameterValue(arguments, "EndPosX");
            bool hasEndY = HasParameterValue(arguments, "EndPosY");

            if ((hasEndX || hasEndY) && (!hasEndX || !hasEndY))
            {
                return FerritaToolResult.Failure("EndPosX和EndPosY坐标参数必须成对提供。");
            }
            if (hasEndCoords == hasEndElement)
            {
                return FerritaToolResult.Failure("结束点必须且只能提供坐标参数(EndPosX, EndPosY)或元素参数(EndElement)之一。");
            }

            int startXAct, startYAct, startx, starty;
            if (hasStartElement)
            {
                int startElementId = arguments.GetInteger("StartElement");
                if (!session.LatestElements.TryGetValue(startElementId, out var rect))
                {
                    return FerritaToolResult.Failure($"未找到标号为 {startElementId} 的起始元素。");
                }
                startXAct = (rect.Left + rect.Right) / 2;
                startYAct = (rect.Top + rect.Bottom) / 2;
                startx = (int)Math.Round(((rect.Left + rect.Right) / 2.0 - session.Screen.Bounds.Left) * session.Scale);
                starty = (int)Math.Round(((rect.Top + rect.Bottom) / 2.0 - session.Screen.Bounds.Top) * session.Scale);
            }
            else
            {
                startx = arguments.GetInteger("StartPosX");
                starty = arguments.GetInteger("StartPosY");
                var (ax, ay) = ComputerUseSessionManager.MapToActualScreen(sessionId, startx, starty);
                startXAct = ax;
                startYAct = ay;
            }

            int endXAct, endYAct, endx, endy;
            if (hasEndElement)
            {
                int endElementId = arguments.GetInteger("EndElement");
                if (!session.LatestElements.TryGetValue(endElementId, out var rect))
                {
                    return FerritaToolResult.Failure($"未找到标号为 {endElementId} 的结束元素。");
                }
                endXAct = (rect.Left + rect.Right) / 2;
                endYAct = (rect.Top + rect.Bottom) / 2;
                endx = (int)Math.Round(((rect.Left + rect.Right) / 2.0 - session.Screen.Bounds.Left) * session.Scale);
                endy = (int)Math.Round(((rect.Top + rect.Bottom) / 2.0 - session.Screen.Bounds.Top) * session.Scale);
            }
            else
            {
                endx = arguments.GetInteger("EndPosX");
                endy = arguments.GetInteger("EndPosY");
                var (ax, ay) = ComputerUseSessionManager.MapToActualScreen(sessionId, endx, endy);
                endXAct = ax;
                endYAct = ay;
            }

            bool ifdither = arguments.GetBoolean("IfDither", false);

            // 移动到起点 -> 按下 -> 移动到终点 -> 释放
            Mouse.MoveTo(new System.Drawing.Point(startXAct, startYAct));
            Mouse.Down(MouseButton.Left);

            if (ifdither)
            {
                int steps = 20;
                for (int i = 0; i <= steps; i++)
                {
                    double ratio = (double)i / steps;
                    double cx = startXAct + (endXAct - startXAct) * ratio;
                    double cy = startYAct + (endYAct - startYAct) * ratio;
                    Mouse.MoveTo(new System.Drawing.Point((int)cx, (int)cy));
                    Thread.Sleep(10);
                }
            }
            else
            {
                Mouse.MoveTo(new System.Drawing.Point(endXAct, endYAct));
            }

            Mouse.Up(MouseButton.Left);
            
            // 操作完成后等待 0.5s 再进行截图
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);

            var screenshotXml = ComputerUseSessionManager.CaptureAndSaveScreenshot(sessionId);
            string startLoc = hasStartElement ? $"element {arguments.GetInteger("StartElement")} center ({startx}, {starty})" : $"({startx}, {starty})";
            string endLoc = hasEndElement ? $"element {arguments.GetInteger("EndElement")} center ({endx}, {endy})" : $"({endx}, {endy})";
            return FerritaToolResult.Success($"{screenshotXml}\nDragged from {startLoc} to {endLoc}.");
        }
    }

    // ==========================================
    // 7. ComputerTextInput
    // ==========================================
    public sealed class ComputerTextInputTool : ComputerUseActionToolBase
    {
        public const string ToolName = "ComputerTextInput";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "向操作系统键盘仿真发送输入的纯文本，会自动处理转义的换行字符。",
            "Device",
            [
                new FerritaToolParameterDefinition("SessionId", "会话的唯一标识 ID。", FerritaToolParameterType.String, isRequired: true),
                new FerritaToolParameterDefinition("TextContent", "要录入输入的文本内容。", FerritaToolParameterType.String, isRequired: true)
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow,
            defaultToolKitKeys: ["computer"]);

        public override FerritaToolDefinition Definition => s_definition;

        public override string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "模拟键盘文本录入，并将 '\\n' 自动映射转义成敲击键盘的 Enter 键。";
        }

        public override FrameworkElement? CreateInvocationPresentation(FerritaToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ToolInvocationCardFactory.CreateComputerUse(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Session ID", "SessionId"),
                    new ToolInvocationCardFieldDefinition("Text", "TextContent")
                ]);
        }

        public override async Task<FerritaToolResult> ExecuteActionAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            string sessionId,
            ComputerUseSession session,
            CancellationToken cancellationToken)
        {
            var text = arguments.GetString("TextContent") ?? string.Empty;

            // 自动转义换行：把 \r\n 换成 \n，接着把文本段以 \n 分割，中间插入 Enter 键输入
            string normalizedText = text.Replace("\r\n", "\n");
            var lines = normalizedText.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    Keyboard.Type(VirtualKeyShort.ENTER);
                    Thread.Sleep(20);
                }
                
                if (lines[i].Length > 0)
                {
                    Keyboard.Type(lines[i]);
                    Thread.Sleep(20);
                }
            }

            // 操作完成后等待 0.5s 再进行截图
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);

            var screenshotXml = ComputerUseSessionManager.CaptureAndSaveScreenshot(sessionId);
            return FerritaToolResult.Success($"{screenshotXml}\nTyped text content successfully.");
        }
    }

    // ==========================================
    // 8. ComputerHoldMouse
    // ==========================================
    public sealed class ComputerHoldMouseTool : ComputerUseActionToolBase
    {
        public const string ToolName = "ComputerHoldMouse";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "按住鼠标左键，不进行释放。",
            "Device",
            [
                new FerritaToolParameterDefinition("SessionId", "会话的唯一标识 ID。", FerritaToolParameterType.String, isRequired: true)
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow,
            defaultToolKitKeys: ["computer"]);

        public override FerritaToolDefinition Definition => s_definition;

        public override string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "在当前坐标位置按下并按住鼠标左键不放。";
        }

        public override FrameworkElement? CreateInvocationPresentation(FerritaToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ToolInvocationCardFactory.CreateComputerUse(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Session ID", "SessionId")
                ]);
        }

        public override async Task<FerritaToolResult> ExecuteActionAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            string sessionId,
            ComputerUseSession session,
            CancellationToken cancellationToken)
        {
            Mouse.Down(MouseButton.Left);
            
            // 操作完成后等待 0.5s 再进行截图
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);

            var screenshotXml = ComputerUseSessionManager.CaptureAndSaveScreenshot(sessionId);
            return FerritaToolResult.Success($"{screenshotXml}\nMouse button held down.");
        }
    }

    // ==========================================
    // 9. ComputerMoveMouse
    // ==========================================
    public sealed class ComputerMoveMouseTool : ComputerUseActionToolBase
    {
        public const string ToolName = "ComputerMoveMouse";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "模拟在指定位置之间移动鼠标指针（坐标或元素标号）。",
            "Device",
            [
                new FerritaToolParameterDefinition("SessionId", "会话的唯一标识 ID。", FerritaToolParameterType.String, isRequired: true),
                new FerritaToolParameterDefinition("StartPosX", "起始点的 X 坐标值。与 StartElement 互斥。", FerritaToolParameterType.Integer, isRequired: false),
                new FerritaToolParameterDefinition("StartPosY", "起始点的 Y 坐标值。与 StartElement 互斥。", FerritaToolParameterType.Integer, isRequired: false),
                new FerritaToolParameterDefinition("StartElement", "起始目标元素标号。与起始坐标参数互斥。", FerritaToolParameterType.Integer, isRequired: false),
                new FerritaToolParameterDefinition("EndPosX", "结束点的 X 坐标值。与 EndElement 互斥。", FerritaToolParameterType.Integer, isRequired: false),
                new FerritaToolParameterDefinition("EndPosY", "结束点的 Y 坐标值。与 EndElement 互斥。", FerritaToolParameterType.Integer, isRequired: false),
                new FerritaToolParameterDefinition("EndElement", "结束目标元素标号。与结束坐标参数互斥。", FerritaToolParameterType.Integer, isRequired: false),
                new FerritaToolParameterDefinition("IfDither", "是否开启平滑插值移动操作。默认不开启。", FerritaToolParameterType.Boolean, isRequired: false, defaultValue: "false")
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow,
            defaultToolKitKeys: ["computer"]);

        public override FerritaToolDefinition Definition => s_definition;

        public override string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "将鼠标指针移动到指定的终点。可选项 IfDither 决定移动是否是平滑的抖动线性过渡。";
        }

        public override FrameworkElement? CreateInvocationPresentation(FerritaToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ToolInvocationCardFactory.CreateComputerUse(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Session ID", "SessionId"),
                    new ToolInvocationCardFieldDefinition("Start X", "StartPosX"),
                    new ToolInvocationCardFieldDefinition("Start Y", "StartPosY"),
                    new ToolInvocationCardFieldDefinition("Start Element", "StartElement"),
                    new ToolInvocationCardFieldDefinition("End X", "EndPosX"),
                    new ToolInvocationCardFieldDefinition("End Y", "EndPosY"),
                    new ToolInvocationCardFieldDefinition("End Element", "EndElement"),
                    new ToolInvocationCardFieldDefinition("If Dither", "IfDither", "false")
                ]);
        }

        public override async Task<FerritaToolResult> ExecuteActionAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            string sessionId,
            ComputerUseSession session,
            CancellationToken cancellationToken)
        {
            // Start point validation
            bool hasStartCoords = HasParameterValue(arguments, "StartPosX") && HasParameterValue(arguments, "StartPosY");
            bool hasStartElement = HasParameterValue(arguments, "StartElement");
            bool hasStartX = HasParameterValue(arguments, "StartPosX");
            bool hasStartY = HasParameterValue(arguments, "StartPosY");

            if ((hasStartX || hasStartY) && (!hasStartX || !hasStartY))
            {
                return FerritaToolResult.Failure("StartPosX和StartPosY坐标参数必须成对提供。");
            }
            if (hasStartCoords == hasStartElement)
            {
                return FerritaToolResult.Failure("起始点必须且只能提供坐标参数(StartPosX, StartPosY)或元素参数(StartElement)之一。");
            }

            // End point validation
            bool hasEndCoords = HasParameterValue(arguments, "EndPosX") && HasParameterValue(arguments, "EndPosY");
            bool hasEndElement = HasParameterValue(arguments, "EndElement");
            bool hasEndX = HasParameterValue(arguments, "EndPosX");
            bool hasEndY = HasParameterValue(arguments, "EndPosY");

            if ((hasEndX || hasEndY) && (!hasEndX || !hasEndY))
            {
                return FerritaToolResult.Failure("EndPosX和EndPosY坐标参数必须成对提供。");
            }
            if (hasEndCoords == hasEndElement)
            {
                return FerritaToolResult.Failure("结束点必须且只能提供坐标参数(EndPosX, EndPosY)或元素参数(EndElement)之一。");
            }

            int startXAct, startYAct, startx, starty;
            if (hasStartElement)
            {
                int startElementId = arguments.GetInteger("StartElement");
                if (!session.LatestElements.TryGetValue(startElementId, out var rect))
                {
                    return FerritaToolResult.Failure($"未找到标号为 {startElementId} 的起始元素。");
                }
                startXAct = (rect.Left + rect.Right) / 2;
                startYAct = (rect.Top + rect.Bottom) / 2;
                startx = (int)Math.Round(((rect.Left + rect.Right) / 2.0 - session.Screen.Bounds.Left) * session.Scale);
                starty = (int)Math.Round(((rect.Top + rect.Bottom) / 2.0 - session.Screen.Bounds.Top) * session.Scale);
            }
            else
            {
                startx = arguments.GetInteger("StartPosX");
                starty = arguments.GetInteger("StartPosY");
                var (ax, ay) = ComputerUseSessionManager.MapToActualScreen(sessionId, startx, starty);
                startXAct = ax;
                startYAct = ay;
            }

            int endXAct, endYAct, endx, endy;
            if (hasEndElement)
            {
                int endElementId = arguments.GetInteger("EndElement");
                if (!session.LatestElements.TryGetValue(endElementId, out var rect))
                {
                    return FerritaToolResult.Failure($"未找到标号为 {endElementId} 的结束元素。");
                }
                endXAct = (rect.Left + rect.Right) / 2;
                endYAct = (rect.Top + rect.Bottom) / 2;
                endx = (int)Math.Round(((rect.Left + rect.Right) / 2.0 - session.Screen.Bounds.Left) * session.Scale);
                endy = (int)Math.Round(((rect.Top + rect.Bottom) / 2.0 - session.Screen.Bounds.Top) * session.Scale);
            }
            else
            {
                endx = arguments.GetInteger("EndPosX");
                endy = arguments.GetInteger("EndPosY");
                var (ax, ay) = ComputerUseSessionManager.MapToActualScreen(sessionId, endx, endy);
                endXAct = ax;
                endYAct = ay;
            }

            bool ifdither = arguments.GetBoolean("IfDither", false);

            // 首先瞬移到起点，然后移到终点
            Mouse.MoveTo(new System.Drawing.Point(startXAct, startYAct));

            if (ifdither)
            {
                int steps = 20;
                for (int i = 0; i <= steps; i++)
                {
                    double ratio = (double)i / steps;
                    double cx = startXAct + (endXAct - startXAct) * ratio;
                    double cy = startYAct + (endYAct - startYAct) * ratio;
                    Mouse.MoveTo(new System.Drawing.Point((int)cx, (int)cy));
                    Thread.Sleep(10);
                }
            }
            else
            {
                Mouse.MoveTo(new System.Drawing.Point(endXAct, endYAct));
            }

            // 操作完成后等待 0.5s 再进行截图
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);

            var screenshotXml = ComputerUseSessionManager.CaptureAndSaveScreenshot(sessionId);
            string startLoc = hasStartElement ? $"element {arguments.GetInteger("StartElement")} center ({startx}, {starty})" : $"({startx}, {starty})";
            string endLoc = hasEndElement ? $"element {arguments.GetInteger("EndElement")} center ({endx}, {endy})" : $"({endx}, {endy})";
            return FerritaToolResult.Success($"{screenshotXml}\nMoved mouse from {startLoc} to {endLoc}.");
        }
    }

    // ==========================================
    // 10. ComputerReleaseMouse
    // ==========================================
    public sealed class ComputerReleaseMouseTool : ComputerUseActionToolBase
    {
        public const string ToolName = "ComputerReleaseMouse";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "释放当前状态按住不放的鼠标左键。",
            "Device",
            [
                new FerritaToolParameterDefinition("SessionId", "会话的唯一标识 ID。", FerritaToolParameterType.String, isRequired: true)
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow,
            defaultToolKitKeys: ["computer"]);

        public override FerritaToolDefinition Definition => s_definition;

        public override string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "释放目前鼠标左键的物理/虚拟按压状态。";
        }

        public override FrameworkElement? CreateInvocationPresentation(FerritaToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ToolInvocationCardFactory.CreateComputerUse(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Session ID", "SessionId")
                ]);
        }

        public override async Task<FerritaToolResult> ExecuteActionAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            string sessionId,
            ComputerUseSession session,
            CancellationToken cancellationToken)
        {
            Mouse.Up(MouseButton.Left);
            
            // 操作完成后等待 0.5s 再进行截图
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);

            var screenshotXml = ComputerUseSessionManager.CaptureAndSaveScreenshot(sessionId);
            return FerritaToolResult.Success($"{screenshotXml}\nMouse button released.");
        }
    }

    // ==========================================
    // 11. EndComputerUseSession
    // ==========================================
    public sealed class EndComputerUseSessionTool : ComputerUseActionToolBase
    {
        public const string ToolName = "EndComputerUseSession";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "结束指定的计算机操作会话。解锁宿主物理键鼠并释放相关资源状态。",
            "Device",
            [
                new FerritaToolParameterDefinition("SessionId", "要关闭的目标会话的标识 ID。", FerritaToolParameterType.String, isRequired: true)
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow,
            defaultToolKitKeys: ["computer"]);

        public override FerritaToolDefinition Definition => s_definition;

        public override string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "解锁用户物理键鼠输入，释放全局钩子，并结束会话操作状态。";
        }

        public override FrameworkElement? CreateInvocationPresentation(FerritaToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ToolInvocationCardFactory.CreateComputerUse(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Session ID", "SessionId")
                ]);
        }

        public override Task<FerritaToolResult> ExecuteActionAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            string sessionId,
            ComputerUseSession session,
            CancellationToken cancellationToken)
        {
            // 在关闭前最后截图一次
            var screenshotXml = ComputerUseSessionManager.CaptureAndSaveScreenshot(sessionId);

            // 卸载钩子
            ComputerUseSessionManager.EndSession(sessionId);

            return Task.FromResult(FerritaToolResult.Success($"{screenshotXml}\nComputer Use session {sessionId} ended successfully. Input is unlocked."));
        }
    }

    // ==========================================
    // 12. ComputerScreenShot
    // ==========================================
    public sealed class ComputerScreenShotTool : ComputerUseActionToolBase
    {
        public const string ToolName = "ComputerScreenShot";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "捕获当前计算机操作会话的屏幕截图。",
            "Device",
            [
                new FerritaToolParameterDefinition("SessionId", "会话的唯一标识 ID。", FerritaToolParameterType.String, isRequired: true)
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow,
            defaultToolKitKeys: ["computer"]);

        public override FerritaToolDefinition Definition => s_definition;

        public override string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "获取当前操作会话的屏幕截图。";
        }

        public override FrameworkElement? CreateInvocationPresentation(FerritaToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ToolInvocationCardFactory.CreateComputerUse(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Session ID", "SessionId")
                ]);
        }

        public override Task<FerritaToolResult> ExecuteActionAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            string sessionId,
            ComputerUseSession session,
            CancellationToken cancellationToken)
        {
            var screenshotXml = ComputerUseSessionManager.CaptureAndSaveScreenshot(sessionId);
            return Task.FromResult(FerritaToolResult.Success(screenshotXml));
        }
    }

    // ==========================================
    // 13. ComputerWait
    // ==========================================
    public sealed class ComputerWaitTool : ComputerUseActionToolBase
    {
        public const string ToolName = "ComputerWait";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "在当前计算机操作会话中等待指定的时间（秒），然后捕获屏幕截图。",
            "Device",
            [
                new FerritaToolParameterDefinition("SessionId", "会话的唯一标识 ID。", FerritaToolParameterType.String, isRequired: true),
                new FerritaToolParameterDefinition("Seconds", "等待的时长，单位为秒，可以为浮点数。", FerritaToolParameterType.Number, isRequired: true)
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow,
            defaultToolKitKeys: ["computer"]);

        public override FerritaToolDefinition Definition => s_definition;

        public override string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "在计算机操作会话中等待指定的秒数，随后重新截取并返回截图。";
        }

        public override FrameworkElement? CreateInvocationPresentation(FerritaToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ToolInvocationCardFactory.CreateComputerUse(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Session ID", "SessionId"),
                    new ToolInvocationCardFieldDefinition("Seconds", "Seconds")
                ]);
        }

        public override async Task<FerritaToolResult> ExecuteActionAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            string sessionId,
            ComputerUseSession session,
            CancellationToken cancellationToken)
        {
            decimal seconds = arguments.GetNumber("Seconds");
            int milliseconds = (int)(seconds * 1000m);
            if (milliseconds > 0)
            {
                await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);
            }

            var screenshotXml = ComputerUseSessionManager.CaptureAndSaveScreenshot(sessionId);
            return FerritaToolResult.Success($"{screenshotXml}\nWaited for {seconds} seconds.");
        }
    }

    // ==========================================
    // 14. ComputerPressKey
    // ==========================================
    public sealed class ComputerPressKeyTool : ComputerUseActionToolBase
    {
        public const string ToolName = "ComputerPressKey";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "在当前计算机操作会话中模拟单个键盘按键的按下与释放。",
            "Device",
            [
                new FerritaToolParameterDefinition("SessionId", "会话的唯一标识 ID。", FerritaToolParameterType.String, isRequired: true),
                new FerritaToolParameterDefinition("Key", "要敲击的按键名称，例如 ENTER、ESCAPE、KEY_A 等。", FerritaToolParameterType.String, isRequired: true)
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow,
            defaultToolKitKeys: ["computer"]);

        public override FerritaToolDefinition Definition => s_definition;

        public override string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "在计算机操作会话中模拟单个按键的按下与释放动作。";
        }

        public override FrameworkElement? CreateInvocationPresentation(FerritaToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ToolInvocationCardFactory.CreateComputerUse(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Session ID", "SessionId"),
                    new ToolInvocationCardFieldDefinition("Key", "Key")
                ]);
        }

        public override async Task<FerritaToolResult> ExecuteActionAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            string sessionId,
            ComputerUseSession session,
            CancellationToken cancellationToken)
        {
            var keyStr = arguments.GetString("Key") ?? string.Empty;
            VirtualKeyShort vk;
            try
            {
                vk = ParseKey(keyStr);
            }
            catch (Exception ex)
            {
                return FerritaToolResult.Failure(ex.Message);
            }

            Keyboard.Type(vk);

            // 操作完成后等待 0.5s 再进行截图
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);

            var screenshotXml = ComputerUseSessionManager.CaptureAndSaveScreenshot(sessionId);
            return FerritaToolResult.Success($"{screenshotXml}\nPressed and released key '{keyStr}' successfully.");
        }
    }

    // ==========================================
    // 15. ComputerHotKey
    // ==========================================
    public sealed class ComputerHotKeyTool : ComputerUseActionToolBase
    {
        public const string ToolName = "ComputerHotKey";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "在当前计算机操作会话中模拟组合快捷键（以 Keys 参数接收 XML 结构体，例如 <Key>CONTROL</Key><Key>A</Key>）。",
            "Device",
            [
                new FerritaToolParameterDefinition("SessionId", "会话的唯一标识 ID。", FerritaToolParameterType.String, isRequired: true),
                new FerritaToolParameterDefinition("Keys", "接收的 XML 结构体，描述需要组合按下的按键序列。", FerritaToolParameterType.String, isRequired: true)
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow,
            defaultToolKitKeys: ["computer"]);

        public override FerritaToolDefinition Definition => s_definition;

        public override string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "模拟同时或依次按下的一组快捷键。Keys 应形如：<Key>CONTROL</Key><Key>A</Key>";
        }

        public override FrameworkElement? CreateInvocationPresentation(FerritaToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ToolInvocationCardFactory.CreateComputerUse(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Session ID", "SessionId"),
                    new ToolInvocationCardFieldDefinition("Keys XML", "Keys")
                ]);
        }

        public override async Task<FerritaToolResult> ExecuteActionAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            string sessionId,
            ComputerUseSession session,
            CancellationToken cancellationToken)
        {
            var keysStr = arguments.GetString("Keys") ?? string.Empty;
            var matches = Regex.Matches(keysStr, @"<Key>(.*?)</Key>", RegexOptions.IgnoreCase);
            if (matches.Count == 0)
            {
                return FerritaToolResult.Failure("未能在 Keys 参数中找到任何 <Key></Key> 结构。");
            }

            var keyList = new List<VirtualKeyShort>();
            var keyNames = new List<string>();
            foreach (Match match in matches)
            {
                var val = match.Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(val))
                {
                    try
                    {
                        var vk = ParseKey(val);
                        keyList.Add(vk);
                        keyNames.Add(val.Trim());
                    }
                    catch (Exception ex)
                    {
                        return FerritaToolResult.Failure(ex.Message);
                    }
                }
            }

            if (keyList.Count == 0)
            {
                return FerritaToolResult.Failure("解析出的按键列表为空。");
            }

            Keyboard.TypeSimultaneously(keyList.ToArray());

            // 操作完成后等待 0.5s 再进行截图
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);

            var screenshotXml = ComputerUseSessionManager.CaptureAndSaveScreenshot(sessionId);
            return FerritaToolResult.Success($"{screenshotXml}\nSimulated hotkeys [{string.Join(" + ", keyNames)}] successfully.");
        }
    }

    // ==========================================
    // 16. ComputerHoldKey
    // ==========================================
    public sealed class ComputerHoldKeyTool : ComputerUseActionToolBase
    {
        public const string ToolName = "ComputerHoldKey";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "在当前计算机操作会话中模拟按下并保持指定按键的按下状态（不自动释放）。",
            "Device",
            [
                new FerritaToolParameterDefinition("SessionId", "会话的唯一标识 ID。", FerritaToolParameterType.String, isRequired: true),
                new FerritaToolParameterDefinition("Key", "要按住的按键名称，例如 CONTROL、SHIFT 等。", FerritaToolParameterType.String, isRequired: true)
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow,
            defaultToolKitKeys: ["computer"]);

        public override FerritaToolDefinition Definition => s_definition;

        public override string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "在计算机操作会话中模拟持续按住某个按键（用于快捷组合或拖拽操作）。";
        }

        public override FrameworkElement? CreateInvocationPresentation(FerritaToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ToolInvocationCardFactory.CreateComputerUse(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Session ID", "SessionId"),
                    new ToolInvocationCardFieldDefinition("Key", "Key")
                ]);
        }

        public override async Task<FerritaToolResult> ExecuteActionAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            string sessionId,
            ComputerUseSession session,
            CancellationToken cancellationToken)
        {
            var keyStr = arguments.GetString("Key") ?? string.Empty;
            VirtualKeyShort vk;
            try
            {
                vk = ParseKey(keyStr);
            }
            catch (Exception ex)
            {
                return FerritaToolResult.Failure(ex.Message);
            }

            Keyboard.Press(vk);

            // 操作完成后等待 0.5s 再进行截图
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);

            var screenshotXml = ComputerUseSessionManager.CaptureAndSaveScreenshot(sessionId);
            return FerritaToolResult.Success($"{screenshotXml}\nHeld down key '{keyStr}' successfully.");
        }
    }

    // ==========================================
    // 17. ComputerReleaseKey
    // ==========================================
    public sealed class ComputerReleaseKeyTool : ComputerUseActionToolBase
    {
        public const string ToolName = "ComputerReleaseKey";

        private static readonly FerritaToolDefinition s_definition = new(
            ToolName,
            "在当前计算机操作会话中模拟释放先前被按住的的指定按键。",
            "Device",
            [
                new FerritaToolParameterDefinition("SessionId", "会话的唯一标识 ID。", FerritaToolParameterType.String, isRequired: true),
                new FerritaToolParameterDefinition("Key", "要释放的按键名称，例如 CONTROL、SHIFT 等。", FerritaToolParameterType.String, isRequired: true)
            ],
            defaultAgentPermission: FerritaToolDefaultAgentPermission.Allow,
            defaultToolKitKeys: ["computer"]);

        public override FerritaToolDefinition Definition => s_definition;

        public override string GetPromptDescription(FerritaToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "在计算机操作会话中模拟释放被按住的按键。";
        }

        public override FrameworkElement? CreateInvocationPresentation(FerritaToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ToolInvocationCardFactory.CreateComputerUse(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Session ID", "SessionId"),
                    new ToolInvocationCardFieldDefinition("Key", "Key")
                ]);
        }

        public override async Task<FerritaToolResult> ExecuteActionAsync(
            FerritaToolContext context,
            FerritaToolArguments arguments,
            string sessionId,
            ComputerUseSession session,
            CancellationToken cancellationToken)
        {
            var keyStr = arguments.GetString("Key") ?? string.Empty;
            VirtualKeyShort vk;
            try
            {
                vk = ParseKey(keyStr);
            }
            catch (Exception ex)
            {
                return FerritaToolResult.Failure(ex.Message);
            }

            Keyboard.Release(vk);

            // 操作完成后等待 0.5s 再进行截图
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);

            var screenshotXml = ComputerUseSessionManager.CaptureAndSaveScreenshot(sessionId);
            return FerritaToolResult.Success($"{screenshotXml}\nReleased key '{keyStr}' successfully.");
        }
    }
}
