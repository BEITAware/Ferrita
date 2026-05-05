namespace Skyweaver.Models.LateralFileSystem
{
    public sealed class LateralFileSystemMergeResult
    {
        public string NodeId { get; init; } = string.Empty;

        public string NodeName { get; init; } = string.Empty;

        public string VirtualRootPath { get; init; } = string.Empty;

        public string ProjectionSourcePath { get; init; } = string.Empty;

        public int SnapshotFileCount { get; init; }

        public int SnapshotDirectoryCount { get; init; }

        public int RemovedSourceFileCount { get; init; }

        public int RemovedSourceDirectoryCount { get; init; }

        public int RestoredSourceFileCount { get; init; }

        public int RestoredSourceDirectoryCount { get; init; }
    }
}
