using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Skyweaver.Services.Daemon
{
    public sealed class SkyweaverDaemonService : IDisposable
    {
        private static readonly Lazy<SkyweaverDaemonService> LazyInstance =
            new(() => new SkyweaverDaemonService());

        public static SkyweaverDaemonService Instance => LazyInstance.Value;

        private const string MutexName = "Local\\SkyweaverSingleInstanceMutex";
        private const string PipeName = "Skyweaver.Daemon.Control";

        private Mutex? _mutex;
        private CancellationTokenSource? _cts;
        private Task? _serverTask;
        private bool _isDisposed;

        public event Action<string[]>? OnMessageReceived;

        private SkyweaverDaemonService()
        {
        }

        /// <summary>
        /// 检查是否为单实例。如果不是第一个实例，则通知主实例并返回 false。
        /// 如果是第一个实例，则启动 IPC 管道服务器并返回 true。
        /// </summary>
        public bool CheckSingleInstanceAndNotify(string[] args)
        {
            try
            {
                _mutex = new Mutex(true, MutexName, out bool createdNew);
                if (createdNew)
                {
                    // 第一个实例，启动 IPC 管道服务
                    StartIpcServer();
                    return true;
                }
            }
            catch (Exception)
            {
                // Mutex 创建或获取失败，保底视为已有实例运行
            }

            // 已有实例在运行，作为客户端向其发送命令行参数后退出
            SendArgsToActiveInstanceAsync(args).GetAwaiter().GetResult();
            return false;
        }

        private void StartIpcServer()
        {
            _cts = new CancellationTokenSource();
            _serverTask = Task.Run(() => RunIpcServerAsync(_cts.Token));
        }

        private async Task RunIpcServerAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var pipeServer = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                try
                {
                    await pipeServer.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    _ = Task.Run(() => HandleClientConnectionAsync(pipeServer, cancellationToken), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    pipeServer.Dispose();
                    break;
                }
                catch
                {
                    pipeServer.Dispose();
                }
            }
        }

        private async Task HandleClientConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
        {
            using (pipe)
            using (var reader = new StreamReader(pipe))
            {
                try
                {
                    var messageJson = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(messageJson))
                    {
                        var message = JsonSerializer.Deserialize<DaemonIpcMessage>(messageJson);
                        if (message?.Args != null)
                        {
                            OnMessageReceived?.Invoke(message.Args);
                        }
                    }
                }
                catch
                {
                    // 忽略 IPC 读取异常
                }
            }
        }

        private async Task SendArgsToActiveInstanceAsync(string[] args)
        {
            try
            {
                using var cts = new CancellationTokenSource(2000); // 2 秒超时
                using var pipeClient = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                
                await pipeClient.ConnectAsync(cts.Token).ConfigureAwait(false);
                
                using var writer = new StreamWriter(pipeClient) { AutoFlush = true };
                var message = new DaemonIpcMessage { Args = args };
                var messageJson = JsonSerializer.Serialize(message);
                
                await writer.WriteLineAsync(messageJson.AsMemory(), cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // 无法连接或发送，可能是前一个实例正在关闭或挂起
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
            }
            catch
            {
            }

            try
            {
                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
            }
            catch
            {
            }
        }

        private sealed class DaemonIpcMessage
        {
            public string[] Args { get; set; } = Array.Empty<string>();
        }
    }
}
