// BethesdaArchiveService.cs — Orchestrates BSA/BA2 texture compression

using System.Diagnostics;

namespace RenoDXCommander.Services;

public class BethesdaArchiveService : IBethesdaArchiveService
{
    private static readonly string[] _archiveExtensions = [".bsa", ".ba2"];

    public string? TexconvPath { get; set; }

    public string GetBackupFolder(string gameFolder) =>
        Path.Combine(gameFolder, "_ntc_archive_backup");

    public async Task<BethesdaScanResult> ScanGameAsync(
        string gameFolder, CancellationToken ct = default)
    {
        var archives = new List<BethesdaArchiveInfo>();
        long totalVram = 0;
        int totalTex = 0;

        await Task.Run(() =>
        {
            foreach (var file in Directory.EnumerateFiles(gameFolder, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!_archiveExtensions.Contains(ext)) continue;

                if (ext == ".ba2")
                {
                    var count = Ba2Archive.CountTextures(file);
                    if (count <= 0) continue;
                    var size = EstimateArchiveVram(file, count);
                    var type = DetectBa2Type(file);
                    archives.Add(new BethesdaArchiveInfo(file, type, count, size));
                    totalTex   += count;
                    totalVram  += size;
                }
                else // .bsa
                {
                    var result = BsaArchive.CountTextures(file);
                    if (result == -1) continue; // not a valid BSA
                    var fileInfo = new FileInfo(file);
                    // Estimate: average DDS texture ~2 MB uncompressed VRAM
                    var count = result == -2 ? (int)(fileInfo.Length / (2L * 1024 * 1024)) : result;
                    if (count == 0) count = 1;
                    archives.Add(new BethesdaArchiveInfo(file, "BSA", count, fileInfo.Length));
                    totalTex  += count;
                    totalVram += fileInfo.Length;
                }
            }
        }, ct);

        return new BethesdaScanResult(archives, totalTex, totalVram);
    }

    public async Task<BethesdaCompressResult> CompressArchivesAsync(
        BethesdaScanResult scan,
        string gameFolder,
        IProgress<NtcProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(TexconvPath) || !File.Exists(TexconvPath))
            throw new InvalidOperationException("texconv.exe no configurado.");

        var backupDir = GetBackupFolder(gameFolder);
        Directory.CreateDirectory(backupDir);

        int processed = 0, failed = 0;
        long vramSaved = 0;
        var total = scan.Archives.Count;

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var archive = scan.Archives[i];
            var archiveName = Path.GetFileName(archive.Path);
            progress?.Report(new NtcProgress($"Procesando {archiveName}…", i, total));

            var stagingDir = Path.Combine(
                Path.GetTempPath(), "SupremeRHI_NTC", Path.GetFileNameWithoutExtension(archive.Path));
            Directory.CreateDirectory(stagingDir);

            try
            {
                // Backup original
                var backupPath = Path.Combine(backupDir,
                    Path.GetRelativePath(gameFolder, archive.Path));
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                if (!File.Exists(backupPath))
                    File.Copy(archive.Path, backupPath);

                // Extract textures
                progress?.Report(new NtcProgress($"{archiveName}: extrayendo…", i, total));
                List<string> textures;
                if (archive.Type.StartsWith("BA2"))
                    textures = await Ba2Archive.ExtractTexturesAsync(archive.Path, stagingDir, ct);
                else
                    textures = await BsaArchive.ExtractTexturesAsync(archive.Path, stagingDir, ct);

                if (textures.Count == 0) { processed++; continue; }

                // Compress with texconv
                progress?.Report(new NtcProgress($"{archiveName}: comprimiendo {textures.Count} texturas…", i, total));
                int compressed = 0;
                foreach (var dds in textures)
                {
                    ct.ThrowIfCancellationRequested();
                    var origSize = new FileInfo(dds).Length;
                    if (await RunTexconvAsync(dds, ct))
                    {
                        var newSize = new FileInfo(dds).Length;
                        vramSaved += Math.Max(0, origSize - newSize);
                        compressed++;
                    }
                }

                if (compressed == 0) { processed++; continue; }

                // Repack
                progress?.Report(new NtcProgress($"{archiveName}: reempaquetando…", i, total));
                var tempOut = archive.Path + ".ntc_tmp";
                if (archive.Type.StartsWith("BA2"))
                    await Ba2Archive.RepackAsync(archive.Path, tempOut, stagingDir, ct);
                else
                    await BsaArchive.RepackAsync(archive.Path, tempOut, stagingDir, ct);

                File.Replace(tempOut, archive.Path, null);
                processed++;
            }
            catch
            {
                failed++;
                try { if (File.Exists(archive.Path + ".ntc_tmp")) File.Delete(archive.Path + ".ntc_tmp"); }
                catch { /* best effort */ }
            }
            finally
            {
                // Clean staging
                try { Directory.Delete(stagingDir, recursive: true); } catch { }
            }
        }

        progress?.Report(new NtcProgress("Completado", total, total));
        return new BethesdaCompressResult(processed, failed, Math.Max(0, vramSaved));
    }

    public async Task<int> RestoreBackupsAsync(string gameFolder)
    {
        var backupDir = GetBackupFolder(gameFolder);
        if (!Directory.Exists(backupDir)) return 0;

        int restored = 0;
        await Task.Run(() =>
        {
            foreach (var backup in Directory.EnumerateFiles(backupDir, "*", SearchOption.AllDirectories)
                .Where(f => _archiveExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
            {
                var relative = Path.GetRelativePath(backupDir, backup);
                var target   = Path.Combine(gameFolder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(backup, target, overwrite: true);
                restored++;
            }
        });
        return restored;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<bool> RunTexconvAsync(string ddsPath, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(ddsPath)!;
        // Auto-pick BC format based on filename hints
        var name = Path.GetFileNameWithoutExtension(ddsPath).ToUpperInvariant();
        string format = name.Contains("_N") || name.Contains("NRM") || name.Contains("NORM")
            ? "BC5_UNORM"
            : name.Contains("_R") || name.Contains("_ROUGH") || name.Contains("_AO")
              ? "BC4_UNORM"
              : "BC7_UNORM_SRGB";

        var psi = new ProcessStartInfo(TexconvPath!)
        {
            Arguments = $"-nologo -y -f {format} -o \"{dir}\" \"{ddsPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        try
        {
            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string DetectBa2Type(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);
            br.ReadUInt32(); // magic
            br.ReadUInt32(); // version
            var type = new string(br.ReadChars(4));
            return type.TrimEnd('\0') == "DX10" ? "BA2-DX10" : "BA2-GNRL";
        }
        catch { return "BA2-GNRL"; }
    }

    private static long EstimateArchiveVram(string path, int textureCount) =>
        textureCount * 4L * 1024 * 1024; // ~4 MB average per texture uncompressed
}
