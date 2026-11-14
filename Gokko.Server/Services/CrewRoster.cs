using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gokko.Server.Services;

/// <summary>
/// 乗務員（ユーザー）と、行路への割り当て（勤怠管理）。Data/Crew.json に保存し、サーバーを再起動しても残す。
/// ・パスワードはサーバーが作って一度だけ見せる（本人にメモしてもらう）。保存するのはハッシュだけ
/// ・忘れたら、登録しておいた秘密の質問3つに全部答えられれば、新しいパスワードを出し直す（管理者がいなくても戻れる）。
///   管理者が勤怠管理の画面から出し直すこともできる
/// ・ユーザーは、タブレットで自分で登録するか、勤怠管理の画面で作る（勤怠管理で作った人は、最初のログインで秘密の質問を決める）
/// ・ログインすると合い札（トークン）を渡し、タブレットはそれで読みに来る。パスワードを出し直すと、それまでの合い札は使えなくなる
/// ・パスワード・答えを続けて間違えたら、しばらく受け付けない
/// ・行路（Data/Duties.csv）1つに、ユーザーを1人割り当てる。タブレットが読みに来た時刻を覚えておき、「ログイン中」と出す
/// </summary>
public sealed class CrewRoster
{
    public const string FileName = "Crew.json";

    /// <summary>選べる秘密の質問。</summary>
    public static readonly string[] Questions =
    [
        "初めて乗った電車の路線は？",
        "いちばん好きな駅は？",
        "いちばん好きな車両の形式は？",
        "子どものころのあだ名は？",
        "初めて飼ったペットの名前は？",
        "好きな食べ物は？",
        "初めて買ったゲームは？",
        "思い出の旅行先は？"
    ];

    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(20);
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockTime = TimeSpan.FromMinutes(10);
    private const string PasswordChars = "abcdefghjkmnpqrstuvwxyz23456789"; // 見間違えやすい i l o 0 1 を除く

