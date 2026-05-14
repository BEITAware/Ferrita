using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Skyweaver.Controls.ChatSessionControl.Views;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class HashFileTool :
        ISkyweaverTool,
        ISkyweaverToolInvocationPresentationProvider,
        ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "HashFile";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Computes a cryptographic hash (MD5, SHA1, SHA256, SHA512) for a specified file.",
            "Earth",
            [
                new SkyweaverToolParameterDefinition(
                    "FilePath",
                    "The absolute or relative path to the file. Relative paths are resolved against the current workspace.",
                    SkyweaverToolParameterType.String,
                    isRequired: true),
                new SkyweaverToolParameterDefinition(
                    "Algorithm",
                    "The hash algorithm to use. Valid values are MD5, SHA1, SHA256, SHA512. Defaults to SHA256.",
                    SkyweaverToolParameterType.String,
                    isRequired: false,
                    defaultValue: "SHA256")
            ],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.Allow);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "Computes the cryptographic hash of a file. This is useful to verify file integrity or check if two files are identical. Supports MD5, SHA1, SHA256, and SHA512.";
        }

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return ToolInvocationCardFactory.Create(
                context,
                [
                    new ToolInvocationCardFieldDefinition("File Path", "FilePath", "Missing path"),
                    new ToolInvocationCardFieldDefinition("Algorithm", "Algorithm", "SHA256")
                ]);
        }

        public async Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var requestedPath = arguments.GetString("FilePath");
            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                return SkyweaverToolResult.Failure("FilePath parameter is required and cannot be empty.");
            }

            var algorithmName = arguments.GetString("Algorithm")?.Trim().ToUpperInvariant() ?? "SHA256";

            try
            {
                var resolvedPath = ToolFileSystemHelper.ResolvePath(requestedPath, context.WorkspacePath);

                if (!File.Exists(resolvedPath))
                {
                    return SkyweaverToolResult.Failure($"The file does not exist: {resolvedPath}");
                }

                using var hashAlgorithm = CreateHashAlgorithm(algorithmName);
                if (hashAlgorithm == null)
                {
                    return SkyweaverToolResult.Failure($"Unsupported algorithm: {algorithmName}. Supported algorithms are MD5, SHA1, SHA256, SHA512.");
                }

                using var stream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                // Read and compute hash asynchronously to avoid blocking
                byte[] hashBytes = await hashAlgorithm.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
                string hashString = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

                return SkyweaverToolResult.Success($"Hash ({algorithmName}) of {Path.GetFileName(resolvedPath)}: {hashString}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HashFileTool execution failed: {ex}");
                return SkyweaverToolResult.Failure($"Failed to compute hash: {ex.Message}");
            }
        }

        private static HashAlgorithm? CreateHashAlgorithm(string name)
        {
            return name switch
            {
                "MD5" => MD5.Create(),
                "SHA1" => SHA1.Create(),
                "SHA256" => SHA256.Create(),
                "SHA512" => SHA512.Create(),
                _ => null
            };
        }
    }
}
