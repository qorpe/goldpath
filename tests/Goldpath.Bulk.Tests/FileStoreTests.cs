using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Bulk.Tests;

/// <summary>
/// The content-addressed store's own contract: streamed chunking at the documented size,
/// the lowercase-hex SHA-256 identity, graceful dedup (no orphaned chunks), exact
/// round-trips on both read paths, and retention deletes.
/// </summary>
public class FileStoreTests : IDisposable
{
    private const int ChunkBytes = 256 * 1024;
    private readonly BulkFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>A stream that trickles bytes N at a time — the partial-read reality of uploads.</summary>
    private sealed class TrickleStream(byte[] data, int trickle) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(trickle, buffer.Length)], cancellationToken);
    }

    private static byte[] Bytes(int length)
    {
        var data = new byte[length];
        new Random(42).NextBytes(data);
        return data;
    }

    private async Task<(GoldpathBulkFile File, bool Created)> SaveAsync(Stream stream)
    {
        using var scope = _fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<BulkTestContext>();
        return await _fixture.Store.SaveAsync(db, stream, "data.bin", CancellationToken.None);
    }

    [Fact]
    public async Task Chunks_land_at_the_documented_size_with_the_exact_tail()
    {
        var data = Bytes(ChunkBytes + 1234);
        var (file, created) = await SaveAsync(new MemoryStream(data));

        Assert.True(created);
        Assert.Equal(data.Length, file.Length);
        Assert.Equal("data.bin", file.FileName);

        var chunks = _fixture.Query(db => db.Set<GoldpathBulkFileChunk>()
            .Where(c => c.FileId == file.Id).OrderBy(c => c.Index).ToList());
        Assert.Equal(2, chunks.Count);
        Assert.Equal([0, 1], chunks.Select(c => c.Index));
        Assert.Equal(ChunkBytes, chunks[0].Data.Length);
        Assert.Equal(1234, chunks[1].Data.Length);
    }

    [Fact]
    public async Task The_hash_is_lowercase_hex_sha256_of_the_bytes()
    {
        var data = Bytes(10_000);
        var (file, _) = await SaveAsync(new MemoryStream(data));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), file.Sha256);
        Assert.Equal(64, file.Sha256.Length);
    }

    [Fact]
    public async Task A_trickling_upload_still_chunks_and_hashes_exactly()
    {
        var data = Bytes(ChunkBytes + 77);
        var (file, _) = await SaveAsync(new TrickleStream(data, trickle: 313));
        Assert.Equal(data.Length, file.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), file.Sha256);
        Assert.Equal(2, _fixture.Query(db => db.Set<GoldpathBulkFileChunk>().Count(c => c.FileId == file.Id)));
    }

    [Fact]
    public async Task Identical_bytes_return_the_existing_row_and_leave_no_orphan_chunks()
    {
        var data = Bytes(ChunkBytes * 2);
        var (first, _) = await SaveAsync(new MemoryStream(data));
        var (second, created) = await SaveAsync(new MemoryStream(data));

        Assert.False(created);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, _fixture.Query(db => db.Set<GoldpathBulkFile>().Count()));
        Assert.Equal(2, _fixture.Query(db => db.Set<GoldpathBulkFileChunk>().Count()));   // the losing write rolled back
    }

    [Fact]
    public async Task Both_read_paths_reassemble_the_bytes_exactly()
    {
        var data = Bytes(ChunkBytes + 999);
        var (file, _) = await SaveAsync(new MemoryStream(data));

        using var scope = _fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<BulkTestContext>();

        // Async path (the parser's).
        await using (var stream = _fixture.Store.OpenRead(db, file.Id))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            Assert.Equal(data, buffer.ToArray());
        }

        // Sync path.
        await using (var stream = _fixture.Store.OpenRead(db, file.Id))
        {
            using var buffer = new MemoryStream();
            var scratch = new byte[8192];
            int read;
            while ((read = stream.Read(scratch, 0, scratch.Length)) > 0)
            {
                buffer.Write(scratch, 0, read);
            }

            Assert.Equal(data, buffer.ToArray());
        }
    }

    /// <summary>
    /// Both read paths honor a partly consumed chunk: a short read followed by a read larger
    /// than what is left returns exactly the remainder, never past the chunk's end.
    /// </summary>
    [Fact]
    public async Task A_short_read_then_a_long_read_returns_exactly_the_remainder()
    {
        var data = Bytes(10_000);
        var (file, _) = await SaveAsync(new MemoryStream(data));
        using var scope = _fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<BulkTestContext>();

        await using (var stream = _fixture.Store.OpenRead(db, file.Id))
        {
            var head = new byte[3];
            var rest = new byte[16_384];
            Assert.Equal(3, stream.Read(head, 0, head.Length));
            Assert.Equal(data.Length - 3, stream.Read(rest, 0, rest.Length));
            Assert.Equal(data, head.Concat(rest.Take(data.Length - 3)).ToArray());
        }

        await using (var stream = _fixture.Store.OpenRead(db, file.Id))
        {
            var head = new byte[3];
            var rest = new byte[16_384];
            Assert.Equal(3, await stream.ReadAsync(head.AsMemory()));
            Assert.Equal(data.Length - 3, await stream.ReadAsync(rest.AsMemory()));
            Assert.Equal(data, head.Concat(rest.Take(data.Length - 3)).ToArray());
        }
    }

    [Fact]
    public async Task Reading_a_missing_file_yields_zero_bytes()
    {
        using var scope = _fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<BulkTestContext>();
        await using var stream = _fixture.Store.OpenRead(db, Guid.NewGuid());
        Assert.Equal(0, await stream.ReadAsync(new byte[16]));
        Assert.Equal(0, stream.Read(new byte[16], 0, 16));
    }

    [Fact]
    public async Task Delete_removes_bytes_and_row_and_is_idempotent()
    {
        var (file, _) = await SaveAsync(new MemoryStream(Bytes(1000)));
        using var scope = _fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<BulkTestContext>();

        await _fixture.Store.DeleteAsync(db, file.Id, CancellationToken.None);
        Assert.Equal(0, _fixture.Query(x => x.Set<GoldpathBulkFile>().Count()));
        Assert.Equal(0, _fixture.Query(x => x.Set<GoldpathBulkFileChunk>().Count()));

        await _fixture.Store.DeleteAsync(db, file.Id, CancellationToken.None);   // no-op, no throw
    }
}
