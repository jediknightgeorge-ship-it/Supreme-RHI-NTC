// IBethesdaArchiveService.cs — Interface for BSA/BA2 Bethesda archive texture compression

namespace RenoDXCommander.Services;

public record BethesdaArchiveInfo(
    string Path,
    string Type,          // "BA2-GNRL", "BA2-DX10", "BSA"
    int TextureCount,
    long UnpackedBytes);

public record BethesdaScanResult(
    List<BethesdaArchiveInfo> Archives,
    int TotalTextures,
    long TotalUnpackedBytes);

public record BethesdaCompressResult(int Processed, int Failed, long VramSavedBytes);

/// <summary>
/// Extracts DDS textures from Bethesda BSA/BA2 archives, compresses them via texconv,
/// and repacks into a new archive. Originals are backed up before modification.
/// Supports: Skyrim BSA (v104/v105), Fallout 4 BA2-GNRL, BA2-DX10.
/// </summary>
public interface IBethesdaArchiveService
{
    /// <summary>Path to texconv.exe used for compression.</summary>
    string? TexconvPath { get; set; }

    /// <summary>Scans a game folder for BSA/BA2 archives that contain textures.</summary>
    Task<BethesdaScanResult> ScanGameAsync(string gameFolder, CancellationToken ct = default);

    /// <summary>
    /// Compresses textures inside the archives found in a prior scan.
    /// Backs up originals, extracts DDS files, runs texconv, repacks.
    /// </summary>
    Task<BethesdaCompressResult> CompressArchivesAsync(
        BethesdaScanResult scan,
        string gameFolder,
        IProgress<NtcProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Restores backed-up original archives for a game folder.</summary>
    Task<int> RestoreBackupsAsync(string gameFolder);

    /// <summary>Returns the backup folder path for a given game folder.</summary>
    string GetBackupFolder(string gameFolder);
}
