using System.Text.Json;
using System.IO;

namespace Curia.Services.Agent;

/// <summary>Stores aggregate tool telemetry only. Prompts, arguments, and result content are deliberately excluded.</summary>
public class AgentToolUsageLogService
{
    private readonly ConfigService _config;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public AgentToolUsageLogService(ConfigService config) => _config = config;

    public async Task RecordAsync(string tool, bool success, string code, long durationMs, int resultChars,
        bool approvalRequested, bool approved, string provider, CancellationToken ct)
    {
        var directory = Path.Combine(_config.ConfigDir, "agent_tool_usage");
        Directory.CreateDirectory(directory);
        var record = new { timestamp = DateTimeOffset.UtcNow, tool, success, code, durationMs, resultChars, approvalRequested, approved, provider };
        await _writeLock.WaitAsync(ct);
        try
        {
            var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyy-MM}.jsonl");
            await File.AppendAllTextAsync(path, JsonSerializer.Serialize(record) + Environment.NewLine, ct);
        }
        finally { _writeLock.Release(); }
    }
}