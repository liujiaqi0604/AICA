using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AICA.Core.Agent;
using Microsoft.Extensions.Logging;

namespace AICA.Core.LLM
{
    /// <summary>
    /// OpenAI-compatible LLM client with streaming and tool calling support
    /// </summary>
    public class OpenAIClient : ILLMClient, IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly LLMClientOptions _options;
        private readonly ILogger<OpenAIClient> _logger;
        private CancellationTokenSource _abortCts;
        private readonly JsonSerializerOptions _jsonOptions;

        public OpenAIClient(LLMClientOptions options, ILogger<OpenAIClient> logger = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger;

            _httpClient = new HttpClient
            {
                // Use infinite timeout for streaming; we control cancellation via CancellationToken
                Timeout = options.Stream ? System.Threading.Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(options.TimeoutSeconds)
            };

            if (!string.IsNullOrEmpty(options.ApiKey))
            {
                _httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", options.ApiKey);
            }

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = false
            };
        }

        public async IAsyncEnumerable<LLMChunk> StreamChatAsync(
            IEnumerable<ChatMessage> messages,
            IEnumerable<ToolDefinition> tools = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _abortCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var linkedCt = _abortCts.Token;

            var request = BuildRequest(messages, tools);
            var requestJson = JsonSerializer.Serialize(request, _jsonOptions);

            _logger?.LogDebug("LLM Request: {Request}", requestJson);

            // Enhanced debug info for MiniMax API diagnostics
            System.Diagnostics.Debug.WriteLine($"[AICA] LLM Request URL: {GetChatEndpoint()}");
            System.Diagnostics.Debug.WriteLine($"[AICA] Model: {request.Model}");
            System.Diagnostics.Debug.WriteLine($"[AICA] Tools count: {request.Tools?.Count ?? 0}");
            System.Diagnostics.Debug.WriteLine($"[AICA] tool_choice: {request.ToolChoice ?? "(null)"}");
            System.Diagnostics.Debug.WriteLine($"[AICA] Messages count: {request.Messages?.Count ?? 0}");
            if (requestJson.Length < 5000)
                System.Diagnostics.Debug.WriteLine($"[AICA] Request JSON: {requestJson}");
            else
                System.Diagnostics.Debug.WriteLine($"[AICA] Request JSON (truncated): {requestJson.Substring(0, 2000)}...");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, GetChatEndpoint())
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                    httpRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    linkedCt);
            }
            catch (OperationCanceledException)
            {
                _logger?.LogDebug("Request cancelled");
                yield break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "HTTP request failed");
                throw new LLMException("Failed to connect to LLM API", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger?.LogError("LLM API error: {StatusCode} - {Content}", response.StatusCode, errorContent);

                // Enhanced error message for MiniMax-specific errors
                string errorMessage = $"LLM API returned {response.StatusCode}: {errorContent}";
                if (errorContent.Contains("does not exist") && errorContent.Contains("model"))
                {
                    errorMessage += "\n\nHint: Please check if the model name is correct in your configuration. " +
                                   "For MiniMax-M2.5, ensure the model name matches exactly what the API expects.";
                }

                throw new LLMException(errorMessage, (int)response.StatusCode);
            }

            if (_options.Stream)
            {
                await foreach (var chunk in ProcessStreamResponseAsync(response, linkedCt))
                {
                    yield return chunk;
                }
            }
            else
            {
                await foreach (var chunk in ProcessNonStreamResponseAsync(response, linkedCt))
                {
                    yield return chunk;
                }
            }
        }

        private async IAsyncEnumerable<LLMChunk> ProcessStreamResponseAsync(
            HttpResponseMessage response,
            [EnumeratorCancellation] CancellationToken ct)
        {
            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            var toolCallsBuilder = new Dictionary<int, ToolCallBuilder>();
            string currentContent = null;

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();

                if (string.IsNullOrEmpty(line))
                    continue;

                if (!line.StartsWith("data: "))
                    continue;

                var data = line.Substring(6);

                if (data == "[DONE]")
                {
                    // Emit accumulated tool calls
                    foreach (var builder in toolCallsBuilder.Values)
                    {
                        var toolCall = builder.Build();
                        if (toolCall != null)
                        {
                            yield return LLMChunk.Tool(toolCall);
                        }
                    }
                    yield return LLMChunk.Done();
                    yield break;
                }

                StreamChunk chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize<StreamChunk>(data, _jsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger?.LogWarning(ex, "Failed to parse chunk: {Data}", data);
                    continue;
                }

                if (chunk?.Choices == null || chunk.Choices.Count == 0)
                    continue;

                var delta = chunk.Choices[0].Delta;
                if (delta == null)
                    continue;

                // Handle text content
                if (!string.IsNullOrEmpty(delta.Content))
                {
                    currentContent = (currentContent ?? string.Empty) + delta.Content;
                    yield return LLMChunk.TextContent(delta.Content);
                }

                // Handle tool calls
                if (delta.ToolCalls != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[AICA] Received tool_calls in delta: {delta.ToolCalls.Count} calls");
                    foreach (var tc in delta.ToolCalls)
                    {
                        if (!toolCallsBuilder.TryGetValue(tc.Index, out var builder))
                        {
                            builder = new ToolCallBuilder();
                            toolCallsBuilder[tc.Index] = builder;
                        }

                        if (!string.IsNullOrEmpty(tc.Id))
                            builder.Id = tc.Id;

                        if (tc.Function != null)
                        {
                            if (!string.IsNullOrEmpty(tc.Function.Name))
                                builder.Name = tc.Function.Name;
                            if (!string.IsNullOrEmpty(tc.Function.Arguments))
                                builder.Arguments += tc.Function.Arguments;
                        }
                    }
                }

                // Log finish reason
                if (!string.IsNullOrEmpty(chunk.Choices[0].FinishReason))
                {
                    System.Diagnostics.Debug.WriteLine($"[AICA] finish_reason: {chunk.Choices[0].FinishReason}");
                }

                // Check for finish reason
                if (chunk.Choices[0].FinishReason == "tool_calls")
                {
                    foreach (var builder in toolCallsBuilder.Values)
                    {
                        var toolCall = builder.Build();
                        if (toolCall != null)
                        {
                            yield return LLMChunk.Tool(toolCall);
                        }
                    }
                    toolCallsBuilder.Clear();
                }

                // Emit finish reason so the caller can detect truncation (finish_reason=length)
                if (!string.IsNullOrEmpty(chunk.Choices[0].FinishReason))
                {
                    yield return LLMChunk.Finished(chunk.Choices[0].FinishReason);
                }
            }
        }

        private async IAsyncEnumerable<LLMChunk> ProcessNonStreamResponseAsync(
            HttpResponseMessage response,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var content = await response.Content.ReadAsStringAsync();
            _logger?.LogDebug("LLM Response: {Response}", content);

            var result = JsonSerializer.Deserialize<ChatCompletionResponse>(content, _jsonOptions);

            if (result?.Choices == null || result.Choices.Count == 0)
            {
                yield return LLMChunk.Done();
                yield break;
            }

            var message = result.Choices[0].Message;

            if (!string.IsNullOrEmpty(message.Content))
            {
                yield return LLMChunk.TextContent(message.Content);
            }

            if (message.ToolCalls != null)
            {
                foreach (var tc in message.ToolCalls)
                {
                    var toolCall = new ToolCall
                    {
                        Id = tc.Id,
                        Name = tc.Function?.Name,
                        Arguments = ParseArguments(tc.Function?.Arguments)
                    };
                    yield return LLMChunk.Tool(toolCall);
                }
            }

            var usage = result.Usage != null ? new UsageInfo
            {
                PromptTokens = result.Usage.PromptTokens,
                CompletionTokens = result.Usage.CompletionTokens
            } : null;

            yield return LLMChunk.Done(usage);
        }

        private ChatCompletionRequest BuildRequest(
            IEnumerable<ChatMessage> messages,
            IEnumerable<ToolDefinition> tools)
        {
            var request = new ChatCompletionRequest
            {
                Model = _options.Model,
                Messages = new List<RequestMessage>(),
                MaxTokens = _options.MaxTokens,
                Temperature = _options.Temperature,
                Stream = _options.Stream
            };

            foreach (var msg in messages)
            {
                var reqMsg = new RequestMessage
                {
                    Role = msg.Role.ToString().ToLowerInvariant(),
                    Content = msg.Content
                };

                if (msg.Role == ChatRole.Tool)
                {
                    reqMsg.ToolCallId = msg.ToolCallId;
                }

                if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
                {
                    reqMsg.ToolCalls = new List<RequestToolCall>();
                    foreach (var tc in msg.ToolCalls)
                    {
                        reqMsg.ToolCalls.Add(new RequestToolCall
                        {
                            Id = tc.Id,
                            Type = tc.Type,
                            Function = new RequestFunction
                            {
                                Name = tc.Function?.Name,
                                Arguments = tc.Function?.Arguments
                            }
                        });
                    }
                }

                request.Messages.Add(reqMsg);
            }

            if (tools != null)
            {
                request.Tools = new List<RequestTool>();
                foreach (var tool in tools)
                {
                    request.Tools.Add(new RequestTool
                    {
                        Type = "function",
                        Function = new RequestToolFunction
                        {
                            Name = tool.Name,
                            Description = tool.Description,
                            Parameters = tool.Parameters
                        }
                    });
                }
                request.ToolChoice = "auto";
            }

            return request;
        }

        private string GetChatEndpoint()
        {
            var baseUrl = _options.ApiEndpoint.TrimEnd('/');
            if (!baseUrl.EndsWith("/chat/completions"))
            {
                baseUrl = baseUrl + "/chat/completions";
            }
            return baseUrl;
        }

        public void Abort()
        {
            _abortCts?.Cancel();
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            _abortCts?.Dispose();
        }

        private static Dictionary<string, object> ParseArguments(string json)
        {
            if (string.IsNullOrEmpty(json))
                return new Dictionary<string, object>();

            try
            {
                var result = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                var dict = new Dictionary<string, object>();
                foreach (var kvp in result)
                {
                    dict[kvp.Key] = ConvertJsonElement(kvp.Value);
                }
                return dict;
            }
            catch
            {
                return new Dictionary<string, object>();
            }
        }

        private static object ConvertJsonElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                    if (element.TryGetInt64(out var l)) return l;
                    return element.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Null:
                    return null;
                case JsonValueKind.Array:
                    var list = new List<object>();
                    foreach (var item in element.EnumerateArray())
                        list.Add(ConvertJsonElement(item));
                    return list;
                case JsonValueKind.Object:
                    var dict = new Dictionary<string, object>();
                    foreach (var prop in element.EnumerateObject())
                        dict[prop.Name] = ConvertJsonElement(prop.Value);
                    return dict;
                default:
                    return element.ToString();
            }
        }

        #region Helper Classes

        private class ToolCallBuilder
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Arguments { get; set; } = string.Empty;

            public ToolCall Build()
            {
                if (string.IsNullOrEmpty(Name))
                    return null;

                return new ToolCall
                {
                    Id = Id ?? Guid.NewGuid().ToString(),
                    Name = Name,
                    Arguments = ParseArguments(Arguments)
                };
            }
        }

        #endregion

        #region Request/Response Models

        private class ChatCompletionRequest
        {
            public string Model { get; set; }
            public List<RequestMessage> Messages { get; set; }
            public List<RequestTool> Tools { get; set; }
            [JsonPropertyName("tool_choice")]
            public string ToolChoice { get; set; }
            [JsonPropertyName("max_tokens")]
            public int MaxTokens { get; set; }
            public double Temperature { get; set; }
            public bool Stream { get; set; }
        }

        private class RequestMessage
        {
            public string Role { get; set; }
            public string Content { get; set; }
            [JsonPropertyName("tool_call_id")]
            public string ToolCallId { get; set; }
            [JsonPropertyName("tool_calls")]
            public List<RequestToolCall> ToolCalls { get; set; }
        }

        private class RequestToolCall
        {
            public string Id { get; set; }
            public string Type { get; set; }
            public RequestFunction Function { get; set; }
        }

        private class RequestFunction
        {
            public string Name { get; set; }
            public string Arguments { get; set; }
        }

        private class RequestTool
        {
            public string Type { get; set; }
            public RequestToolFunction Function { get; set; }
        }

        private class RequestToolFunction
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public ToolParameters Parameters { get; set; }
        }

        private class ChatCompletionResponse
        {
            public List<ResponseChoice> Choices { get; set; }
            public ResponseUsage Usage { get; set; }
        }

        private class ResponseChoice
        {
            public ResponseMessage Message { get; set; }
            [JsonPropertyName("finish_reason")]
            public string FinishReason { get; set; }
        }

        private class ResponseMessage
        {
            public string Role { get; set; }
            public string Content { get; set; }
            [JsonPropertyName("tool_calls")]
            public List<ResponseToolCall> ToolCalls { get; set; }
        }

        private class ResponseToolCall
        {
            public string Id { get; set; }
            public string Type { get; set; }
            public ResponseFunction Function { get; set; }
        }

        private class ResponseFunction
        {
            public string Name { get; set; }
            public string Arguments { get; set; }
        }

        private class ResponseUsage
        {
            [JsonPropertyName("prompt_tokens")]
            public int PromptTokens { get; set; }
            [JsonPropertyName("completion_tokens")]
            public int CompletionTokens { get; set; }
        }

        private class StreamChunk
        {
            public List<StreamChoice> Choices { get; set; }
        }

        private class StreamChoice
        {
            public StreamDelta Delta { get; set; }
            [JsonPropertyName("finish_reason")]
            public string FinishReason { get; set; }
        }

        private class StreamDelta
        {
            public string Role { get; set; }
            public string Content { get; set; }
            [JsonPropertyName("tool_calls")]
            public List<StreamToolCall> ToolCalls { get; set; }
        }

        private class StreamToolCall
        {
            public int Index { get; set; }
            public string Id { get; set; }
            public string Type { get; set; }
            public StreamFunction Function { get; set; }
        }

        private class StreamFunction
        {
            public string Name { get; set; }
            public string Arguments { get; set; }
        }

        #endregion
    }

    /// <summary>
    /// Exception for LLM communication errors
    /// </summary>
    public class LLMException : Exception
    {
        public int StatusCode { get; }

        public LLMException(string message) : base(message) { }
        public LLMException(string message, Exception inner) : base(message, inner) { }
        public LLMException(string message, int statusCode) : base(message)
        {
            StatusCode = statusCode;
        }

        /// <summary>
        /// Whether this error indicates the context window was exceeded.
        /// Detects patterns from OpenAI, vLLM, ollama, LM Studio, MiniMax, etc.
        /// </summary>
        public bool IsContextExceeded =>
            StatusCode == 400 &&
            (Message.Contains("context_length_exceeded") ||
             Message.Contains("maximum context length") ||
             Message.Contains("context window") ||
             Message.Contains("token limit") ||
             Message.Contains("max_tokens") ||
             Message.Contains("too long") ||
             Message.Contains("exceeds the model") ||
             Message.Contains("上下文长度") ||
             Message.Contains("令牌限制"));

        /// <summary>
        /// Whether this error is transient and worth retrying (server errors, timeouts).
        /// </summary>
        public bool IsTransient =>
            StatusCode == 429 || StatusCode == 500 || StatusCode == 502 ||
            StatusCode == 503 || StatusCode == 504;
    }
}
