namespace DbDataKit.Update.Tests;

/// <summary>A program file keeps its execute bit when its zip is extracted (plan 24a UC09: the zip of a Linux release holds the host program).</summary>
public class ExtractorModeTests
{
    [Fact]
    public void A_zip_entry_keeps_its_execute_bit_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "dbdcv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var zip = Path.Combine(dir, "a.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            {
                var run = archive.CreateEntry("run");
                run.ExternalAttributes = 0x81ED << 16; // -rwxr-xr-x
                using (var w = new StreamWriter(run.Open())) w.Write("#!/bin/sh");
                using (var w = new StreamWriter(archive.CreateEntry("data").Open())) w.Write("x");
            }

            ArchiveExtractor.ExtractZip(zip, Path.Combine(dir, "out"));
            Assert.True(File.GetUnixFileMode(Path.Combine(dir, "out", "run")).HasFlag(UnixFileMode.UserExecute));
            Assert.False(File.GetUnixFileMode(Path.Combine(dir, "out", "data")).HasFlag(UnixFileMode.UserExecute));
        }
        finally { Directory.Delete(dir, true); }
    }
}
