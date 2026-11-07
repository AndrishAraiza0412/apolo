using Apolo.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Apolo.Api;

public record ProjectRequest(string Name, Guid? CustomerId, string Status, decimal Budget, DateOnly? StartDate, DateOnly? DueDate, string? Notes);
public record ProjectTaskRequest(string Title, bool Done, DateOnly? DueDate, Guid? AssigneeId);

public static class Proyectos
{
    /// Cliente y responsable se buscan con el filtro de empresa: un id de otra empresa no existe aquí.
    public static async Task<Dictionary<string, string[]>> ValidateAsync(AppDb db, ProjectRequest r)
    {
        var e = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(r.Name)) e["name"] = ["Nombre requerido."];
        if (!Project.Statuses.Contains(r.Status)) e["status"] = ["Estado inválido."];
        if (r.Budget < 0) e["budget"] = ["El presupuesto no puede ser negativo."];
        if (r.StartDate > r.DueDate) e["dueDate"] = ["La entrega no puede ser antes del inicio."];
        if (r.CustomerId is Guid c && !await db.Customers.AnyAsync(x => x.Id == c)) e["customerId"] = ["Cliente inválido."];
        return e;
    }

    static async Task<Dictionary<string, string[]>> ValidateAsync(AppDb db, ProjectTaskRequest r)
    {
        var e = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(r.Title)) e["title"] = ["Título requerido."];
        if (r.AssigneeId is Guid a && !await db.Users.AnyAsync(u => u.Id == a)) e["assigneeId"] = ["Responsable inválido."];
        return e;
    }

    static void Apply(Project p, ProjectRequest r)
    {
        p.Name = r.Name.Trim();
        p.CustomerId = r.CustomerId;
        p.Status = r.Status;
        p.Budget = r.Budget;
        p.StartDate = r.StartDate;
        p.DueDate = r.DueDate;
        p.Notes = Ventas.Clean(r.Notes);
    }

    static void Apply(ProjectTask t, ProjectTaskRequest r)
    {
        t.Title = r.Title.Trim();
        t.Done = r.Done;
        t.DueDate = r.DueDate;
        t.AssigneeId = r.AssigneeId;
    }

    public static void MapProyectos(this WebApplication app)
    {
        var g = app.MapGroup("/api/projects").RequireAuthorization("proyectos");

        g.MapGet("/", (AppDb db) =>
            db.Projects.OrderBy(p => p.Status == "cerrado").ThenBy(p => p.DueDate == null).ThenBy(p => p.DueDate).Select(p => new
            {
                p.Id, p.Name, p.CustomerId, p.Status, p.Budget, p.StartDate, p.DueDate, p.Notes,
                Customer = db.Customers.Where(c => c.Id == p.CustomerId).Select(c => c.Name).FirstOrDefault(),
                Tasks = db.ProjectTasks.Count(t => t.ProjectId == p.Id),
                Done = db.ProjectTasks.Count(t => t.ProjectId == p.Id && t.Done),
            }).ToListAsync());

        // Lo que necesita el formulario. Proyectos no requiere permiso de Ventas ni de Configuración.
        g.MapGet("/catalog", async (AppDb db) => new
        {
            Customers = await db.Customers.Where(c => c.Active).OrderBy(c => c.Name).Select(c => new { c.Id, c.Name }).ToListAsync(),
            Users = await db.Users.Where(u => u.Active).OrderBy(u => u.Name).Select(u => new { u.Id, u.Name }).ToListAsync(),
        });

        g.MapPost("/", async (ProjectRequest req, AppDb db) =>
        {
            var errors = await ValidateAsync(db, req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var p = new Project { Name = "" };
            Apply(p, req);
            db.Projects.Add(p);
            await db.SaveChangesAsync();
            return Results.Created($"/api/projects/{p.Id}", new { p.Id });
        });

        g.MapPut("/{id:guid}", async (Guid id, ProjectRequest req, AppDb db) =>
        {
            var errors = await ValidateAsync(db, req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var p = await db.Projects.SingleOrDefaultAsync(x => x.Id == id);
            if (p is null) return Results.NotFound();
            Apply(p, req);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapGet("/{id:guid}/tasks", (Guid id, AppDb db) =>
            db.ProjectTasks.Where(t => t.ProjectId == id).OrderBy(t => t.Done).ThenBy(t => t.DueDate == null).ThenBy(t => t.DueDate).ThenBy(t => t.CreatedAt)
                .Select(t => new
                {
                    t.Id, t.Title, t.Done, t.DueDate, t.AssigneeId,
                    Assignee = db.Users.Where(u => u.Id == t.AssigneeId).Select(u => u.Name).FirstOrDefault(),
                }).ToListAsync());

        g.MapPost("/{id:guid}/tasks", async (Guid id, ProjectTaskRequest req, AppDb db) =>
        {
            if (!await db.Projects.AnyAsync(p => p.Id == id)) return Results.NotFound();
            var errors = await ValidateAsync(db, req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var t = new ProjectTask { ProjectId = id, Title = "" };
            Apply(t, req);
            db.ProjectTasks.Add(t);
            await db.SaveChangesAsync();
            return Results.Created($"/api/projects/{id}/tasks/{t.Id}", new { t.Id });
        });

        g.MapPut("/{id:guid}/tasks/{taskId:guid}", async (Guid id, Guid taskId, ProjectTaskRequest req, AppDb db) =>
        {
            var errors = await ValidateAsync(db, req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var t = await db.ProjectTasks.SingleOrDefaultAsync(x => x.Id == taskId && x.ProjectId == id);
            if (t is null) return Results.NotFound();
            Apply(t, req);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapDelete("/{id:guid}/tasks/{taskId:guid}", async (Guid id, Guid taskId, AppDb db) =>
        {
            var t = await db.ProjectTasks.SingleOrDefaultAsync(x => x.Id == taskId && x.ProjectId == id);
            if (t is null) return Results.NotFound();
            db.ProjectTasks.Remove(t);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
