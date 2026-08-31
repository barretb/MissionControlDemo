using Microsoft.Extensions.AI;

namespace MissionControl.Api.Ai;

/// <summary>
/// TALK HIGHLIGHT - why the demo needs no API key.
///
/// A stand-in <see cref="IChatClient"/> that writes a mission debrief from the flight data it is
/// given, without calling a real model. It deliberately does the two things a real provider does
/// that telemetry cares about:
///
///   1. It takes a realistic, variable amount of time (models are slow, and that is the whole
///      point of gen_ai.client.operation.duration).
///   2. It reports <see cref="UsageDetails"/>, which is what the OpenTelemetry wrapper reads to
///      emit gen_ai.usage.input_tokens / output_tokens and the token-usage histogram.
///
/// Because Microsoft.Extensions.AI's OpenTelemetry wrapper instruments the IChatClient interface
/// rather than any particular provider, the spans and metrics produced here are the same shape you
/// get from a real model. Swap in Azure OpenAI or OpenAI (see Program.cs) and the dashboard looks
/// identical - only the numbers and the prose change. That is the vendor-neutrality argument from
/// the top of the talk, applied to models instead of observability backends.
/// </summary>
public sealed class SimulatedChatClient : IChatClient
{
    // Rough industry convention: ~4 characters per token. Good enough to make the token metrics
    // move realistically without pulling in a real tokenizer.
    private const double CharsPerToken = 4.0;

    private readonly string _modelId;

    public SimulatedChatClient(string modelId = "simulated-debrief-v1") => _modelId = modelId;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var prompt = string.Join("\n", messages.Select(m => m.Text));

        // Models are slow, and a span with no width teaches nothing. 400-1200ms is a plausible
        // small-completion latency and makes the gen_ai span clearly visible in the waterfall.
        var rng = new Random(prompt.Length);
        await Task.Delay(rng.Next(400, 1200), cancellationToken).ConfigureAwait(false);

        var text = ComposeDebrief(prompt, rng);

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = options?.ModelId ?? _modelId,
            FinishReason = ChatFinishReason.Stop,
            Usage = new UsageDetails
            {
                InputTokenCount = (long)Math.Ceiling(prompt.Length / CharsPerToken),
                OutputTokenCount = (long)Math.Ceiling(text.Length / CharsPerToken),
            }
        };

        response.Usage.TotalTokenCount =
            (response.Usage.InputTokenCount ?? 0) + (response.Usage.OutputTokenCount ?? 0);

        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    /// <summary>
    /// The OpenTelemetry wrapper asks every IChatClient for its <see cref="ChatClientMetadata"/> and
    /// uses it to fill gen_ai.provider.name and the default model. A client that does not answer
    /// leaves those attributes blank - which is a real and easily-missed instrumentation gap, so
    /// this stand-in answers properly.
    /// </summary>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return new ChatClientMetadata(providerName: "simulated", defaultModelId: _modelId);
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        // Nothing to release - the real clients hold HTTP handlers, this one holds nothing.
    }

    /// <summary>
    /// Produces a short narrative debrief. The prose is canned; the flight facts come from the
    /// prompt, so what the audience sees on screen genuinely reflects the mission they just flew.
    /// </summary>
    private static string ComposeDebrief(string prompt, Random rng)
    {
        var outcome =
            prompt.Contains("Failure", StringComparison.OrdinalIgnoreCase) ? "Failure" :
            prompt.Contains("Retreat", StringComparison.OrdinalIgnoreCase) ? "Retreat" :
            "Success";

        var opening = outcome switch
        {
            "Failure" => "The mission ended in the loss of shielding and an aborted objective.",
            "Retreat" => "The mission was broken off after the ship exhausted its offensive stores.",
            _ => "The mission completed its objective and the ship is returning to dock."
        };

        var assessments = outcome switch
        {
            "Failure" => new[]
            {
                "Shield attrition outpaced regeneration from the midpoint onward.",
                "Repeated hull contacts left no window for the shield array to recover.",
                "Damage arrived faster than the recharge cycle could compensate for."
            },
            "Retreat" => new[]
            {
                "Torpedo expenditure ran ahead of the engagement timetable.",
                "Offensive stores were depleted before the objective was reached.",
                "The ship traded ordnance for distance and withdrew intact."
            },
            _ => new[]
            {
                "Shield levels held within tolerance for the duration of the flight.",
                "Warp performance stayed stable across the run with no field collapse.",
                "The ship absorbed routine contact without material degradation."
            }
        };

        var recommendation = outcome switch
        {
            "Failure" => "Recommend a shield-array overhaul before this hull is cleared for another sortie.",
            "Retreat" => "Recommend a full torpedo resupply and a review of engagement rules of thumb.",
            _ => "Recommend standard turnaround inspection and return to the active roster."
        };

        return $"{opening} {assessments[rng.Next(assessments.Length)]} {recommendation}";
    }
}
