using System.IO.Compression;
using System.Text;
using SnapZap.Core.Stego;

namespace SnapZap.Tests;

public class PayloadZipperTests : IDisposable
{
    readonly string _work = Path.Combine(Path.GetTempPath(), "pc_stego_zip_" + Guid.NewGuid().ToString("N"));

    public PayloadZipperTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch { /* best-effort cleanup */ }
    }

    string WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_work, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void CreateZip_then_ExtractZip_round_trip_recovers_byte_identical_content()
    {
        var a = WriteFile("src1/a.jpg", "photo a bytes");
        var b = WriteFile("src1/b.jpg", "photo b bytes");

        var zip = PayloadZipper.CreateZip([a, b]);
        var outDir = Path.Combine(_work, "out");
        var written = PayloadZipper.ExtractZip(zip, outDir);

        Assert.Equal(2, written.Count);
        var aOut = written.Single(p => Path.GetFileName(p) == "a.jpg");
        var bOut = written.Single(p => Path.GetFileName(p) == "b.jpg");
        Assert.Equal("photo a bytes", File.ReadAllText(aOut));
        Assert.Equal("photo b bytes", File.ReadAllText(bOut));
    }

    [Fact]
    public void CreateZip_with_same_filename_from_different_source_folders_keeps_both_entries_distinct()
    {
        var a = WriteFile("folderA/beach.jpg", "from folder A");
        var b = WriteFile("folderB/beach.jpg", "from folder B");

        var zip = PayloadZipper.CreateZip([a, b]);
        var written = PayloadZipper.ExtractZip(zip, Path.Combine(_work, "out"));

        Assert.Equal(2, written.Count);
        var contents = written.Select(File.ReadAllText).ToList();
        Assert.Contains("from folder A", contents);
        Assert.Contains("from folder B", contents);
        // Neither silently overwrote the other before encryption.
        Assert.Equal(2, contents.Distinct().Count());
    }

    [Fact]
    public void ExtractZip_into_directory_with_existing_same_name_file_writes_a_numbered_alternate()
    {
        var a = WriteFile("src/photo.jpg", "recovered content");
        var zip = PayloadZipper.CreateZip([a]);

        var outDir = Path.Combine(_work, "out2");
        Directory.CreateDirectory(outDir);
        var preexisting = Path.Combine(outDir, "photo.jpg");
        File.WriteAllText(preexisting, "unrelated pre-existing content");

        var written = PayloadZipper.ExtractZip(zip, outDir);

        Assert.Single(written);
        Assert.NotEqual(preexisting, written[0]);
        Assert.Equal("photo (2).jpg", Path.GetFileName(written[0]));
        // The pre-existing file was never overwritten.
        Assert.Equal("unrelated pre-existing content", File.ReadAllText(preexisting));
        Assert.Equal("recovered content", File.ReadAllText(written[0]));
    }

    [Fact]
    public void ExtractZip_creates_the_output_directory_if_it_does_not_exist()
    {
        var a = WriteFile("src/x.jpg", "x");
        var zip = PayloadZipper.CreateZip([a]);

        var outDir = Path.Combine(_work, "does-not-exist-yet", "nested");
        Assert.False(Directory.Exists(outDir));

        PayloadZipper.ExtractZip(zip, outDir);

        Assert.True(Directory.Exists(outDir));
    }

    [Fact]
    public void ExtractZip_with_a_pre_cancelled_token_throws_before_writing_any_file()
    {
        var a = WriteFile("src/x.jpg", "x");
        var zip = PayloadZipper.CreateZip([a]);

        var outDir = Path.Combine(_work, "out3");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => PayloadZipper.ExtractZip(zip, outDir, cts.Token));
        Assert.Empty(Directory.Exists(outDir) ? Directory.GetFiles(outDir) : []);
    }

    [Fact]
    public void ExtractZip_throws_StegoException_on_a_directory_only_entry()
    {
        // PayloadZipper.CreateZip never produces an entry like this itself — this simulates a
        // corrupted or hostile zip reaching ExtractZip (decrypted, untrusted-origin content), for
        // which entry.Name is empty. Without a guard this reaches File.Create(outputDir) itself
        // (Path.Combine(dir, "") == dir) and throws an uncaught, unhelpful exception.
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            archive.CreateEntry("subdir/");
        var malformedZip = ms.ToArray();

        var outDir = Path.Combine(_work, "out-malformed");
        Assert.Throws<StegoException>(() => PayloadZipper.ExtractZip(malformedZip, outDir));
    }

    [Fact]
    public void ExtractZip_throws_StegoException_when_a_single_entry_exceeds_the_per_entry_cap()
    {
        // Regression coverage for a decompression-bomb carrier: a small crafted entry that
        // decompresses to an implausible size must be rejected, not written unbounded to disk.
        // The cap is overridden to a tiny value so the test doesn't need gigabytes of real data.
        var a = WriteFile("src/a.jpg", new string('x', 1000));
        var zip = PayloadZipper.CreateZip([a]);

        var outDir = Path.Combine(_work, "out-entry-cap");
        Assert.Throws<StegoException>(() => PayloadZipper.ExtractZip(zip, outDir, maxEntryBytes: 100));
    }

    [Fact]
    public void ExtractZip_throws_StegoException_when_the_aggregate_cap_is_exceeded_across_entries()
    {
        // A zip with many entries individually under the per-entry cap can still add up to an
        // implausible total — the aggregate ceiling has to be checked independently of the
        // per-entry one.
        var a = WriteFile("src/a.jpg", new string('x', 100));
        var b = WriteFile("src/b.jpg", new string('y', 100));
        var zip = PayloadZipper.CreateZip([a, b]);

        var outDir = Path.Combine(_work, "out-total-cap");
        Assert.Throws<StegoException>(() =>
            PayloadZipper.ExtractZip(zip, outDir, maxEntryBytes: 1000, maxTotalBytes: 150));
    }
}
