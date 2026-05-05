using System.Globalization;
using System.IO;
using System.Linq;
using Skyweaver.Models.LateralFileSystem;

namespace Skyweaver.Services.LateralFileSystem
{
    public sealed class LateralFileSystemRuntime : IDisposable
    {
        private readonly object _syncRoot = new();
        private readonly LateralFileSystemConfigurationRepository _configurationRepository;
        private readonly LateralFileSystemTreeRepository _treeRepository;
        private readonly LateralFileSystemStorageAnalyzer _storageAnalyzer;
        private LateralFileSystemConfiguration _configuration;
        private LateralFileSystemService _service;
        private bool _isVirtualizationBackendAvailable;
        private int _activationRequestId;
        private string _virtualizationBackendStatusMessage = "侧向文件系统原生后端可用。";
        private bool _disposed;

        private LateralFileSystemRuntime()
        {
            LateralFileSystemDebugConsole.Write("Runtime", "LateralFileSystemRuntime constructor start.");
            var pathProvider = new LateralFileSystemPathProvider();
            _configurationRepository = new LateralFileSystemConfigurationRepository(pathProvider);
            _treeRepository = new LateralFileSystemTreeRepository();
            _storageAnalyzer = new LateralFileSystemStorageAnalyzer();
            _configuration = CloneConfiguration(_configurationRepository.Load());
            LateralFileSystemDebugConsole.Write("Runtime", $"Loaded configuration: IsEnabled={_configuration.IsEnabled}; WorkingRootDirectory='{_configuration.WorkingRootDirectory}'.");
            _service = CreateService();
            RefreshVirtualizationBackendAvailabilityLocked();
            QueueActivateAllLocked();
            LateralFileSystemDebugConsole.Write("Runtime", "LateralFileSystemRuntime constructor end.");
        }

        public static LateralFileSystemRuntime Instance { get; } = new();

        public string ConfigurationFilePath => _configurationRepository.ConfigurationFilePath;

        public bool IsVirtualizationBackendAvailable
        {
            get
            {
                lock (_syncRoot)
                {
                    ThrowIfDisposed();
                    return _isVirtualizationBackendAvailable;
                }
            }
        }

        public string VirtualizationBackendStatusMessage
        {
            get
            {
                lock (_syncRoot)
                {
                    ThrowIfDisposed();
                    return _virtualizationBackendStatusMessage;
                }
            }
        }

        public event EventHandler? ConfigurationChanged;

        public event EventHandler? TreeChanged;

        public event EventHandler<LateralFileSystemSourceChangedEventArgs>? SourceChanged;

