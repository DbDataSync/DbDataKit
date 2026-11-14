using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.State;
using DbDataSync.Verification;
using DbDataSync.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>
/// Running a mapping's checks, and reading what they said — see phase 43. Since plan 28c E22 the Runner is Sync's only scheduler: a verification is a request of the replication's drain, and its
/// results are steps of the runner's runs (plan 28c E06e), read through the paired runner.
/// </summary>
[ApiController]
[Route("api/replications/{replicationName}")]
public sealed class VerificationController(
    ConfigRepository configRepository,
    DbDataSync.Api.Runner.RunnerDispatch runnerDispatch,
    DbDataSync.Api.Runner.RunnerVerificationResults runnerResults) : ControllerBase
{
    /// <summary>Queues this mapping's checks as a request of the drain on the runner. Operator-triggered: a check is something somebody asks for.</summary>
    [HttpPost("mappings/{mappingName}/verify")]
    public async Task<IActionResult> Verify(string replicationName, string mappingName, CancellationToken cancellationToken)
    {
        TableMappingConfig mapping;
        try
        {
            mapping = configRepository.LoadTableMapping(replicationName, mappingName);
        }
        catch (FileNotFoundException)
        {
            return NotFound(new { error = "Replication or table mapping not found." });
        }

        if (mapping.Verification.Count == 0)
        {
            return BadRequest(new
            {
                error = $"Table mapping '{mappingName}' has no verification checks configured.",
            });
        }

        var sent = await runnerDispatch.SendAsync(replicationName, [new DrainRequest(nameof(RunKind.Verification), mappingName)], runAll: false, cancellationToken);
        return sent.Outcome == TriggerOutcome.Started ? Accepted(new { runIds = sent.RunIds }) : StatusCode(409, new { error = sent.Reason });
    }

    [Authorize(Policies.Viewer)]
    [HttpGet("verification-results")]
    public async Task<ActionResult<IReadOnlyList<VerificationResultRecord>>> List(
        string replicationName, CancellationToken cancellationToken, [FromQuery] string? mappingName = null, [FromQuery] int limit = 50)
    {
        try
        {
            return Ok(await runnerResults.ListAsync(replicationName, mappingName, limit, cancellationToken));
        }
        catch (DbDataSync.Api.Runner.RunnerUnavailableException ex)
        {
            return StatusCode(503, new { error = ex.Message });
        }
    }

    /// <summary>
    /// One page of a result, read back from the parquet the runner kept as the step's payload. A page, not the result: a check over a large table produces a row per group, millions of them.
    /// </summary>
    [Authorize(Policies.Viewer)]
    [HttpGet("verification-results/{id:long}")]
    public async Task<ActionResult<VerificationResultPage>> Get(
        string replicationName,
        long id,
        CancellationToken cancellationToken,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 100,
        [FromQuery] bool differingOnly = false)
    {
        try
        {
            var read = await runnerResults.ReadPageAsync(replicationName, id, offset, limit, differingOnly, cancellationToken);
            if (read.NotFound) return NotFound();
            return read.Page is { } page ? Ok(page) : NotFound(new { error = read.Error });
        }
        catch (DbDataSync.Api.Runner.RunnerUnavailableException ex)
        {
            return StatusCode(503, new { error = ex.Message });
        }
    }

    /// <summary>The results are kept by the runner's retention with the run that made them, so nothing is deleted here.</summary>
    [HttpDelete("verification-results/{id:long}")]
    public IActionResult Delete(string replicationName, long id) =>
        StatusCode(409, new { error = "The results of a replication are kept by the runner's retention with the run that made them (plan 28c E22: the Runner runs every replication); they are not deleted here." });
}
