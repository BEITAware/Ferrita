using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Skyweaver.Services.ChatSession;
using Skyweaver.Services.Memory;

namespace Skyweaver.Services.Daemon
{
    public sealed class BackgroundMemoryQueue : IDisposable
    {
        private static readonly Lazy<BackgroundMemoryQueue> LazyInstance =
            new(() => new BackgroundMemoryQueue());

        public static BackgroundMemoryQueue Instance => LazyInstance.Value;

        private readonly Channel<string> _sessionIds = Channel.CreateUnbounded<string>();
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly ChatSessionRepository _repository = new();
        private readonly MemoryService _memoryService = new();
        private readonly object _gate = new();
        private readonly HashSet<string> _queuedOrRunningSessionIds = new(StringComparer.OrdinalIgnoreCase);
        private Task? _workerTask;
        private bool _isAcceptingWork = true;
        private bool _isDisposed;

        private BackgroundMemoryQueue()
        {
        }

        public bool IsIdle
        {
            get
            {
                lock (_gate)
                {
                    return _queuedOrRunningSessionIds.Count == 0;
                }
            }
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_workerTask == null)
                {
                    _workerTask = Task.Run(() => ProcessQueueAsync(_cancellationTokenSource.Token));
                }
            }
        }

        public void Enqueue(IEnumerable<string>? sessionIds)
        {
            if (sessionIds == null)
            {
                return;
            }

            foreach (var rawSessionId in sessionIds)
            {
                var sessionId = rawSessionId.Trim();
                if (sessionId.Length == 0)
                {
                    continue;
                }

                lock (_gate)
                {
                    if (!_isAcceptingWork)
                    {
                        continue;
                    }

                    if (!_queuedOrRunningSessionIds.Add(sessionId))
                    {
                        continue;
                    }
                }

                if (!_sessionIds.Writer.TryWrite(sessionId))
                {
                    lock (_gate)
                    {
                        _queuedOrRunningSessionIds.Remove(sessionId);
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            BeginShutdown();
            _cancellationTokenSource.Dispose();
        }

        public void BeginShutdown()
        {
            lock (_gate)
            {
                _isAcceptingWork = false;
                _queuedOrRunningSessionIds.Clear();
            }

            while (_sessionIds.Reader.TryRead(out _))
            {
            }

            _sessionIds.Writer.TryComplete();

            try
            {
                _cancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            await foreach (var sessionId in _sessionIds.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var session = _repository.LoadBySessionId(sessionId);
                    if (session != null)
                    {
                        await _memoryService.GenerateForClosedSessionAsync(session, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Background memory generation should not crash the main application.
                }
                finally
                {
                    lock (_gate)
                    {
                        _queuedOrRunningSessionIds.Remove(sessionId);
                    }
                }
            }
        }
    }
}
