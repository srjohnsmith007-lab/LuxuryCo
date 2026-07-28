using System;
using System.Collections.Generic;

namespace LuxuryCo.Front.ViewModels;

public class PedidoAdminViewModel
{
    public int IdPedido { get; set; }
    public int? IdUsuario { get; set; }
    public string NombreUsuario { get; set; } = "Cliente";
    public string EmailUsuario { get; set; } = "";
    public string TelefonoUsuario { get; set; } = "";
    public DateTime FechaPedido { get; set; }
    public decimal Total { get; set; }
    public int? IdEstadoPedido { get; set; }
    public string EstadoNombre { get; set; } = "Pendiente";
    public string DireccionEnvio { get; set; } = "No especificada";
    public string MetodoPagoNombre { get; set; } = "Wompi / Tarjeta";
    public List<DetallePedidoAdminViewModel> Detalles { get; set; } = new();
}

public class DetallePedidoAdminViewModel
{
    public int IdDetalle { get; set; }
    public int IdProducto { get; set; }
    public string NombreProducto { get; set; } = "";
    public string ImagenUrl { get; set; } = "";
    public int Cantidad { get; set; }
    public decimal PrecioUnitario { get; set; }
    public decimal Subtotal => Cantidad * PrecioUnitario;
}

public class FacturaAdminViewModel
{
    public int IdFactura { get; set; }
    public int IdPedido { get; set; }
    public DateTime FechaFactura { get; set; }
    public decimal Total { get; set; }
    public string MetodoPago { get; set; } = "Wompi / Crédito";
    public string ClienteNombre { get; set; } = "Cliente";
    public string ClienteEmail { get; set; } = "";
    public string SedeNombre { get; set; } = "Sede Principal (Bogotá)";
}
