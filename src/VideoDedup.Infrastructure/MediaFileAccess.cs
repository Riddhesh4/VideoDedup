using System.Security.Cryptography;
using VideoDedup.Application;

namespace VideoDedup.Infrastructure;

public static class MediaFileAccess
{
    public static FileRevision Revision(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new MediaProcessingException(MediaFailure.Missing);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new MediaProcessingException(MediaFailure.Unsupported);
            return new FileRevision(info.Length, info.LastWriteTimeUtc, OperatingSystem.IsWindows() ? WindowsRevision.Capture(path) : null);
        }
        catch (UnauthorizedAccessException) { throw new MediaProcessingException(MediaFailure.AccessDenied); }
        catch (IOException) { throw new MediaProcessingException(MediaFailure.Missing); }
    }

    public static void Verify(string path, FileRevision expected)
    {
        var actual = Revision(path);
        if (actual.SizeBytes != expected.SizeBytes || actual.ModifiedUtc != expected.ModifiedUtc ||
            (expected.FileIdentity is not null && actual.FileIdentity != expected.FileIdentity)) throw new MediaProcessingException(MediaFailure.Changed);
    }
}

public sealed class Sha256ContentHasher : IContentHasher
{
    public async Task<HashResult> HashAsync(string path, FileRevision expected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MediaFileAccess.Verify(path, expected);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            byte[] hash;
            try { hash = await SHA256.HashDataAsync(stream, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new MediaProcessingException(MediaFailure.Timeout); }
            MediaFileAccess.Verify(path, expected);
            return new HashResult(expected, Convert.ToHexStringLower(hash));
        }
        catch (UnauthorizedAccessException) { throw new MediaProcessingException(MediaFailure.AccessDenied); }
        catch (IOException) { throw new MediaProcessingException(MediaFailure.AccessDenied); }
    }
}
