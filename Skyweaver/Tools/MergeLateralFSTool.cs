using System.IO;
using System.Linq;
using System.Windows;
using Skyweaver.Controls.ChatSessionControl.Views;
using Skyweaver.Models.LateralFileSystem;
using Skyweaver.Services.LateralFileSystem;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class MergeLateralFSTool : ISkyweaverTool, ISkyweaverToolInvocationPresentationProvider
    {
        public const string ToolName = "MergeLateralFS";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Merge a LateralFS node owned by the current sessionId back into its projection source. This replaces all source folder contents with the node's current virtual folder view, then removes the LateralFS node and virtual folder. NodeName is optional; if omitted, the newest node owned by the current session is merged.",
            "Script",
            [
                new SkyweaverToolParameterDefinition(
                    "NodeName",
                    "Optional LateralFS node name to merge. If omitted, the newest LateralFS node owned by the current session is merged.",
                    SkyweaverToolParameterType.String,
                    isRequired: false)
            ]);

        public SkyweaverToolDefinition Definition => s_definition;

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return ToolInvocationCardFactory.Create(
                context,
                [
                    new ToolInvocationCardFieldDefinition("Node", "NodeName", "Newest session-owned node")
                ]);
        }

        public Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var requestedNodeName = arguments.GetString("NodeName") ?? string.Empty;

            try
            {
                var sessionId = LateralFileSystemSessionToolSupport.GetRequiredSessionId(context);
                var runtime = LateralFileSystemRuntime.Instance;
                var node = ResolveNode(runtime, sessionId, requestedNodeName);
                if (node is null)
                {
                    var message = string.IsNullOrWhiteSpace(requestedNodeName)
                        ? "No LateralFS node owned by the current session exists."
                        : $"No LateralFS node named '{requestedNodeName}' is owned by the current session.";

                    return Task.FromResult(SkyweaverToolResult.Failure(
                        message,
                        BuildData(null, requestedNodeName, sessionId, null, null, null, null, null, null, null, null)));
                }

                var result = runtime.MergeVirtualRoot(node.Id);

                return Task.FromResult(SkyweaverToolResult.Success(
                    $"Merged LateralFS node '{result.NodeName}' into its source folder and removed the node.",
                    BuildData(
                        result.NodeId,
                        result.NodeName,
                        sessionId,
                        result.ProjectionSourcePath,
                        result.VirtualRootPath,
                        result.SnapshotFileCount,
                        result.SnapshotDirectoryCount,
                        result.RemovedSourceFileCount,
                        result.RemovedSourceDirectoryCount,
                        result.RestoredSourceFileCount,
                        result.RestoredSourceDirectoryCount)));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsExpectedException(ex))
            {
                return Task.FromResult(SkyweaverToolResult.Failure(
                    $"Failed to merge LateralFS node: {ex.Message}",
                    BuildData(null, requestedNodeName, null, null, null, null, null, null, null, null, null)));
            }
        }

        private static LateralFileSystemNodeModel? ResolveNode(
            LateralFileSystemRuntime runtime,
            string sessionId,
            string requestedNodeName)
        {
            if (string.IsNullOrWhiteSpace(requestedNodeName))
            {
                return LateralFileSystemSessionToolSupport.FindLatestSessionOwnedNode(runtime, sessionId);
            }

            return runtime.GetNodes()
                .Where(node => string.Equals(node.Owner, sessionId, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(node => string.Equals(node.Name, requestedNodeName.Trim(), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(node.Id, requestedNodeName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private static IReadOnlyDictionary<string, object?> BuildData(
            string? nodeId,
            string? nodeName,
            string? owner,
            string? projectionSourcePath,
            string? virtualRootPath,
            int? snapshotFileCount,
            int? snapshotDirectoryCount,
            int? removedSourceFileCount,
            int? removedSourceDirectoryCount,
            int? restoredSourceFileCount,
            int? restoredSourceDirectoryCount)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["nodeId"] = nodeId,
                ["nodeName"] = nodeName,
                ["owner"] = owner,
                ["projectionSourcePath"] = projectionSourcePath,
                ["virtualRootPath"] = virtualRootPath,
                ["snapshotFileCount"] = snapshotFileCount,
                ["snapshotDirectoryCount"] = snapshotDirectoryCount,
                ["removedSourceFileCount"] = removedSourceFileCount,
                ["removedSourceDirectoryCount"] = removedSourceDirectoryCount,
                ["restoredSourceFileCount"] = restoredSourceFileCount,
                ["restoredSourceDirectoryCount"] = restoredSourceDirectoryCount
            };
        }

        private static bool IsExpectedException(Exception ex)
        {
            return ex is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException
                or NotSupportedException;
        }
    }
}
