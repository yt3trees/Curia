using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Curia.Models;

namespace Curia.Services;

public class AgentDeploymentService
{
    public sealed class DeploymentTarget
    {
        public DeploymentScopeType ScopeType { get; init; }
        public string ScopeId { get; init; } = "";
        public ProjectInfo? Project { get; init; }
        public string TargetSubPath { get; init; } = "";
        public GlobalDeploymentProfile? Profile { get; init; }
    }

    private readonly ConfigService _configService;

    public AgentDeploymentService(ConfigService configService)
    {
        _configService = configService;
    }

    public DeploymentTarget CreateProjectTarget(ProjectInfo project, string targetSubPath)
    {
        var normalizedSubPath = NormalizeTargetSubPath(targetSubPath);
        return new DeploymentTarget
        {
            ScopeType = DeploymentScopeType.Project,
            ScopeId = project.Name,
            Project = project,
            TargetSubPath = normalizedSubPath
        };
    }

    public DeploymentTarget CreateGlobalTarget(GlobalDeploymentProfile profile)
    {
        return new DeploymentTarget
        {
            ScopeType = DeploymentScopeType.Global,
            ScopeId = profile.Id,
            Profile = profile,
            TargetSubPath = ""
        };
    }

    // ─── Agent Deploy/Undeploy ────────────────────────────────────────────

    public void DeployAgent(ProjectInfo project, string targetSubPath, AgentDefinition def, string content, CliTarget cli)
        => DeployAgent(CreateProjectTarget(project, targetSubPath), def, content, cli);

    public void DeployAgent(DeploymentTarget target, AgentDefinition def, string content, CliTarget cli)
    {
        var writeDir = ResolveAgentWriteDir(target, cli);
        try
        {
            Directory.CreateDirectory(writeDir);
            var filePath = ResolveAgentFilePath(writeDir, def, cli);
            var normalizedContent = NormalizeAgentContent(def, content, cli);
            File.WriteAllText(filePath, normalizedContent, new UTF8Encoding(false));
            Debug.WriteLine($"[AgentDeploy] '{def.Name}' → {filePath}");
            UpdateAgentState(target, def.Id, cli, deployed: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AgentDeploy] Failed: {ex.Message}");
            throw;
        }
    }

    public void UndeployAgent(ProjectInfo project, string targetSubPath, AgentDefinition def, CliTarget cli)
        => UndeployAgent(CreateProjectTarget(project, targetSubPath), def, cli);

