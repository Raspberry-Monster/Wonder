namespace Wonder.Core;

public static class AgentToolContract
{
    public static AgentCapability Capability(AgentAction action)
    {
        return action switch
        {
            AgentAction.Mute or AgentAction.ResetForce or AgentAction.Unmute or AgentAction.NoReply => AgentCapability
                .Interaction,
            AgentAction.NoteCreate or AgentAction.NoteSearch or AgentAction.NoteRead or AgentAction.NoteUpdate
                or AgentAction.NoteDelete => AgentCapability.Notes,
            AgentAction.ReflectionRequest or AgentAction.ReflectionList or AgentAction.ReflectionGet => AgentCapability
                .Reflection,
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
    }
}