using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Curia.Models;
using Curia.Helpers;
using Curia.Services;

namespace Curia.ViewModels;

/// <summary>Git リポジトリ情報</summary>
public class GitRepoItem
{
    public string ProjectName { get; set; } = "";
    public string RepoName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string RemoteOrigin { get; set; } = "";
    public string Branch { get; set; } = "";
    public string LastCommitDate { get; set; } = "";
}

/// <summary>git_repos.json の保存フォーマット</summary>
internal class GitReposJson
{
    [JsonPropertyName("savedAt")] public string SavedAt { get; set; } = "";
    [JsonPropertyName("machine")] public string Machine { get; set; } = "";
    [JsonPropertyName("repos")] public List<GitRepoJsonEntry> Repos { get; set; } = [];
}

internal class GitRepoJsonEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("relativePath")] public string RelativePath { get; set; } = "";
    [JsonPropertyName("relPath")] public string RelPath { get; set; } = "";
    [JsonPropertyName("remoteOrigin")] public string RemoteOrigin { get; set; } = "";
    [JsonPropertyName("branch")] public string Branch { get; set; } = "";
    [JsonPropertyName("lastCommitDate")] public string LastCommitDate { get; set; } = "";
    [JsonPropertyName("projectName")] public string ProjectName { get; set; } = "";
}

public partial class GitReposViewModel : ObservableObject
{
    private readonly ProjectDiscoveryService _discoveryService;
    private readonly ConfigService _configService;
    private readonly AsyncInitializationGate _initialization = new();

    [ObservableProperty]
    private ObservableCollection<ProjectInfo> projects = [];

    [ObservableProperty]
    private ProjectInfo? selectedProject;

    [ObservableProperty]
    private bool isScanning;

    [ObservableProperty]
    private string statusText = "Select a project and scan";

    [ObservableProperty]
    private GitRepoItem? selectedRepo;

    public ObservableCollection<GitRepoItem> Repos { get; } = [];

    public GitReposViewModel(ProjectDiscoveryService discoveryService, ConfigService configService)
    {
        _discoveryService = discoveryService;
        _configService = configService;
    }

    public async Task InitAsync()
    {
        var infos = await Task.Run(() => _discoveryService.GetProjectInfoList());
        var hiddenKeys = _configService.LoadHiddenProjects();
        Projects.Clear();
        foreach (var p in infos.Where(p => !hiddenKeys.Contains(p.HiddenKey)))
            Projects.Add(p);
    }

    public Task EnsureInitializedAsync() => _initialization.EnsureAsync(InitAsync);

    // --- コマンド ---

