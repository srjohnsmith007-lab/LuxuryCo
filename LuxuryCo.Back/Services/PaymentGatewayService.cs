using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LuxuryCo.Back.Services;

public class PaymentGatewayService : IPaymentGatewayService
{
    private readonly IConfiguration _config;
    private readonly ILogger<PaymentGatewayService> _logger;

    public PaymentGatewayService(IConfiguration config, ILogger<PaymentGatewayService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<(bool Success, string TransactionId)> ProcessPaymentAsync(string token, decimal amount)
    {
        // Si el token es de simulación (fallback para testing manual)
        if (token.StartsWith("tok_mock_"))
        {
            await Task.Delay(1000);
            return (true, $"WOMPI-TX-{Guid.NewGuid().ToString().Substring(0, 8).ToUpper()}");
        }

        try
        {
            string baseUrl = _config["Wompi:BaseUrl"] ?? "https://production.wompi.co/v1";
            string privateKey = _config["Wompi:PrivateKey"] ?? Environment.GetEnvironmentVariable("Wompi__PrivateKey") ?? "";

            using var client = new HttpClient();
            var response = await client.GetAsync($"{baseUrl}/transactions/{token}");

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogWarning($"Wompi devolvió error {response.StatusCode} para la transacción {token}: {error}");
                return (false, token);
            }

            var content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            var data = doc.RootElement.GetProperty("data");
            var status = data.GetProperty("status").GetString();
            var amountInCents = data.GetProperty("amount_in_cents").GetDecimal();
            
            // Validar que el pago haya sido aprobado y el monto sea igual o mayor al esperado (amount en COP * 100 = centavos)
            decimal expectedAmountInCents = amount * 100;

            if (status == "APPROVED" && amountInCents >= expectedAmountInCents)
            {
                _logger.LogInformation($"Transacción Wompi {token} verificada y APROBADA.");
                return (true, token);
            }

            _logger.LogWarning($"Transacción Wompi {token} inválida. Estado: {status}, Monto: {amountInCents}, Esperado: {expectedAmountInCents}");
            return (false, token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Excepción verificando transacción {token} en Wompi.");
            return (false, token);
        }
    }
}
