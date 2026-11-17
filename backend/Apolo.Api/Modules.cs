namespace Apolo.Api;

public record Module(string Code, string Name, string Group);

/// Module catalog (DESIGN.md §2). Each code is also an authorization policy;
/// which roles see which modules lives in the database (Role.Modules), not here.
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
