using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileSnapshot3;

// ========== クライアント⇔サーバのDTO ==========
public sealed class ClientSnapshot
{
    public List<ClientFileInfo>? Files { get; set; }
}
public sealed class ClientFileInfo
{
    public string FilePath { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset ModifiedUtc { get; set; }
    public string? Sha256Hex { get; set; } // クライアント側で計算したハッシュ（16進文字列）
}
public sealed class ServerDiffResponse
{
    public List<string> ToDownload { get; set; } = new();
    public List<string> ToDelete { get; set; } = new();
    public List<string> UpToDate { get; set; } = new();
}

