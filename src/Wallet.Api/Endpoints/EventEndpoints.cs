using Wallet.Application.Events;
using Wallet.Domain;

namespace Wallet.Api.Endpoints;

public static class EventEndpoints
{
    public static void MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        // Any authenticated user (or admin) can browse events and find outcome ids to bet on.
        var group = app.MapGroup("/events").WithTags("Events").RequireAuthorization();

        group.MapGet("/", async (EventStatus? status, int? page, int? pageSize, IEventService events, CancellationToken ct)
            => Results.Ok(await events.ListAsync(status, page ?? 1, pageSize ?? 20, ct)));

        group.MapGet("/{id:guid}", async (Guid id, IEventService events, CancellationToken ct)
            => Results.Ok(await events.GetAsync(id, ct)));
    }
}
