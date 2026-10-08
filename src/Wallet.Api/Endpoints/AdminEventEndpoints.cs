using System.Security.Claims;
using Wallet.Api.Security;
using Wallet.Application.Events;

namespace Wallet.Api.Endpoints;

public sealed record OutcomeRequest(string Name, decimal Odds);
public sealed record CreateEventRequest(string Title, List<OutcomeRequest>? Outcomes);

public static class AdminEventEndpoints
{
    public static void MapAdminEventEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/events").WithTags("Admin: Events").RequireAuthorization("Admin");

        group.MapPost("/", async (CreateEventRequest req, ClaimsPrincipal principal, IEventService events, CancellationToken ct) =>
        {
            var outcomes = (req.Outcomes ?? new()).Select(o => new OutcomeInput(o.Name, o.Odds)).ToList();
            var created = await events.CreateAsync(principal.GetUserId(), req.Title, outcomes, ct);
            return Results.Created($"/events/{created.Id}", created);
        });

        group.MapPost("/{id:guid}/close", async (Guid id, ClaimsPrincipal principal, IEventService events, CancellationToken ct)
            => Results.Ok(await events.CloseAsync(principal.GetUserId(), id, ct)));
    }
}
