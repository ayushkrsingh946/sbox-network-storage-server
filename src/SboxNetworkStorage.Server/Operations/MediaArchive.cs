using System.Security.Cryptography;
using SboxNetworkStorage.Application.NetworkStorage.Media;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Operations;

/// <summary>
/// Export side of media: lists a project's object rows and stages the bytes behind them. Bytes come through
/// <see cref="IMediaBlobStore"/>, so local disk and S3 share one path. A row whose bytes are missing, or whose bytes
/// do not match the recorded size and hash, fails the export instead of producing an archive that cannot be restored.
/// </summary>
public sealed class MediaExport(IMediaStore media, IMediaBlobStore blobs, string stagingRoot)
{
    private const int CopyBufferBytes = 81920;

    /// <summary>Size of each blob already staged, keyed by project and hash, so every row is checked and a blob is copied once.</summary>
    private readonly Dictionary<(string ProjectId, string Sha256), long> _staged = new();

    public Task<IReadOnlyList<MediaObjectRecord>> ListAsync(string projectId, CancellationToken ct) =>
        media.ListMediaObjectsAsync(projectId, ct);

    /// <summary>Copies one object's bytes into staging and returns the archive entry name they were written to.</summary>
    public async Task<string> StageBlobAsync(MediaObjectRecord record, CancellationToken ct)
    {
        if (!ExportFormat.IsMediaSha256(record.Sha256))
        {
            throw new ExportArchiveException($"Media object '{record.MediaId}' has a malformed hash; export refused.");
        }
        var entry = ExportFormat.MediaBlobEntry(record.ProjectId, record.Sha256);
        if (_staged.TryGetValue((record.ProjectId, record.Sha256), out var stagedSize))
        {
            if (stagedSize != record.SizeBytes)
            {
                throw new ExportArchiveException($"Media object '{record.MediaId}' disagrees with another row for the same bytes about size; export refused.");
            }
            return entry;
        }

        using var blob = await blobs.OpenReadAsync(record.ProjectId, record.Sha256, ct)
            ?? throw new ExportArchiveException($"Media object '{record.MediaId}' has no stored bytes; export refused.");
        if (blob.Length != record.SizeBytes)
        {
            throw new ExportArchiveException($"Media object '{record.MediaId}' does not match its recorded size; export refused.");
        }

        var path = StagingPath(stagingRoot, entry);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes))
        {
            var buffer = new byte[CopyBufferBytes];
            int read;
            while ((read = await blob.Content.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                total += read;
            }
        }

        if (total != record.SizeBytes || !HashMatches(hash, record.Sha256))
        {
            throw new ExportArchiveException($"Media object '{record.MediaId}' does not match its recorded hash; export refused.");
        }

        _staged[(record.ProjectId, record.Sha256)] = record.SizeBytes;
        return entry;
    }

    internal static string StagingPath(string stagingRoot, string entry) =>
        Path.Combine(stagingRoot, entry.Replace('/', Path.DirectorySeparatorChar));

    private static bool HashMatches(IncrementalHash hash, string sha256) =>
        string.Equals(Convert.ToHexString(hash.GetHashAndReset()), sha256, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Import side of media. Blob entries are staged and verified against their archive names while the tar is read. Rows
/// are restored only against staged bytes of matching length, and a staged blob that no row references fails the
/// import. With a null <paramref name="blobs"/> the archive is validated but no bytes reach a blob store, which is how
/// <c>ArchiveVerifier</c> checks an archive without changing a server.
/// </summary>
public sealed class MediaImport(string stagingRoot, IMediaBlobStore? blobs)
{
    private const int CopyBufferBytes = 81920;

    private readonly Dictionary<(string ProjectId, string Sha256), StagedBlob> _staged = new();
    private readonly HashSet<(string ProjectId, string Sha256)> _referenced = new();
    private readonly HashSet<(string ProjectId, string Sha256)> _written = new();

    private sealed record StagedBlob(string Path, long Length);

    /// <summary>Stages one blob entry's bytes and checks them against the hash in the entry name.</summary>
    public async Task StageAsync(string projectId, string sha256, Stream content, CancellationToken ct)
    {
        var path = MediaExport.StagingPath(stagingRoot, ExportFormat.MediaBlobEntry(projectId, sha256));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes))
        {
            var buffer = new byte[CopyBufferBytes];
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                total += read;
            }
        }

        if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExportArchiveException($"Media blob '{sha256}' does not match its archive name; import refused.");
        }

        _staged[(projectId, sha256)] = new StagedBlob(path, total);
    }

    /// <summary>
    /// Checks one restored object row against its staged bytes and writes the bytes to the blob store once. Fails when
    /// the row names bytes the archive does not contain, or bytes of a different length.
    /// </summary>
    public async Task RestoreRowAsync(MediaObjectRecord record, CancellationToken ct)
    {
        var key = (record.ProjectId, record.Sha256);
        if (!_staged.TryGetValue(key, out var staged))
        {
            throw new FormatException($"media object '{record.MediaId}' refers to bytes the archive does not contain");
        }

        if (staged.Length != record.SizeBytes)
        {
            throw new FormatException($"media object '{record.MediaId}' size does not match its archived bytes");
        }

        _referenced.Add(key);
        if (blobs is not null && _written.Add(key))
        {
            await blobs.WriteAsync(record.ProjectId, record.Sha256, await File.ReadAllBytesAsync(staged.Path, ct), ct);
        }
    }

    /// <summary>Fails when the archive carries a blob that no restored object row references.</summary>
    public void EnsureAllReferenced()
    {
        foreach (var key in _staged.Keys)
        {
            if (!_referenced.Contains(key))
            {
                throw new ExportArchiveException($"Archive contains media blob '{key.Sha256}' that no media object references; import refused.");
            }
        }
    }
}
