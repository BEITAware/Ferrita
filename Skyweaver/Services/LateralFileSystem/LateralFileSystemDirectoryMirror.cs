using System.IO;

namespace Skyweaver.Services.LateralFileSystem
{
    internal readonly record struct LateralFileSystemDirectoryOperationSummary(
        int FileCount,
        int DirectoryCount);

    internal readonly record struct LateralFileSystemDirectoryReplaceSummary(
        LateralFileSystemDirectoryOperationSummary Removed,
        LateralFileSystemDirectoryOperationSummary Restored);

    internal static class LateralFileSystemDirectoryMirror
    {
        private static readonly EnumerationOptions ImmediateEnumerationOptions = new()
        {
            AttributesToSkip = (FileAttributes)0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false
        };

        public static LateralFileSystemDirectoryOperationSummary CopyContents(
            string sourceRootDirectory,
            string destinationRootDirectory,
            bool overwrite)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceRootDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationRootDirectory);

            if (!Directory.Exists(sourceRootDirectory))
            {
                throw new DirectoryNotFoundException(sourceRootDirectory);
            }

            Directory.CreateDirectory(destinationRootDirectory);

            var counters = new DirectoryOperationCounters();
            foreach (var entryPath in EnumerateImmediateFileSystemEntries(sourceRootDirectory))
            {
                var entryName = Path.GetFileName(entryPath);
                if (string.IsNullOrWhiteSpace(entryName))
                {
                    continue;
                }

                var destinationPath = Path.Combine(destinationRootDirectory, entryName);
                if (IsDirectory(entryPath))
                {
                    CopyDirectoryTree(entryPath, destinationPath, overwrite, counters);
                }
                else
                {
                    CopyFile(entryPath, destinationPath, overwrite, counters);
                }
            }

