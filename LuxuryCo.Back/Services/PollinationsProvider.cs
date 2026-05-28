using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace LuxuryCo.Back.Services;

public class PollinationsProvider : IImageProvider
{
    private readonly HttpClient _httpClient;

    public string ProviderName => "Pollinations";

    public PollinationsProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ImageGenerationProviderResponse> GenerateImageAsync(string optimizedPrompt, string negativePrompt, int seed)
    {
        try
        {
            var promptCombined = optimizedPrompt;
            if (!string.IsNullOrWhiteSpace(negativePrompt))
            {
                promptCombined += $" (negative prompt: {negativePrompt})";
            }
            
            var encodedPrompt = Uri.EscapeDataString(promptCombined);
            var url = $"https://image.pollinations.ai/prompt/{encodedPrompt}?width=512&height=512&seed={seed}&nologo=true&private=true";

            // Verificar que el endpoint responda correctamente
            var response = await _httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                return new ImageGenerationProviderResponse
                {
                    Success = true,
                    ImageUrl = url
                };
            }

            return new ImageGenerationProviderResponse
            {
                Success = false,
                ErrorMessage = $"Pollinations API returned status: {response.StatusCode}"
            };
        }
        catch (Exception ex)
        {
            return new ImageGenerationProviderResponse
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }
}
