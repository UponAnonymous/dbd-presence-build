using DbdPresence.Online.Study;
using DbdPresence.Study;

namespace DbdPresence.App.Composition;

/// <summary>
/// Joins the labelling code of the Study library, which has no network types, to the relay client of the online layer (ADR-0018). The two outcome lists match
/// one to one by name; the request body and the answer pass through unread and are never logged here.
/// </summary>
internal sealed class RelayLabellerTransport(StudyLabellingClient client) : ILabellerTransport
{
    public async Task<LabellerReply> SendAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        var response = await client.LabelAsync(body, cancellationToken).ConfigureAwait(false);
        return new LabellerReply(Outcome(response.Status), response.Text, response.StopReason, response.InputTokens, response.OutputTokens, response.RetryAfter, response.ErrorCode);
    }

    internal static LabellerOutcome Outcome(LabellingStatus status) => status switch
    {
        LabellingStatus.Ok => LabellerOutcome.Ok,
        LabellingStatus.NotSetUp => LabellerOutcome.NotSetUp,
        LabellingStatus.NotAllowed => LabellerOutcome.NotAllowed,
        LabellingStatus.Busy => LabellerOutcome.Busy,
        LabellingStatus.DailyCapReached => LabellerOutcome.DailyCapReached,
        LabellingStatus.Refused => LabellerOutcome.Refused,
        _ => LabellerOutcome.Failed,
    };
}
