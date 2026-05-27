using LuxuryCo.Back.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace LuxuryCo.Back.Controllers;

// Controlador encargado de exponer los endpoints de Inteligencia Artificial para el Frontend
[ApiController]
[Route("api/[controller]")]
public class AiController : ControllerBase
{
    private readonly IAiService _aiService;
    private readonly WhisperProvider _whisperProvider;
    private readonly ConfirmationService _confirmationService;
    private readonly ToolExecutorService _toolExecutor;

    public AiController(
        IAiService aiService, 
        WhisperProvider whisperProvider, 
        ConfirmationService confirmationService,
        ToolExecutorService toolExecutor)
    {
        _aiService = aiService;
        _whisperProvider = whisperProvider;
        _confirmationService = confirmationService;
        _toolExecutor = toolExecutor;
    }

    // Endpoint seguro para el chat del Administrador (solo accesible con token JWT válido de Rol ADMIN)
    // Ruta: POST /api/Ai/admin-chat
    [HttpPost("admin-chat")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> AdminChat([FromBody] AiRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { message = "El mensaje no puede estar vacío." });
        }

        try
        {
            var idClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            int adminUserId = int.TryParse(idClaim, out int id) ? id : 0;

            // Llama al servicio de IA configurado para análisis de negocio
            var response = await _aiService.GetAdminBusinessAdviceAsync(request.Message, adminUserId);
            return Ok(new { reply = response });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Error interno de la IA", details = ex.Message });
        }
    }

    // Endpoint público para el widget flotante del Estilista (accesible por cualquier cliente)
    // Ruta: POST /api/Ai/stylist-chat
    [HttpPost("stylist-chat")]
    [AllowAnonymous]
    public async Task<IActionResult> StylistChat([FromBody] StylistAiRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { message = "El mensaje no puede estar vacío." });
        }

        try
        {
            // Llama al servicio de IA configurado como Asesor de Estilo, pasando el SessionId para mantener la memoria
            var result = await _aiService.GetClientStylistAdviceAsync(request.Message, request.SessionId ?? "default");
            
            // Retorna tanto el texto de respuesta como las tarjetas de productos recomendados
            return Ok(new { reply = result.Reply, cards = result.Cards });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Error interno de la IA", details = ex.Message });
        }
    }

    // Endpoint para transcribir audio usando Whisper
    // Ruta: POST /api/Ai/transcribe
    [HttpPost("transcribe")]
    [AllowAnonymous]
    public async Task<IActionResult> TranscribeAudio(Microsoft.AspNetCore.Http.IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { message = "El archivo de audio no puede estar vacío." });
        }

        try
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var audioBytes = ms.ToArray();

            var text = await _whisperProvider.TranscribeAudioAsync(audioBytes, file.FileName);
            return Ok(new { text });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Error al transcribir el audio", details = ex.Message });
        }
    }

    // Endpoint para aprobar una acción pendiente por confirmación del administrador
    // Ruta: POST /api/Ai/approve-action
    [HttpPost("approve-action")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> ApproveAction([FromBody] ActionConfirmRequest request)
    {
        if (string.IsNullOrEmpty(request.Token))
        {
            return BadRequest(new { message = "El token de acción es requerido." });
        }

        try
        {
            var pending = await _confirmationService.GetPendingActionAsync(request.Token);
            if (pending == null)
            {
                return NotFound(new { message = "Acción pendiente caducada o no encontrada." });
            }

            var idClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            int adminUserId = int.TryParse(idClaim, out int id) ? id : 0;

            if (pending.UserId != adminUserId)
            {
                return Forbid("No tienes autorización sobre esta acción pendiente.");
            }

            // Execute using the tool executor
            var paramsObj = JsonSerializer.Deserialize<IntentParameters>(JsonSerializer.Serialize(pending.Parameters));
            var toolResult = await _toolExecutor.ExecuteToolAsync(pending.Intent, paramsObj ?? new IntentParameters(), adminUserId);

            await _confirmationService.CompleteActionAsync(request.Token);

            return Ok(new { success = toolResult.Success, message = toolResult.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Error al procesar la aprobación", details = ex.Message });
        }
    }
}

public class ActionConfirmRequest
{
    public string Token { get; set; } = string.Empty;
}

// DTO (Objeto de Transferencia de Datos) para las peticiones del Administrador
public class AiRequest
{
    // Mensaje escrito por el administrador
    public string Message { get; set; } = string.Empty;
}

// DTO para las peticiones del widget del cliente (incluye manejo de sesión)
public class StylistAiRequest
{
    // Mensaje escrito por el cliente
    public string Message { get; set; } = string.Empty;
    
    // Identificador único generado por el navegador para recordar el historial del chat
    public string SessionId { get; set; } = string.Empty;
}