    private readonly object _lock = new();
    private readonly string _path;
    private readonly ILogger<CrewRoster>? _log;
    private List<CrewUser> _users = new();
    private Dictionary<string, string> _assign = new(); // 行路番号 → ユーザー名
    private readonly Dictionary<string, (DateTime Utc, string Train)> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int Count, DateTime Until)> _failures = new(StringComparer.OrdinalIgnoreCase);

    public sealed class CrewUser
    {
        public string Name { get; set; } = "";
        public string Salt { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string AnswerSalt { get; set; } = "";
        public List<string> Questions { get; set; } = new();
        public List<string> AnswerHashes { get; set; } = new();
        public List<string> TokenHashes { get; set; } = new();
        public DateTime CreatedUtc { get; set; }
    }

    private sealed class FileShape
    {
        public List<CrewUser> Users { get; set; } = new();
        public Dictionary<string, string> Assignments { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public CrewRoster(string dataDir, ILogger<CrewRoster>? log = null)
    {
        _path = Path.Combine(dataDir, FileName);
        _log = log;
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<FileShape>(File.ReadAllText(_path), Json) is { } f)
            {
                _users = f.Users.Where(u => !string.IsNullOrWhiteSpace(u.Name)).ToList();
                _assign = f.Assignments.Where(a => Find(a.Value) != null).ToDictionary(a => a.Key, a => Find(a.Value)!.Name);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "{File} を読めませんでした（空で始めます）", FileName);
        }
    }

    private void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new FileShape { Users = _users, Assignments = _assign }, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "{File} に保存できませんでした", FileName);
        }
    }

    private CrewUser? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : _users.FirstOrDefault(u => string.Equals(u.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    // ---- パスワード・答えのハッシュ ----

    private static string NewPassword()
    {
        var s = new StringBuilder();
        for (var i = 0; i < 8; i++)
        {
            if (i == 4) s.Append('-');
            s.Append(PasswordChars[RandomNumberGenerator.GetInt32(PasswordChars.Length)]);
        }

        return s.ToString(); // 例: k7mq-3xwp
    }

    private static string Hash(string text, string salt) =>
        Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(text), Convert.FromBase64String(salt), 100_000, HashAlgorithmName.SHA256, 32));

    private static bool SameHash(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    /// <summary>答えのゆれをならす（前後と途中の空白、全角・半角、大文字・小文字、ひらがな・カタカナ）。</summary>
    private static string NormalizeAnswer(string? a)
    {
        var s = (a ?? "").Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c)) continue;
            sb.Append(c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c);
        }

        return sb.ToString();
    }

    private static string NormalizePassword(string? p) => (p ?? "").Trim().ToLowerInvariant().Replace(" ", "");

    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    /// <summary>新しいパスワードを決め、これまでの合い札を捨てる。新しいパスワードを返す。</summary>
    private static string ResetPassword(CrewUser u)
    {
        var pw = NewPassword();
        u.Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        u.PasswordHash = Hash(pw.Replace("-", ""), u.Salt);
        u.TokenHashes.Clear();
        return pw;
    }

    private static string IssueToken(CrewUser u)
    {
        var t = NewToken();
        u.TokenHashes.Add(TokenHash(t));
        while (u.TokenHashes.Count > 5) u.TokenHashes.RemoveAt(0); // 5台まで
        return t;
    }

    private string? Locked(string key) =>
        _failures.TryGetValue(key, out var f) && f.Count >= MaxFailures && DateTime.UtcNow < f.Until
            ? $"続けて間違えたため、{Math.Ceiling((f.Until - DateTime.UtcNow).TotalMinutes)}分ほど受け付けません"
            : null;

    private void Fail(string key)
    {
        var f = _failures.GetValueOrDefault(key);
        if (f.Count >= MaxFailures && DateTime.UtcNow >= f.Until) f = default;
        _failures[key] = (f.Count + 1, DateTime.UtcNow + LockTime);
    }

    private static string? CheckName(string n) =>
        n.Length == 0 ? "ユーザー名を入れてください" : n.Length > 20 ? "ユーザー名は20文字までです" : null;

    private static string? CheckQuestions(IReadOnlyList<(string Q, string A)> qa)
    {
        if (qa.Count != 3) return "秘密の質問を3つ選んでください";
        if (qa.Any(x => !Questions.Contains(x.Q))) return "秘密の質問は一覧から選んでください";
        if (qa.Select(x => x.Q).Distinct().Count() != 3) return "秘密の質問は、ちがうものを3つ選んでください";
        if (qa.Any(x => NormalizeAnswer(x.A).Length == 0)) return "秘密の質問の答えを、3つとも入れてください";
        return null;
    }

    private static void SetQuestions(CrewUser u, IReadOnlyList<(string Q, string A)> qa)
    {
        u.AnswerSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        u.Questions = qa.Select(x => x.Q).ToList();
        u.AnswerHashes = qa.Select(x => Hash(NormalizeAnswer(x.A), u.AnswerSalt)).ToList();
    }

    // ---- 登録・ログイン・出し直し ----

    public sealed record Issued(string Name, string Password, string Token);

    /// <summary>タブレットで自分で登録する（秘密の質問3つも一緒に）。パスワードと合い札を返す。</summary>
    public (Issued? Ok, string? Error) Register(string? name, IReadOnlyList<(string Q, string A)> qa)
    {
        var n = (name ?? "").Trim();
        if (CheckName(n) is { } e1) return (null, e1);
        if (CheckQuestions(qa) is { } e2) return (null, e2);
        lock (_lock)
        {
            if (Find(n) != null) return (null, $"{n} はもう使われています");
            var u = new CrewUser { Name = n, CreatedUtc = DateTime.UtcNow };
            var pw = ResetPassword(u);
            SetQuestions(u, qa);
            var token = IssueToken(u);
            _users.Add(u);
            Save();
            return (new Issued(n, pw, token), null);
        }
    }

    /// <summary>勤怠管理の画面でユーザーを作る（秘密の質問は、本人が最初のログインで決める）。パスワードを返す。</summary>
    public (string? Password, string? Error) AddUser(string? name)
    {
        var n = (name ?? "").Trim();
        if (CheckName(n) is { } e) return (null, e);
        lock (_lock)
        {
            if (Find(n) != null) return (null, $"{n} はもうあります");
            var u = new CrewUser { Name = n, CreatedUtc = DateTime.UtcNow };
            var pw = ResetPassword(u);
            _users.Add(u);
            Save();
            return (pw, null);
        }
    }

    /// <summary>ログイン。合い札と、秘密の質問がまだ決まっていないかを返す。</summary>
    public (string? Name, string? Token, bool NeedQuestions, string? Error) Login(string? name, string? password)
    {
        lock (_lock)
        {
            var key = "login:" + (name ?? "").Trim();
            if (Locked(key) is { } l) return (null, null, false, l);
            var u = Find(name);
            if (u == null || u.PasswordHash.Length == 0 || !SameHash(Hash(NormalizePassword(password).Replace("-", ""), u.Salt), u.PasswordHash))
            {
                Fail(key);
                return (null, null, false, "ユーザー名かパスワードがちがいます");
            }

            _failures.Remove(key);
            var token = IssueToken(u);
            Save();
            return (u.Name, token, u.Questions.Count != 3, null);
        }
    }

    /// <summary>合い札から、ログインしているユーザー名。使えない合い札なら null。</summary>
    public string? UserOfToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var h = TokenHash(token);
        lock (_lock) return _users.FirstOrDefault(u => u.TokenHashes.Contains(h))?.Name;
    }

    public bool NeedQuestions(string name)
    {
        lock (_lock) return Find(name) is { } u && u.Questions.Count != 3;
    }

    /// <summary>ログアウト（その合い札を捨てる）。</summary>
    public void Logout(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        var h = TokenHash(token);
        lock (_lock)
        {
            foreach (var u in _users.Where(u => u.TokenHashes.Remove(h))) { }
            Save();
        }
    }

    /// <summary>秘密の質問を決める・決め直す（ログインしている本人）。</summary>
    public string? SetQuestions(string name, IReadOnlyList<(string Q, string A)> qa)
    {
        if (CheckQuestions(qa) is { } e) return e;
        lock (_lock)
        {
            if (Find(name) is not { } u) return "ユーザーがいません";
            SetQuestions(u, qa);
            Save();
        }

        return null;
    }

    /// <summary>パスワードを忘れたとき: そのユーザーの秘密の質問。</summary>
    public (List<string>? Questions, string? Error) QuestionsOf(string? name)
    {
        lock (_lock)
        {
            if (Find(name) is not { } u) return (null, "そのユーザー名はありません");
            if (u.Questions.Count != 3) return (null, "秘密の質問がまだ決まっていません。勤怠管理の担当者に、パスワードの再発行を頼んでください");
            return (u.Questions.ToList(), null);
        }
    }

    /// <summary>パスワードを忘れたとき: 秘密の質問に3つとも答えられたら、新しいパスワードと合い札を出す。</summary>
    public (Issued? Ok, string? Error) ResetByAnswers(string? name, IReadOnlyList<string> answers)
    {
        lock (_lock)
        {
            var key = "reset:" + (name ?? "").Trim();
            if (Locked(key) is { } l) return (null, l);
            if (Find(name) is not { } u || u.Questions.Count != 3) return (null, "そのユーザー名はないか、秘密の質問が決まっていません");
            var ok = answers.Count == 3 && Enumerable.Range(0, 3).All(i => SameHash(Hash(NormalizeAnswer(answers[i]), u.AnswerSalt), u.AnswerHashes[i]));
            if (!ok)
            {
                Fail(key);
                return (null, "答えがちがいます（3つとも合っている必要があります）");
            }

            _failures.Remove(key);
            var pw = ResetPassword(u);
            var token = IssueToken(u);
            Save();
            return (new Issued(u.Name, pw, token), null);
        }
    }

    /// <summary>勤怠管理の画面からパスワードを出し直す（秘密の質問はそのまま。それまでの合い札は使えなくなる）。</summary>
    public (string? Password, string? Error) ResetByAdmin(string? name)
    {
        lock (_lock)
        {
            if (Find(name) is not { } u) return (null, "そのユーザーはいません");
            var pw = ResetPassword(u);
            _failures.Remove("login:" + u.Name);
            _failures.Remove("reset:" + u.Name);
            Save();
            return (pw, null);
        }
    }

    // ---- 一覧・割り当て ----

    /// <summary>登録されているユーザー名（大文字・小文字は区別しない）。なければ null。</summary>
    public string? User(string? name)
    {
        lock (_lock) return Find(name)?.Name;
    }

    public List<(string Name, bool HasQuestions, DateTime Created)> Users()
    {
        lock (_lock) return _users.Select(u => (u.Name, u.Questions.Count == 3, u.CreatedUtc)).ToList();
    }

    /// <summary>ユーザーを消す（割り当てていた行路は、担当なしに戻す）。</summary>
    public bool RemoveUser(string? name)
    {
        lock (_lock)
        {
            if (Find(name) is not { } u) return false;
            _users.Remove(u);
            foreach (var d in _assign.Where(x => x.Value == u.Name).Select(x => x.Key).ToList()) _assign.Remove(d);
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
                if (Find(user) is not { } u) return $"{user} というユーザーはいません";
                _assign[duty] = u.Name;
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

    /// <summary>そのユーザーが担当する行路の番号（行路番号の順）。</summary>
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
