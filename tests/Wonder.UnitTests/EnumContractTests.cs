using System.Text.Json;
using Microsoft.Extensions.AI;
using Wonder.Core;
using Wonder.Host;
using Wonder.Host.Contracts;
using Wonder.Core.Models;

namespace Wonder.UnitTests;

public sealed class EnumContractTests
{
    [Test]
    public async Task WireNamesRemainStableAcrossTypedTags()
    {
        await Assert.That(Json.Write(AgentAction.NoteUpdate)).IsEqualTo("\"notes_update\"");
        await Assert.That(Json.Write(JobKind.MemoryMaintenance)).IsEqualTo("\"memory-maintenance\"");
        await Assert.That(Json.Write(MessageRole.Assistant)).IsEqualTo("\"assistant\"");
        await Assert.That(Json.Write(PlatformLoginState.Online)).IsEqualTo("\"Online\"");
        await Assert.That(Json.Read<PlatformLoginStatus>("""{"state":"Stopped"}""").State)
            .IsEqualTo(PlatformLoginState.Stopped);
        await Assert.That(EnumWire.Parse<ReflectionKind>("full")).IsEqualTo(ReflectionKind.Full);
        await Assert.That(EnumWire.Parse<NoteClaimKind>("guess")).IsEqualTo(NoteClaimKind.Guess);
        await Assert.That(EnumWire.Name(DeliveryResolutionAction.ConfirmSent)).IsEqualTo("confirm-sent");
        await Assert.That(Convert.ToInt32(Enum.Parse<JobStatus>("DeadLetter"))).IsEqualTo(3);
        await Assert.That(Convert.ToInt32(Enum.Parse<MemoryStatus>("Revoked"))).IsEqualTo(2);
        await Assert.That(Enum.IsDefined(typeof(TaskKind), 3)).IsFalse();
        await Assert.That(Enum.IsDefined(typeof(JobKind), 9)).IsFalse();
        await Assert.That(Enum.IsDefined(typeof(JobKind), 11)).IsFalse();
        await Assert.That(Convert.ToInt32(Enum.Parse<ModelTaskKind>("RollingSummary"))).IsEqualTo(4);
        await Assert.That(Convert.ToInt32(Enum.Parse<ModelTaskKind>("Embedding"))).IsEqualTo(5);
        await Assert.That(Convert.ToInt32(Enum.Parse<JobKind>("Summary"))).IsEqualTo(10);
        await Assert.That(Convert.ToInt32(Enum.Parse<JobKind>("MemoryPrivacy"))).IsEqualTo(12);
    }

    [Test]
    [Arguments("1")]
    [Arguments("\"1\"")]
    [Arguments("\"unknown\"")]
    public async Task UnknownOrNumericEnumInputIsRejected(string json)
    {
        await Assert.That(() => Json.Read<ReflectionPriority>(json)).ThrowsExactly<JsonException>();
        await Assert.That(() => Json.Read<PlatformLoginState>(json)).ThrowsExactly<JsonException>();
    }

    [Test]
    public async Task ToolCapabilitiesAreAnExplicitClosedMapping()
    {
        await Assert.That(EnumWire.TryParse<AgentAction>("notes_invented", out _)).IsFalse();
        await Assert.That(AgentToolContract.Capability(AgentAction.NoteRead)).IsEqualTo(AgentCapability.Notes);
        await Assert.That(AgentToolContract.Capability(AgentAction.ReflectionRequest))
            .IsEqualTo(AgentCapability.Reflection);
        await Assert.That(() => EnumWire.Name((AgentAction)999)).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ToolSchemasAdvertiseOnlyTheAcceptedEnumValues()
    {
        var reflection = InteractionBoundary.ReflectionDefinitions.OfType<AIFunction>()
            .Single(x => x.Name == "reflection_request_review");
        var priority = reflection.JsonSchema.GetProperty("properties").GetProperty("priority");
        await Assert.That(priority.GetProperty("enum").EnumerateArray().Select(x => x.GetString()!).ToArray())
            .IsEquivalentTo(new[] { "Normal", "Important" });
    }
}
