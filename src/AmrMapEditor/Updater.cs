using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AmrMapEditor;

/// <summary>GitHub 정식 릴리스 (태그 vX.Y.Z)</summary>
public sealed record ReleaseInfo(Version Version, string Notes, string PageUrl, string AssetUrl, long Size,
                                 string? Sha256, string? Sha256Url, DateTimeOffset Published);

/// <summary>받아서 검증까지 끝난 새 exe (종료할 때 교체)</summary>
public sealed record PendingUpdate(Version Version, string Path, string Sha256);

/// <summary>업데이트 실패: 사용자 문구(원인) + 해결</summary>
public sealed class UpdateException(string message, string fix, Exception? inner = null) : Exception(message, inner)
{
    public string Fix { get; } = fix;

    /// <summary>받아 둔 파일이 없거나 손상됨 → 다시 받아야 함</summary>
    public bool DropPending { get; init; }
}

/// <summary>
/// 자동 업데이트: GitHub 최신 정식 릴리스 확인 → 받기(SHA-256 검증) → 종료할 때 exe 교체.
/// 실행 중인 exe는 덮어쓸 수 없지만 이름은 바꿀 수 있으므로 현재 exe → .old, 새 exe → 원래 이름.
/// .old는 한 세대 보관해 '이전 버전으로 되돌리기'에 씀
/// </summary>
public static class Updater
{
    public const string Repo = "Leepy0/AmrMapEditor";
    public const string AssetName = "AmrMapEditor.exe";
    private const string ManifestName = "AmrMapEditor.json";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases";

    /// <summary>최신 정식 버전 exe 고정 주소 (버전이 바뀌어도 같음, 브라우저로 직접 받기)</summary>
    public static string DownloadUrl => $"https://github.com/{Repo}/releases/latest/download/{AssetName}";

    private const string PendingKey = "UpdatePending";
    private static readonly HttpClient Http;   // 버전(User-Agent)을 읽은 뒤 만들어야 하므로 정적 생성자에서

    static Updater()
    {
        // CI가 Version · SourceRevisionId(커밋) · BuildChannel을 넣음. InformationalVersion = "0.7.0+<커밋>"
        Assembly asm = typeof(Updater).Assembly;
        string info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        string[] parts = info.Split('+', 2);
        CurrentVersion = TryParseVersion(parts[0], out Version v) ? v : Normalize(asm.GetName().Version ?? new Version(0, 0, 0));
        Commit = parts.Length > 1 && parts[1].Length >= 7 ? parts[1][..7] : null;
        Channel = asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildChannel")?.Value ?? "";
        Http = CreateClient();
    }

    public static Version CurrentVersion { get; }
    public static string VersionText => CurrentVersion.ToString(3);
    public static string? Commit { get; }

    /// <summary>release = 정식, dev = main 테스트 빌드, local = 로컬 빌드</summary>
    public static string Channel { get; }

    public static string ChannelName => Channel switch
    {
        "release" => "정식 배포",
        "dev" => "테스트 빌드",
        _ => "로컬 빌드",
    };

    /// <summary>CI로 만든 exe만 스스로 교체 (로컬 빌드는 bin 폴더라 교체하지 않음)</summary>
    public static bool CanSelfUpdate => Channel is "release" or "dev" && ExePath != null;

    public static string? ExePath => Environment.ProcessPath;
    private static string? OldPath => ExePath != null ? ExePath + ".old" : null;

    private static string UpdateDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmrMapEditor", "updates");

