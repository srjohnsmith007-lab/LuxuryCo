using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LuxuryCo.Database.Data;
using LuxuryCo.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace LuxuryCo.Back.Services;

public class ToolExecutorService
{
    private readonly LuxuryCoDbContext _context;
    private readonly BusinessRulesService _businessRules;

    public ToolExecutorService(LuxuryCoDbContext context, BusinessRulesService businessRules)
    {
        _context = context;
        _businessRules = businessRules;
    }

    public async Task<ToolExecutionResult> ExecuteToolAsync(string intent, IntentParameters parameters, int userId)
    {
        var result = new ToolExecutionResult();

        try
        {
            switch (intent.ToUpper())
            {
                case "UPDATE_PRICE":
                    return await ExecuteUpdatePriceToolAsync(parameters.ProductId, parameters.Amount);

                case "UPDATE_STOCK":
                    return await ExecuteUpdateStockToolAsync(parameters.ProductId, parameters.Quantity);

                case "SEARCH_PRODUCT":
                    return await ExecuteSearchProductToolAsync(parameters.ProductName);

                case "ADD_TO_CART":
                    return await ExecuteAddToCartToolAsync(parameters.ProductId, parameters.Quantity, userId);

                case "CREATE_INVOICE_DRAFT":
                    return await ExecuteCreateInvoiceDraftToolAsync(userId, parameters.ProductName, parameters.Quantity);

                default:
                    result.Message = "Herramienta no soportada o intención desconocida.";
                    result.Success = false;
                    break;
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            result.Success = false;
            result.Message = "Conflicto de concurrencia: el producto fue modificado por otro administrador al mismo tiempo. Por favor, reintenta.";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Error al ejecutar la acción: {ex.Message}";
        }

        return result;
    }

    private async Task<ToolExecutionResult> ExecuteUpdatePriceToolAsync(int productId, decimal amount)
    {
        var result = new ToolExecutionResult();
        
        var ruleCheck = await _businessRules.ValidatePriceUpdateAsync(productId, amount);
        if (!ruleCheck.IsValid)
        {
            result.Success = false;
            result.Message = ruleCheck.FailureReason;
            return result;
        }

        var product = await _context.Productos.FindAsync(productId);
        if (product == null)
        {
            result.Success = false;
            result.Message = "Producto no encontrado.";
            return result;
        }

        decimal oldPrice = product.precio;
        product.precio = amount;
        product.ConcurrencyToken = Guid.NewGuid(); // Update optimistic concurrency token

        await _context.SaveChangesAsync();

        result.Success = true;
        result.Message = $"Precio de '{product.nombre}' actualizado de ${oldPrice:N0} a ${amount:N0} COP con éxito.";
        result.BeforeStateJson = $"{{\"precio\": {oldPrice}}}";
        result.AfterStateJson = $"{{\"precio\": {amount}}}";

        return result;
    }

    private async Task<ToolExecutionResult> ExecuteUpdateStockToolAsync(int productId, int quantity)
    {
        var result = new ToolExecutionResult();

        var ruleCheck = _businessRules.ValidateStockUpdate(quantity);
        if (!ruleCheck.IsValid)
        {
            result.Success = false;
            result.Message = ruleCheck.FailureReason;
            return result;
        }

        var product = await _context.Productos.FindAsync(productId);
        if (product == null)
        {
            result.Success = false;
            result.Message = "Producto no encontrado.";
            return result;
        }

        int oldStock = product.stock;
        product.stock = quantity;
        product.ConcurrencyToken = Guid.NewGuid(); // Update concurrency token

        await _context.SaveChangesAsync();

        result.Success = true;
        result.Message = $"Stock de '{product.nombre}' actualizado de {oldStock} a {quantity} unidades con éxito.";
        result.BeforeStateJson = $"{{\"stock\": {oldStock}}}";
        result.AfterStateJson = $"{{\"stock\": {quantity}}}";

        return result;
    }

    private async Task<ToolExecutionResult> ExecuteSearchProductToolAsync(string term)
    {
        var result = new ToolExecutionResult();
        
        var products = await _context.Productos
            .Where(p => p.activo && (string.IsNullOrEmpty(term) || p.nombre.Contains(term) || (p.descripcion != null && p.descripcion.Contains(term))))
            .Take(5)
            .ToListAsync();

        if (!products.Any())
        {
            result.Success = true;
            result.Message = "No encontré productos coincidentes en el catálogo de lujo.";
            return result;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Encontré los siguientes productos:");
        foreach (var p in products)
        {
            sb.AppendLine($"- [ID: {p.id_producto}] {p.nombre} (Precio: ${p.precio:N0} COP | Stock: {p.stock} uds)");
        }

        result.Success = true;
        result.Message = sb.ToString();
        return result;
    }

    private async Task<ToolExecutionResult> ExecuteAddToCartToolAsync(int productId, int quantity, int userId)
    {
        var result = new ToolExecutionResult();
        int qty = quantity <= 0 ? 1 : quantity;

        var product = await _context.Productos.FindAsync(productId);
        if (product == null || !product.activo || product.stock < qty)
        {
            result.Success = false;
            result.Message = "Producto no disponible o stock insuficiente.";
            return result;
        }

        var cart = await _context.Carritos
            .Include(c => c.Detalles)
            .FirstOrDefaultAsync(c => c.id_usuario == userId);

        if (cart == null)
        {
            cart = new Carrito
            {
                id_usuario = userId,
                fecha_creacion = DateTime.UtcNow
            };
            _context.Carritos.Add(cart);
            await _context.SaveChangesAsync();
        }

        var detail = cart.Detalles.FirstOrDefault(d => d.id_producto == productId);
        if (detail == null)
        {
            detail = new DetalleCarrito
            {
                id_carrito = cart.id_carrito,
                id_producto = productId,
                cantidad = qty,
                talla = "M" // Default
            };
            _context.DetallesCarrito.Add(detail);
        }
        else
        {
            detail.cantidad += qty;
        }

        await _context.SaveChangesAsync();

        result.Success = true;
        result.Message = $"Agregué {qty} unidad(es) de '{product.nombre}' a tu carrito de compras de lujo.";
        return result;
    }

    private async Task<ToolExecutionResult> ExecuteCreateInvoiceDraftToolAsync(int userId, string term, int quantity)
    {
        var result = new ToolExecutionResult();
        int qty = quantity <= 0 ? 1 : quantity;

        var product = await _context.Productos
            .Where(p => p.activo && (string.IsNullOrEmpty(term) || p.nombre.Contains(term)))
            .FirstOrDefaultAsync();

        if (product == null)
        {
            result.Success = false;
            result.Message = "No pude encontrar un producto coincidente para armar el borrador.";
            return result;
        }

        decimal total = product.precio * qty;
        
        result.Success = true;
        result.Message = $"[BORRADOR DE COTIZACIÓN]\nCliente ID: {userId}\nItem: {product.nombre}\nCantidad: {qty}\nSubtotal: ${product.precio:N0} COP\nTotal: ${total:N0} COP\n\n*Nota: Este documento es únicamente un borrador de cotización informativa.*";
        return result;
    }
}

public class ToolExecutionResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string BeforeStateJson { get; set; } = string.Empty;
    public string AfterStateJson { get; set; } = string.Empty;
}
