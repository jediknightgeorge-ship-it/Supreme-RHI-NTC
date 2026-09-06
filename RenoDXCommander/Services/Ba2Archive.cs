// Ba2Archive.cs — Read and write Fallout 4 / Starfield BA2 archives (GNRL + DX10)

using System.IO.Compression;
using System.Text;

namespace RenoDXCommander.Services;

/// <summary>Reads a BA2 archive and extracts DDS textures to a staging directory.</summary>
internal static class Ba2Archive
{
    private const uint MAGIC   = 0x58445442; // "BTDX"
    private const uint MAGIC_GNRL = 0x4C524E47; // "GNRL"
    private const uint MAGIC_DX10 = 0x30315844; // "DX10"
    private const uint BAADF00D  = 0xBAADF00D;

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>Returns DDS texture count inside the BA2. Returns -1 if not a BA2.</summary>
    public static int CountTextures(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);
            if (!ReadHeader(br, out var type, out var fileCount, out _)) return -1;
            if (type != MAGIC_GNRL && type != MAGIC_DX10) return -1;
            return CountDdsEntries(br, type, fileCount, path);
        }
        catch { return -1; }
    }

    /// <summary>Extracts all DDS textures from a BA2 archive to stagingDir.</summary>
    public static async Task<List<string>> ExtractTexturesAsync(
        string archivePath, string stagingDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(stagingDir);
        using var fs   = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br   = new BinaryReader(fs);

        if (!ReadHeader(br, out var type, out var fileCount, out var nameTableOffset))
            throw new InvalidDataException("Not a valid BA2 archive.");

        var names = ReadNameTable(archivePath, nameTableOffset, fileCount);

        return type == MAGIC_GNRL
            ? await ExtractGnrlAsync(br, fileCount, names, stagingDir, ct)
            : await ExtractDx10Async(br, fileCount, names, stagingDir, ct);
    }

    /// <summary>Repacks a BA2-GNRL archive replacing textures with files from stagingDir.</summary>
    public static async Task RepackAsync(
        string originalPath, string newPath, string stagingDir, CancellationToken ct = default)
    {
        using var fsIn  = new FileStream(originalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var brIn  = new BinaryReader(fsIn);

        if (!ReadHeader(brIn, out var type, out var fileCount, out var nameTableOffset))
            throw new InvalidDataException("Not a valid BA2 archive.");

        var names = ReadNameTable(originalPath, nameTableOffset, fileCount);

        // Always repack as GNRL (works for both GNRL and DX10 originals — games accept it)
        using var fsOut = new FileStream(newPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw    = new BinaryWriter(fsOut);

        if (type == MAGIC_GNRL)
            await RepackGnrlAsync(brIn, bw, fileCount, names, stagingDir, ct);
        else
            await RepackDx10AsGnrlAsync(brIn, bw, fileCount, names, stagingDir, ct);
    }

    // ── Header ───────────────────────────────────────────────────────────────

    private static bool ReadHeader(BinaryReader br, out uint type, out uint fileCount, out long nameOffset)
    {
        type = 0; fileCount = 0; nameOffset = 0;
        try
        {
            if (br.ReadUInt32() != MAGIC) return false;
            br.ReadUInt32(); // version
            type        = br.ReadUInt32();
            fileCount   = br.ReadUInt32();
            nameOffset  = (long)br.ReadUInt64();
            return true;
        }
        catch { return false; }
    }

    // ── Name table ───────────────────────────────────────────────────────────

    private static List<string> ReadNameTable(string archivePath, long offset, uint count)
    {
        var names = new List<string>((int)count);
        try
        {
            using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Seek(offset, SeekOrigin.Begin);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
            for (uint i = 0; i < count; i++)
            {
                var len = br.ReadUInt16();
                names.Add(new string(br.ReadChars(len)));
            }
        }
        catch { /* partial names are fine — we use index as fallback */ }
        return names;
    }

    private static string GetName(List<string> names, int idx, string ext) =>
        idx < names.Count && !string.IsNullOrEmpty(names[idx])
            ? names[idx]
            : $"texture_{idx}.{ext}";

    // ── GNRL extraction ──────────────────────────────────────────────────────

    private static async Task<List<string>> ExtractGnrlAsync(
        BinaryReader br, uint fileCount, List<string> names, string stagingDir, CancellationToken ct)
    {
        var extracted = new List<string>();

        // Read all file entries first (each is 28 bytes)
        var entries = new (uint nameHash, string ext, uint dirHash, uint flags,
                           long offset, uint packed, uint unpacked)[fileCount];
        for (uint i = 0; i < fileCount; i++)
        {
            var nh  = br.ReadUInt32();
            var ext = new string(br.ReadChars(4)).TrimEnd('\0');
            var dh  = br.ReadUInt32();
            var fl  = br.ReadUInt32();
            var off = (long)br.ReadUInt64();
            var pk  = br.ReadUInt32();
            var un  = br.ReadUInt32();
            br.ReadUInt32(); // align
            entries[i] = (nh, ext, dh, fl, off, pk, un);
        }

        for (int i = 0; i < (int)fileCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var e = entries[i];
            if (!e.ext.Equals("dds", StringComparison.OrdinalIgnoreCase)) continue;

            var name     = GetName(names, i, "dds");
            var safeName = MakeSafeName(name);
            var outPath  = Path.Combine(stagingDir, safeName);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

            br.BaseStream.Seek(e.offset, SeekOrigin.Begin);
            await ExtractEntryAsync(br.BaseStream, e.packed, e.unpacked, outPath, ct);
            extracted.Add(outPath);
        }
        return extracted;
    }

    // ── DX10 extraction ──────────────────────────────────────────────────────

    private static async Task<List<string>> ExtractDx10Async(
        BinaryReader br, uint fileCount, List<string> names, string stagingDir, CancellationToken ct)
    {
        var extracted = new List<string>();

        // Build entry list (variable size due to per-entry chunk arrays)
        var entries = new List<Dx10Entry>((int)fileCount);
        for (uint i = 0; i < fileCount; i++)
        {
            var e = new Dx10Entry();
            e.NameHash    = br.ReadUInt32();
            e.Ext         = new string(br.ReadChars(4)).TrimEnd('\0');
            e.DirHash     = br.ReadUInt32();
            br.ReadByte();              // unk
            var numChunks = br.ReadByte();
            br.ReadUInt16();            // chunkHdrLen (= 24)
            e.Height      = br.ReadUInt16();
            e.Width       = br.ReadUInt16();
            e.NumMips     = br.ReadByte();
            e.DxgiFormat  = br.ReadByte();
            e.IsCubemap   = br.ReadByte();
            br.ReadByte();              // tileMode
            e.Chunks = new Dx10Chunk[numChunks];
            for (int c = 0; c < numChunks; c++)
            {
                e.Chunks[c].Offset      = (long)br.ReadUInt64();
                e.Chunks[c].PackedSize  = br.ReadUInt32();
                e.Chunks[c].UnpackedSize = br.ReadUInt32();
                e.Chunks[c].StartMip   = br.ReadUInt16();
                e.Chunks[c].EndMip     = br.ReadUInt16();
                br.ReadUInt32();        // align
            }
            entries.Add(e);
        }

        var pos = br.BaseStream.Position;

        for (int i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var e = entries[i];
            if (!e.Ext.Equals("dds", StringComparison.OrdinalIgnoreCase)) continue;

            var name     = GetName(names, i, "dds");
            var safeName = MakeSafeName(name);
            var outPath  = Path.Combine(stagingDir, safeName);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

            await ExtractDx10TextureAsync(br.BaseStream, e, outPath, ct);
            extracted.Add(outPath);
        }
        return extracted;
    }

    private static async Task ExtractDx10TextureAsync(Stream s, Dx10Entry e, string outPath, CancellationToken ct)
    {
        await using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
        await using var bw = new BinaryWriter(fs);

        // Write DDS magic + header
        bw.Write(0x20534444u); // "DDS "
        bw.Write(124u);        // header size
        // dwFlags: DDSD_CAPS|DDSD_HEIGHT|DDSD_WIDTH|DDSD_PIXELFORMAT|DDSD_MIPMAPCOUNT
        bw.Write(0x000A1007u);
        bw.Write((uint)e.Height);
        bw.Write((uint)e.Width);
        bw.Write(0u);          // pitch (for compressed, computed later — we leave 0)
        bw.Write(0u);          // depth
        bw.Write((uint)e.NumMips);
        for (int r = 0; r < 11; r++) bw.Write(0u); // reserved

        // PixelFormat — force DX10
        bw.Write(32u);         // pfSize
        bw.Write(0x4u);        // DDPF_FOURCC
        bw.Write(0x30315844u); // "DX10"
        bw.Write(0u); bw.Write(0u); bw.Write(0u); bw.Write(0u); bw.Write(0u);

        // Caps
        uint caps = 0x1000u; // DDSCAPS_TEXTURE
        if (e.NumMips > 1) caps |= 0x400008u;
        bw.Write(caps);
        bw.Write(e.IsCubemap != 0 ? 0xFE00u : 0u);
        bw.Write(0u); bw.Write(0u); bw.Write(0u); // caps3/caps4/reserved

        // DX10 extension
        bw.Write((uint)e.DxgiFormat);
        bw.Write(3u);  // D3D10_RESOURCE_DIMENSION_TEXTURE2D
        bw.Write(e.IsCubemap != 0 ? 4u : 0u);
        bw.Write(1u);  // arraySize
        bw.Write(0u);  // miscFlags2

        // Append each chunk's data
        foreach (var chunk in e.Chunks)
        {
            ct.ThrowIfCancellationRequested();
            s.Seek(chunk.Offset, SeekOrigin.Begin);
            await DecompressChunkAsync(s, chunk.PackedSize, chunk.UnpackedSize, fs, ct);
        }
    }

    // ── Repack GNRL ──────────────────────────────────────────────────────────

    private static async Task RepackGnrlAsync(
        BinaryReader brIn, BinaryWriter bw, uint fileCount,
        List<string> names, string stagingDir, CancellationToken ct)
    {
        // Load original entries
        var entries = new (uint nh, string ext, uint dh, uint fl, long offOrig, uint pk, uint un)[fileCount];
        for (uint i = 0; i < fileCount; i++)
        {
            entries[i] = (brIn.ReadUInt32(), new string(brIn.ReadChars(4)).TrimEnd('\0'),
                brIn.ReadUInt32(), brIn.ReadUInt32(),
                (long)brIn.ReadUInt64(), brIn.ReadUInt32(), brIn.ReadUInt32());
            brIn.ReadUInt32(); // align
        }

        WriteGnrlHeader(bw, fileCount);
        long entryTableStart = bw.BaseStream.Position;
        // Reserve entry table space (28 bytes each)
        for (uint i = 0; i < fileCount; i++) for (int j = 0; j < 7; j++) bw.Write(0u);

        // Write file data, collecting new offsets/sizes
        var newEntries = new (long offset, uint packed, uint unpacked)[fileCount];
        for (int i = 0; i < (int)fileCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var e = entries[i];
            newEntries[i].offset = bw.BaseStream.Position;

            if (e.ext.Equals("dds", StringComparison.OrdinalIgnoreCase))
            {
                var name     = GetName(names, i, "dds");
                var safeName = MakeSafeName(name);
                var diskPath = Path.Combine(stagingDir, safeName);
                if (File.Exists(diskPath))
                {
                    var data = await File.ReadAllBytesAsync(diskPath, ct);
                    bw.Write(data);
                    newEntries[i].packed   = 0; // uncompressed
                    newEntries[i].unpacked = (uint)data.Length;
                    continue;
                }
            }
            // Fall back to original data
            brIn.BaseStream.Seek(e.offOrig, SeekOrigin.Begin);
            var origData = brIn.ReadBytes((int)(e.pk > 0 ? e.pk : e.un));
            bw.Write(origData);
            newEntries[i].packed   = e.pk;
            newEntries[i].unpacked = e.un;
        }

        long nameTablePos = bw.BaseStream.Position;
        WriteNameTable(bw, names, fileCount);

        // Go back and fill in entry table
        bw.BaseStream.Seek(entryTableStart, SeekOrigin.Begin);
        for (int i = 0; i < (int)fileCount; i++)
        {
            var e = entries[i];
            bw.Write(e.nh);
            bw.Write(PadExt(e.ext));
            bw.Write(e.dh);
            bw.Write(e.fl & ~0x40u); // clear compression flag
            bw.Write((ulong)newEntries[i].offset);
            bw.Write(newEntries[i].packed);
            bw.Write(newEntries[i].unpacked);
            bw.Write(BAADF00D);
        }

        // Fix name table offset in header
        bw.BaseStream.Seek(16, SeekOrigin.Begin);
        bw.Write((ulong)nameTablePos);
    }

    private static async Task RepackDx10AsGnrlAsync(
        BinaryReader brIn, BinaryWriter bw, uint fileCount,
        List<string> names, string stagingDir, CancellationToken ct)
    {
        // Skip DX10 entry headers (we already have names + staging files)
        // Collect entry metadata to skip correctly
        var dx10Entries = new List<Dx10Entry>((int)fileCount);
        for (uint i = 0; i < fileCount; i++)
        {
            var e = new Dx10Entry();
            e.NameHash = brIn.ReadUInt32();
            e.Ext = new string(brIn.ReadChars(4)).TrimEnd('\0');
            e.DirHash = brIn.ReadUInt32();
            brIn.ReadByte();
            var nc = brIn.ReadByte();
            brIn.ReadUInt16();
            e.Height = brIn.ReadUInt16(); e.Width = brIn.ReadUInt16();
            e.NumMips = brIn.ReadByte(); e.DxgiFormat = brIn.ReadByte();
            e.IsCubemap = brIn.ReadByte(); brIn.ReadByte();
            e.Chunks = new Dx10Chunk[nc];
            for (int c = 0; c < nc; c++)
            {
                e.Chunks[c].Offset = (long)brIn.ReadUInt64();
                e.Chunks[c].PackedSize = brIn.ReadUInt32();
                e.Chunks[c].UnpackedSize = brIn.ReadUInt32();
                e.Chunks[c].StartMip = brIn.ReadUInt16();
                e.Chunks[c].EndMip = brIn.ReadUInt16();
                brIn.ReadUInt32();
            }
            dx10Entries.Add(e);
        }

        // Now repack as GNRL using staging files (same logic as GNRL repack)
        WriteGnrlHeader(bw, fileCount);
        long entryTableStart = bw.BaseStream.Position;
        for (uint i = 0; i < fileCount; i++) for (int j = 0; j < 7; j++) bw.Write(0u);

        var newEntries = new (long offset, uint packed, uint unpacked)[fileCount];
        for (int i = 0; i < (int)fileCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var e = dx10Entries[i];
            newEntries[i].offset = bw.BaseStream.Position;

            if (e.Ext.Equals("dds", StringComparison.OrdinalIgnoreCase))
            {
                var name     = GetName(names, i, "dds");
                var safeName = MakeSafeName(name);
                var diskPath = Path.Combine(stagingDir, safeName);
                if (File.Exists(diskPath))
                {
                    var data = await File.ReadAllBytesAsync(diskPath, ct);
                    bw.Write(data);
                    newEntries[i].packed   = 0;
                    newEntries[i].unpacked = (uint)data.Length;
                    continue;
                }
            }
            // Fall back: re-extract from original archive chunks
            var ms = new MemoryStream();
            foreach (var chunk in e.Chunks)
            {
                brIn.BaseStream.Seek(chunk.Offset, SeekOrigin.Begin);
                await DecompressChunkAsync(brIn.BaseStream, chunk.PackedSize, chunk.UnpackedSize, ms, ct);
            }
            var raw = ms.ToArray();
            bw.Write(raw);
            newEntries[i].packed   = 0;
            newEntries[i].unpacked = (uint)raw.Length;
        }

        long nameTablePos = bw.BaseStream.Position;
        WriteNameTable(bw, names, fileCount);

        bw.BaseStream.Seek(entryTableStart, SeekOrigin.Begin);
        for (int i = 0; i < (int)fileCount; i++)
        {
            var e = dx10Entries[i];
            bw.Write(e.NameHash);
            bw.Write(PadExt(e.Ext));
            bw.Write(e.DirHash);
            bw.Write(0u);  // flags
            bw.Write((ulong)newEntries[i].offset);
            bw.Write(newEntries[i].packed);
            bw.Write(newEntries[i].unpacked);
            bw.Write(BAADF00D);
        }
        bw.BaseStream.Seek(16, SeekOrigin.Begin);
        bw.Write((ulong)nameTablePos);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void WriteGnrlHeader(BinaryWriter bw, uint fileCount)
    {
        bw.Write(MAGIC);       // "BTDX"
        bw.Write(1u);          // version 1 (FO4)
        bw.Write(MAGIC_GNRL); // "GNRL"
        bw.Write(fileCount);
        bw.Write(0UL);         // nameTableOffset placeholder
    }

    private static void WriteNameTable(BinaryWriter bw, List<string> names, uint count)
    {
        for (uint i = 0; i < count; i++)
        {
            var s = i < (uint)names.Count ? names[(int)i] : string.Empty;
            var bytes = Encoding.UTF8.GetBytes(s);
            bw.Write((ushort)bytes.Length);
            bw.Write(bytes);
        }
    }

    private static int CountDdsEntries(BinaryReader br, uint type, uint fileCount, string path)
    {
        int count = 0;
        var names = ReadNameTable(path, 0, 0); // just to get correct offsets
        if (type == MAGIC_GNRL)
        {
            for (uint i = 0; i < fileCount; i++)
            {
                br.ReadUInt32(); // nameHash
                var ext = new string(br.ReadChars(4)).TrimEnd('\0');
                br.ReadBytes(20); // rest of entry
                if (ext.Equals("dds", StringComparison.OrdinalIgnoreCase)) count++;
            }
        }
        else // DX10 — all entries are textures
        {
            count = (int)fileCount;
        }
        return count;
    }

    private static async Task ExtractEntryAsync(
        Stream s, uint packed, uint unpacked, string outPath, CancellationToken ct)
    {
        await using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
        if (packed == 0)
        {
            // Uncompressed
            var buf = new byte[unpacked];
            await s.ReadExactlyAsync(buf, ct);
            await fs.WriteAsync(buf, ct);
        }
        else
        {
            await DecompressChunkAsync(s, packed, unpacked, fs, ct);
        }
    }

    private static async Task DecompressChunkAsync(
        Stream s, uint packed, uint unpacked, Stream dest, CancellationToken ct)
    {
        if (packed == 0)
        {
            var buf = new byte[unpacked];
            await s.ReadExactlyAsync(buf, ct);
            await dest.WriteAsync(buf, ct);
            return;
        }
        var compressed = new byte[packed];
        await s.ReadExactlyAsync(compressed, ct);
        using var ms     = new MemoryStream(compressed);
        await using var z = new ZLibStream(ms, CompressionMode.Decompress);
        await z.CopyToAsync(dest, ct);
    }

    private static string MakeSafeName(string name) =>
        name.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

    private static char[] PadExt(string ext)
    {
        var arr = new char[4];
        for (int i = 0; i < Math.Min(ext.Length, 4); i++) arr[i] = ext[i];
        return arr;
    }

    // ── Internal types ───────────────────────────────────────────────────────

    private class Dx10Entry
    {
        public uint NameHash; public string Ext = ""; public uint DirHash;
        public ushort Height, Width; public byte NumMips, DxgiFormat, IsCubemap;
        public Dx10Chunk[] Chunks = [];
    }

    private struct Dx10Chunk
    {
        public long Offset; public uint PackedSize, UnpackedSize;
        public ushort StartMip, EndMip;
    }
}