    private static HttpClient CreateClient()
    {
        // 시스템 프록시 사용 + 사내 프록시가 Windows 로그인 인증을 요구해도 통과
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };   // 시간 제한은 호출마다 토큰으로
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"AmrMapEditor/{VersionText}");
        return http;
    }

    // ───────────── 확인 ─────────────

    /// <summary>
    /// 최신 정식 릴리스 (prerelease · draft 제외). 정식 릴리스가 없거나 exe가 없으면 null.
    /// 사내망은 api.github.com이 막혀 있어 github.com 주소(매니페스트 · 리다이렉트)를 먼저 쓰고 API는 마지막에
    /// </summary>
    public static async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        Exception? first = null;
        foreach (Func<CancellationToken, Task<ReleaseInfo?>> source in new Func<CancellationToken, Task<ReleaseInfo?>>[]
                 { FromManifestAsync, FromRedirectAsync, FromApiAsync })
        {
            try
            {
                ReleaseInfo? r = await source(ct);
                if (r != null) return r;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                first ??= ex;   // 다음 방법으로
            }
        }
        if (first != null) throw first;
        return null;
    }

    /// <summary>릴리스에 함께 올린 AmrMapEditor.json (버전 · SHA-256 · 크기 · 변경 내용). 없으면 null</summary>
    private static async Task<ReleaseInfo?> FromManifestAsync(CancellationToken ct)
    {
        using HttpResponseMessage res = await Http.GetAsync($"https://github.com/{Repo}/releases/latest/download/{ManifestName}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        JsonElement m = doc.RootElement;
        if (!TryParseVersion(Str(m, "version"), out Version version)) return null;
        string tag = "v" + version.ToString(3);
        string file = Str(m, "file") is { Length: > 0 } f ? f : AssetName;
        string sha = Str(m, "sha256");
        DateTimeOffset published = DateTimeOffset.TryParse(Str(m, "published"), out DateTimeOffset p) ? p : DateTimeOffset.Now;
        return new ReleaseInfo(version, Str(m, "notes"), Str(m, "page") is { Length: > 0 } page ? page : $"{ReleasesPage}/tag/{tag}",
            $"{ReleasesPage}/download/{tag}/{file}",
            m.TryGetProperty("size", out JsonElement s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0,
            sha.Length == 64 ? sha.ToLowerInvariant() : null, $"{ReleasesPage}/download/{tag}/{file}.sha256", published);
    }

    /// <summary>매니페스트가 없는 릴리스: releases/latest → releases/tag/vX.Y.Z 로 넘어가는 주소에서 버전만 읽음</summary>
    private static async Task<ReleaseInfo?> FromRedirectAsync(CancellationToken ct)
    {
        using HttpResponseMessage res = await Http.GetAsync($"{ReleasesPage}/latest", HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        string final = res.RequestMessage?.RequestUri?.AbsolutePath ?? "";
        int i = final.LastIndexOf("/tag/", StringComparison.Ordinal);
        if (i < 0 || !TryParseVersion(Uri.UnescapeDataString(final[(i + 5)..]), out Version version)) return null;
        string tag = "v" + version.ToString(3);
        return new ReleaseInfo(version, "", $"{ReleasesPage}/tag/{tag}", $"{ReleasesPage}/download/{tag}/{AssetName}", 0,
            null, $"{ReleasesPage}/download/{tag}/{AssetName}.sha256", DateTimeOffset.Now);
    }

    /// <summary>GitHub API (집 · 사외망)</summary>
    private static async Task<ReleaseInfo?> FromApiAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using HttpResponseMessage res = await Http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new UpdateException("GitHub 확인 횟수 제한에 걸렸습니다 (같은 인터넷 주소에서 시간당 60회).", "1시간쯤 뒤에 다시 확인하세요.");
        res.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        JsonElement r = doc.RootElement;
        if (!TryParseVersion(Str(r, "tag_name"), out Version version)) return null;

        JsonElement? exe = null;
        string? shaUrl = null;
        foreach (JsonElement a in r.GetProperty("assets").EnumerateArray())
        {
            string name = Str(a, "name");
            if (name.Equals(AssetName, StringComparison.OrdinalIgnoreCase)) exe = a;
            else if (name.Equals(AssetName + ".sha256", StringComparison.OrdinalIgnoreCase)) shaUrl = Str(a, "browser_download_url");
        }
        if (exe is not JsonElement e) return null;

        // GitHub가 계산한 해시(digest)가 있으면 우선, 없으면 함께 올린 .sha256 파일
        string digest = Str(e, "digest");
        string? sha = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;
        DateTimeOffset published = DateTimeOffset.TryParse(Str(r, "published_at"), out DateTimeOffset p) ? p : DateTimeOffset.Now;
        return new ReleaseInfo(version, Str(r, "body"), Str(r, "html_url") is { Length: > 0 } page ? page : ReleasesPage,
            Str(e, "browser_download_url"), e.TryGetProperty("size", out JsonElement s) ? s.GetInt64() : 0, sha, shaUrl, published);
    }

    // ───────────── 받기 ─────────────

    /// <summary>새 exe 받기 → SHA-256 검증. 이미 받아 둔 같은 파일이 있으면 그대로 씀</summary>
    public static async Task<PendingUpdate> DownloadAsync(ReleaseInfo r, IProgress<double> progress, CancellationToken ct)
    {
        string expected = await ExpectedShaAsync(r, ct);
        Directory.CreateDirectory(UpdateDir);
        string target = Path.Combine(UpdateDir, $"AmrMapEditor_{r.Version.ToString(3)}.exe");
        if (File.Exists(target) && await Task.Run(() => HashFile(target), ct) == expected)
            return new PendingUpdate(r.Version, target, expected);
        await DownloadFileAsync(r, target, expected, progress, ct);
        return new PendingUpdate(r.Version, target, expected);
    }

    /// <summary>
    /// 지정한 위치에 최신 exe 저장 (다른 PC에 옮기거나 직접 바꿀 때).
    /// 프로그램이 직접 받은 파일은 '인터넷에서 받음' 표시가 없어 SmartScreen 경고가 뜨지 않음
    /// </summary>
    public static async Task DownloadToAsync(ReleaseInfo r, string path, IProgress<double> progress, CancellationToken ct)
    {
        string expected = await ExpectedShaAsync(r, ct);
        await DownloadFileAsync(r, path, expected, progress, ct);
    }

    private static async Task<string> ExpectedShaAsync(ReleaseInfo r, CancellationToken ct) =>
        (r.Sha256 ?? await FetchShaAsync(r.Sha256Url, ct)
            ?? throw new UpdateException("릴리스에 검증용 SHA-256 정보가 없습니다.", "릴리스 페이지에서 직접 받으세요.")).ToLowerInvariant();

    /// <summary>임시 파일(.partial)로 받고 SHA-256이 맞을 때만 target으로 옮김</summary>
    private static async Task DownloadFileAsync(ReleaseInfo r, string target, string expected, IProgress<double> progress, CancellationToken ct)
    {
        string part = target + ".partial";
        try
        {
            using (HttpResponseMessage res = await Http.GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                res.EnsureSuccessStatusCode();
                long total = res.Content.Headers.ContentLength ?? r.Size;
                await using Stream src = await res.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buf = new byte[81920];
                long done = 0;
                double reported = -1;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    double pct = total > 0 ? done * 100.0 / total : 0;
                    if (pct - reported >= 1)
                    {
                        progress.Report(pct);
                        reported = pct;
                    }
                }
            }
        }
        catch (Exception)
        {
            TryDelete(part);   // 취소 · 끊김: 반쯤 받은 파일을 남기지 않음
            throw;
        }

        string actual = await Task.Run(() => HashFile(part), ct);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(part);
            throw new UpdateException("받은 파일이 손상됐습니다 (SHA-256 불일치).", "네트워크 상태를 확인하고 다시 받으세요.");
        }
        File.Move(part, target, overwrite: true);
    }

    private static async Task<string?> FetchShaAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url)) return null;
        string text = await Http.GetStringAsync(url, ct);
        // "해시  파일명" 또는 "해시 *파일명"
        string first = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.Length == 64 ? first.ToLowerInvariant() : null;
    }

    // ───────────── 받아 둔 업데이트 (설정에 기록) ─────────────

    public static PendingUpdate? LoadPending(IDictionary<string, string> settings)
    {
        if (!settings.TryGetValue(PendingKey, out string? v)) return null;
        string[] p = v.Split('|');
        if (p.Length == 3 && TryParseVersion(p[0], out Version ver) && ver > CurrentVersion && File.Exists(p[1]))
            return new PendingUpdate(ver, p[1], p[2]);
        // 이미 적용됐거나 파일이 없어짐
        if (p.Length >= 2) TryDelete(p[1]);
        settings.Remove(PendingKey);
        return null;
    }

    public static void SavePending(IDictionary<string, string> settings, PendingUpdate? p)
    {
        if (p == null) settings.Remove(PendingKey);
        else settings[PendingKey] = $"{p.Version.ToString(3)}|{p.Path}|{p.Sha256}";
    }

    // ───────────── 교체 · 되돌리기 ─────────────

    /// <summary>exe 폴더에 쓸 수 있는지 (Program Files · 읽기 전용 공유 폴더면 자동 교체 불가)</summary>
    public static bool CanWriteExeDir()
    {
        string? dir = ExePath != null ? Path.GetDirectoryName(ExePath) : null;
        if (dir == null) return false;
        string probe = Path.Combine(dir, $".amr-write-test-{Environment.ProcessId}");
        try
        {
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>받아 둔 exe로 교체 (현재 exe → .old). 실패하면 현재 exe를 그대로 두고 예외</summary>
    public static void Apply(PendingUpdate p) =>
        Apply(p, ExePath ?? throw new UpdateException("실행 파일 경로를 알 수 없습니다.", "릴리스 페이지에서 직접 받으세요."));

    internal static void Apply(PendingUpdate p, string exe)
    {
        string old = exe + ".old";
        if (!File.Exists(p.Path) || !HashFile(p.Path).Equals(p.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("받아 둔 업데이트 파일이 없거나 손상됐습니다.", "정보(ⓘ) 창에서 다시 받으세요.") { DropPending = true };
        try
        {
            if (File.Exists(old)) File.Delete(old);
        }
        catch (Exception ex)
        {
            throw new UpdateException("이전 버전 파일(.old)을 지울 수 없습니다.", "이전 버전으로 실행 중인 창이 있으면 모두 닫은 뒤 다시 시도하세요.", ex);
        }
        File.Move(exe, old);
        try
        {
            File.Copy(p.Path, exe);
        }
        catch (Exception)
        {
            File.Move(old, exe);   // 원래대로
            throw;
        }
        TryDelete(p.Path);
    }

    /// <summary>되돌릴 이전 버전 (.old) 버전 문자열, 없으면 null</summary>
    public static string? OldVersionText
    {
        get
        {
            string? old = OldPath;
            if (old == null || !File.Exists(old)) return null;
            string? pv = FileVersionInfo.GetVersionInfo(old).ProductVersion;
            return pv != null && TryParseVersion(pv.Split('+')[0], out Version v) ? v.ToString(3) : "이전 버전";
        }
    }

    /// <summary>.old로 되돌림: 현재 exe → .rollback (다음 실행 때 삭제), .old → exe</summary>
    public static void Rollback() =>
        Rollback(ExePath ?? throw new UpdateException("실행 파일 경로를 알 수 없습니다.", "릴리스 페이지에서 이전 버전을 직접 받으세요."));

    internal static void Rollback(string exe)
    {
        string old = exe + ".old";
        string tmp = exe + ".rollback";
        if (File.Exists(tmp)) File.Delete(tmp);
        File.Move(exe, tmp);
        try
        {
            File.Move(old, exe);
        }
        catch (Exception)
        {
            File.Move(tmp, exe);
            throw;
        }
    }

    /// <summary>같은 경로의 exe를 새로 실행 (교체 후 다시 시작)</summary>
    public static void Restart()
    {
        if (ExePath == null) return;
        Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ExePath) ?? "" });
    }

    /// <summary>시작할 때: 되돌리기 잔여 파일 · 중단된 받기 정리</summary>
    public static void CleanupLeftovers()
    {
        if (ExePath != null) TryDelete(ExePath + ".rollback");
        try
        {
            if (!Directory.Exists(UpdateDir)) return;
            foreach (string f in Directory.GetFiles(UpdateDir, "*.partial")) TryDelete(f);
        }
        catch (Exception)
        {
            // 정리 실패는 무시
        }
    }

    /// <summary>탐색기에서 파일 선택해 보여주기</summary>
    public static void ShowInFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 탐색기 실행 실패는 무시
        }
    }

    public static void OpenPage(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 브라우저 실행 실패는 무시
        }
    }

    // ───────────── 공통 ─────────────

    private static string HashFile(string path)
    {
        using FileStream fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>"v0.7.1" · "0.7.1" → 0.7.1 (3자리로 맞춤)</summary>
    private static bool TryParseVersion(string? s, out Version v)
    {
        v = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(s)) return false;
        string t = s.Trim().TrimStart('v', 'V').Split('-')[0];
        if (!Version.TryParse(t, out Version? parsed)) return false;
        v = Normalize(parsed);
        return true;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 사용 중이면 다음에 다시
        }
    }
}