        public LateralFileSystemConfiguration GetConfiguration()
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                LateralFileSystemDebugConsole.Write("Runtime", "GetConfiguration invoked.");
                return CloneConfiguration(_configuration);
            }
        }

        public IReadOnlyList<LateralFileSystemNodeModel> GetNodes()
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                LateralFileSystemDebugConsole.Write("Runtime", $"GetNodes start; WorkingRootDirectory='{_configuration.WorkingRootDirectory}'.");

                var workingRootDirectory = _configuration.WorkingRootDirectory;
                if (string.IsNullOrWhiteSpace(workingRootDirectory))
                {
                    LateralFileSystemDebugConsole.Write("Runtime", "GetNodes returning empty because working root is not configured.");
                    return Array.Empty<LateralFileSystemNodeModel>();
                }

                var nodes = _treeRepository.Load(workingRootDirectory).Nodes
                    .Select(CloneNode)
                    .ToList();
                LateralFileSystemDebugConsole.Write("Runtime", $"GetNodes end; count={nodes.Count}.");
                return nodes;
            }
        }

        public LateralFileSystemNodeModel CreateProjection(string virtualFolderName, string projectionSourcePath)
        {
            return CreateProjection(virtualFolderName, projectionSourcePath, owner: "User");
        }

        public LateralFileSystemNodeModel CreateProjection(string virtualFolderName, string projectionSourcePath, string owner)
        {
            LateralFileSystemNodeModel node;
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                var workingRootDirectory = EnsureWorkingRootIsReadyForActivation();
                node = _service.CreateProjection(workingRootDirectory, virtualFolderName, owner, projectionSourcePath);
            }

            TreeChanged?.Invoke(this, EventArgs.Empty);
            return CloneNode(node);
        }

        public LateralFileSystemNodeModel CreateInheritance(string virtualFolderName, string parentNodeId, string projectionSourcePath)
        {
            return CreateInheritance(virtualFolderName, parentNodeId, projectionSourcePath, owner: "User");
        }

        public LateralFileSystemNodeModel CreateInheritance(string virtualFolderName, string parentNodeId, string projectionSourcePath, string owner)
        {
            LateralFileSystemNodeModel node;
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                var workingRootDirectory = EnsureWorkingRootIsReadyForActivation();
                node = _service.CreateInheritance(workingRootDirectory, virtualFolderName, owner, parentNodeId, projectionSourcePath);
            }

            TreeChanged?.Invoke(this, EventArgs.Empty);
            return CloneNode(node);
        }

        public void DeleteVirtualRoot(string nodeId)
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                var workingRootDirectory = EnsureWorkingRootIsConfigured();
                _service.DeleteVirtualRoot(workingRootDirectory, nodeId);
            }

            TreeChanged?.Invoke(this, EventArgs.Empty);
        }

        public LateralFileSystemMergeResult MergeVirtualRoot(string nodeId)
        {
            LateralFileSystemMergeResult result;
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                var workingRootDirectory = EnsureWorkingRootIsReadyForActivation();
                _service.ActivateAll(workingRootDirectory);
                result = _service.MergeVirtualRoot(workingRootDirectory, nodeId);
            }

            TreeChanged?.Invoke(this, EventArgs.Empty);
            return result;
        }

        public void SaveConfiguration(LateralFileSystemConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            lock (_syncRoot)
            {
                ThrowIfDisposed();

                var normalized = CloneConfiguration(configuration);
                var serviceNeedsRestart = !string.Equals(
                    _configuration.WorkingRootDirectory,
                    normalized.WorkingRootDirectory,
                    StringComparison.OrdinalIgnoreCase)
                    || _configuration.IsEnabled != normalized.IsEnabled;

                _configuration = normalized;
                _configurationRepository.Save(_configuration);

                if (serviceNeedsRestart)
                {
                    ResetServiceLocked();
                }

                QueueActivateAllLocked();
            }

            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
            TreeChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SaveNodeVisualPosition(string nodeId, double x, double y)
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                var workingRootDirectory = EnsureWorkingRootIsConfigured();
                var tree = _treeRepository.Load(workingRootDirectory);
                var node = tree.Nodes.FirstOrDefault(candidate => string.Equals(candidate.Id, nodeId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("待保存位置的侧向文件夹不存在。");

                UpsertProperty(node, "VisualX", x.ToString(CultureInfo.InvariantCulture));
                UpsertProperty(node, "VisualY", y.ToString(CultureInfo.InvariantCulture));
                node.UpdatedAtUtc = DateTime.UtcNow;
                _treeRepository.Save(workingRootDirectory, tree);
            }
        }

        public void SyncNode(string nodeId)
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                LateralFileSystemDebugConsole.Write("Runtime", $"SyncNode start; nodeId={nodeId}.");

                if (!_configuration.IsEnabled || string.IsNullOrWhiteSpace(_configuration.WorkingRootDirectory))
                {
                    LateralFileSystemDebugConsole.Write("Runtime", "SyncNode skipped because backend is disabled or working root is empty.");
                    return;
                }

                if (!_isVirtualizationBackendAvailable)
                {
                    LateralFileSystemDebugConsole.Write("Runtime", "SyncNode skipped because virtualization backend is unavailable.");
                    return;
                }

                _service.SyncNode(_configuration.WorkingRootDirectory, nodeId);
                LateralFileSystemDebugConsole.Write("Runtime", $"SyncNode end; nodeId={nodeId}.");
            }
        }

        public LateralFileSystemNodeStorageSummary GetNodeStorageSummary(string nodeId)
        {
            LateralFileSystemDebugConsole.Write("Runtime", $"GetNodeStorageSummary start; nodeId={nodeId}.");
            var virtualRootPath = GetNodeVirtualRootPath(nodeId);
            var summary = _storageAnalyzer.Analyze(virtualRootPath);
            LateralFileSystemDebugConsole.Write("Runtime", $"GetNodeStorageSummary end; nodeId={nodeId}; virtualRootPath='{virtualRootPath}'; files={summary.TotalFileCount}; directories={summary.TotalDirectoryCount}; hydratedBytes={summary.HydratedBytes}.");
            return summary;
        }

        public IReadOnlyList<LateralFileSystemFileEntryModel> GetNodeEntries(string nodeId, string relativeDirectoryPath)
        {
            LateralFileSystemDebugConsole.Write("Runtime", $"GetNodeEntries start; nodeId={nodeId}; relativeDirectoryPath='{relativeDirectoryPath}'.");
            var virtualRootPath = GetNodeVirtualRootPath(nodeId);
            var entries = _storageAnalyzer.GetEntries(virtualRootPath, relativeDirectoryPath);
            LateralFileSystemDebugConsole.Write("Runtime", $"GetNodeEntries end; nodeId={nodeId}; relativeDirectoryPath='{relativeDirectoryPath}'; count={entries.Count}; virtualRootPath='{virtualRootPath}'.");
            return entries;
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                _service.SourceChanged -= OnSourceChanged;
                _service.Dispose();
                _activationRequestId++;
                _disposed = true;
            }
        }

        private LateralFileSystemService CreateService()
        {
            var service = new LateralFileSystemService(_treeRepository, new LateralFileSystemProjectionHost(_treeRepository));
            service.SourceChanged += OnSourceChanged;
            return service;
        }

        private void ResetServiceLocked()
        {
            _service.SourceChanged -= OnSourceChanged;
            _service.Dispose();
            _service = CreateService();
        }

        private void QueueActivateAllLocked()
        {
            var requestId = ++_activationRequestId;
            _ = Task.Run(() => ActivateAllForRequest(requestId));
        }

        private void ActivateAllForRequest(int requestId)
        {
            var didRun = false;

            try
            {
                LateralFileSystemService service;
                string workingRootDirectory;

                lock (_syncRoot)
                {
                    if (!TryPrepareActivationLocked(requestId, out service, out workingRootDirectory))
                    {
                        return;
                    }
                }

                try
                {
                    service.ActivateAll(workingRootDirectory);
                    LateralFileSystemDebugConsole.Write("Runtime", "Background activation finished activating all nodes.");
                }
                catch (Exception ex) when (ProjFsNative.IsAvailabilityException(ex))
                {
                    lock (_syncRoot)
                    {
                        if (!_disposed && requestId == _activationRequestId)
                        {
                            _isVirtualizationBackendAvailable = false;
                            _virtualizationBackendStatusMessage = ProjFsNative.BuildAvailabilityErrorMessage(ex);
                        }
                    }

                    LateralFileSystemDebugConsole.WriteException("Runtime", ex, "Background activation failed due to virtualization backend availability");
                }

                didRun = true;
            }
            catch (Exception ex)
            {
                LateralFileSystemDebugConsole.WriteException("Runtime", ex, $"Background activation failed; requestId={requestId}");
            }

            if (didRun)
            {
                ConfigurationChanged?.Invoke(this, EventArgs.Empty);
                TreeChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private bool TryPrepareActivationLocked(int requestId, out LateralFileSystemService service, out string workingRootDirectory)
        {
            service = _service;
            workingRootDirectory = _configuration.WorkingRootDirectory;

            if (_disposed || requestId != _activationRequestId)
            {
                return false;
            }

            LateralFileSystemDebugConsole.Write("Runtime", $"TryPrepareActivationLocked start; IsEnabled={_configuration.IsEnabled}; WorkingRootDirectory='{_configuration.WorkingRootDirectory}'.");
            RefreshVirtualizationBackendAvailabilityLocked();

            if (!_configuration.IsEnabled || string.IsNullOrWhiteSpace(_configuration.WorkingRootDirectory))
            {
                LateralFileSystemDebugConsole.Write("Runtime", "TryPrepareActivationLocked skipped because backend is disabled or working root is empty.");
                return false;
            }

            if (!_isVirtualizationBackendAvailable)
            {
                LateralFileSystemDebugConsole.Write("Runtime", $"TryPrepareActivationLocked skipped because backend is unavailable: {_virtualizationBackendStatusMessage}");
                return false;
            }

            service = _service;
            workingRootDirectory = _configuration.WorkingRootDirectory;
            return true;
        }

        private void RefreshVirtualizationBackendAvailabilityLocked()
        {
            if (ProjFsNative.TryGetAvailability(out var unavailableReason))
            {
                _isVirtualizationBackendAvailable = true;
                LateralFileSystemDebugConsole.Write("Runtime", "Virtualization backend is available.");
                _virtualizationBackendStatusMessage = "侧向文件系统原生后端可用。";
                return;
            }

            _isVirtualizationBackendAvailable = false;
            _virtualizationBackendStatusMessage = unavailableReason ?? "侧向文件系统原生后端不可用。";
        }

        private string EnsureWorkingRootIsConfigured()
        {
            if (string.IsNullOrWhiteSpace(_configuration.WorkingRootDirectory))
            {
                throw new InvalidOperationException("请先在首选项中的“侧向文件系统”页设置工作根目录。");
            }

            return _configuration.WorkingRootDirectory;
        }

        private string EnsureWorkingRootIsReadyForActivation()
        {
            if (!_configuration.IsEnabled)
            {
                throw new InvalidOperationException("侧向文件系统当前未启用，请先在配置页面中启用后再创建或继承侧向文件夹。");
            }

            RefreshVirtualizationBackendAvailabilityLocked();
            if (!_isVirtualizationBackendAvailable)
            {
                throw new InvalidOperationException(_virtualizationBackendStatusMessage);
            }

            return EnsureWorkingRootIsConfigured();
        }

        private string GetNodeVirtualRootPath(string nodeId)
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();

                var workingRootDirectory = EnsureWorkingRootIsConfigured();
                var tree = _treeRepository.Load(workingRootDirectory);
                var node = tree.Nodes.FirstOrDefault(candidate => string.Equals(candidate.Id, nodeId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("指定的侧向文件夹不存在。");

                return node.VirtualRootPath;
            }
        }

        private void OnSourceChanged(object? sender, LateralFileSystemSourceChangedEventArgs e)
        {
            SourceChanged?.Invoke(this, e);
        }

        private static void UpsertProperty(LateralFileSystemNodeModel node, string key, string value)
        {
            var property = node.Properties.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
            if (property is null)
            {
                node.Properties.Add(new LateralFileSystemNodeProperty { Key = key, Value = value });
                return;
            }

            property.Value = value;
        }

        private static LateralFileSystemConfiguration CloneConfiguration(LateralFileSystemConfiguration configuration)
        {
            return new LateralFileSystemConfiguration
            {
                IsEnabled = configuration.IsEnabled,
                WorkingRootDirectory = configuration.WorkingRootDirectory?.Trim() ?? string.Empty
            };
        }

        private static LateralFileSystemNodeModel CloneNode(LateralFileSystemNodeModel source)
        {
            var clone = new LateralFileSystemNodeModel
            {
                Id = source.Id,
                Name = source.Name,
                VirtualRootPath = source.VirtualRootPath,
                Owner = source.Owner,
                Kind = source.Kind,
                ProjectionSourcePath = source.ProjectionSourcePath,
                ParentNodeId = source.ParentNodeId,
                IsActive = source.IsActive,
                ProviderInstanceId = source.ProviderInstanceId,
                ContentVersion = source.ContentVersion,
                CreatedAtUtc = source.CreatedAtUtc,
                UpdatedAtUtc = source.UpdatedAtUtc,
                SchemaVersion = source.SchemaVersion
            };

            foreach (var path in source.EditedRelativePaths)
            {
                clone.EditedRelativePaths.Add(path);
            }

            foreach (var path in source.LocalOnlyRelativePaths)
            {
                clone.LocalOnlyRelativePaths.Add(path);
            }

            foreach (var property in source.Properties)
            {
                clone.Properties.Add(new LateralFileSystemNodeProperty
                {
                    Key = property.Key,
                    Value = property.Value
                });
            }

            return clone;
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
