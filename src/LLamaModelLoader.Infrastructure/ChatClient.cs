using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LLamaModelLoader.Core;

namespace LLamaModelLoader.Infrastructure;

public sealed class ChatClient(HttpMessageHandler? handler = null) : IDisposable
{
    private readonly HttpClient _http = new(handler ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    { Timeout = Timeout.InfiniteTimeSpan };

    public async IAsyncEnumerable<ChatUpdate> StreamAsync(ChatTarget target, IReadOnlyList<ChatMessage> messages, int maxTokens,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        if (!Uri.TryCreate(target.BaseUrl, UriKind.Absolute, out var address) || address.Scheme != "http" || address.Host != "127.0.0.1" ||
            address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0 || address.UserInfo.Length != 0)
            throw new ArgumentException("Chat requires the managed local server address.");
        if (string.IsNullOrWhiteSpace(target.Model)) throw new ArgumentException("No running model alias is available.");
        if (maxTokens is < 1 or > 131072) throw new ArgumentException("Output tokens must be between 1 and 131072.");
        if (messages.Count is < 1 or > 200 || messages.Any(m => m.Role is not ("system" or "user" or "assistant") || string.IsNullOrWhiteSpace(m.Content)))
            throw new ArgumentException("Chat messages must contain text and valid roles (up to 200 messages).");
        var body = JsonSerializer.Serialize(new { model = target.Model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            stream = true, stream_options = new { include_usage = true }, max_tokens = maxTokens });
        if (Encoding.UTF8.GetByteCount(body) > 1024 * 1024) throw new ArgumentException("Chat history exceeds 1 MiB. Start a new chat or shorten the message.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(address, "v1/chat/completions"))
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            using var errorReader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            var buffer = new char[4096]; var count = await errorReader.ReadBlockAsync(buffer, timeout.Token);
            throw new HttpRequestException($"Chat request failed (HTTP {(int)response.StatusCode}): {ErrorText(new string(buffer, 0, count))}");
        }
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            throw new InvalidDataException("The server did not return a chat event stream. Check its chat API support.");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token), Encoding.UTF8);
        var data = new StringBuilder(); var total = 0; string? finish = null;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            total = checked(total + line.Length);
            if (total > 8 * 1024 * 1024 || line.Length > 1024 * 1024) throw new InvalidDataException("Chat response is too large.");
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.AppendLine(line[5..].TrimStart(' '));
                continue;
            }
            if (line.Length != 0 || data.Length == 0) continue;
            var payload = data.ToString().Trim(); data.Clear();
            if (payload == "[DONE]")
            {
                if (finish is null) throw new InvalidDataException("Chat ended without a completion reason.");
                yield break;
            }
            var update = Parse(payload);
            if (update.FinishReason is not null) finish = update.FinishReason;
            yield return update;
        }
        throw new InvalidDataException("The chat stream disconnected before completion. The partial response was not added to conversation context.");
    }

    private static ChatUpdate Parse(string payload)
    {
        using var document = JsonDocument.Parse(payload); var root = document.RootElement;
        if (root.TryGetProperty("error", out _)) throw new InvalidDataException(ErrorText(payload));
        int? Count(JsonElement usage, string name) => usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0 ? number : null;
        string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        int? input = null, output = null;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        { input = Count(usage, "prompt_tokens"); output = Count(usage, "completion_tokens"); }
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return new(PromptTokens: input, CompletionTokens: output);
        var choice = choices[0]; var finish = Text(choice, "finish_reason");
        string content = "", reasoning = "";
        if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
        {
            if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() > 0)
                throw new InvalidDataException("The model returned a tool call. This chat accepts text responses only.");
            content = Text(delta, "content") ?? "";
            reasoning = Text(delta, "reasoning_content") ?? Text(delta, "reasoning") ?? "";
        }
        if (finish is not null and not ("stop" or "length")) throw new InvalidDataException("The server ended this text response with: " + finish);
        return new(content, reasoning, finish, input, output);
    }

    private static string ErrorText(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? "Server error.";
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)) return message.GetString() ?? "Server error.";
            }
        }
        catch (JsonException) { }
        return string.IsNullOrWhiteSpace(text) ? "No error details were returned." : text;
    }
    public void Dispose() => _http.Dispose();
}
