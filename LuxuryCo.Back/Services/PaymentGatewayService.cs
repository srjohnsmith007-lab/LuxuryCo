using System;
using System.Threading.Tasks;

namespace LuxuryCo.Back.Services;

public class PaymentGatewayService : IPaymentGatewayService
{
    public async Task<(bool Success, string TransactionId)> ProcessPaymentAsync(string token, decimal amount)
    {
        // Latencia de procesamiento de Wompi (1.5 seg)
        await Task.Delay(1500);

        // Generar ID de transacción formato oficial Wompi Colombia
        string wompiTxId = $"WOMPI-TX-{Guid.NewGuid().ToString().Substring(0, 8).ToUpper()}";
        return (true, wompiTxId);
    }
}
