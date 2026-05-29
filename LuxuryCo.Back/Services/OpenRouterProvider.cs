using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;

namespace LuxuryCo.Back.Services;

public class OpenRouterProvider : IAiProvider
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ILogger<OpenRouterProvider> _logger;

    public string ProviderName => "OpenRouter";

    public OpenRouterProvider(HttpClient httpClient, IConfiguration config, ILogger<OpenRouterProvider> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public async Task<ProviderResponse> GenerateCompletionAsync(string systemPrompt, string userPrompt, double temperature = 0.7)
    {
        var response = new ProviderResponse();
        var sw = Stopwatch.StartNew();

        try
        {
            // Read from config first, then env vars. Skip empty or placeholder values.
            var configKey = _config["OpenRouter:ApiKey"];
            var apiKey = (!string.IsNullOrWhiteSpace(configKey) ? configKey : null)
                ?? _config["openrouter:key"]
                ?? Environment.GetEnvironmentVariable("openrouter__key") 
                ?? Environment.GetEnvironmentVariable("open_router_key");
            
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                response.Success = false;
                response.ErrorMessage = $"OpenRouter API Key not configured. Config[openrouter:key]={_config["openrouter:key"] ?? "NULL"} | EnvVar={Environment.GetEnvironmentVariable("openrouter__key") ?? "NULL"}";
                return response;
            }
            apiKey = apiKey.Trim().Trim('"').Trim('\'').Replace("\n","").Replace("\r","");
            // List of free models to try in order
            var freeModels = new[]
            {
                _config["OpenRouter:Model"] ?? "",
                "mistralai/mistral-nemo:free",
                "google/gemma-2-9b-it:free",
                "deepseek/deepseek-chat-v3-0324:free",
                "qwen/qwen3-8b:free",
                "meta-llama/llama-3.2-3b-instruct:free",
                "mistralai/mistral-7b-instruct:free",
                "nousresearch/hermes-3-llama-3.1-8b:free",
                "google/gemma-3-12b-it:free",
                "qwen/qwen-2.5-7b-instruct:free"
            };

            string? lastError = null;
            foreach (var model in freeModels.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct())
            {
                var requestBody = new
                {
                    model = model,
                    temperature = temperature,
                    messages = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt }
                    }
                };

                var jsonContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.Add("HTTP-Referer", "https://luxuryco.com");
                request.Headers.Add("X-Title", "LuxuryCo AI");
                request.Content = jsonContent;

                var httpResponse = await _httpClient.SendAsync(request);
                var responseString = await httpResponse.Content.ReadAsStringAsync();

                sw.Stop();
                response.LatencyMs = sw.ElapsedMilliseconds;

                if (httpResponse.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(responseString);
                    var root = document.RootElement;

                    var choices = root.GetProperty("choices");
                    if (choices.GetArrayLength() > 0)
                    {
                        var msg = choices[0].GetProperty("message");
                        response.Reply = msg.GetProperty("content").GetString() ?? "";
                        response.Success = true;
                        break; // Exit loop on success
                    }
                }

                // 404 = model unavailable, 429 = rate limited — try next model in both cases
                var statusCode = (int)httpResponse.StatusCode;
                lastError = $"[{model}] {httpResponse.StatusCode}: {responseString}";

                if (statusCode != 404 && statusCode != 429)
                {
                    _logger.LogWarning("OpenRouter fatal error on model {Model}: {Error}", model, lastError);
                    break;
                }

                _logger.LogWarning("OpenRouter model {Model} not found, trying next...", model);
                sw = Stopwatch.StartNew();
            }

            if (!response.Success)
            {
                response.ErrorMessage = $"All OpenRouter models failed. Last error: {lastError}";
                _logger.LogWarning("All OpenRouter models failed: {Error}", response.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            response.LatencyMs = sw.ElapsedMilliseconds;
            response.Success = false;
            response.ErrorMessage = $"Exception in OpenRouterProvider: {ex.Message}";
            _logger.LogError(ex, "Exception in OpenRouterProvider");
        }

        return response;
    }
}
