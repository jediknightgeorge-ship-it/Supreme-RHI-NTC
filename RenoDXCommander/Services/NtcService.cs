// NtcService.cs — BCn texture compression via texconv (Microsoft DirectXTex)

using System.Diagnostics;
using System.Text;

namespace RenoDXCommander.Services;

public class NtcService : INtcService
{
    private static readonly HashSet<string> _ddsExtensions = new(StringComparer.OrdinalIgnoreCase) { ".dds" };

    // DDS FourCC constants
    private const uint DDPF_RGB    = 0x40;
    private const uint DDPF_ALPHA  = 0x02;
    private const uint DDPF_FOURCC = 0x04;
    private const uint DDS_MAGIC   = 0x20534444; // "DDS "

    // Uncompressed DX10 DXGI formats
    private static readonly HashSet<uint> _uncompressedDxgiFormats = new()
    {
        28,  // DXGI_FORMAT_R8G8B8A8_UNORM
        29,  // DXGI_FORMAT_R8G8B8A8_UNORM_SRGB
        87,  // DXGI_FORMAT_B8G8R8A8_UNORM
        56,  // DXGI_FORMAT_R8_UNORM
        57,  // DXGI_FORMAT_R8_UINT
        49,  // DXGI_FORMAT_R16G16_UNORM
        35,  // DXGI_FORMAT_R16G16B16A16_FLOAT
        2,   // DXGI_FORMAT_R32G32B32A32_FLOAT
        10,  // DXGI_FORMAT_R16G16B16A16_UNORM
    };

    // BC-compressed DXGI formats
    private static readonly HashSet<uint> _compressedDxgiFormats = new()
    {
        71,72,73,74,75,76,77,78,79,80,81,82,83,84,85,86,  // BC1–BC5
        95,96,97,98,99,                                    // BC6H
        98,99,100,101,102,103,104,105,106,107,108,109,110,111,  // BC7
    };

    private string? _texconvPath;

    public string? TexconvPath
    {
        get => _texconvPath;
        set => _texconvPath = value;
    }

    public bool IsTexconvAvailable =>
        !string.IsNullOrEmpty(_texconvPath) && File.Exists(_texconvPath);

    public async Task<NtcScanResult> ScanGameAsync(string gameFolder, CancellationToken ct = default)
    {
        var textures = new List<NtcTextureInfo>();
        long totalVram = 0;
        long uncompressedVram = 0;
        int uncompressedCount = 0;

        await Task.Run(() =>
        {
            foreach (var file in Directory.EnumerateFiles(gameFolder, "*.dds", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (!TryReadDdsInfo(file, out var info)) continue;
                textures.Add(info!);
                totalVram += info!.VramBytes;
                if (!info.IsCompressed)
                {
                    uncompressedVram += info.VramBytes;
                    uncompressedCount++;
                }
            }
        }, ct);

        return new NtcScanResult(textures, totalVram, uncompressedVram, uncompressedCount);
    }

    public async Task<NtcCompressResult> CompressAsync(
        NtcScanResult scan,
        string gameFolder,
        IProgress<NtcProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsTexconvAvailable)
            throw new InvalidOperationException("texconv.exe not configured.");

        var uncompressed = scan.Textures.Where(t => !t.IsCompressed).ToList();
        var backupDir = GetBackupFolder(gameFolder);
        Directory.CreateDirectory(backupDir);

        int done = 0, failed = 0;
        long vramSaved = 0;

        foreach (var tex in uncompressed)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new NtcProgress($"Comprimiendo: {Path.GetFileName(tex.FilePath)}", done, uncompressed.Count));

            var backupPath = Path.Combine(backupDir, Path.GetRelativePath(gameFolder, tex.FilePath));
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            if (!File.Exists(backupPath))
                File.Copy(tex.FilePath, backupPath);

