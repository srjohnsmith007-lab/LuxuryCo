using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using LuxuryCo.Database.Data;
using LuxuryCo.Database.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Polly;
using Polly.CircuitBreaker;

namespace LuxuryCo.Back.Services;

public class MultiModelAiService : IAiService
{
    private readonly LuxuryCoDbContext _context;
    private readonly PromptSecurityService _promptSecurity;
    private readonly IntentParserService _intentParser;
    private readonly ColombianDialectParserService _dialectParser;
    private readonly PermissionEngine _permissionEngine;
    private readonly ConfirmationService _confirmationService;
    private readonly ToolExecutorService _toolExecutor;
    private readonly IAiProvider _groqProvider;
    private readonly IAiProvider _geminiProvider;

    // Resiliency Policies
    private readonly AsyncPolicy _retryAndFallbackPolicy;

    public MultiModelAiService(
        LuxuryCoDbContext context,
        PromptSecurityService promptSecurity,
        IntentParserService intentParser,
        ColombianDialectParserService dialectParser,
        PermissionEngine permissionEngine,
        ConfirmationService confirmationService,
        ToolExecutorService toolExecutor,
        GroqProvider groqProvider,
        GeminiProvider geminiProvider)
    {
        _context = context;
        _promptSecurity = promptSecurity;
        _intentParser = intentParser;
        _dialectParser = dialectParser;
        _permissionEngine = permissionEngine;
        _confirmationService = confirmationService;
        _toolExecutor = toolExecutor;
        _groqProvider = groqProvider;
        _geminiProvider = geminiProvider;

        // Polly: Retry up to 2 times, then fallback to Gemini if Groq fails
        _retryAndFallbackPolicy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(2, retryAttempt => TimeSpan.FromMilliseconds(200 * retryAttempt))
            .WrapAsync(Policy.TimeoutAsync(TimeSpan.FromSeconds(8)));
    }

    public async Task<string> GetAdminBusinessAdviceAsync(string userMessage, int adminUserId)
    {
        // 1. Audit Log initialization
        var auditLog = new AiActionLog
        {
            UserId = adminUserId,
            SessionId = "ADMIN_CONSOLE",
            PromptOriginal = userMessage,
            Timestamp = DateTime.UtcNow
        };

        // 2. WAF Prompt Security Sanitization
        if (!_promptSecurity.IsPromptSafe(userMessage, out string violationReason))
        {
            auditLog.SanitizedPrompt = "[BLOCKED]";
            auditLog.ErrorMessage = $"Security Block: {violationReason}";
            auditLog.Success = false;
            
            _context.AiActionLogs.Add(auditLog);
            await _context.SaveChangesAsync();
            return $"Acceso Denegado por Seguridad de IA: {violationReason}";
        }

        string sanitized = _promptSecurity.SanitizedInput(userMessage);
        auditLog.SanitizedPrompt = sanitized;

        // 3. Dialect Parsing (Colombian slang translations)
        var dialectResult = _dialectParser.ParseAmount(sanitized);
        if (dialectResult.Parsed)
        {
            sanitized += $" [Monto Detectado: {dialectResult.Amount} COP, Clarificación Requerida: {dialectResult.RequiresClarification}]";
        }

        // 4. Intent Classification
        var intent = await _intentParser.ParseIntentAsync(sanitized);
        auditLog.IntentDetected = intent.Intent;
        auditLog.Confidence = intent.Confidence;

        // 5. RBAC & Validation Layer
        var hasPermission = await _permissionEngine.HasPermissionAsync(adminUserId, "ADMIN");
        if (!hasPermission)
        {
            auditLog.Success = false;
            auditLog.ErrorMessage = "Insuficientes privilegios RBAC.";
            
            _context.AiActionLogs.Add(auditLog);
            await _context.SaveChangesAsync();
            return "No tienes los permisos requeridos para interactuar con la IA de administración.";
        }

        // 6. Intent Execution or Confirmation Routing
        if (intent.Intent != "GENERAL_CONVERSATION")
        {
            // Dialect parsing check: if high ambiguity, override auto-run
            bool requiresConfirmation = intent.Confidence < 0.95 || dialectResult.RequiresClarification;
            auditLog.RiskLevel = requiresConfirmation ? "MEDIUM" : "LOW";

            if (requiresConfirmation)
            {
                // Register Pending Action in confirmation engine
                string actionDesc = $"Modificar {intent.Intent} con parámetros: Producto {intent.Parameters.ProductId}, Cantidad/Monto: {(dialectResult.Parsed ? dialectResult.Amount : intent.Parameters.Amount)}";
                var token = await _confirmationService.RegisterPendingActionAsync(adminUserId, intent.Intent, intent.Parameters, actionDesc);

                auditLog.ActionExecuted = "PENDING_CONFIRMATION";
                _context.AiActionLogs.Add(auditLog);
                await _context.SaveChangesAsync();

                return $"[REQUIERE_CONFIRMACION] He registrado la intención '{intent.Intent}' para tu aprobación. Token: {token}. Descripción: {actionDesc}";
            }
            else
            {
                // Direct Tool Execution
                var toolResult = await _toolExecutor.ExecuteToolAsync(intent.Intent, intent.Parameters, adminUserId);
                
                auditLog.Success = toolResult.Success;
                auditLog.ActionExecuted = intent.Intent;
                auditLog.BeforeState = toolResult.BeforeStateJson;
                auditLog.AfterState = toolResult.AfterStateJson;
                auditLog.ErrorMessage = toolResult.Message;

                _context.AiActionLogs.Add(auditLog);
                await _context.SaveChangesAsync();

                return toolResult.Message;
            }
        }

        // 7. General Business Advice conversation with RAG
        // Build Prompt Context (RAG: catalog & metrics)
        var totalProducts = await _context.Productos.CountAsync();
        var totalStock = await _context.Productos.SumAsync(p => (int?)p.stock) ?? 0;
        
        string systemPrompt = $@"
Eres el Asistente Financiero y Operativo de lujo (AI) exclusivo para el Administrador de la marca 'LuxuryCo'.
REGLAS ESTRICTAS E INQUEBRANTABLES:
1. NUNCA inventes números internos. Para datos internos, usa EXCLUSIVAMENTE los datos proporcionados abajo.
2. Habla de manera profesional, estratégica y directa.

DATOS EN TIEMPO REAL:
- Total de productos en catálogo: {totalProducts}
- Unidades totales de ropa en stock: {totalStock}
";

        // Query AI Provider with Polly Fallback
        ProviderResponse response;
        try
        {
            response = await _retryAndFallbackPolicy.ExecuteAsync(() => 
                _groqProvider.GenerateCompletionAsync(systemPrompt, sanitized));
            
            if (!response.Success)
            {
                // Fallback to Gemini
                response = await _geminiProvider.GenerateCompletionAsync(systemPrompt, sanitized);
            }
        }
        catch (Exception ex)
        {
            response = new ProviderResponse
            {
                Success = false,
                ErrorMessage = $"Polly policies exhausted: {ex.Message}"
            };
        }

        auditLog.ModelUsed = response.Success ? "Groq" : "None";
        auditLog.Success = response.Success;
        auditLog.ErrorMessage = response.ErrorMessage;

        _context.AiActionLogs.Add(auditLog);
        await _context.SaveChangesAsync();

        if (response.Success)
        {
            return _promptSecurity.SanitizeOutput(response.Reply);
        }

        return "El servicio de IA empresarial está temporalmente no disponible. Inténtalo más tarde.";
    }

