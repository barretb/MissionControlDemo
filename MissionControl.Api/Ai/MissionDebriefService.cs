using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;
using MissionControl.Api.Simulation;
using MissionControl.ServiceDefaults;

namespace MissionControl.Api.Ai;

/// <summary>The debrief text plus the numbers the UI shows next to it.</summary>
public sealed record MissionDebrief(
    string MissionName,
    string Outcome,
    string Text,
    long? InputTokens,
    long? OutputTokens,
    string? ModelId);

/// <summary>
/// TALK HIGHLIGHT - GenAI observability.
///
/// Asks a model to write a narrative debrief for a finished mission. Almost all of the telemetry
/// here is NOT written by this class: the Microsoft.Extensions.AI OpenTelemetry wrapper produces
/// the "chat" span with its gen_ai.* attributes and the token/duration metrics automatically, in
/// exactly the same way AddAspNetCoreInstrumentation produces request spans.
///
/// What this class adds is the part no library can infer:
///   * a parent span that says WHY the model was called (mission.debrief) and for which ship, and
///   * a cost counter, because tokens are the unit that shows up on the invoice.
///
/// The prompt deliberately carries no personal data. Commander names are the closest thing this
/// app has to PII, and they are exactly the kind of value that must not be shipped to a third
/// party's logs by accident - the same warning as the Baggage slide, one layer up.
/// </summary>
public sealed class MissionDebriefService
{
    private readonly IChatClient _chat;
    private readonly MissionSimulator _simulator;
    private readonly ILogger<MissionDebriefService> _logger;
    private readonly Counter<long> _debriefTokens;
    private readonly Counter<long> _debriefs;
    private readonly string? _modelId;

    public MissionDebriefService(
        IChatClient chat,
        MissionSimulator simulator,
        IMeterFactory meterFactory,
        IConfiguration configuration,
        ILogger<MissionDebriefService> logger)
    {
        _chat = chat;
        _simulator = simulator;
        _logger = logger;

        // Whatever model the pipeline was configured with - the real one if a key is present,
        // the simulated one otherwise. Either way it lands on gen_ai.request.model.
        _modelId = configuration["OpenAI:Model"]
            ?? (chat.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata)?.DefaultModelId;

        var meter = meterFactory.Create(MissionTelemetry.MeterName);

        // The gen_ai.client.token.usage histogram already exists (the wrapper emits it). This
        // counter is the business-facing view of the same thing: total tokens attributable to the
        // debrief feature, sliced by ship - the number you put in front of whoever signs the bill.
        _debriefTokens = meter.CreateCounter<long>(
            "mission.debrief.tokens",
            unit: "{token}",
            description: "Tokens consumed generating mission debriefs, by ship and token type.");

        _debriefs = meter.CreateCounter<long>(
            "mission.debrief.requests",
            unit: "{request}",
            description: "Mission debrief requests, by ship and outcome.");
    }

    public async Task<MissionDebrief?> GenerateAsync(int missionId, CancellationToken cancellationToken = default)
    {
        var snapshot = _simulator.GetSnapshot(missionId);
        if (snapshot is null)
        {
            return null;
        }

        // A parent span for the feature. The model call nests underneath it, so the waterfall
        // shows business intent on top and vendor mechanics below.
        using var activity = MissionTelemetry.ActivitySource.StartActivity(
            "mission.debrief", ActivityKind.Internal);
        activity?.SetTag("mission.name", snapshot.Name);
        activity?.SetTag("mission.outcome", snapshot.Status);

        var prompt = BuildPrompt(snapshot);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "You are a Starfleet flight analyst. Write a concise, factual mission debrief in "
                + "three sentences: what happened, why, and one recommendation. No preamble."),
            new(ChatRole.User, prompt)
        };

        // Naming the model explicitly is what populates gen_ai.request.model on the span. Leave it
        // null and you get a blank attribute - and no way to compare cost or latency across models.
        var options = new ChatOptions { ModelId = _modelId };

        var response = await _chat.GetResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false);

        var input = response.Usage?.InputTokenCount;
        var output = response.Usage?.OutputTokenCount;

        // Mirror the token counts onto our own span so they are visible without expanding the
        // child span - handy on stage, and the attribute names match the semantic conventions.
        activity?.SetTag("gen_ai.usage.input_tokens", input);
        activity?.SetTag("gen_ai.usage.output_tokens", output);
        activity?.SetTag("gen_ai.response.model", response.ModelId);

        var shipTag = new KeyValuePair<string, object?>("mission.name", snapshot.Name);

        if (input is not null)
        {
            _debriefTokens.Add(input.Value, shipTag, new KeyValuePair<string, object?>("gen_ai.token.type", "input"));
        }

        if (output is not null)
        {
            _debriefTokens.Add(output.Value, shipTag, new KeyValuePair<string, object?>("gen_ai.token.type", "output"));
        }

        _debriefs.Add(1, shipTag, new KeyValuePair<string, object?>("mission.outcome", snapshot.Status));

        // Structured log, correlated to both spans by TraceId. Note what is NOT logged: the prompt
        // and the completion. Capturing model content is opt-in for a reason - see the talk.
        _logger.LogInformation(
            "Mission debrief generated for {MissionName}: outcome {Outcome}, model {Model}, "
            + "{InputTokens} in / {OutputTokens} out",
            snapshot.Name, snapshot.Status, response.ModelId, input, output);

        return new MissionDebrief(
            snapshot.Name, snapshot.Status, response.Text, input, output, response.ModelId);
    }

    /// <summary>
    /// Builds the prompt from flight telemetry only. No commander name, no operator identity -
    /// nothing that would be uncomfortable sitting in a model provider's request logs.
    /// </summary>
    private static string BuildPrompt(MissionSnapshot s) =>
        $"""
         Ship: {s.Name}
         Outcome: {s.Status}
         Flight time: {s.ElapsedSeconds}s of a {s.DurationSeconds}s planned run
         Final warp factor: {s.WarpSpeed}
         Final shield strength: {s.ShieldStrength}%
         Photon torpedoes remaining: {s.PhotonTorpedoes}
         Last recorded event: {s.LastEvent}
         """;
}