            var format = PickBcFormat(tex);
            var success = await RunTexconvAsync(tex.FilePath, format, ct);
            if (success)
            {
                vramSaved += tex.VramBytes - EstimateCompressedSize(tex, format);
                done++;
            }
            else
            {
                failed++;
            }
        }

        progress?.Report(new NtcProgress("Completado", done, uncompressed.Count));
        return new NtcCompressResult(done, failed, Math.Max(0, vramSaved));
    }

    public async Task<int> RestoreBackupsAsync(string gameFolder)
    {
        var backupDir = GetBackupFolder(gameFolder);
        if (!Directory.Exists(backupDir)) return 0;

        int restored = 0;
        await Task.Run(() =>
        {
            foreach (var backup in Directory.EnumerateFiles(backupDir, "*.dds", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(backupDir, backup);
                var target = Path.Combine(gameFolder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(backup, target, overwrite: true);
                restored++;
            }
        });
        return restored;
    }

    public string GetBackupFolder(string gameFolder) =>
        Path.Combine(gameFolder, "_ntc_backup");

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool TryReadDdsInfo(string path, out NtcTextureInfo? info)
    {
        info = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);

            if (br.ReadUInt32() != DDS_MAGIC) return false;
            fs.Seek(4, SeekOrigin.Current); // header size
            var flags    = br.ReadUInt32();
            var height   = (int)br.ReadUInt32();
            var width    = (int)br.ReadUInt32();
            fs.Seek(8, SeekOrigin.Current); // pitch/linearSize, depth
            var mipCount = (int)br.ReadUInt32();
            if (mipCount < 1) mipCount = 1;
            fs.Seek(44, SeekOrigin.Current); // reserved

            // PixelFormat
            fs.Seek(4, SeekOrigin.Current); // size
            var pfFlags  = br.ReadUInt32();
            var fourCC   = br.ReadUInt32();
            var rgbBits  = br.ReadUInt32();
            fs.Seek(16, SeekOrigin.Current); // masks

            bool isDx10 = (pfFlags & DDPF_FOURCC) != 0 && fourCC == MakeFourCC('D','X','1','0');
            bool isCompressed;
            string formatName;
            int bpp;

            if (isDx10)
            {
                var dxgiFormat = br.ReadUInt32();
                isCompressed = _compressedDxgiFormats.Contains(dxgiFormat);
                formatName = DxgiFormatName(dxgiFormat);
                bpp = isCompressed ? BcBpp(dxgiFormat) : (int)rgbBits;
                if (bpp == 0) bpp = 32;
            }
            else if ((pfFlags & DDPF_FOURCC) != 0)
            {
                isCompressed = IsFourCCCompressed(fourCC);
                formatName = FourCCName(fourCC);
                bpp = FourCCBpp(fourCC);
            }
            else
            {
                isCompressed = false;
                bpp = (int)rgbBits;
                formatName = $"RGB{bpp}";
            }

            long vram = EstimateVram(width, height, mipCount, bpp, isCompressed);
            info = new NtcTextureInfo(path, formatName, width, height, vram, isCompressed);
            return true;
        }
        catch { return false; }
    }

    private static long EstimateVram(int w, int h, int mips, int bpp, bool compressed)
    {
        long total = 0;
        int cw = w, ch = h;
        int blockSize = compressed ? (bpp <= 4 ? 8 : 16) : 0;
        for (int i = 0; i < mips; i++)
        {
            if (compressed)
            {
                int bw = Math.Max(1, (cw + 3) / 4);
                int bh = Math.Max(1, (ch + 3) / 4);
                total += (long)bw * bh * blockSize;
            }
            else
            {
                total += (long)cw * ch * bpp / 8;
            }
            cw = Math.Max(1, cw / 2);
            ch = Math.Max(1, ch / 2);
        }
        return total;
    }

    private static long EstimateCompressedSize(NtcTextureInfo tex, string bcFormat)
    {
        int blockBytes = bcFormat is "BC1" or "BC4" ? 8 : 16;
        int bw = Math.Max(1, (tex.Width + 3) / 4);
        int bh = Math.Max(1, (tex.Height + 3) / 4);
        return (long)bw * bh * blockBytes;
    }

    private static string PickBcFormat(NtcTextureInfo tex)
    {
        var name = tex.FormatName.ToUpperInvariant();
        // Grayscale/single-channel → BC4
        if (name.Contains("R8_") || name.Contains("R8U") || name.Contains("_L8") || name.Contains("LUM"))
            return "BC4_UNORM";
        // Normal maps (RG) → BC5
        if (name.Contains("RG") || name.Contains("_G8") || name.Contains("NRM") || name.Contains("NORM"))
            return "BC5_UNORM";
        // General color → BC7
        return "BC7_UNORM_SRGB";
    }

    private async Task<bool> RunTexconvAsync(string ddsPath, string format, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(ddsPath)!;
        var psi = new ProcessStartInfo(_texconvPath!)
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

    // ── FourCC helpers ───────────────────────────────────────────────────────

    private static uint MakeFourCC(char a, char b, char c, char d) =>
        (uint)a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);

    private static bool IsFourCCCompressed(uint cc) => cc is var c &&
        (c == MakeFourCC('D','X','T','1') || c == MakeFourCC('D','X','T','3') ||
         c == MakeFourCC('D','X','T','5') || c == MakeFourCC('B','C','4','U') ||
         c == MakeFourCC('B','C','4','S') || c == MakeFourCC('A','T','I','2') ||
         c == MakeFourCC('B','C','5','U') || c == MakeFourCC('B','C','5','S'));

    private static string FourCCName(uint cc)
    {
        var b = new byte[4];
        b[0] = (byte)(cc & 0xFF); b[1] = (byte)((cc >> 8) & 0xFF);
        b[2] = (byte)((cc >> 16) & 0xFF); b[3] = (byte)((cc >> 24) & 0xFF);
        return Encoding.ASCII.GetString(b).TrimEnd('\0');
    }

    private static int FourCCBpp(uint cc)
    {
        if (cc == MakeFourCC('D','X','T','1') || cc == MakeFourCC('B','C','4','U') || cc == MakeFourCC('B','C','4','S')) return 4;
        return 8;
    }

    private static string DxgiFormatName(uint fmt) => fmt switch
    {
        28 => "R8G8B8A8_UNORM",       29 => "R8G8B8A8_UNORM_SRGB",
        87 => "B8G8R8A8_UNORM",       56 => "R8_UNORM",
        71 => "BC1_UNORM",            72 => "BC1_UNORM_SRGB",
        74 => "BC2_UNORM",            75 => "BC2_UNORM_SRGB",
        77 => "BC3_UNORM",            78 => "BC3_UNORM_SRGB",
        80 => "BC4_UNORM",            81 => "BC4_SNORM",
        83 => "BC5_UNORM",            84 => "BC5_SNORM",
        95 => "BC6H_UF16",            96 => "BC6H_SF16",
        98 => "BC7_UNORM",            99 => "BC7_UNORM_SRGB",
        _  => $"DXGI_{fmt}",
    };

    private static int BcBpp(uint fmt) => fmt switch
    {
        71 or 72 or 80 or 81 => 4,   // BC1, BC4
        _                    => 8,   // BC2, BC3, BC5, BC6H, BC7
    };
}
