using System.Text;

namespace AtmNexus.Agent.Ej;

public interface IEJProvider
{
    string Vendor { get; }
    bool Supports(EjSource source);
    IReadOnlyList<string> ListFiles(EjSource source);
    /// <summary>Complete lines only; a trailing partial line is left for the next pass.</summary>
    IEnumerable<EjLine> ReadLines(EjSource source, string file, long fromOffset);
}

/// <summary>Journal files written by the ATM application (read with FileShare.ReadWrite so the ATM is never blocked).</summary>
public abstract class FileEjProvider : IEJProvider
{
    public abstract string Vendor { get; }
    public virtual bool Supports(EjSource s) => string.Equals(s.Vendor, Vendor, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> ListFiles(EjSource s) =>
        !Directory.Exists(s.Directory)
            ? []
            : new DirectoryInfo(s.Directory).EnumerateFiles(s.FilePattern, SearchOption.TopDirectoryOnly)
                .OrderBy(f => f.LastWriteTimeUtc).Select(f => f.FullName).ToList();

    public IEnumerable<EjLine> ReadLines(EjSource s, string file, long fromOffset) =>
        LineFileReader.ReadLines(file, fromOffset, Encoding.GetEncoding(s.Encoding));
}

/// <summary>Incremental, share-friendly line reader: yields only complete lines with exact byte offsets.</summary>
public static class LineFileReader
{
    public static IEnumerable<EjLine> ReadLines(string file, long fromOffset, Encoding enc)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fromOffset > fs.Length) fromOffset = 0;                // truncated or rotated in place
        fs.Seek(fromOffset, SeekOrigin.Begin);

        var buf = new byte[64 * 1024];
        using var line = new MemoryStream();
        long lineStart = fromOffset, pos = fromOffset;
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            int segStart = 0;
            for (int i = 0; i < n; i++)
            {
                if (buf[i] != (byte)'\n') continue;
                line.Write(buf, segStart, i - segStart);
                pos += i - segStart + 1;
                segStart = i + 1;
                yield return new EjLine(file, lineStart, pos, enc.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r'));
                line.SetLength(0);
                lineStart = pos;
            }
            line.Write(buf, segStart, n - segStart);
            pos += n - segStart;
        }
    }
}

public sealed class NcrEjProvider : FileEjProvider { public override string Vendor => "NCR"; }
public sealed class DieboldNixdorfEjProvider : FileEjProvider
{
    public override string Vendor => "DieboldNixdorf";
    public override bool Supports(EjSource s) => s.Vendor.ToUpperInvariant() is "DIEBOLDNIXDORF" or "WINCOR" or "DN";
}
public sealed class KalEjProvider : FileEjProvider { public override string Vendor => "KAL"; }
public sealed class GenericEjProvider : FileEjProvider
{
    public override string Vendor => "Generic";
    public override bool Supports(EjSource s) => true;      // fallback; register last
}
