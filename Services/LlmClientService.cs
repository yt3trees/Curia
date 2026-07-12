using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using Curia.Models;

namespace Curia.Services;

public class LlmClientService
{
    private readonly ConfigService _configService;
    // 大きな入力 (PDF 取り込み等) でも応答待ちで打ち切られにくいよう余裕を持たせる。
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public string LastSystemPrompt { get; private set; } = "";
    public string LastUserPrompt   { get; private set; } = "";
    public string LastResponse     { get; private set; } = "";

    public LlmClientService(ConfigService configService)
    {
        _configService = configService;
    }

    /// <summary>シングルターン (system + user 1往復)</summary>
    public async Task<string> ChatCompletionAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken ct = default)
    {
        var messages = new List<(string role, string content)>
        {
            ("user", userPrompt)
        };
        return await ChatWithHistoryAsync(systemPrompt, messages, ct);
    }

    public static bool IsCliProvider(string provider) =>
        provider.Equals("claude_code",    StringComparison.OrdinalIgnoreCase) ||
        provider.Equals("gemini_cli",     StringComparison.OrdinalIgnoreCase) ||
        provider.Equals("codex_cli",      StringComparison.OrdinalIgnoreCase) ||
        provider.Equals("github_copilot", StringComparison.OrdinalIgnoreCase);

    public static bool SupportsNativeToolCalling(string provider) =>
        provider.Equals("openai", StringComparison.OrdinalIgnoreCase) ||
        provider.Equals("azure_openai", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// OpenAI compatible providers に native function calling を依頼する。
    /// CLI providers continue to use the prompt JSON protocol.
    /// </summary>
    public async Task<NativeAgentCompletion> ChatWithToolsAsync(
        string systemPrompt,
        IReadOnlyList<NativeAgentMessage> messages,
        IReadOnlyList<AgentToolDescriptor> tools,
        CancellationToken ct = default,
        bool allowTools = true)
    {
        var settings = _configService.LoadSettings();
        if (!SupportsNativeToolCalling(settings.LlmProvider))
            throw new InvalidOperationException("Native function calling is only available for OpenAI and Azure OpenAI providers.");
        if (string.IsNullOrWhiteSpace(settings.LlmApiKey))
            throw new InvalidOperationException("LLM API key is not configured. Please set it in Settings > LLM API.");

        var effectiveSystemPrompt = systemPrompt;
        if (!string.IsNullOrWhiteSpace(settings.LlmUserProfile))
            effectiveSystemPrompt = $"## User Profile\n{settings.LlmUserProfile.Trim()}\n\n{systemPrompt}";

        var result = await SendWithToolsAsync(
            settings,
            effectiveSystemPrompt,
            messages,
            tools,
            settings.LlmProvider.Equals("azure_openai", StringComparison.OrdinalIgnoreCase),
            allowTools,
            ct);
        LastSystemPrompt = effectiveSystemPrompt;
        LastUserPrompt = messages.LastOrDefault(message => message.Role == "user")?.Content ?? "";
        LastResponse = result.Content ?? JsonSerializer.Serialize(result.ToolCalls);
        return result;
    }

    /// <summary>マルチターン (会話履歴付き)</summary>
    public async Task<string> ChatWithHistoryAsync(
        string systemPrompt,
        IReadOnlyList<(string role, string content)> messages,
        CancellationToken ct = default,
        bool injectProfile = true)
    {
        var settings = _configService.LoadSettings();

        // CLI プロバイダは API キー不要 (CLI 自体が認証を管理)
        if (!IsCliProvider(settings.LlmProvider) && string.IsNullOrWhiteSpace(settings.LlmApiKey))
            throw new InvalidOperationException(
                "LLM API key is not configured. Please set it in Settings > LLM API.");

        var effectiveSystemPrompt = systemPrompt;
        if (injectProfile && !string.IsNullOrWhiteSpace(settings.LlmUserProfile))
            effectiveSystemPrompt = $"## User Profile\n{settings.LlmUserProfile.Trim()}\n\n{systemPrompt}";

        string response;
        if (IsCliProvider(settings.LlmProvider))
            response = await SendViaCliAsync(settings, effectiveSystemPrompt, messages, ct);
        else if (settings.LlmProvider.Equals("azure_openai", StringComparison.OrdinalIgnoreCase))
            response = await SendAsync(settings, effectiveSystemPrompt, messages, isAzure: true,  ct);
        else
            response = await SendAsync(settings, effectiveSystemPrompt, messages, isAzure: false, ct);

        // デバッグログ: 最後のユーザーメッセージを記録
        LastSystemPrompt = effectiveSystemPrompt;
        LastUserPrompt   = messages.LastOrDefault(m => m.role == "user").content ?? "";
        LastResponse     = response;
        return response;
    }

    public async Task<string> TestConnectionAsync(CancellationToken ct = default)
    {
        var messages = new List<(string role, string content)> { ("user", "Reply with exactly 'OK'.") };
        return await ChatWithHistoryAsync("You are a test assistant.", messages, ct, injectProfile: false);
    }

    // -----------------------------------------------------------------------
    // CLI プロバイダ (claude_code / gemini_cli / codex_cli)
    // -----------------------------------------------------------------------
    private static async Task<string> SendViaCliAsync(
        AppSettings settings,
        string systemPrompt,
        IReadOnlyList<(string role, string content)> messages,
        CancellationToken ct)
    {
        // 会話全体を stdin に渡すテキストとして構築
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            sb.AppendLine($"[System]\n{systemPrompt}\n");

        foreach (var (role, content) in messages)
        {
            var label = role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
                ? "Assistant" : "User";
            sb.AppendLine($"[{label}]\n{content}\n");
        }
        var inputText = sb.ToString().TrimEnd();

        // (exe, args-for-ArgumentList, useStdin)
        var (exe, argList, useStdin) = settings.LlmProvider.ToLowerInvariant() switch
        {
            "claude_code"    => BuildClaudeCodeArgs(settings),
            "gemini_cli"     => BuildGeminiArgs(settings, inputText),
            "codex_cli"      => BuildCodexArgs(settings),
            "github_copilot" => BuildCopilotArgs(inputText),
            _ => throw new InvalidOperationException($"Unknown CLI provider: {settings.LlmProvider}")
        };

        // Windows では .cmd スクリプトを直接起動できないため cmd.exe 経由で呼び出す
        // WorkingDirectory をホームディレクトリに設定 (git repo 要件のある CLI への対応)
        var psi = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardInput  = useStdin,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding  = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        if (useStdin)
            psi.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(exe);
        foreach (var arg in argList)
            psi.ArgumentList.Add(arg);

        // Codex CLI: settings に API キーがあれば環境変数として渡す
        if (settings.LlmProvider.Equals("codex_cli", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(settings.LlmApiKey))
            psi.Environment["OPENAI_API_KEY"] = settings.LlmApiKey;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{exe}'.");

        try
        {
            if (useStdin)
            {
                await process.StandardInput.WriteAsync(inputText.AsMemory(), ct);
                process.StandardInput.Close();
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask  = process.StandardError.ReadToEndAsync(ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(300));
            await process.WaitForExitAsync(timeoutCts.Token);
            var output = StripAnsi((await outputTask).Trim());
            var error  = SanitizeCliText((await errorTask).Trim());

            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
                throw new InvalidOperationException($"'{exe}' exited with code {process.ExitCode}: {error}");

            if (settings.LlmProvider.Equals("claude_code", StringComparison.OrdinalIgnoreCase))
                output = ExtractClaudeResponse(output);
            else if (settings.LlmProvider.Equals("codex_cli", StringComparison.OrdinalIgnoreCase))
                output = ExtractCodexResponse(output);
            return output;
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"'{exe}' did not respond within 5 minutes.");
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    // claude --print --output-format json  stdin にプロンプトを流す
    private static (string exe, List<string> args, bool useStdin) BuildClaudeCodeArgs(AppSettings s)
    {
        var args = new List<string> { "--print", "--output-format", "json" };
        return ("claude", args, true);
    }

    // claude JSON 出力から result フィールドを抽出する
    // 出力形式: { "result": "...", "cost_usd": ..., ... }
    private static string ExtractClaudeResponse(string raw)
    {
        try
        {
            var node = JsonNode.Parse(raw);
            var result = node?["result"]?.GetValue<string>();
            return result ?? raw.Trim();
        }
        catch { return raw.Trim(); }
    }

    // gemini --prompt TEXT --yolo --output-format text  プロンプトを引数として渡す
    private static (string exe, List<string> args, bool useStdin) BuildGeminiArgs(AppSettings s, string inputText)
    {
        var args = new List<string> { "--prompt", inputText };
        // --yolo: ツール使用の承認待ちをスキップ (非対話モードに必要)
        args.Add("--yolo");
        args.Add("--output-format"); args.Add("text");
        return ("gemini", args, false);
    }

    // copilot -p PROMPT
    private static (string exe, List<string> args, bool useStdin) BuildCopilotArgs(string inputText)
    {
        var args = new List<string> { "-p", inputText };
        return ("copilot", args, false);
    }

    // codex exec --skip-git-repo-check --json  stdin にプロンプトを流す
    private static (string exe, List<string> args, bool useStdin) BuildCodexArgs(AppSettings s)
    {
        var args = new List<string> { "exec", "--skip-git-repo-check", "--json" };
        return ("codex", args, true);
    }

    // ANSI エスケープコードを除去する (CLIの色付き出力対策)
    // CSI シーケンス: ESC[ + パラメータ + 終端文字 (@-~)
    // 2文字シーケンス: ESC + (@-Z, \-_)
    // OSC シーケンス: ESC] ... BEL
    private static readonly Regex AnsiEscapeRegex = new(
        @"\x1B(?:[@-Z\\-_]|\[[0-9;?<=>!]*[@-~]|\][^\x07]*\x07)",
        RegexOptions.Compiled);

    private static string StripAnsi(string text) =>
        string.IsNullOrEmpty(text) ? text : AnsiEscapeRegex.Replace(text, "");

    // CLIエラー出力を人間が読める形に整形する
    // ANSI除去 → 制御文字除去 → 英数字/CJK/記号を含む最初の行を抽出
    private static string SanitizeCliText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = StripAnsi(text);

        // 制御文字 (ESC含む) を除去
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c >= 0x20 || c == '\n' || c == '\r') sb.Append(c);
        }
        text = sb.ToString();

        // 英数字または CJK を含む行だけを候補にする
        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Any(c => char.IsLetterOrDigit(c)))
            .ToList();

        if (lines.Count == 0) return text.Trim();

        // 先頭の意味ある行を最大200文字で返す
        var first = string.Join(" / ", lines.Take(3));
        return first.Length > 200 ? first[..200] + "…" : first;
    }

    // codex --json 出力 (NDJSON) からアシスタント本文を抽出する
    // 各行が JSON。type="item.completed" かつ item.type="agent_message" の text を収集する
    private static string ExtractCodexResponse(string raw)
    {
        var texts = new List<string>();
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var node = JsonNode.Parse(line.Trim());
                if (node?["type"]?.GetValue<string>() != "item.completed") continue;
                var item = node["item"];
                if (item?["type"]?.GetValue<string>() != "agent_message") continue;
                var text = item["text"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(text)) texts.Add(text);
            }
            catch { /* JSON パース失敗行はスキップ */ }
        }
        return texts.Count > 0 ? string.Join("\n", texts) : raw.Trim();
    }

    // -----------------------------------------------------------------------
    private async Task<string> SendAsync(
        AppSettings settings,
        string systemPrompt,
        IReadOnlyList<(string role, string content)> messages,
        bool isAzure,
        CancellationToken ct)
    {
        string url;
        if (isAzure)
        {
            if (string.IsNullOrWhiteSpace(settings.LlmEndpoint))
                throw new InvalidOperationException(
                    "Azure OpenAI endpoint is not configured. Please set LlmEndpoint in Settings.");
            var endpoint   = settings.LlmEndpoint.TrimEnd('/');
            var model      = string.IsNullOrWhiteSpace(settings.LlmModel)      ? "gpt-4o"             : settings.LlmModel;
            var apiVersion = string.IsNullOrWhiteSpace(settings.LlmApiVersion) ? "2024-12-01-preview" : settings.LlmApiVersion;
            url = $"{endpoint}/openai/deployments/{model}/chat/completions?api-version={apiVersion}";
        }
        else
        {
            url = "https://api.openai.com/v1/chat/completions";
        }

        var model2 = string.IsNullOrWhiteSpace(settings.LlmModel) ? "gpt-4o" : settings.LlmModel;

        // messages 配列を構築 (system → 履歴)
        var allMessages = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };
        foreach (var (role, content) in messages)
            allMessages.Add(new { role, content });

        // ペイロードを動的に構築
        var payload = new Dictionary<string, object>
        {
            ["messages"] = allMessages
        };
        if (!isAzure)
            payload["model"] = model2;

        // ユーザー設定パラメータをマージ
        foreach (var (key, rawValue) in settings.LlmParameters)
            payload[key] = ParseParamValue(rawValue);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (isAzure)
            request.Headers.Add("api-key", settings.LlmApiKey);
        else
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.LlmApiKey);

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var provider = isAzure ? "Azure OpenAI" : "OpenAI";
            throw new HttpRequestException($"{provider} API error {(int)response.StatusCode}: {json}");
        }

        return ExtractContent(json);
    }

    private async Task<NativeAgentCompletion> SendWithToolsAsync(
        AppSettings settings,
        string systemPrompt,
        IReadOnlyList<NativeAgentMessage> messages,
        IReadOnlyList<AgentToolDescriptor> tools,
        bool isAzure,
        bool allowTools,
        CancellationToken ct)
    {
        var url = GetChatCompletionsUrl(settings, isAzure);
        var allMessages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPrompt }
        };
        foreach (var message in messages)
            allMessages.Add(BuildNativeMessage(message));

        var nativeTools = new JsonArray();
        foreach (var descriptor in tools)
            nativeTools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = descriptor.Name,
                    ["description"] = descriptor.Description,
                    ["parameters"] = ParseToolParameters(descriptor.ParametersSchema)
                }
            });

        var payload = new JsonObject
        {
            ["messages"] = allMessages,
            ["tools"] = nativeTools,
            ["tool_choice"] = allowTools ? "auto" : "none"
        };
        if (!isAzure)
            payload["model"] = string.IsNullOrWhiteSpace(settings.LlmModel) ? "gpt-4o" : settings.LlmModel;
        foreach (var (key, rawValue) in settings.LlmParameters)
            payload[key] = JsonSerializer.SerializeToNode(ParseParamValue(rawValue));

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (isAzure) request.Headers.Add("api-key", settings.LlmApiKey);
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.LlmApiKey);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var provider = isAzure ? "Azure OpenAI" : "OpenAI";
            throw new HttpRequestException($"{provider} API error {(int)response.StatusCode}: {json}");
        }
        return ExtractNativeCompletion(json);
    }

    private static string GetChatCompletionsUrl(AppSettings settings, bool isAzure)
    {
        if (!isAzure) return "https://api.openai.com/v1/chat/completions";
        if (string.IsNullOrWhiteSpace(settings.LlmEndpoint))
            throw new InvalidOperationException("Azure OpenAI endpoint is not configured. Please set LlmEndpoint in Settings.");
        var endpoint = settings.LlmEndpoint.TrimEnd('/');
        var model = string.IsNullOrWhiteSpace(settings.LlmModel) ? "gpt-4o" : settings.LlmModel;
        var apiVersion = string.IsNullOrWhiteSpace(settings.LlmApiVersion) ? "2024-12-01-preview" : settings.LlmApiVersion;
        return $"{endpoint}/openai/deployments/{model}/chat/completions?api-version={apiVersion}";
    }

    private static JsonObject BuildNativeMessage(NativeAgentMessage message)
    {
        var node = new JsonObject { ["role"] = message.Role };
        if (message.Content != null) node["content"] = message.Content;
        if (!string.IsNullOrWhiteSpace(message.ToolCallId)) node["tool_call_id"] = message.ToolCallId;
        if (message.ToolCalls.Count > 0)
        {
            var calls = new JsonArray();
            foreach (var call in message.ToolCalls)
                calls.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments.ToJsonString() }
                });
            node["tool_calls"] = calls;
        }
        return node;
    }

    private static JsonObject ParseToolParameters(string schema)
    {
        // Legacy compact descriptors are normalized once into a strict JSON Schema.
        // Do not silently publish an unconstrained schema: that makes provider-side
        // function calls disagree with tool argument validation.
        var normalizedSchema = schema.Replace(": optional number", ": \"optional number\"", StringComparison.OrdinalIgnoreCase);
        if (JsonNode.Parse(normalizedSchema) is not JsonObject parsed)
            throw new InvalidOperationException("Agent tool parameters must be a JSON object.");
        if (parsed.ContainsKey("type"))
        {
            if (!string.Equals(parsed["type"]?.GetValue<string>(), "object", StringComparison.Ordinal)
                || parsed["properties"] is not JsonObject)
                throw new InvalidOperationException("Agent tool parameters must be an object JSON Schema.");
            parsed["additionalProperties"] ??= false;
            return parsed;
        }

        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, descriptionNode) in parsed)
        {
            var description = descriptionNode?.GetValue<string>()
                ?? throw new InvalidOperationException($"Invalid description for agent tool parameter '{name}'.");
            var type = name.Equals("minutes", StringComparison.OrdinalIgnoreCase)
                || name.Equals("limit", StringComparison.OrdinalIgnoreCase) ? "integer" : "string";
            properties[name] = new JsonObject { ["type"] = type, ["description"] = description };
            if (description.Contains("required", StringComparison.OrdinalIgnoreCase)) required.Add(name);
        }
        var result = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };
        if (required.Count > 0) result["required"] = required;
        return result;
    }

    private static NativeAgentCompletion ExtractNativeCompletion(string json)
    {
        var message = JsonNode.Parse(json)?["choices"]?[0]?["message"] as JsonObject
            ?? throw new InvalidOperationException("Failed to parse native tool completion.");
        var result = new NativeAgentCompletion { Content = message["content"]?.GetValue<string>() };
        if (message["tool_calls"] is not JsonArray calls) return result;
        foreach (var item in calls.OfType<JsonObject>())
        {
            var function = item["function"] as JsonObject;
            var name = function?["name"]?.GetValue<string>();
            var id = item["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
            JsonObject arguments;
            try { arguments = JsonNode.Parse(function?["arguments"]?.GetValue<string>() ?? "{}") as JsonObject ?? new JsonObject(); }
            catch (JsonException) { arguments = new JsonObject(); }
            result.ToolCalls.Add(new NativeAgentToolCall { Id = id, Name = name, Arguments = arguments });
        }
        return result;
    }

    private static object ParseParamValue(string value)
    {
        var v = value.Trim();
        if (bool.TryParse(v, out var b)) return b;
        if (long.TryParse(v, out var l)) return l;
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return v;
    }

    private static string ExtractContent(string json)
    {
        var node = JsonNode.Parse(json);
        return node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Failed to parse LLM response.");
    }
}
