using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace LuxuryCo.Back.Services;

public class IntentParserService
{
    private readonly GroqProvider _groqProvider;

    public IntentParserService(GroqProvider groqProvider)
    {
        _groqProvider = groqProvider;
    }

    public async Task<ParsedIntent> ParseIntentAsync(string prompt)
    {
        var systemPrompt = @"
Analiza el mensaje del usuario y clasifícalo estrictamente en una de las siguientes intenciones (Intent):
- UPDATE_PRICE (Cambiar, subir o bajar precio de un producto)
- UPDATE_STOCK (Aumentar o reducir stock en inventario)
- SEARCH_PRODUCT (Buscar productos en catálogo)
- ADD_TO_CART (Agregar prendas al carrito)
- CREATE_INVOICE_DRAFT (Generar borrador de factura/cotización)
- GENERAL_CONVERSATION (Preguntas de estilo, saludos, etc.)

Debes devolver EXCLUSIVAMENTE un objeto JSON con el siguiente formato, sin explicaciones ni markdown:
{
  ""Intent"": ""INTENCION_DETECTADA"",
  ""Confidence"": 0.95,
  ""Parameters"": {
    ""ProductName"": ""nombre o fragmento"",
    ""ProductId"": 12,
    ""Amount"": 150000.0,
    ""Quantity"": 2,
    ""RawAmountText"": ""5 lucas""
  }
}
Si la intención no está totalmente clara o no hay datos suficientes, pon una confianza baja (por debajo de 0.80) o clasifica como GENERAL_CONVERSATION.
";

        try
        {
            var response = await _groqProvider.GenerateCompletionAsync(systemPrompt, prompt, temperature: 0.1);
            if (response.Success)
            {
                var cleanJson = response.Reply.Trim();
                
                // Remove markdown json wrappers if present
                if (cleanJson.StartsWith("```"))
                {
                    cleanJson = cleanJson.Replace("```json", "").Replace("```", "").Trim();
                }

                var parsed = JsonSerializer.Deserialize<ParsedIntent>(cleanJson);
                if (parsed != null)
                {
                    return parsed;
                }
            }
        }
        catch
        {
            // Fallback inside catch
        }

        return new ParsedIntent
        {
            Intent = "GENERAL_CONVERSATION",
            Confidence = 0.50
        };
    }
}

public class ParsedIntent
{
    public string Intent { get; set; } = "GENERAL_CONVERSATION";
    public double Confidence { get; set; }
    public IntentParameters Parameters { get; set; } = new();
}

public class IntentParameters
{
    public string ProductName { get; set; } = string.Empty;
    public int ProductId { get; set; }
    public decimal Amount { get; set; }
    public int Quantity { get; set; }
    public string RawAmountText { get; set; } = string.Empty;
}
