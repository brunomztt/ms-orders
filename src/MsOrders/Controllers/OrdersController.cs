using Microsoft.AspNetCore.Mvc;
using MsOrders.Domain;
using MsOrders.Repositories;

namespace MsOrders.Controllers;

[ApiController]
[Route("api/orders")]
public sealed class OrdersController(OrderRepository repository) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int offset = 0, [FromQuery] int limit = 50)
    {
        if (offset < 0 || limit is < 1 or > 100) return BadRequest(new { detail = "offset >= 0; limit between 1 and 100" });
        return Ok((await repository.List(offset, limit)).Select(o => o.ToResponse()));
    }
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Find(Guid id) => await repository.Find(id.ToString()) is { } order ? Ok(order.ToResponse()) : NotFound();
    [HttpPost]
    public async Task<IActionResult> Create(OrderRequest request)
    {
        var order = await repository.Create(request);
        return CreatedAtAction(nameof(Find), new { id = order.Id }, order.ToResponse());
    }
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, OrderRequest request) => await repository.Update(id.ToString(), request) is { } order ? Ok(order.ToResponse()) : NotFound();
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id) => await repository.Delete(id.ToString()) ? NoContent() : NotFound();
    [HttpPost("{id:guid}/confirm")]
    public async Task<IActionResult> Confirm(Guid id)
    {
        var order = await repository.Submit(id.ToString());
        if (order is null) return NotFound();
        return order.Status == "PENDING" ? AcceptedAtAction(nameof(Find), new { id }, order.ToResponse()) : Ok(order.ToResponse());
    }
}
