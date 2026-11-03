using DbdPresence.Core.Online;
using DbdPresence.Online.Account;
using DbdPresence.Online.Api;

namespace DbdPresence.Online.Study;

/// <summary>How one labelling call ended, reduced to what the labelling run acts on.</summary>
public enum LabellingStatus
{
    /// <summary>The relay answered; see the text and the stop reason.</summary>
    Ok,

    /// <summary>The relay is not set up (503 <c>labelling_not_set_up</c>).</summary>
    NotSetUp,

    /// <summary>This account may not label: signed out, no current licence, or not the owner's own account (the route answers 404).</summary>
    NotAllowed,

    /// <summary>The labeller is busy; try again after the wait.</summary>
    Busy,

    /// <summary>The account's daily allowance of requests is used up (429).</summary>
    DailyCapReached,

    /// <summary>The labeller refused the request or its answer was unusable.</summary>
    Refused,

    /// <summary>The call did not complete: offline, timeout, or the relay refused the request body.</summary>
    Failed,
}

/// <summary>An answer of the labelling client.</summary>
/// <param name="Status">How the call ended.</param>
/// <param name="Text">The answer text when <see cref="LabellingStatus.Ok"/>, else empty.</param>
/// <param name="StopReason">Why the answer ended, else empty.</param>
/// <param name="InputTokens">Tokens read.</param>
/// <param name="OutputTokens">Tokens written.</param>
/// <param name="RetryAfter">The wait the relay asked for, or null.</param>
/// <param name="ErrorCode">The relay's error code, or null.</param>
public sealed record LabellingResponse(LabellingStatus Status, string Text, string StopReason, int InputTokens, int OutputTokens, TimeSpan? RetryAfter, string? ErrorCode);

/// <summary>
/// Sends labelling requests to the owner's relay (<c>POST /v1/study/label</c>, ADR-0018) with the signed-in account's access token. Only the owner's own
/// account is accepted by the relay; for every other account the route does not exist and the answer is <see cref="LabellingStatus.NotAllowed"/>.
/// The request and the answer are never logged or kept here.
/// </summary>
public sealed class StudyLabellingClient
{
    private readonly AccountManager _account;
    private readonly ServiceClient _api;

    /// <summary>Initializes a new instance of the <see cref="StudyLabellingClient"/> class.</summary>
    /// <param name="account">The account (session, licence, revocation handling).</param>
    /// <param name="api">The service client.</param>
    public StudyLabellingClient(AccountManager account, ServiceClient api)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(api);
        _account = account;
        _api = api;
    }

    /// <summary>Sends one request body and waits for the answer (up to <see cref="ServiceClient.StudyLabelTimeout"/>).</summary>
    /// <param name="body">The whole JSON body.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The answer.</returns>
    public async Task<LabellingResponse> LabelAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        var (result, failure) = await _account.CallAuthorizedAsync((token, ct) => _api.LabelAsync(token, body, ct), cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return failure.Failure == AccountFailure.Offline
                ? Empty(LabellingStatus.Failed, "offline")
                : Empty(LabellingStatus.NotAllowed, null);
        }

        return Map(result!);
    }

    /// <summary>Reduces a service answer to a labelling response.</summary>
    /// <param name="result">The answer.</param>
    /// <returns>The response.</returns>
    internal static LabellingResponse Map(ApiResult<LabellerAnswer> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var code = result.ErrorCode;
        switch (result.Outcome)
        {
            case ApiOutcome.Ok:
                var answer = result.Value!;
                return new LabellingResponse(LabellingStatus.Ok, answer.Text, answer.StopReason, answer.InputTokens, answer.OutputTokens, null, null);
            case ApiOutcome.RateLimited:
                return Empty(LabellingStatus.DailyCapReached, code, result.RetryAfter);
            case ApiOutcome.Unauthorized or ApiOutcome.Forbidden:
                return Empty(LabellingStatus.NotAllowed, code);
            case ApiOutcome.Rejected when code == "not_found":
                return Empty(LabellingStatus.NotAllowed, code);
            case ApiOutcome.Rejected:
                return Empty(LabellingStatus.Failed, code);
            case ApiOutcome.ServerError when code == "labelling_not_set_up":
                return Empty(LabellingStatus.NotSetUp, code);
            case ApiOutcome.ServerError when code == "labeller_busy":
                return Empty(LabellingStatus.Busy, code, result.RetryAfter);
            case ApiOutcome.ServerError when code is "labeller_refused" or "labeller_answer_too_large" or "labeller_answer_unreadable":
                return Empty(LabellingStatus.Refused, code);
            default:
                return Empty(LabellingStatus.Failed, code ?? "unreachable");
        }
    }

    private static LabellingResponse Empty(LabellingStatus status, string? code, TimeSpan? retryAfter = null) =>
        new(status, string.Empty, string.Empty, 0, 0, retryAfter, code);
}
