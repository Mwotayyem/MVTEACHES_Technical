using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MVTeaches.Application.Files;
using MVTeaches.Domain.Files;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.Files;

/// <summary>
/// The local-disk implementation of the storage half of §26.2's existing
/// <see cref="FileRecord"/> design (metadata table already existed —
/// <see cref="FilePurpose.PaymentProof"/> was already a named case — only
/// the actual save/serve mechanism did not). §26.2's own object-storage
/// notes point at Cloudflare R2 for production; nothing there is configured
/// in this environment, so this stores bytes on local disk instead, behind
/// the SAME <see cref="IFileStorageService"/> boundary a future R2-backed
/// implementation can replace without touching any caller.
/// </summary>
public class FileStorageService : IFileStorageService
{
    // Magic-byte signatures for the only formats a photographed/scanned
    // receipt is ever legitimately in — never trusts the browser-supplied
    // extension or Content-Type header, which either can lie about.
    private static readonly (string ContentType, byte[] Signature)[] AllowedSignatures =
    {
        ("image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF }),
        ("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        ("application/pdf", new byte[] { 0x25, 0x50, 0x44, 0x46 }), // "%PDF"
    };

    private readonly MvTeachesDbContext _db;
    private readonly IClock _clock;
    private readonly string _storagePath;
    private readonly long _maxSizeBytes;

    public FileStorageService(MvTeachesDbContext db, IClock clock, IOptions<FileStorageOptions> options)
    {
        _db = db;
        _clock = clock;
        var configured = options.Value;
        _storagePath = string.IsNullOrWhiteSpace(configured.StoragePath)
            ? Path.Combine(AppContext.BaseDirectory, "private-uploads")
            : configured.StoragePath;
        _maxSizeBytes = configured.MaxSizeBytes;
        Directory.CreateDirectory(_storagePath);
    }

    public async Task<SaveUploadResult> SaveAsync(Stream content, string purpose, string originalFileName,
        long uploadedByUserId, CancellationToken cancellationToken, long? ownerStudentId = null)
    {
        var filePurpose = ParsePurpose(purpose);

        // Read fully into memory up to the limit +1 byte, so an oversized
        // upload is rejected without ever writing a partial file to disk —
        // a rejected upload leaves no trace anywhere.
        using var buffer = new MemoryStream();
        var readLimit = _maxSizeBytes + 1;
        var copyBuffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        while ((bytesRead = await content.ReadAsync(copyBuffer.AsMemory(0, copyBuffer.Length), cancellationToken)) > 0)
        {
            totalRead += bytesRead;
            if (totalRead > readLimit)
            {
                return new SaveUploadResult(SaveUploadOutcome.RejectedTooLarge);
            }
            await buffer.WriteAsync(copyBuffer.AsMemory(0, bytesRead), cancellationToken);
        }

        if (totalRead == 0)
        {
            return new SaveUploadResult(SaveUploadOutcome.RejectedEmpty);
        }

        var bytes = buffer.ToArray();
        var verifiedContentType = SniffContentType(bytes);
        if (verifiedContentType is null)
        {
            return new SaveUploadResult(SaveUploadOutcome.RejectedContentType);
        }

        // A generated, unguessable name — never the original filename, never
        // derived from any user-controlled input (§26.2's own explicit
        // warning against a predictable name), so a path-traversal or
        // executable-disguised-as-image attempt has nothing to land on.
        var objectKey = Guid.NewGuid();
        var fullPath = Path.Combine(_storagePath, objectKey.ToString("N"));
        await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken);

        var sha256Hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var record = new FileRecord(objectKey, originalFileName, verifiedContentType, bytes.LongLength,
            sha256Hash, filePurpose, uploadedByUserId, _clock.GetCurrentInstant(), ownerStudentId);
        _db.Files.Add(record);
        await _db.SaveChangesAsync(cancellationToken);

        return new SaveUploadResult(SaveUploadOutcome.Saved, record.Id);
    }

    public async Task<OpenedDocument?> OpenAsync(long documentId, CancellationToken cancellationToken)
    {
        var record = await _db.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == documentId, cancellationToken);
        if (record is null)
        {
            return null;
        }

        var fullPath = Path.Combine(_storagePath, record.ObjectKey.ToString("N"));
        if (!File.Exists(fullPath))
        {
            return null;
        }

        var stream = File.OpenRead(fullPath);
        return new OpenedDocument(stream, record.ContentType, record.OriginalFileName);
    }

    private static FilePurpose ParsePurpose(string purpose) =>
        Enum.TryParse<FilePurpose>(purpose, out var parsed) ? parsed : FilePurpose.Other;

    private static string? SniffContentType(byte[] bytes)
    {
        foreach (var (contentType, signature) in AllowedSignatures)
        {
            if (bytes.Length >= signature.Length && bytes.AsSpan(0, signature.Length).SequenceEqual(signature))
            {
                return contentType;
            }
        }

        return null;
    }
}