            return counters.ToSummary();
        }

        public static LateralFileSystemDirectoryOperationSummary ClearContents(string rootDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
            Directory.CreateDirectory(rootDirectory);

            var counters = new DirectoryOperationCounters();
            foreach (var entryPath in EnumerateImmediateFileSystemEntries(rootDirectory))
            {
                if (IsDirectory(entryPath))
                {
                    DeleteDirectoryTree(entryPath, counters);
                }
                else
                {
                    DeleteFile(entryPath, counters);
                }
            }

            return counters.ToSummary();
        }

        public static LateralFileSystemDirectoryReplaceSummary ReplaceContentsWithRollback(
            string targetRootDirectory,
            string stagedSourceRootDirectory,
            string backupRootDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetRootDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(stagedSourceRootDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(backupRootDirectory);

            if (!Directory.Exists(stagedSourceRootDirectory))
            {
                throw new DirectoryNotFoundException(stagedSourceRootDirectory);
            }

            if (!Directory.Exists(backupRootDirectory))
            {
                throw new DirectoryNotFoundException(backupRootDirectory);
            }

            try
            {
                var removed = ClearContents(targetRootDirectory);
                var restored = CopyContents(stagedSourceRootDirectory, targetRootDirectory, overwrite: true);
                return new LateralFileSystemDirectoryReplaceSummary(removed, restored);
            }
            catch (Exception ex) when (IsExpectedFileSystemException(ex))
            {
                try
                {
                    ClearContents(targetRootDirectory);
                    CopyContents(backupRootDirectory, targetRootDirectory, overwrite: true);
                }
                catch (Exception rollbackEx) when (IsExpectedFileSystemException(rollbackEx))
                {
                    throw new InvalidOperationException(
                        "LateralFS Merge 失败，且回滚源目录内容时也失败。源目录可能需要人工检查。",
                        new AggregateException(ex, rollbackEx));
                }

                throw;
            }
        }

        public static void DeleteDirectoryIfExists(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                return;
            }

            var counters = new DirectoryOperationCounters();
            DeleteDirectoryTree(directoryPath, counters);
        }

        private static void CopyDirectoryTree(
            string sourceDirectory,
            string destinationDirectory,
            bool overwrite,
            DirectoryOperationCounters counters)
        {
            DeleteConflictingFile(destinationDirectory);
            Directory.CreateDirectory(destinationDirectory);
            counters.DirectoryCount++;

            if (IsLinkLikeReparsePoint(sourceDirectory))
            {
                ApplyDirectoryMetadata(sourceDirectory, destinationDirectory);
                return;
            }

            foreach (var entryPath in EnumerateImmediateFileSystemEntries(sourceDirectory))
            {
                var entryName = Path.GetFileName(entryPath);
                if (string.IsNullOrWhiteSpace(entryName))
                {
                    continue;
                }

                var destinationPath = Path.Combine(destinationDirectory, entryName);
                if (IsDirectory(entryPath))
                {
                    CopyDirectoryTree(entryPath, destinationPath, overwrite, counters);
                }
                else
                {
                    CopyFile(entryPath, destinationPath, overwrite, counters);
                }
            }

            ApplyDirectoryMetadata(sourceDirectory, destinationDirectory);
        }

        private static void CopyFile(
            string sourceFile,
            string destinationFile,
            bool overwrite,
            DirectoryOperationCounters counters)
        {
            var destinationDirectory = Path.GetDirectoryName(destinationFile);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            if (Directory.Exists(destinationFile))
            {
                var deleteCounters = new DirectoryOperationCounters();
                DeleteDirectoryTree(destinationFile, deleteCounters);
            }

            if (File.Exists(destinationFile))
            {
                if (!overwrite)
                {
                    throw new IOException($"目标文件已存在：{destinationFile}");
                }

                PrepareForMutation(destinationFile);
            }

            File.Copy(sourceFile, destinationFile, overwrite);
            ApplyFileMetadata(sourceFile, destinationFile);
            counters.FileCount++;
        }

        private static void DeleteDirectoryTree(string directoryPath, DirectoryOperationCounters counters)
        {
            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            PrepareForMutation(directoryPath);

            if (!IsLinkLikeReparsePoint(directoryPath))
            {
                foreach (var entryPath in EnumerateImmediateFileSystemEntries(directoryPath))
                {
                    if (IsDirectory(entryPath))
                    {
                        DeleteDirectoryTree(entryPath, counters);
                    }
                    else
                    {
                        DeleteFile(entryPath, counters);
                    }
                }
            }

            Directory.Delete(directoryPath, recursive: false);
            counters.DirectoryCount++;
        }

        private static void DeleteFile(string filePath, DirectoryOperationCounters counters)
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            PrepareForMutation(filePath);
            File.Delete(filePath);
            counters.FileCount++;
        }

        private static void DeleteConflictingFile(string directoryPath)
        {
            if (!File.Exists(directoryPath))
            {
                return;
            }

            var counters = new DirectoryOperationCounters();
            DeleteFile(directoryPath, counters);
        }

        private static IEnumerable<string> EnumerateImmediateFileSystemEntries(string directoryPath)
        {
            return Directory.EnumerateFileSystemEntries(directoryPath, "*", ImmediateEnumerationOptions);
        }

        private static bool IsDirectory(string path)
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.Directory);
        }

        private static bool IsLinkLikeReparsePoint(string path)
        {
            try
            {
                if (!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                {
                    return false;
                }

                if (Directory.Exists(path))
                {
                    return !string.IsNullOrWhiteSpace(new DirectoryInfo(path).LinkTarget);
                }

                if (File.Exists(path))
                {
                    return !string.IsNullOrWhiteSpace(new FileInfo(path).LinkTarget);
                }

                return false;
            }
            catch (Exception ex) when (IsExpectedFileSystemException(ex))
            {
                return false;
            }
        }

        private static void PrepareForMutation(string path)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                }
            }
            catch (Exception ex) when (IsExpectedFileSystemException(ex))
            {
            }
        }

        private static void ApplyFileMetadata(string sourceFile, string destinationFile)
        {
            TryApplyTimestamps(sourceFile, destinationFile, isDirectory: false);
            TryApplyAttributes(sourceFile, destinationFile);
        }

        private static void ApplyDirectoryMetadata(string sourceDirectory, string destinationDirectory)
        {
            TryApplyTimestamps(sourceDirectory, destinationDirectory, isDirectory: true);
            TryApplyAttributes(sourceDirectory, destinationDirectory);
        }

        private static void TryApplyTimestamps(string sourcePath, string destinationPath, bool isDirectory)
        {
            try
            {
                if (isDirectory)
                {
                    Directory.SetCreationTimeUtc(destinationPath, Directory.GetCreationTimeUtc(sourcePath));
                    Directory.SetLastAccessTimeUtc(destinationPath, Directory.GetLastAccessTimeUtc(sourcePath));
                    Directory.SetLastWriteTimeUtc(destinationPath, Directory.GetLastWriteTimeUtc(sourcePath));
                    return;
                }

                File.SetCreationTimeUtc(destinationPath, File.GetCreationTimeUtc(sourcePath));
                File.SetLastAccessTimeUtc(destinationPath, File.GetLastAccessTimeUtc(sourcePath));
                File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));
            }
            catch (Exception ex) when (IsExpectedFileSystemException(ex))
            {
            }
        }

        private static void TryApplyAttributes(string sourcePath, string destinationPath)
        {
            try
            {
                var attributes = File.GetAttributes(sourcePath);
                attributes &= ~FileAttributes.Directory;
                attributes &= ~FileAttributes.ReparsePoint;
                attributes &= ~FileAttributes.Offline;
                File.SetAttributes(destinationPath, attributes);
            }
            catch (Exception ex) when (IsExpectedFileSystemException(ex))
            {
            }
        }

        private static bool IsExpectedFileSystemException(Exception ex)
        {
            return ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or DirectoryNotFoundException
                or FileNotFoundException;
        }

        private sealed class DirectoryOperationCounters
        {
            public int FileCount { get; set; }

            public int DirectoryCount { get; set; }

            public LateralFileSystemDirectoryOperationSummary ToSummary()
            {
                return new LateralFileSystemDirectoryOperationSummary(FileCount, DirectoryCount);
            }
        }
    }
}
