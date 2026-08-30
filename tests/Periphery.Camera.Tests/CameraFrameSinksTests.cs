using Periphery.Camera.Tests.Fakes;

namespace Periphery.Camera.Tests;

public sealed class CameraFrameSinksTests
{
    private static async IAsyncEnumerable<ICameraFrame> Yield(IEnumerable<FakeFrame> frames)
    {
        foreach (var f in frames)
        {
            yield return f;
            await Task.Yield();
        }
    }

    [Fact]
    public async Task SaveToDirectoryAsync_MjpegFrames_WritesJpgFiles()
    {
        var dir = TempDir();
        try
        {
            var frames = new[]
            {
                new FakeFrame([0xFF, 0xD8, 0xFF, 0xE0]), // pretend JPEG SOI
                new FakeFrame([0x01, 0x02, 0x03]),
                new FakeFrame([0x04, 0x05, 0x06, 0x07, 0x08]),
            };

            int count = await Yield(frames).SaveToDirectoryAsync(dir);

            Assert.Equal(3, count);
            var written = Directory.GetFiles(dir).OrderBy(p => p).ToArray();
            Assert.Equal(3, written.Length);
            Assert.EndsWith("frame-0001.jpg", written[0]);
            Assert.EndsWith("frame-0002.jpg", written[1]);
            Assert.EndsWith("frame-0003.jpg", written[2]);

            Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, await File.ReadAllBytesAsync(written[0]));
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, await File.ReadAllBytesAsync(written[1]));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task SaveToDirectoryAsync_RawFrames_WritesRawFilesWithDimensionsAndFormat()
    {
        var dir = TempDir();
        try
        {
            var frames = new[]
            {
                new FakeFrame([0xAA, 0xBB], width: 640, height: 480, pixelFormat: CameraPixelFormat.Yuy2),
            };

            int count = await Yield(frames).SaveToDirectoryAsync(dir);

            Assert.Equal(1, count);
            var written = Assert.Single(Directory.GetFiles(dir));
            Assert.EndsWith("frame-0001-640x480-Yuy2.raw", written);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task SaveToDirectoryAsync_TimestampNaming_UsesMillisecondTimestamp()
    {
        var dir = TempDir();
        try
        {
            var frames = new[]
            {
                new FakeFrame([0x01], timestamp: TimeSpan.FromMilliseconds(123)),
                new FakeFrame([0x02], timestamp: TimeSpan.FromMilliseconds(4567)),
            };
            var opts = new CameraFrameWriteOptions(Naming: CameraFrameNaming.Timestamp);

            await Yield(frames).SaveToDirectoryAsync(dir, opts);

            var names = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
            Assert.Equal(["frame-123.jpg", "frame-4567.jpg"], names);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task SaveToDirectoryAsync_CustomPrefix_AppliedToFilenames()
    {
        var dir = TempDir();
        try
        {
            var frames = new[] { new FakeFrame([0x01]) };
            var opts = new CameraFrameWriteOptions(FilenamePrefix: "shot");

            await Yield(frames).SaveToDirectoryAsync(dir, opts);

            var written = Assert.Single(Directory.GetFiles(dir));
            Assert.EndsWith("shot-0001.jpg", written);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task SaveToDirectoryAsync_DisposesEveryFrame()
    {
        var dir = TempDir();
        try
        {
            var frames = new[]
            {
                new FakeFrame([0x01]),
                new FakeFrame([0x02]),
                new FakeFrame([0x03]),
            };

            await Yield(frames).SaveToDirectoryAsync(dir);

            Assert.All(frames, f => Assert.True(f.Disposed));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task SaveToDirectoryAsync_CreatesDirectoryIfMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Assert.False(Directory.Exists(dir));
        try
        {
            await Yield([new FakeFrame([0x01])]).SaveToDirectoryAsync(dir);
            Assert.True(Directory.Exists(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task WriteContiguousToAsync_ConcatenatesFrameBytes()
    {
        var frames = new[]
        {
            new FakeFrame([0x01, 0x02]),
            new FakeFrame([0x03]),
            new FakeFrame([0x04, 0x05, 0x06]),
        };

        using var ms = new MemoryStream();
        int count = await Yield(frames).WriteContiguousToAsync(ms);

        Assert.Equal(3, count);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 }, ms.ToArray());
        Assert.All(frames, f => Assert.True(f.Disposed));
    }

    [Fact]
    public async Task WriteContiguousToAsync_NonWritableStream_Throws()
    {
        using var readOnly = new MemoryStream(new byte[1], writable: false);
        IAsyncEnumerable<ICameraFrame> empty = AsyncEnumerable.Empty<ICameraFrame>();
        await Assert.ThrowsAsync<ArgumentException>(
            () => empty.WriteContiguousToAsync(readOnly));
    }

    [Fact]
    public async Task SaveToDirectoryAsync_NullSource_Throws()
    {
        IAsyncEnumerable<ICameraFrame>? nil = null;
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => nil!.SaveToDirectoryAsync(TempDir()));
    }

    [Fact]
    public async Task SaveToDirectoryAsync_EmptyDirectory_Throws()
    {
        var frames = AsyncEnumerable.Empty<ICameraFrame>();
        await Assert.ThrowsAsync<ArgumentException>(
            () => frames.SaveToDirectoryAsync(""));
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static class AsyncEnumerable
    {
        public static async IAsyncEnumerable<T> Empty<T>()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