    public void UndeployAgent(DeploymentTarget target, AgentDefinition def, CliTarget cli)
    {
        var writeDir = ResolveAgentWriteDir(target, cli);
        var filePath = ResolveAgentFilePath(writeDir, def, cli);
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                Debug.WriteLine($"[AgentUndeploy] '{def.Name}' ← {filePath}");
            }
            UpdateAgentState(target, def.Id, cli, deployed: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AgentUndeploy] Failed: {ex.Message}");
            throw;
        }
    }

    public bool IsAgentDeployed(ProjectInfo project, string targetSubPath, AgentDefinition def, CliTarget cli)
        => IsAgentDeployed(CreateProjectTarget(project, targetSubPath), def, cli);

    public bool IsAgentDeployed(DeploymentTarget target, AgentDefinition def, CliTarget cli)
    {
        var writeDir = ResolveAgentWriteDir(target, cli);
        return File.Exists(ResolveAgentFilePath(writeDir, def, cli));
    }

    // ─── Rule Deploy/Undeploy ─────────────────────────────────────────────

    public void DeployRule(ProjectInfo project, string targetSubPath, ContextRuleDefinition def, string content, CliTarget cli)
        => DeployRule(CreateProjectTarget(project, targetSubPath), def, content, cli);

    public void DeployRule(DeploymentTarget target, ContextRuleDefinition def, string content, CliTarget cli)
    {
        var contextFilePaths = ResolveRuleContextFilePaths(target, cli);
        if (contextFilePaths.Count == 0) return;

        try
        {
            foreach (var contextFilePath in contextFilePaths)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(contextFilePath)!);

                var existing = File.Exists(contextFilePath)
                    ? File.ReadAllText(contextFilePath, new UTF8Encoding(false))
                    : "";

                var markerStart = $"<!-- [AgentHub:{def.Id}] -->";
                var newSection = $"{markerStart}\n{content.TrimEnd()}\n<!-- [/AgentHub:{def.Id}] -->";

                string newContent;
                if (existing.Contains(markerStart))
                    newContent = ReplaceMarkedSection(existing, def.Id, newSection);
                else
                    newContent = existing.TrimEnd() + "\n\n" + newSection + "\n";

                File.WriteAllText(contextFilePath, newContent, new UTF8Encoding(false));
            }
            UpdateRuleState(target, def.Id, cli, deployed: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RuleDeploy] Failed: {ex.Message}");
            throw;
        }
    }

    public void UndeployRule(ProjectInfo project, string targetSubPath, ContextRuleDefinition def, CliTarget cli)
        => UndeployRule(CreateProjectTarget(project, targetSubPath), def, cli);

    public void UndeployRule(DeploymentTarget target, ContextRuleDefinition def, CliTarget cli)
    {
        var contextFilePaths = ResolveRuleContextFilePaths(target, cli);
        if (contextFilePaths.Count == 0) return;

        try
        {
            foreach (var contextFilePath in contextFilePaths)
            {
                if (!File.Exists(contextFilePath))
                    continue;

                var existing = File.ReadAllText(contextFilePath, new UTF8Encoding(false));
                var markerStart = $"<!-- [AgentHub:{def.Id}] -->";
                if (!existing.Contains(markerStart))
                    continue;

                var newContent = RemoveMarkedSection(existing, def.Id);
                File.WriteAllText(contextFilePath, newContent, new UTF8Encoding(false));
            }
            UpdateRuleState(target, def.Id, cli, deployed: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RuleUndeploy] Failed: {ex.Message}");
            throw;
        }
    }

    public bool IsRuleDeployed(ProjectInfo project, string targetSubPath, ContextRuleDefinition def, CliTarget cli)
        => IsRuleDeployed(CreateProjectTarget(project, targetSubPath), def, cli);

    public bool IsRuleDeployed(DeploymentTarget target, ContextRuleDefinition def, CliTarget cli)
    {
        var contextFilePaths = ResolveRuleContextFilePaths(target, cli);
        if (contextFilePaths.Count == 0) return false;

        foreach (var contextFilePath in contextFilePaths)
        {
            if (!File.Exists(contextFilePath))
                return false;

            try
            {
                var content = File.ReadAllText(contextFilePath, new UTF8Encoding(false));
                if (!content.Contains($"<!-- [AgentHub:{def.Id}] -->"))
                    return false;
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    // ─── Skill Deploy/Undeploy ────────────────────────────────────────────

    public void DeploySkill(ProjectInfo project, string targetSubPath, SkillDefinition def, CliTarget cli)
        => DeploySkill(CreateProjectTarget(project, targetSubPath), def, cli);

    public void DeploySkill(DeploymentTarget target, SkillDefinition def, CliTarget cli)
    {
        var targetSkillDir = ResolveSkillWriteDir(target, cli, def.Id);
        try
        {
            CopyDirectory(def.ContentDirectory, targetSkillDir);
            Debug.WriteLine($"[SkillDeploy] '{def.Name}' → {targetSkillDir}");
            UpdateSkillState(target, def.Id, cli, deployed: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SkillDeploy] Failed: {ex.Message}");
            throw;
        }
    }

    public void UndeploySkill(ProjectInfo project, string targetSubPath, SkillDefinition def, CliTarget cli)
        => UndeploySkill(CreateProjectTarget(project, targetSubPath), def, cli);

    public void UndeploySkill(DeploymentTarget target, SkillDefinition def, CliTarget cli)
    {
        var targetSkillDir = ResolveSkillWriteDir(target, cli, def.Id);
        try
        {
            if (Directory.Exists(targetSkillDir))
            {
                Directory.Delete(targetSkillDir, recursive: true);
                Debug.WriteLine($"[SkillUndeploy] '{def.Name}' ← {targetSkillDir}");
            }
            UpdateSkillState(target, def.Id, cli, deployed: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SkillUndeploy] Failed: {ex.Message}");
            throw;
        }
    }

    public bool IsSkillDeployed(ProjectInfo project, string targetSubPath, SkillDefinition def, CliTarget cli)
        => IsSkillDeployed(CreateProjectTarget(project, targetSubPath), def, cli);

    public bool IsSkillDeployed(DeploymentTarget target, SkillDefinition def, CliTarget cli)
    {
        var skillDir = ResolveSkillWriteDir(target, cli, def.Id);
        return Directory.Exists(skillDir) && File.Exists(Path.Combine(skillDir, "SKILL.md"));
    }

    public string ResolveSkillWriteDir(string projectPath, string targetSubPath, CliTarget cli, string skillName)
        => ResolveSkillWriteDir(new DeploymentTarget
        {
            ScopeType = DeploymentScopeType.Project,
            ScopeId = "",
            Project = new ProjectInfo { Path = projectPath },
            TargetSubPath = NormalizeTargetSubPath(targetSubPath)
        }, cli, skillName);

    private string ResolveSkillWriteDir(DeploymentTarget target, CliTarget cli, string skillName)
    {
        string skillsBaseDir;
        if (target.ScopeType == DeploymentScopeType.Global)
        {
            var profile = target.Profile ?? throw new InvalidOperationException("Global profile is required for global scope.");
            var basePath = GetBasePath(profile, cli);
            if (string.IsNullOrWhiteSpace(basePath))
                throw new InvalidOperationException($"Base path for {cli} is not configured.");
            skillsBaseDir = cli == CliTarget.Copilot
                ? Path.Combine(basePath, ".copilot", "skills")
                : Path.Combine(basePath, "skills");
        }
        else
        {
            var projectPath = target.Project?.Path ?? throw new InvalidOperationException("Project is required for project scope.");
            var normalizedSubPath = NormalizeTargetSubPath(target.TargetSubPath);
            var targetDir = string.IsNullOrEmpty(normalizedSubPath)
                ? projectPath
                : Path.Combine(projectPath, normalizedSubPath);

            if (cli == CliTarget.Copilot)
            {
                var copilotDir = Path.Combine(targetDir, ".copilot");
                var resolved = ResolveJunctionTarget(copilotDir);
                skillsBaseDir = Path.Combine(resolved ?? copilotDir, "skills");
            }
            else
            {
                var cliDirName = cli switch
                {
                    CliTarget.Claude => ".claude",
                    CliTarget.Codex => ".codex",
                    CliTarget.Gemini => ".gemini",
                    _ => ".claude"
                };
                var localCliDir = Path.Combine(targetDir, cliDirName);
                var resolved = ResolveJunctionTarget(localCliDir);
                skillsBaseDir = Path.Combine(resolved ?? localCliDir, "skills");
            }
        }

        return Path.Combine(skillsBaseDir, skillName);
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        if (!Directory.Exists(sourceDir)) return;
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        foreach (var subDir in Directory.GetDirectories(sourceDir))
            CopyDirectory(subDir, Path.Combine(targetDir, Path.GetFileName(subDir)));
    }

    // ─── State Sync ───────────────────────────────────────────────────────

    public void SyncDeploymentState(
        List<AgentDefinition> agentDefs,
        List<ContextRuleDefinition> ruleDefs,
        List<ProjectInfo> projects,
        List<SkillDefinition>? skillDefs = null)
    {
        var state = _configService.LoadAgentHubState();
        var profiles = _configService.LoadGlobalAgentHubProfiles();
        bool changed = false;

        var validAgents = new List<AgentDeployment>();
        foreach (var dep in state.AgentDeployments)
        {
            var scopeType = dep.ScopeType;
            var scopeId = ResolveScopeId(scopeType, dep.ScopeId, dep.ProjectName);
            var target = ResolveDeploymentTarget(scopeType, scopeId, dep.TargetSubPath, projects, profiles);
            var agentDef = agentDefs.FirstOrDefault(a => a.Id == dep.AgentId);
            if (target == null || agentDef == null) { changed = true; continue; }

            var validClis = dep.CliTargets
                .Where(cli => IsAgentDeployed(target, agentDef, cli))
                .ToList();

            if (validClis.Count != dep.CliTargets.Count) changed = true;
            if (validClis.Count > 0)
                validAgents.Add(new AgentDeployment
                {
                    ProjectName = dep.ProjectName,
                    TargetSubPath = dep.TargetSubPath,
                    ScopeType = scopeType,
                    ScopeId = scopeId,
                    AgentId = dep.AgentId,
                    CliTargets = validClis,
                    DeployedAt = dep.DeployedAt
                });
            else
                changed = true;
        }

        var validRules = new List<RuleDeployment>();
        foreach (var dep in state.RuleDeployments)
        {
            var scopeType = dep.ScopeType;
            var scopeId = ResolveScopeId(scopeType, dep.ScopeId, dep.ProjectName);
            var target = ResolveDeploymentTarget(scopeType, scopeId, dep.TargetSubPath, projects, profiles);
            var ruleDef = ruleDefs.FirstOrDefault(r => r.Id == dep.RuleId);
            if (target == null || ruleDef == null) { changed = true; continue; }

            var validClis = dep.CliTargets
                .Where(cli => IsRuleDeployed(target, ruleDef, cli))
                .ToList();

            if (validClis.Count != dep.CliTargets.Count) changed = true;
            if (validClis.Count > 0)
                validRules.Add(new RuleDeployment
                {
                    ProjectName = dep.ProjectName,
                    TargetSubPath = dep.TargetSubPath,
                    ScopeType = scopeType,
                    ScopeId = scopeId,
                    RuleId = dep.RuleId,
                    CliTargets = validClis,
                    DeployedAt = dep.DeployedAt
                });
            else
                changed = true;
        }

        var validSkills = new List<SkillDeployment>();
        if (skillDefs != null)
        {
            foreach (var dep in state.SkillDeployments)
            {
                var scopeType = dep.ScopeType;
                var scopeId = ResolveScopeId(scopeType, dep.ScopeId, dep.ProjectName);
                var target = ResolveDeploymentTarget(scopeType, scopeId, dep.TargetSubPath, projects, profiles);
                var skillDef = skillDefs.FirstOrDefault(s => s.Id == dep.SkillId);
                if (target == null || skillDef == null) { changed = true; continue; }

                var validClis = dep.CliTargets
                    .Where(cli => IsSkillDeployed(target, skillDef, cli))
                    .ToList();

                if (validClis.Count != dep.CliTargets.Count) changed = true;
                if (validClis.Count > 0)
                    validSkills.Add(new SkillDeployment
                    {
                        ProjectName = dep.ProjectName,
                        TargetSubPath = dep.TargetSubPath,
                        ScopeType = scopeType,
                        ScopeId = scopeId,
                        SkillId = dep.SkillId,
                        CliTargets = validClis,
                        DeployedAt = dep.DeployedAt
                    });
                else
                    changed = true;
            }
        }
        else
        {
            validSkills.AddRange(state.SkillDeployments);
        }

        if (changed)
        {
            state.AgentDeployments = validAgents;
            state.RuleDeployments = validRules;
            state.SkillDeployments = validSkills;
            _configService.SaveAgentHubState(state);
        }
    }

    // ─── Path Resolution ─────────────────────────────────────────────────

    public string ResolveAgentWriteDir(string projectPath, string targetSubPath, CliTarget cli)
        => ResolveAgentWriteDir(new DeploymentTarget
        {
            ScopeType = DeploymentScopeType.Project,
            ScopeId = "",
            Project = new ProjectInfo { Path = projectPath },
            TargetSubPath = NormalizeTargetSubPath(targetSubPath)
        }, cli);

    public string ResolveAgentWriteDir(DeploymentTarget target, CliTarget cli)
    {
        if (target.ScopeType == DeploymentScopeType.Global)
        {
            var profile = target.Profile ?? throw new InvalidOperationException("Global profile is required for global scope.");
            var basePath = GetBasePath(profile, cli);
            if (string.IsNullOrWhiteSpace(basePath))
                throw new InvalidOperationException($"Base path for {cli} is not configured.");

            return cli == CliTarget.Copilot
                ? Path.Combine(basePath, ".copilot", "agents")
                : Path.Combine(basePath, "agents");
        }

        var projectPath = target.Project?.Path ?? throw new InvalidOperationException("Project is required for project scope.");
        var normalizedSubPath = NormalizeTargetSubPath(target.TargetSubPath);
        var targetDir = string.IsNullOrEmpty(normalizedSubPath)
            ? projectPath
            : Path.Combine(projectPath, normalizedSubPath);

        if (cli == CliTarget.Copilot)
        {
            var copilotDir = Path.Combine(targetDir, ".copilot");
            var resolvedCopilot = ResolveJunctionTarget(copilotDir);
            return Path.Combine(resolvedCopilot ?? copilotDir, "agents");
        }

        var cliDirName = cli switch
        {
            CliTarget.Claude => ".claude",
            CliTarget.Codex => ".codex",
            CliTarget.Gemini => ".gemini",
            _ => ".claude"
        };

        var localCliDir = Path.Combine(targetDir, cliDirName);
        var resolved = ResolveJunctionTarget(localCliDir);
        return Path.Combine(resolved ?? localCliDir, "agents");
    }

    private static string? ResolveContextFilePath(string projectPath, string targetSubPath, CliTarget cli)
    {
        var normalizedSubPath = NormalizeTargetSubPath(targetSubPath);
        var targetDir = string.IsNullOrEmpty(normalizedSubPath)
            ? projectPath
            : Path.Combine(projectPath, normalizedSubPath);

        if (cli == CliTarget.Copilot)
        {
            var githubDir = Path.Combine(targetDir, ".github");
            var resolved = ResolveJunctionTarget(githubDir);
            return Path.Combine(resolved ?? githubDir, "copilot-instructions.md");
        }

        return cli switch
        {
            CliTarget.Claude => Path.Combine(targetDir, "CLAUDE.md"),
            CliTarget.Codex => Path.Combine(targetDir, "AGENTS.md"),
            CliTarget.Gemini => Path.Combine(targetDir, "GEMINI.md"),
            _ => null
        };
    }

    private static List<string> ResolveRuleContextFilePaths(DeploymentTarget target, CliTarget cli)
    {
        var result = new List<string>();
        if (target.ScopeType == DeploymentScopeType.Global)
        {
            var profile = target.Profile;
            if (profile == null)
                return result;

            var globalPath = GetRuleFilePath(profile, cli);
            if (!string.IsNullOrWhiteSpace(globalPath))
                result.Add(globalPath);
            return result;
        }

        var projectPath = target.Project?.Path;
        if (string.IsNullOrWhiteSpace(projectPath))
            return result;

        var targetSubPath = NormalizeTargetSubPath(target.TargetSubPath);
        var primary = ResolveContextFilePath(projectPath, targetSubPath, cli);
        if (!string.IsNullOrWhiteSpace(primary))
            result.Add(primary);

        // Shared mirror: when target is project root, apply same rule file to shared side.
        if (string.IsNullOrEmpty(targetSubPath))
        {
            var sharedDir = Path.Combine(projectPath, "shared");
            if (Directory.Exists(sharedDir))
            {
                var sharedPath = ResolveContextFilePath(projectPath, "shared", cli);
                if (!string.IsNullOrWhiteSpace(sharedPath) &&
                    !result.Contains(sharedPath, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(sharedPath);
                }
            }
        }

        return result;
    }

    private static string? ResolveJunctionTarget(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            if ((attr & FileAttributes.ReparsePoint) != 0)
            {
                var target = Directory.ResolveLinkTarget(path, returnFinalTarget: false);
                if (target != null)
                    return Path.GetFullPath(target.FullName);
            }
        }
        catch { }
        return null;
    }

    private static string NormalizeTargetSubPath(string? targetSubPath)
    {
        if (string.IsNullOrWhiteSpace(targetSubPath))
            return "";
        var normalized = targetSubPath.Replace('/', '\\').Trim();
        while (normalized.StartsWith('\\'))
            normalized = normalized[1..];
        return normalized.TrimEnd('\\');
    }

    private static string NormalizeAgentContent(AgentDefinition def, string content, CliTarget cli)
    {
        return cli == CliTarget.Codex
            ? NormalizeAgentContentForCodex(def, content)
            : NormalizeAgentContentForMarkdownFrontmatter(def, content, cli);
    }

    private static string NormalizeAgentContentForMarkdownFrontmatter(AgentDefinition def, string content, CliTarget cli)
    {
        var trimmed = content.TrimStart();
        var requiredName = ToKebabCase(string.IsNullOrWhiteSpace(def.Id) ? def.Name : def.Id);
        var requiredDescription = string.IsNullOrWhiteSpace(def.Description)
            ? "Curia managed sub-agent."
            : def.Description.Trim();
        var extraFrontmatter = NormalizeFrontmatterLines(GetFrontmatterByCli(def, cli));

        if (trimmed.StartsWith("---\n", StringComparison.Ordinal) || trimmed.StartsWith("---\r\n", StringComparison.Ordinal))
        {
            return EnsureRequiredFrontmatter(trimmed, requiredName, requiredDescription, extraFrontmatter);
        }

        var yamlName = EscapeYamlScalar(requiredName);
        var yamlDescription = EscapeYamlScalar(requiredDescription);
        var extraBlock = string.IsNullOrWhiteSpace(extraFrontmatter) ? "" : $"\n{extraFrontmatter}";

        return
            $"---\nname: \"{yamlName}\"\ndescription: \"{yamlDescription}\"{extraBlock}\n---\n\n{content.TrimStart()}";
    }

    private static string NormalizeAgentContentForCodex(AgentDefinition def, string content)
    {
        var name = ToKebabCase(string.IsNullOrWhiteSpace(def.Id) ? def.Name : def.Id);
        var description = string.IsNullOrWhiteSpace(def.Description)
            ? "Curia managed sub-agent."
            : def.Description.Trim();
        var extraLines = NormalizeTomlLines(GetFrontmatterByCli(def, CliTarget.Codex));

        var lines = new List<string>
        {
            $"name = \"{EscapeTomlString(name)}\"",
            $"description = \"{EscapeTomlString(description)}\""
        };

        foreach (var line in extraLines)
        {
            var key = ExtractTomlKey(line);
            if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "description", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "developer_instructions", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            lines.Add(line);
        }

        var body = StripYamlFrontmatter(content);
        lines.Add($"developer_instructions = \"\"\"\n{body.Trim()}\n\"\"\"");
        return string.Join("\n", lines) + "\n";
    }

    private static string StripYamlFrontmatter(string content)
    {
        var trimmed = content.TrimStart();
        var match = Regex.Match(trimmed, @"\A---\r?\n[\s\S]*?\r?\n---\r?\n?", RegexOptions.CultureInvariant);
        return match.Success ? trimmed[match.Length..] : content;
    }

    private static string EscapeYamlScalar(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string EscapeTomlString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string EnsureRequiredFrontmatter(
        string contentWithFrontmatter,
        string requiredName,
        string requiredDescription,
        string extraFrontmatter)
    {
        var newline = contentWithFrontmatter.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var match = Regex.Match(
            contentWithFrontmatter,
            @"\A---\r?\n(?<fm>[\s\S]*?)\r?\n---\r?\n?",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            return contentWithFrontmatter;

        var frontmatter = match.Groups["fm"].Value;
        var body = contentWithFrontmatter[match.Length..];

        var hasName = Regex.IsMatch(frontmatter, @"(?im)^\s*name\s*:");
        var hasDescription = Regex.IsMatch(frontmatter, @"(?im)^\s*description\s*:");
        if (!hasName)
            frontmatter += $"{newline}name: \"{EscapeYamlScalar(requiredName)}\"";
        if (!hasDescription)
            frontmatter += $"{newline}description: \"{EscapeYamlScalar(requiredDescription)}\"";
        if (!string.IsNullOrWhiteSpace(extraFrontmatter))
        {
            foreach (var line in extraFrontmatter.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;
                var keyMatch = Regex.Match(trimmed, @"^(?<k>[A-Za-z][A-Za-z0-9_-]*)\s*:");
                if (keyMatch.Success)
                {
                    var key = keyMatch.Groups["k"].Value;
                    var exists = Regex.IsMatch(frontmatter, $@"(?im)^\s*{Regex.Escape(key)}\s*:");
                    if (exists)
                        continue;
                }
                frontmatter += $"{newline}{trimmed}";
            }
        }

        return $"---{newline}{frontmatter.TrimEnd()}{newline}---{newline}{newline}{body.TrimStart()}";
    }

    private static string NormalizeFrontmatterLines(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var lines = raw
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l) && l != "---")
            .ToList();

        return string.Join("\n", lines);
    }

    private static string GetAgentFileBaseName(AgentDefinition def)
    {
        var raw = string.IsNullOrWhiteSpace(def.Id) ? def.Name : def.Id;
        var kebab = ToKebabCase(raw);
        return string.IsNullOrWhiteSpace(kebab) ? "agent" : kebab;
    }

    private static string ToKebabCase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "agent";
        var kebab = Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-");
        kebab = Regex.Replace(kebab, "-{2,}", "-").Trim('-');
        return string.IsNullOrWhiteSpace(kebab) ? "agent" : kebab;
    }

    private static string ResolveAgentFilePath(string writeDir, AgentDefinition def, CliTarget cli)
    {
        var ext = cli == CliTarget.Codex ? ".toml" : ".md";
        return Path.Combine(writeDir, GetAgentFileBaseName(def) + ext);
    }

    private static List<string> NormalizeTomlLines(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return
        [
            .. raw.Replace("\r\n", "\n")
                  .Split('\n')
                  .Select(l => l.Trim())
                  .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#'))
        ];
    }

    private static string ExtractTomlKey(string line)
    {
        var idx = line.IndexOf('=');
        if (idx <= 0) return "";
        return line[..idx].Trim();
    }

    private static string GetFrontmatterByCli(AgentDefinition def, CliTarget cli)
    {
        var value = cli switch
        {
            CliTarget.Claude => def.FrontmatterClaude,
            CliTarget.Codex => def.FrontmatterCodex,
            CliTarget.Copilot => def.FrontmatterCopilot,
            CliTarget.Gemini => def.FrontmatterGemini,
            _ => ""
        };

        // Backward compatibility with existing single-field config.
        if (string.IsNullOrWhiteSpace(value))
            value = def.FrontmatterYaml;

        return value ?? "";
    }

    // ─── State Management ─────────────────────────────────────────────────

    private void UpdateAgentState(DeploymentTarget target, string agentId, CliTarget cli, bool deployed)
    {
        var state = _configService.LoadAgentHubState();
        var targetSubPath = NormalizeTargetSubPath(target.TargetSubPath);
        var scopeType = target.ScopeType;
        var scopeId = ResolveScopeId(scopeType, target.ScopeId, target.Project?.Name ?? target.Profile?.Id ?? "");
        var projectName = scopeType == DeploymentScopeType.Project ? (target.Project?.Name ?? scopeId) : "";
        var existing = state.AgentDeployments
            .FirstOrDefault(d => d.ScopeType == scopeType
                              && ResolveScopeId(d.ScopeType, d.ScopeId, d.ProjectName) == scopeId
                              && d.TargetSubPath == targetSubPath
                              && d.AgentId == agentId);

        if (deployed)
        {
            if (existing == null)
                state.AgentDeployments.Add(new AgentDeployment
                {
                    ProjectName = projectName,
                    TargetSubPath = targetSubPath,
                    ScopeType = scopeType,
                    ScopeId = scopeId,
                    AgentId = agentId,
                    CliTargets = [cli],
                    DeployedAt = DateTimeOffset.Now
                });
            else if (!existing.CliTargets.Contains(cli))
                existing.CliTargets.Add(cli);
        }
        else
        {
            if (existing != null)
            {
                existing.CliTargets.Remove(cli);
                if (existing.CliTargets.Count == 0)
                    state.AgentDeployments.Remove(existing);
            }
        }
        _configService.SaveAgentHubState(state);
    }

    private void UpdateRuleState(DeploymentTarget target, string ruleId, CliTarget cli, bool deployed)
    {
        var state = _configService.LoadAgentHubState();
        var targetSubPath = NormalizeTargetSubPath(target.TargetSubPath);
        var scopeType = target.ScopeType;
        var scopeId = ResolveScopeId(scopeType, target.ScopeId, target.Project?.Name ?? target.Profile?.Id ?? "");
        var projectName = scopeType == DeploymentScopeType.Project ? (target.Project?.Name ?? scopeId) : "";
        var existing = state.RuleDeployments
            .FirstOrDefault(d => d.ScopeType == scopeType
                              && ResolveScopeId(d.ScopeType, d.ScopeId, d.ProjectName) == scopeId
                              && d.TargetSubPath == targetSubPath
                              && d.RuleId == ruleId);

        if (deployed)
        {
            if (existing == null)
                state.RuleDeployments.Add(new RuleDeployment
                {
                    ProjectName = projectName,
                    TargetSubPath = targetSubPath,
                    ScopeType = scopeType,
                    ScopeId = scopeId,
                    RuleId = ruleId,
                    CliTargets = [cli],
                    DeployedAt = DateTimeOffset.Now
                });
            else if (!existing.CliTargets.Contains(cli))
                existing.CliTargets.Add(cli);
        }
        else
        {
            if (existing != null)
            {
                existing.CliTargets.Remove(cli);
                if (existing.CliTargets.Count == 0)
                    state.RuleDeployments.Remove(existing);
            }
        }
        _configService.SaveAgentHubState(state);
    }

    private void UpdateSkillState(DeploymentTarget target, string skillId, CliTarget cli, bool deployed)
    {
        var state = _configService.LoadAgentHubState();
        var targetSubPath = NormalizeTargetSubPath(target.TargetSubPath);
        var scopeType = target.ScopeType;
        var scopeId = ResolveScopeId(scopeType, target.ScopeId, target.Project?.Name ?? target.Profile?.Id ?? "");
        var projectName = scopeType == DeploymentScopeType.Project ? (target.Project?.Name ?? scopeId) : "";
        var existing = state.SkillDeployments
            .FirstOrDefault(d => d.ScopeType == scopeType
                              && ResolveScopeId(d.ScopeType, d.ScopeId, d.ProjectName) == scopeId
                              && d.TargetSubPath == targetSubPath
                              && d.SkillId == skillId);

        if (deployed)
        {
            if (existing == null)
                state.SkillDeployments.Add(new SkillDeployment
                {
                    ProjectName = projectName,
                    TargetSubPath = targetSubPath,
                    ScopeType = scopeType,
                    ScopeId = scopeId,
                    SkillId = skillId,
                    CliTargets = [cli],
                    DeployedAt = DateTimeOffset.Now
                });
            else if (!existing.CliTargets.Contains(cli))
                existing.CliTargets.Add(cli);
        }
        else
        {
            if (existing != null)
            {
                existing.CliTargets.Remove(cli);
                if (existing.CliTargets.Count == 0)
                    state.SkillDeployments.Remove(existing);
            }
        }
        _configService.SaveAgentHubState(state);
    }

    private DeploymentTarget? ResolveDeploymentTarget(
        DeploymentScopeType scopeType,
        string scopeId,
        string targetSubPath,
        List<ProjectInfo> projects,
        List<GlobalDeploymentProfile> profiles)
    {
        if (scopeType == DeploymentScopeType.Global)
        {
            var profile = profiles.FirstOrDefault(p => string.Equals(p.Id, scopeId, StringComparison.OrdinalIgnoreCase));
            if (profile == null)
                return null;
            return CreateGlobalTarget(profile);
        }

        var project = projects.FirstOrDefault(p => string.Equals(p.Name, scopeId, StringComparison.OrdinalIgnoreCase));
        if (project == null)
            return null;
        return CreateProjectTarget(project, targetSubPath);
    }

    private static string ResolveScopeId(DeploymentScopeType scopeType, string? scopeId, string? fallbackProjectName)
    {
        if (!string.IsNullOrWhiteSpace(scopeId))
            return scopeId;

        return scopeType == DeploymentScopeType.Project
            ? fallbackProjectName ?? ""
            : "";
    }

    private static string GetBasePath(GlobalDeploymentProfile profile, CliTarget cli)
    {
        return cli switch
        {
            CliTarget.Claude => profile.ClaudeBasePath,
            CliTarget.Codex => profile.CodexBasePath,
            CliTarget.Copilot => profile.CopilotBasePath,
            CliTarget.Gemini => profile.GeminiBasePath,
            _ => ""
        };
    }

    private static string GetRuleFilePath(GlobalDeploymentProfile profile, CliTarget cli)
    {
        return cli switch
        {
            CliTarget.Claude => profile.ClaudeRuleFilePath,
            CliTarget.Codex => profile.CodexRuleFilePath,
            CliTarget.Copilot => profile.CopilotRuleFilePath,
            CliTarget.Gemini => profile.GeminiRuleFilePath,
            _ => ""
        };
    }

    // ─── Marker Helpers ───────────────────────────────────────────────────

    private static string ReplaceMarkedSection(string content, string id, string newSection)
    {
        var openMarker = Regex.Escape($"<!-- [AgentHub:{id}] -->");
        var closeMarker = Regex.Escape($"<!-- [/AgentHub:{id}] -->");
        // Use (\r?\n)+ to handle CRLF and any number of preceding newlines (not just \n\n)
        var pattern = $@"(\r?\n)+[ \t]*{openMarker}[\s\S]*?{closeMarker}[ \t]*";
        var result = Regex.Replace(content, pattern, "\n\n" + newSection);
        return result.TrimEnd() + "\n";
    }

    private static string RemoveMarkedSection(string content, string id)
    {
        var openMarker = Regex.Escape($"<!-- [AgentHub:{id}] -->");
        var closeMarker = Regex.Escape($"<!-- [/AgentHub:{id}] -->");
        // Use (\r?\n)+ to handle CRLF and any number of preceding newlines (not just \n\n).
        // Replace the matched block (including its preceding newlines) with a single \n
        // so that text before and after the section remains separated by a line break.
        var pattern = $@"(\r?\n)+[ \t]*{openMarker}[\s\S]*?{closeMarker}[ \t]*(\r?\n)?";
        var result = Regex.Replace(content, pattern, "\n");
        return result.TrimEnd() + "\n";
    }
}
