using System.IO;
using System.Text.Json;
using Curia.Models;

namespace Curia.Services.Agent;

public class AgentChatHistoryService
{
    private const int MaxSessions = 30;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private string? _currentSessionPath;
    private readonly ConfigService _configService;

    public AgentChatHistoryService(ConfigService configService) => _configService = configService;

    public string HistoryDirectory => Path.Combine(_configService.ConfigDir, "agent_chat_history");

    public async Task<List<AgentChatMessage>> LoadLatestSessionAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(HistoryDirectory)) return [];
        var latest = new DirectoryInfo(HistoryDirectory)
            .GetFiles("*.json")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();
        if (latest == null) return [];

        return await LoadSessionAsync(latest.FullName, ct);
    }

    public async Task<List<AgentChatSessionSummary>> ListSessionsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(HistoryDirectory)) return [];

        var summaries = new List<AgentChatSessionSummary>();
        foreach (var file in new DirectoryInfo(HistoryDirectory).GetFiles("*.json")
                     .OrderByDescending(item => item.LastWriteTimeUtc))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(file.FullName);
                var session = await JsonSerializer.DeserializeAsync<AgentChatHistorySession>(stream, cancellationToken: ct);
                if (session == null) continue;
                var firstUserMessage = session.Messages.FirstOrDefault(message => message.Kind == AgentMessageKind.User)?.Text ?? "New chat";
                summaries.Add(new AgentChatSessionSummary
                {
                    Path = file.FullName,
                    CreatedAt = session.CreatedAt,
                    UpdatedAt = session.UpdatedAt,
                    MessageCount = session.Messages.Count,
                    Title = firstUserMessage.Length <= 72 ? firstUserMessage : firstUserMessage[..72] + "..."
                });
            }
            catch (JsonException) { }
            catch (IOException) { }
        }
        return summaries;
    }

    public async Task<List<AgentChatMessage>> LoadSessionAsync(string sessionPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionPath) || !File.Exists(sessionPath)) return [];

        try
        {
            await using var stream = File.OpenRead(sessionPath);
            var session = await JsonSerializer.DeserializeAsync<AgentChatHistorySession>(stream, cancellationToken: ct);
            if (session == null) return [];
            _currentSessionPath = sessionPath;
            return session.Messages.Select(entry => new AgentChatMessage
            {
                Kind = entry.Kind,
                Text = entry.Text,
                ToolCall = entry.ToolCall,
                Timestamp = entry.Timestamp
            }).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    public Task DeleteSessionAsync(string sessionPath)
    {
        if (string.IsNullOrWhiteSpace(sessionPath) || !File.Exists(sessionPath)) return Task.CompletedTask;
        File.Delete(sessionPath);
        if (string.Equals(_currentSessionPath, sessionPath, StringComparison.OrdinalIgnoreCase))
            _currentSessionPath = null;
        return Task.CompletedTask;
    }

    public void StartNewSession() => _currentSessionPath = null;

    public async Task SaveAsync(IEnumerable<AgentChatMessage> messages, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(HistoryDirectory);
            _currentSessionPath ??= Path.Combine(HistoryDirectory, $"{DateTime.Now:yyyy-MM-dd_HHmmss}.json");
            var persisted = messages
                .Where(message => message.Kind is AgentMessageKind.User or AgentMessageKind.Assistant or AgentMessageKind.ToolCall)
                .Select(message => new AgentChatHistoryEntry
                {
                    Kind = message.Kind,
                    Text = message.Text,
                    ToolCall = message.ToolCall,
                    Timestamp = message.Timestamp
                }).ToList();
            var session = new AgentChatHistorySession
            {
                CreatedAt = File.Exists(_currentSessionPath) ? File.GetCreationTime(_currentSessionPath) : DateTime.Now,
                UpdatedAt = DateTime.Now,
                Messages = persisted
            };
            var temporaryPath = _currentSessionPath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(session, _jsonOptions), ct);
            File.Move(temporaryPath, _currentSessionPath, overwrite: true);
            RemoveExpiredSessions();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void RemoveExpiredSessions()
    {
        var expired = new DirectoryInfo(HistoryDirectory).GetFiles("*.json")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(MaxSessions);
        foreach (var file in expired)
        {
            try { file.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}