using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NovaLauncher.Kernel;

namespace NovaLauncher.Services;

/// <summary>
/// 内置 AI 助手 Nova：OpenAI 兼容接口（DeepSeek / 通义 / 本地 Ollama 等均可）。
/// 在设置中填入 接口地址 / API Key / 模型名 即可启用。
/// </summary>
public static class NovaAiService
{
    public static bool Configured =>
        !string.IsNullOrWhiteSpace(SettingsService.S.AiEndpoint) &&
        !string.IsNullOrWhiteSpace(SettingsService.S.AiKey);

    public static async Task<string> ChatAsync(
        IReadOnlyList<(string role, string content)> messages, CancellationToken ct)
    {
        var s = SettingsService.S;
        if (!Configured) throw new Exception("Nova 未配置：请到 设置 → AI 助手 填写接口地址与 Key");

        var endpoint = s.AiEndpoint.TrimEnd('/');
        if (!endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            endpoint += "/chat/completions";

        var arr = new JsonArray();
        foreach (var (role, content) in messages)
        {
            arr.Add(new JsonObject { ["role"] = role, ["content"] = content });
        }
        var body = JsonSerializer.Serialize(new { model = s.AiModel, messages = arr, stream = false });
        var resp = await Task.Run(() => NovaKernel.HttpPostEx(endpoint, body, "application/json",
            "Authorization: Bearer " + s.AiKey, 120000), ct);
        if (resp == null) throw new Exception("Nova 无响应");
        using var doc = JsonDocument.Parse(resp);
        var content2 = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        return content2 ?? "";
    }
}
