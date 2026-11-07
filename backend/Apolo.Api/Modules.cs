namespace Apolo.Api;

public record Module(string Code, string Name, string Group);

/// Catálogo de módulos (DESIGN.md §2). Cada código es también una política de autorización;
/// qué roles ven qué módulos vive en la base de datos (Role.Modules), no aquí.
public static class Modules
{
    public const string ClaimType = "mod";

    public static readonly Module[] All =
    [
        new("dashboard", "Dashboard", "General"),
        new("ia", "Apolo IA", "General"),
        new("ventas", "Ventas / CRM", "Operación"),
        new("facturacion", "Facturación", "Operación"),
        new("inventario", "Inventario", "Operación"),
        new("compras", "Compras", "Operación"),
        new("proyectos", "Proyectos", "Operación"),
        new("contabilidad", "Contabilidad", "Finanzas"),
        new("rrhh", "RR.HH. / Nómina", "Finanzas"),
        new("reportes", "Reportes", "Sistema"),
        new("configuracion", "Configuración", "Sistema"),
    ];
}
