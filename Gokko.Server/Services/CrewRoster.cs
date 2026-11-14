using System.Text.Json;

namespace Gokko.Server.Services;

/// <summary>
/// 乗務員（ユーザー名）と、行路への割り当て（勤怠管理）。Data/Crew.json に保存し、サーバーを再起動しても残す。
/// ・ユーザー名は勤怠管理の画面で作る。乗務タブレットは、そのユーザー名でログインする（合言葉はない。運転会の中で使う名札）
/// ・行路（Data/Duties.csv）1つに、ユーザーを1人割り当てる。同じ人に複数の行路を割り当ててもよい（午前・午後など）
/// ・タブレットが読みに来た時刻を覚えておき、勤怠管理の画面に「ログイン中」と出す（出勤の目安）
/// </summary>
public sealed class CrewRoster
{
    public const string FileName = "Crew.json";
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(20);

    private readonly object _lock = new();
    private readonly string _path;
    private readonly ILogger<CrewRoster>? _log;
    private readonly List<string> _users = new();
    private readonly Dictionary<string, string> _assign = new(); // 行路番号 → ユーザー名
    private readonly Dictionary<string, (DateTime Utc, string Train)> _seen = new(StringComparer.OrdinalIgnoreCase);

    private sealed class FileShape
    {
        public List<string> Users { get; set; } = new();
        public Dictionary<string, string> Assignments { get; set; } = new();
    }

    public CrewRoster(string dataDir, ILogger<CrewRoster>? log = null)
    {
        _path = Path.Combine(dataDir, FileName);
        _log = log;
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<FileShape>(File.ReadAllText(_path), Json) is { } f)
            {
                _users.AddRange(f.Users.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.OrdinalIgnoreCase));
                foreach (var (duty, user) in f.Assignments)
                    if (Find(user) is { } name) _assign[duty] = name;
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "{File} を読めませんでした（空で始めます）", FileName);
        }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new FileShape { Users = _users.ToList(), Assignments = new(_assign) }, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "{File} に保存できませんでした", FileName);
        }
    }

    private string? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : _users.FirstOrDefault(u => string.Equals(u, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>登録されているユーザー名（大文字・小文字は区別しない）。なければ null。</summary>
    public string? User(string? name)
    {
        lock (_lock) return Find(name);
    }

    public List<string> Users()
    {
        lock (_lock) return _users.ToList();
    }

    /// <summary>ユーザーを作る。うまくいかなければ理由を返す。</summary>
    public string? AddUser(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return "ユーザー名を入れてください";
        if (n.Length > 20) return "ユーザー名は20文字までです";
        lock (_lock)
        {
            if (Find(n) != null) return $"{n} はもうあります";
            _users.Add(n);
            Save();
        }

        return null;
    }

    /// <summary>ユーザーを消す（割り当てていた行路は、担当なしに戻す）。</summary>
    public bool RemoveUser(string? name)
    {
        lock (_lock)
        {
            if (Find(name) is not { } n) return false;
            _users.Remove(n);
            foreach (var d in _assign.Where(x => x.Value == n).Select(x => x.Key).ToList()) _assign.Remove(d);
            Save();
            return true;
        }
    }

    /// <summary>行路にユーザーを割り当てる（user が空なら担当なし）。うまくいかなければ理由を返す。</summary>
    public string? Assign(string duty, string? user)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(user))
            {
                _assign.Remove(duty);
            }
            else
            {
                if (Find(user) is not { } n) return $"{user} というユーザーはいません";
                _assign[duty] = n;
            }

            Save();
        }

        return null;
    }

    /// <summary>行路の担当者（いなければ null）。</summary>
    public string? AssignedTo(string duty)
    {
        lock (_lock) return _assign.GetValueOrDefault(duty);
    }

    /// <summary>そのユーザーが担当する行路の番号（割り当てた順ではなく、行路番号の順）。</summary>
    public List<string> DutiesOf(string user)
    {
        lock (_lock)
            return _assign.Where(x => string.Equals(x.Value, user, StringComparison.OrdinalIgnoreCase)).Select(x => x.Key)
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    /// <summary>タブレットが読みに来た（ログイン中の目安）。</summary>
    public void Seen(string user, string train)
    {
        lock (_lock) _seen[user] = (DateTime.UtcNow, train);
    }

    /// <summary>今タブレットでログインしているか（最後に読みに来てから20秒以内）と、見ている列車。</summary>
    public (bool Online, string? Train, DateTime? LastUtc) Presence(string user)
    {
        lock (_lock)
        {
            if (!_seen.TryGetValue(user, out var s)) return (false, null, null);
            return (DateTime.UtcNow - s.Utc < OnlineWindow, s.Train, s.Utc);
        }
    }
}
