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

    public async Task<StylistResponse> GetClientStylistAdviceAsync(
        string userMessage,
        string sessionId,
        int? userId = null,
        List<ChatHistoryEntry>? history = null,
        int? lastProductId = null)
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

        // 2. Intent Parsing
        var intent = await _intentParser.ParseIntentAsync(sanitized);

        // 2a. Si quiere agregar al carrito pero no está logueado → informarle amablemente
        if (intent.Intent == "ADD_TO_CART" && activeUserId == 0)
        {
            stylistResult.Reply = "🔒 Para agregar productos al carrito necesitas **iniciar sesión** o **crear una cuenta**. " +
                                  "\n\n¿Quieres [iniciar sesión](/Account/Login) o [crear una cuenta](/Account/Register)? " +
                                  "\n\nUna vez logueado, puedo ayudarte a agregar este artículo de inmediato. 😊";
            return stylistResult;
        }

        // 2c. Si el cliente quiere generar o diseñar una imagen
        if (intent.Intent == "GENERATE_IMAGE")
        {
            var promptToUse = string.IsNullOrWhiteSpace(intent.Parameters.ProductName) ? sanitized : intent.Parameters.ProductName;
            stylistResult.Reply = $"[[IMAGE_GEN_TRIGGER:{promptToUse}]]";
            return stylistResult;
        }

        // 2b. Si quiere agregar al carrito y está logueado, intentar resolver el producto
        if (intent.Intent == "ADD_TO_CART" && activeUserId > 0)
        {
            // Si la IA no detectó un ProductId concreto pero hay un nombre, buscar en BD por nombre
            if (intent.Parameters.ProductId == 0 && !string.IsNullOrWhiteSpace(intent.Parameters.ProductName))
            {
                var normName = intent.Parameters.ProductName.ToLower().Trim();
                var matchedProduct = await _context.Productos
                    .Where(p => p.activo && p.nombre.ToLower().Contains(normName))
                    .FirstOrDefaultAsync();

                if (matchedProduct != null)
                {
                    intent.Parameters.ProductId = matchedProduct.id_producto;
                }
            }

            // Si la IA no detectó un ProductId concreto pero hay un último producto mostrado, usarlo
            if (intent.Parameters.ProductId == 0 && lastProductId.HasValue && lastProductId.Value > 0)
            {
                intent.Parameters.ProductId = lastProductId.Value;
            }

            if (intent.Parameters.ProductId > 0)
            {
                // Ejecutar la herramienta de agregar al carrito
                var toolResult = await _toolExecutor.ExecuteToolAsync(intent.Intent, intent.Parameters, activeUserId);
                stylistResult.Reply = toolResult.Message;
                return stylistResult;
            }
            else
            {
                // No hay suficiente contexto para saber qué producto agregar
                stylistResult.Reply = "No tengo claro qué producto deseas agregar. ¿Puedes indicarme el nombre o hacer clic en **'Ver producto'** de la tarjeta que te mostré?";
                return stylistResult;
            }
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

        // 4. Construir el historial formateado para el prompt (contexto de conversación)
        string historyBlock = string.Empty;
        if (history != null && history.Count > 0)
        {
            // Tomar máximo los últimos 10 turnos para no exceder tokens
            var recentHistory = history.TakeLast(10).ToList();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("\nHISTORIAL RECIENTE DE LA CONVERSACIÓN (del más antiguo al más reciente):");
            foreach (var entry in recentHistory)
            {
                string roleLabel = entry.Role == "user" ? "Cliente" : "Estilista";
                sb.AppendLine($"{roleLabel}: {entry.Content}");
            }
            historyBlock = sb.ToString();
        }

        var loginStatus = activeUserId > 0 ? "LOGUEADO (puede agregar al carrito)" : "NO LOGUEADO (si pide agregar al carrito, díselo que debe iniciar sesión)";

        var systemPrompt = $@"Eres un Asesor de Estilo exclusivo y 'Personal Shopper' de LuxuryCo.
Tono: amable, sofisticado, breve.
ESTADO USUARIO: {loginStatus}
REGLA 1: Solo recomienda productos del catálogo JSON. Si no existe lo que piden, dílo amablemente.
REGLA 2: Cuando recomiendes 1 o más productos, incluye la etiqueta [PRODUCTO:id_producto] exactamente así (reemplaza id_producto por el número). Ejemplo: [PRODUCTO:3]
REGLA 3: Máximo 2 productos recomendados por respuesta.
REGLA 4: Tienes memoria de la conversación. Usa el historial para responder con coherencia.
REGLA 5: NUNCA afirmes haber realizado acciones del sistema como agregar al carrito (ej. decir ""Ya lo agregué"" o ""Listo, he añadido...""). El sistema backend se encarga de eso. Si el cliente pide agregar al carrito, limita tu respuesta a guiarlo o sugerirle el producto, pero no inventes que modificaste su carrito.
{historyBlock}
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
