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
            var apiKey = _config["OpenRouter:ApiKey"] 
                ?? _config["openrouter:key"]
                ?? Environment.GetEnvironmentVariable("openrouter__key") 
                ?? Environment.GetEnvironmentVariable("open_router_key");
            
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                response.Success = false;
                response.ErrorMessage = "OpenRouter API Key not configured.";
                return response;
            }
            apiKey = apiKey.Trim().Trim('"').Trim('\'');
            var model = _config["OpenRouter:Model"] ?? "google/gemini-2.0-flash-exp:free";

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
            request.Headers.Add("HTTP-Referer", "https://luxuryco.com"); // Reemplazar con dominio real
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
                    var message = choices[0].GetProperty("message");
                    response.Reply = message.GetProperty("content").GetString() ?? "";
                    response.Success = true;
                }
                else
                {
                    response.Success = false;
                    response.ErrorMessage = "No choices returned by OpenRouter API.";
                }
            }
            else
            {
                response.Success = false;
                response.ErrorMessage = $"OpenRouter API Error: {httpResponse.StatusCode} - {responseString}";
                _logger.LogWarning("OpenRouter failed: {Error}", response.ErrorMessage);
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
