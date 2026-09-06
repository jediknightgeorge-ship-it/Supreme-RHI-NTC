// BsaArchive.cs — Read and write Skyrim/SSE BSA archives (v104 / v105)

using System.IO.Compression;
using System.Text;

namespace RenoDXCommander.Services;

/// <summary>Handles Skyrim (v104) and Skyrim Special Edition (v105) BSA archives.</summary>
internal static class BsaArchive
{
    private const uint MAGIC        = 0x00415342; // "BSA\0"
    private const uint VERSION_104  = 104;        // Skyrim
    private const uint VERSION_105  = 105;        // Skyrim SE / Fallout 4 BSA

    private const uint FLAG_COMPRESSED  = 0x0004;
    private const uint FLAG_EMBED_NAMES = 0x0100;
    private const uint FILE_FLAG_TEXTURES = 0x0002;

    // Per-file: bit 30 of size field toggles compression vs archive default
    private const uint COMPRESS_TOGGLE = 1u << 30;

    // ── Public API ───────────────────────────────────────────────────────────

    public static int CountTextures(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);
            if (!ReadHeader(br, out _, out _, out _, out var fileFlags)) return -1;
            // If texture bit not set in fileFlags, no textures
            if ((fileFlags & FILE_FLAG_TEXTURES) == 0) return 0;
            // We count .dds files during actual scan — return -1 to trigger full scan
            return -2; // signal "has textures, count via full scan"
        }
        catch { return -1; }
    }

    public static async Task<List<string>> ExtractTexturesAsync(
        string archivePath, string stagingDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(stagingDir);
        var extracted = new List<string>();

        using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);

        if (!ReadHeader(br, out var version, out var archFlags, out var folderCount, out _))
            throw new InvalidDataException("Not a valid BSA archive.");

        bool defaultCompressed = (archFlags & FLAG_COMPRESSED) != 0;
        bool embedNames        = (archFlags & FLAG_EMBED_NAMES) != 0;
        bool isSse             = version == VERSION_105;

        // Read folder records
        var folders = new (ulong hash, uint count, uint offset)[folderCount];
        for (uint i = 0; i < folderCount; i++)
        {
            var hash   = br.ReadUInt64();
            var count  = br.ReadUInt32();
            if (isSse) br.ReadUInt32(); // padding
            var offset = isSse ? (uint)br.ReadUInt64() : br.ReadUInt32();
            folders[i] = (hash, count, offset);
        }

        // Read folder name + file records
        var allFiles = new List<(string folder, ulong hash, uint sizeRaw, uint offset)>();
        foreach (var f in folders)
        {
            ct.ThrowIfCancellationRequested();
            var nameLen  = br.ReadByte();
            var name     = new string(br.ReadChars(nameLen)).TrimEnd('\0');
            for (uint j = 0; j < f.count; j++)
            {
                var hash      = br.ReadUInt64();
                var sizeRaw   = br.ReadUInt32();
                var fileOffset = br.ReadUInt32();
                allFiles.Add((name, hash, sizeRaw, fileOffset));
            }
        }

        // Read file names
        var fileNames = new List<string>(allFiles.Count);
        foreach (var _ in allFiles)
        {
            var sb = new StringBuilder();
            byte c;
            while ((c = br.ReadByte()) != 0) sb.Append((char)c);
            fileNames.Add(sb.ToString());
        }

        // Extract DDS files
        for (int i = 0; i < allFiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (folder, _, sizeRaw, fileOffset) = allFiles[i];
            var fileName = fileNames[i];
            if (!fileName.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) continue;

            bool toggleCompressed = (sizeRaw & COMPRESS_TOGGLE) != 0;
            bool isCompressed = defaultCompressed ^ toggleCompressed;
            uint dataSize = sizeRaw & ~(COMPRESS_TOGGLE | (1u << 31));

            var relPath = Path.Combine(folder.Replace('/', Path.DirectorySeparatorChar), fileName);
            var outPath = Path.Combine(stagingDir, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

            fs.Seek(fileOffset, SeekOrigin.Begin);

            uint unpackedSize = 0;
            if (embedNames)
            {
                var embLen = br.ReadByte();
                br.ReadChars(embLen + 1); // name + null
            }
            if (isCompressed)
            {
                unpackedSize = br.ReadUInt32();
                dataSize -= 4;
            }

            await using var outFs = new FileStream(outPath, FileMode.Create);
            if (isCompressed)
            {
                var compressed = br.ReadBytes((int)dataSize);
                using var ms = new MemoryStream(compressed);
                if (version == VERSION_105)
                {
                    // SSE uses LZ4 or zlib — try zlib first
                    try
                    {
                        await using var z = new ZLibStream(ms, CompressionMode.Decompress);
                        await z.CopyToAsync(outFs, ct);
                    }
                    catch { ms.Seek(0, SeekOrigin.Begin); await ms.CopyToAsync(outFs, ct); }
                }
                else
                {
                    await using var z = new ZLibStream(ms, CompressionMode.Decompress);
                    await z.CopyToAsync(outFs, ct);
                }
            }
            else
            {
                var raw = br.ReadBytes((int)dataSize);
                await outFs.WriteAsync(raw, ct);
            }
            extracted.Add(outPath);
        }
        return extracted;
    }

    /// <summary>Repacks BSA by replacing DDS entries with files from stagingDir.
    /// Non-DDS entries and entries missing from staging are copied from the original.</summary>
    public static async Task RepackAsync(
        string originalPath, string newPath, string stagingDir, CancellationToken ct = default)
    {
        // Strategy: read all file data from original, replace DDS files from staging,
        // write a new BSA with no compression (simplest safe approach).
        using var fsIn = new FileStream(originalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var brIn = new BinaryReader(fsIn);

        if (!ReadHeader(brIn, out var version, out var archFlags, out var folderCount, out var fileFlags))
            throw new InvalidDataException("Not a valid BSA archive.");

        bool defaultCompressed = (archFlags & FLAG_COMPRESSED) != 0;
        bool embedNames        = (archFlags & FLAG_EMBED_NAMES) != 0;
        bool isSse             = version == VERSION_105;

        var folders = new (ulong hash, uint count, uint offset)[folderCount];
        for (uint i = 0; i < folderCount; i++)
        {
            folders[i].hash  = brIn.ReadUInt64();
            folders[i].count = brIn.ReadUInt32();
            if (isSse) brIn.ReadUInt32();
            folders[i].offset = isSse ? (uint)brIn.ReadUInt64() : brIn.ReadUInt32();
        }

        var allFiles = new List<(string folder, ulong hash, uint sizeRaw, uint offset)>();
        var folderNames = new List<(string name, int fileStart, int fileCount)>();
        foreach (var f in folders)
        {
            ct.ThrowIfCancellationRequested();
            var nameLen = brIn.ReadByte();
            var name    = new string(brIn.ReadChars(nameLen)).TrimEnd('\0');
            int start   = allFiles.Count;
            for (uint j = 0; j < f.count; j++)
            {
                var hash   = brIn.ReadUInt64();
                var sz     = brIn.ReadUInt32();
                var off    = brIn.ReadUInt32();
                allFiles.Add((name, hash, sz, off));
            }
            folderNames.Add((name, start, (int)f.count));
        }

        var fileNames = new List<string>(allFiles.Count);
        foreach (var _ in allFiles)
        {
            var sb = new StringBuilder(); byte c;
            while ((c = brIn.ReadByte()) != 0) sb.Append((char)c);
            fileNames.Add(sb.ToString());
        }

        // Extract all file data (using staging for DDS, original for rest)
        var fileData = new byte[allFiles.Count][];
        for (int i = 0; i < allFiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (folder, _, sizeRaw, fileOffset) = allFiles[i];
            var fileName = fileNames[i];

            if (fileName.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                var stagingPath = Path.Combine(stagingDir,
                    folder.Replace('/', Path.DirectorySeparatorChar), fileName);
                if (File.Exists(stagingPath))
                {
                    fileData[i] = await File.ReadAllBytesAsync(stagingPath, ct);
                    continue;
                }
            }

            // Fall back to original data (decompress + store raw)
            bool toggleCompressed = (sizeRaw & COMPRESS_TOGGLE) != 0;
            bool isCompressed = defaultCompressed ^ toggleCompressed;
            uint dataSize = sizeRaw & ~(COMPRESS_TOGGLE | (1u << 31));

            fsIn.Seek(fileOffset, SeekOrigin.Begin);
            if (embedNames) { var el = brIn.ReadByte(); brIn.ReadChars(el + 1); }
            uint unpackedSize = 0;
            if (isCompressed) { unpackedSize = brIn.ReadUInt32(); dataSize -= 4; }
            var raw = brIn.ReadBytes((int)dataSize);

            if (isCompressed)
            {
                using var ms = new MemoryStream(raw);
                try { using var z = new ZLibStream(ms, CompressionMode.Decompress);
                      var buf = new MemoryStream(); await z.CopyToAsync(buf, ct);
                      fileData[i] = buf.ToArray(); }
                catch { fileData[i] = raw; }
            }
            else fileData[i] = raw;
        }

        // Write new BSA (uncompressed, no embed names — simplest valid format)
        using var fsOut = new FileStream(newPath, FileMode.Create, FileAccess.Write);
        using var bw    = new BinaryWriter(fsOut, Encoding.UTF8, leaveOpen: true);

        int totalFolderNameLen = folderNames.Sum(f => f.name.Length + 2); // +1 len byte +1 null
        int totalFileNameLen   = fileNames.Sum(n => n.Length + 1);        // +1 null

        // Header
        bw.Write(MAGIC);
        bw.Write(version);
        bw.Write(36u);         // folderOffset
        uint newArchFlags = archFlags & ~FLAG_COMPRESSED & ~FLAG_EMBED_NAMES;
        bw.Write(newArchFlags);
        bw.Write(folderCount);
        bw.Write((uint)allFiles.Count);
        bw.Write((uint)totalFolderNameLen);
        bw.Write((uint)totalFileNameLen);
        bw.Write(fileFlags);

        // Placeholder folder records
        long folderRecordStart = bw.BaseStream.Position;
        int folderRecordSize   = isSse ? 24 : 16;
        for (int i = 0; i < (int)folderCount; i++)
            bw.Write(new byte[folderRecordSize]);

        // File records + names (compute data offsets)
        // First pass: figure out where file data will start
        long fileRecordBlockStart = bw.BaseStream.Position;
        long fileRecordBlockSize  = (long)allFiles.Count * 16 + totalFolderNameLen;
        long fileNamesBlockSize   = totalFileNameLen;
        long dataStart            = fileRecordBlockStart + fileRecordBlockSize + fileNamesBlockSize;

        // Write folder records (back-patch) + file records now
        var fileOffsets = new uint[allFiles.Count];
        long cursor = dataStart;
        for (int i = 0; i < allFiles.Count; i++)
        {
            fileOffsets[i] = (uint)cursor;
            cursor += fileData[i].Length;
        }

        // Patch folder records
        bw.BaseStream.Seek(folderRecordStart, SeekOrigin.Begin);
        foreach (var (fname, fstart, fcount) in folderNames)
        {
            // offset = position of this folder's file records (after folder record block)
            long frOffset = fileRecordBlockStart + (isSse ? 24 : 16) * folderNames.Count;
            for (int j = 0; j < folderNames.IndexOf((fname, fstart, fcount)); j++)
                frOffset += 1 + folderNames[j].name.Length + 1 + folderNames[j].fileCount * 16;
            frOffset += 1 + fname.Length + 1; // folder name entry itself

            bw.Write(allFiles[fstart].hash);
            bw.Write((uint)fcount);
            if (isSse) { bw.Write(0u); bw.Write((ulong)frOffset); }
            else bw.Write((uint)frOffset);
        }

        // Write file record block (folder name + file records per folder)
        bw.BaseStream.Seek(fileRecordBlockStart, SeekOrigin.Begin);
        foreach (var (fname, fstart, fcount) in folderNames)
        {
            bw.Write((byte)(fname.Length + 1));
            bw.Write(Encoding.ASCII.GetBytes(fname));
            bw.Write((byte)0);
            for (int j = 0; j < fcount; j++)
            {
                var fi = fstart + j;
                bw.Write(allFiles[fi].hash);
                bw.Write((uint)fileData[fi].Length); // size, no flags
                bw.Write(fileOffsets[fi]);
            }
        }

        // File name block
        foreach (var n in fileNames) { bw.Write(Encoding.ASCII.GetBytes(n)); bw.Write((byte)0); }

        // File data
        for (int i = 0; i < allFiles.Count; i++)
            await fsOut.WriteAsync(fileData[i], ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool ReadHeader(BinaryReader br, out uint version, out uint archFlags,
        out uint folderCount, out uint fileFlags)
    {
        version = archFlags = folderCount = fileFlags = 0;
        try
        {
            if (br.ReadUInt32() != MAGIC) return false;
            version     = br.ReadUInt32();
            if (version != VERSION_104 && version != VERSION_105) return false;
            br.ReadUInt32();         // folderOffset (always 36)
            archFlags   = br.ReadUInt32();
            folderCount = br.ReadUInt32();
            br.ReadUInt32();         // fileCount
            br.ReadUInt32();         // totalFolderNameLength
            br.ReadUInt32();         // totalFileNameLength
            fileFlags   = br.ReadUInt32();
            return true;
        }
        catch { return false; }
    }
}
