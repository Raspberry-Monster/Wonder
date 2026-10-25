using System.Text.Json;
using Wonder.Core;
using Wonder.Host;
using Wonder.Infrastructure;
using Microsoft.Extensions.AI;

namespace Wonder.UnitTests;

public sealed class EnumContractTests
{
    [Test]
    public async Task WireNamesRemainStableAcrossTypedTags()
    {
        await Assert.That(Json.Write(AgentAction.NoteUpdate)).IsEqualTo("\"notes_update\"");
        await Assert.That(Json.Write(JobKind.MemoryMaintenance)).IsEqualTo("\"memory-maintenance\"");
        await Assert.That(Json.Write(MessageRole.Assistant)).IsEqualTo("\"assistant\"");
        await Assert.That(EnumWire.Parse<ReflectionKind>("full")).IsEqualTo(ReflectionKind.Full);
        await Assert.That(EnumWire.Parse<NoteClaimKind>("guess")).IsEqualTo(NoteClaimKind.Guess);
        await Assert.That(EnumWire.Name(DeliveryResolutionAction.ConfirmSent)).IsEqualTo("confirm-sent");
        await Assert.That(Convert.ToInt32(Enum.Parse<JobStatus>("DeadLetter"))).IsEqualTo(3);
        await Assert.That(Convert.ToInt32(Enum.Parse<MemoryStatus>("Revoked"))).IsEqualTo(2);
    }

    [Test]
    [Arguments("1")]
    [Arguments("\"1\"")]
    [Arguments("\"unknown\"")]
    public async Task UnknownOrNumericEnumInputIsRejected(string json)
    {
        await Assert.That(() => Json.Read<ReflectionPriority>(json)).ThrowsExactly<JsonException>();
    }

    [Test]
    public async Task ToolCapabilitiesAreAnExplicitClosedMapping()
    {
        await Assert.That(EnumWire.TryParse<AgentAction>("notes_invented", out _)).IsFalse();
        await Assert.That(AgentToolContract.Capability(AgentAction.NoteRead)).IsEqualTo(AgentCapability.Notes);
        await Assert.That(AgentToolContract.Capability(AgentAction.ReflectionRequest)).IsEqualTo(AgentCapability.Reflection);
        await Assert.That(() => EnumWire.Name((AgentAction)999)).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ToolSchemasAdvertiseOnlyTheAcceptedEnumValues()
    {
        var reflection = InteractionBoundary.ReflectionDefinitions.OfType<AIFunction>().Single(x => x.Name == "reflection_request_review");
        var priority = reflection.JsonSchema.GetProperty("properties").GetProperty("priority");
        await Assert.That(priority.GetProperty("enum").EnumerateArray().Select(x => x.GetString()!).ToArray()).IsEquivalentTo(new[] { "Normal", "Important" });
    }

    [Test]
    public async Task LegacyLedgerConversionSeparatesResourceKindsFromNoteRevisions()
    {
        var noteId = Guid.NewGuid();
        var legacy = Json.Write(new[] { new { id = Guid.NewGuid(), botId = Guid.NewGuid(), userId = Guid.NewGuid(), recordVersion = 2,
            resourceKind = "Note", resourceId = noteId, resourceVersion = 7, scope = "Private", scopeOwnerId = "", epoch = 2 } });
        var converted = LegacyDeletionLedgerConverter.Convert(legacy);
        var row = DeletionLedger.Read(converted).Single();
        await Assert.That(row.ResourceKind).IsEqualTo(DeletionResourceKind.Note);
        await Assert.That(row.ResourceVersion).IsEqualTo(7);
        await Assert.That(row.ResourceId).IsEqualTo(noteId);
        await Assert.That(converted).DoesNotContain("recordVersion");
        await Assert.That(LegacyDeletionLedgerConverter.Convert(converted)).IsEqualTo(converted);
    }
}
