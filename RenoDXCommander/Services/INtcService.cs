// INtcService.cs — Interface for Neural Texture Compression (BCn via texconv)

namespace RenoDXCommander.Services;

/// <summary>Progress report during NTC compression batch.</summary>
public record NtcProgress(string Message, int Done, int Total);

/// <summary>Info about a single DDS texture found during scan.</summary>
public record NtcTextureInfo(
    string FilePath,
    string FormatName,
    int Width,
    int Height,
    long VramBytes,
    bool IsCompressed);

/// <summary>Result of scanning a game folder for compressible textures.</summary>
public record NtcScanResult(
    List<NtcTextureInfo> Textures,
    long TotalVramBytes,
    long UncompressedVramBytes,
    int UncompressedCount);

/// <summary>Result of a compression batch.</summary>
public record NtcCompressResult(int Compressed, int Failed, long VramSavedBytes);

/// <summary>
/// Manages BCn texture compression for games using texconv (Microsoft DirectXTex).
/// Compresses uncompressed DDS textures to BC7/BC5/BC4 to free VRAM.
/// </summary>
public interface INtcService
{
    /// <summary>Path to texconv.exe, or null if not configured.</summary>
    string? TexconvPath { get; set; }

    /// <summary>True if texconv.exe exists at the configured path.</summary>
    bool IsTexconvAvailable { get; }

    /// <summary>Scans a game folder and returns a list of compressible DDS textures.</summary>
    Task<NtcScanResult> ScanGameAsync(string gameFolder, CancellationToken ct = default);

    /// <summary>
    /// Compresses uncompressed textures from a prior scan result.
    /// Makes backups before modifying files.
    /// </summary>
    Task<NtcCompressResult> CompressAsync(
        NtcScanResult scan,
        string gameFolder,
        IProgress<NtcProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Restores backed-up originals for a game folder.</summary>
    Task<int> RestoreBackupsAsync(string gameFolder);

    /// <summary>Returns the backup folder path for a given game folder.</summary>
    string GetBackupFolder(string gameFolder);
}