    [RelayCommand]
    private async Task Scan()
    {
        if (SelectedProject == null)
        {
            StatusText = "Please select a project.";
            return;
        }

        IsScanning = true;
        Repos.Clear();
        StatusText = "Scanning...";

        var project = SelectedProject;
        try
        {
            var results = await Task.Run(() => ScanGitRepos(project));
            foreach (var r in results) Repos.Add(r);

            StatusText = results.Count > 0
                ? $"Found {results.Count} repos in {project.Name}"
                : $"No Git repositories found ({project.Name})";
        }
        catch (Exception ex)
        {
            StatusText = $"[ERROR] {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void SaveToCloud()
    {
        if (Repos.Count == 0)
        {
            StatusText = "まずスキャンを実行してください。";
            return;
        }
        if (SelectedProject == null) return;

        var sharedPath = Path.Combine(SelectedProject.Path, "shared");
        if (!Directory.Exists(sharedPath))
        {
            StatusText = $"shared/ フォルダが見つかりません ({SelectedProject.Name})";
            return;
        }

        var localRoot = _configService.LoadSettings().LocalProjectsRoot
            .TrimEnd('\\', '/');

        var entries = Repos.Select(r =>
        {
            var relPath = r.FullPath;
            if (relPath.StartsWith(localRoot, StringComparison.OrdinalIgnoreCase))
                relPath = relPath[localRoot.Length..].TrimStart('\\', '/');
            return new GitRepoJsonEntry
            {
                Name = r.RepoName,
                RelativePath = r.RelativePath,
                RelPath = relPath,
                RemoteOrigin = r.RemoteOrigin,
                Branch = r.Branch,
                LastCommitDate = r.LastCommitDate,
                ProjectName = r.ProjectName,
            };
        }).ToList();

        var payload = new GitReposJson
        {
            SavedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            Machine = Environment.MachineName,
            Repos = entries,
        };

        var outPath = Path.Combine(sharedPath, "git_repos.json");
        try
        {
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(outPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            StatusText = $"Saved to shared/git_repos.json ({Repos.Count} repos)";
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void LoadFromCloud()
    {
        if (SelectedProject == null)
        {
            StatusText = "プロジェクトを選択してください。";
            return;
        }

        var sharedPath = Path.Combine(SelectedProject.Path, "shared");
        var jsonPath = Path.Combine(sharedPath, "git_repos.json");
        if (!File.Exists(jsonPath))
        {
            StatusText = "shared/git_repos.json が見つかりません。";
            return;
        }

        try
        {
            var content = File.ReadAllText(jsonPath, new UTF8Encoding(false));
            var data = JsonSerializer.Deserialize<GitReposJson>(content) ?? new GitReposJson();

            Repos.Clear();
            foreach (var e in data.Repos)
            {
                Repos.Add(new GitRepoItem
                {
                    ProjectName = e.ProjectName,
                    RepoName = e.Name,
                    RelativePath = e.RelativePath,
                    FullPath = e.RelPath,
                    RemoteOrigin = e.RemoteOrigin,
                    Branch = e.Branch,
                    LastCommitDate = e.LastCommitDate,
                });
            }
            StatusText = $"Loaded {Repos.Count} repos (saved: {data.SavedAt}, machine: {data.Machine})";
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CopyCloneScript()
    {
        if (Repos.Count == 0)
        {
            StatusText = "まずスキャンを実行してください。";
            return;
        }

        var devSource = SelectedProject != null
            ? Path.Combine(SelectedProject.Path, "development", "source")
            : ".";

        var sb = new StringBuilder();
        var projLabel = SelectedProject?.Name ?? "Unknown";
        sb.AppendLine($"# Git repos restore script: {projLabel}");
        sb.AppendLine($"# Generated: {DateTime.Today:yyyy-MM-dd} on {Environment.MachineName}");
        sb.AppendLine($"cd \"{devSource}\"");

        foreach (var repo in Repos)
        {
            if (string.IsNullOrEmpty(repo.RemoteOrigin) || repo.RemoteOrigin == "(none)") continue;

            if (repo.RelativePath == ".")
            {
                sb.AppendLine($"git clone {repo.RemoteOrigin}");
            }
            else
            {
                var parent = Path.GetDirectoryName(repo.RelativePath);
                if (!string.IsNullOrEmpty(parent) && parent != ".")
                    sb.AppendLine($"cd \"{parent}\"");
                sb.AppendLine($"git clone {repo.RemoteOrigin}");
            }
        }

        try
        {
            System.Windows.Clipboard.SetText(sb.ToString());
            StatusText = $"Clone script copied to clipboard ({Repos.Count} repos)";
        }
        catch (Exception ex)
        {
            StatusText = $"Clipboard error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenRepoFolder(GitRepoItem? repo)
    {
        var target = repo ?? SelectedRepo;
        if (target == null)
        {
            StatusText = "Openするリポジトリを選択してください。";
            return;
        }

        var resolvedPath = ResolveRepoPath(target);
        if (string.IsNullOrEmpty(resolvedPath) || !Directory.Exists(resolvedPath))
        {
            StatusText = $"フォルダが見つかりません: {target.RepoName}";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{resolvedPath}\"",
                UseShellExecute = true,
            });
            StatusText = $"Opened: {target.RepoName}";
        }
        catch (Exception ex)
        {
            StatusText = $"Open failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenRepoTerminal(GitRepoItem? repo)
    {
        var target = repo ?? SelectedRepo;
        if (target == null)
        {
            StatusText = "Terminalを開くリポジトリを選択してください。";
            return;
        }

        var resolvedPath = ResolveRepoPath(target);
        if (string.IsNullOrEmpty(resolvedPath) || !Directory.Exists(resolvedPath))
        {
            StatusText = $"フォルダが見つかりません: {target.RepoName}";
            return;
        }

        if (TryStart("wt.exe", $"-d \"{resolvedPath}\"") ||
            TryStart("pwsh.exe", $"-NoExit -Command \"Set-Location '{resolvedPath}'\"") ||
            TryStart("powershell.exe", $"-NoExit -Command \"Set-Location '{resolvedPath}'\""))
        {
            StatusText = $"Terminal opened: {target.RepoName}";
            return;
        }

        StatusText = "Terminal起動に失敗しました。";
    }

    // --- スキャンロジック ---

    private static List<GitRepoItem> ScanGitRepos(ProjectInfo project)
    {
        var results = new List<GitRepoItem>();
        var devSource = Path.Combine(project.Path, "development", "source");
        if (!Directory.Exists(devSource)) return results;

        try
        {
            foreach (var gitDir in Directory.EnumerateDirectories(devSource, ".git",
                         SearchOption.AllDirectories))
            {
                var repoPath = Path.GetDirectoryName(gitDir)!;
                var relative = repoPath.Length > devSource.Length
                    ? repoPath[devSource.Length..].TrimStart('\\', '/')
                    : ".";

                var remote = RunGitCommand(repoPath, "remote", "get-url", "origin");
                if (string.IsNullOrEmpty(remote)) remote = "(none)";

                var branch = RunGitCommand(repoPath, "branch", "--show-current");
                if (string.IsNullOrEmpty(branch)) branch = "?";

                var lastDate = RunGitCommand(repoPath, "log", "-1", "--format=%cd", "--date=short");
                if (string.IsNullOrEmpty(lastDate)) lastDate = "-";

                results.Add(new GitRepoItem
                {
                    ProjectName = project.Name,
                    RepoName = Path.GetFileName(repoPath),
                    RelativePath = relative,
                    FullPath = repoPath,
                    RemoteOrigin = remote,
                    Branch = branch,
                    LastCommitDate = lastDate,
                });
            }
        }
        catch { /* 部分的な結果を返す */ }

        return results;
    }

    private static string RunGitCommand(string repoPath, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"-C \"{repoPath}\" {string.Join(" ", args)}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "";
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return stdout.Trim();
        }
        catch { return ""; }
    }

    public string GetGitLog(GitRepoItem repo, int maxCount = 50)
    {
        var path = ResolveRepoPath(repo);
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return "(path not found)";
        var log = RunGitCommand(path, "log", "--graph", $"-n {maxCount}", "\"--format=%h %ad %s\"", "\"--date=format:%Y-%m-%d %H:%M\"");
        return string.IsNullOrEmpty(log) ? "(no commits)" : log;
    }

    private string ResolveRepoPath(GitRepoItem repo)
    {
        if (!string.IsNullOrWhiteSpace(repo.FullPath))
        {
            if (Path.IsPathRooted(repo.FullPath) && Directory.Exists(repo.FullPath))
                return repo.FullPath;

            var localRoot = _configService.LoadSettings().LocalProjectsRoot?.TrimEnd('\\', '/');
            if (!string.IsNullOrWhiteSpace(localRoot) && !Path.IsPathRooted(repo.FullPath))
            {
                var candidate = Path.GetFullPath(Path.Combine(localRoot!, repo.FullPath));
                if (Directory.Exists(candidate)) return candidate;
            }
        }

        if (SelectedProject != null && !string.IsNullOrWhiteSpace(repo.RelativePath))
        {
            var devSource = Path.Combine(SelectedProject.Path, "development", "source");
            var relative = repo.RelativePath == "." ? "" : repo.RelativePath;
            var candidate = string.IsNullOrEmpty(relative)
                ? devSource
                : Path.GetFullPath(Path.Combine(devSource, relative));
            if (Directory.Exists(candidate)) return candidate;
        }

        return repo.FullPath;
    }

    private static bool TryStart(string exe, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