    public async Task<StylistResponse> GetClientStylistAdviceAsync(string userMessage, string sessionId, int? userId = null)
    {
        var stylistResult = new StylistResponse();
        int activeUserId = userId ?? 0;

        // 1. WAF Security Sanitization
        if (!_promptSecurity.IsPromptSafe(userMessage, out string violationReason))
        {
            stylistResult.Reply = $"Acceso bloqueado por políticas de seguridad: {violationReason}";
            return stylistResult;
        }

        string sanitized = _promptSecurity.SanitizedInput(userMessage);

        // 2. Intent Parsing (to see if client wants to add to cart or search)
        var intent = await _intentParser.ParseIntentAsync(sanitized);

        if (intent.Intent == "ADD_TO_CART" && activeUserId > 0)
        {
            // Execute add to cart tool safely
            var toolResult = await _toolExecutor.ExecuteToolAsync(intent.Intent, intent.Parameters, activeUserId);
            stylistResult.Reply = toolResult.Message;
            return stylistResult;
        }

        // 3. Stylist RAG Catalogo Recommendations
        var activeProducts = await _context.Productos
            .Where(p => p.activo && p.stock > 0)
            .Select(p => new
            {
                p.id_producto, p.nombre, p.precio, p.seccion,
                imagen = p.Imagenes.Where(i => i.principal).Select(i => i.url_imagen).FirstOrDefault()
            })
            .ToListAsync();

        var catalogForPrompt = activeProducts.Select(p => new { p.id_producto, p.nombre, p.precio, p.seccion });
        var productsJson = JsonSerializer.Serialize(catalogForPrompt);

        var systemPrompt = $@"Eres un Asesor de Estilo exclusivo y 'Personal Shopper' de LuxuryCo.
Tono: amable, sofisticado, breve.
REGLA 1: Solo recomienda productos del catálogo JSON. Si no existe lo que piden, dilo amablemente.
REGLA 2: Cuando recomiendes 1 o más productos, incluye la etiqueta [PRODUCTO:id_producto] exactamente así (reemplaza id_producto por el número). Ejemplo: [PRODUCTO:3]
REGLA 3: Máximo 2 productos recomendados por respuesta.

CATÁLOGO:
{productsJson}";

        var response = await _retryAndFallbackPolicy.ExecuteAsync(() =>
            _groqProvider.GenerateCompletionAsync(systemPrompt, sanitized));

        if (!response.Success)
        {
            response = await _geminiProvider.GenerateCompletionAsync(systemPrompt, sanitized);
        }

        if (response.Success)
        {
            var rawReply = response.Reply;
            var tagPattern = new System.Text.RegularExpressions.Regex(@"\[PRODUCTO:(\d+)\]");
            var matches = tagPattern.Matches(rawReply);

            var mentionedIds = matches.Cast<System.Text.RegularExpressions.Match>()
                .Select(m => int.Parse(m.Groups[1].Value))
                .Distinct().ToList();

            foreach (var id in mentionedIds)
            {
                var p = activeProducts.FirstOrDefault(x => x.id_producto == id);
                if (p != null)
                {
                    stylistResult.Cards.Add(new ProductCard
                    {
                        Id = p.id_producto,
                        Nombre = p.nombre,
                        Precio = p.precio,
                        Seccion = p.seccion ?? "",
                        Imagen = p.imagen ?? "/img/placeholder.png",
                        Url = $"/Shop/Product/{p.id_producto}"
                    });
                }
            }

            stylistResult.Reply = _promptSecurity.SanitizeOutput(tagPattern.Replace(rawReply, "").Trim());
        }
        else
        {
            stylistResult.Reply = "Lo lamento, no puedo procesar tu recomendación en este momento. Prueba de nuevo.";
        }

        return stylistResult;
    }
}
