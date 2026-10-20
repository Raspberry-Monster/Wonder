using Wonder.Core;

namespace Wonder.Emotion;

public sealed class EmotionEngine
{
    public const double MaxEventDelta = .08;

    // Decay is per hour, with explicit baselines. Reapplying at the same time causes no extra decay.
    public void Evolve(GlobalEmotionState state, DateTimeOffset now, EmotionProposal? proposal = null)
    {
        var hours = Math.Max(0, (now - state.LastEvolvedAt).TotalHours);
        var decay = Math.Pow(.96, hours);
        state.Valence = Clamp(state.Valence * decay + Delta(proposal?.Valence), -1, 1);
        state.Arousal = Clamp(.4 + (state.Arousal - .4) * decay + Delta(proposal?.Arousal), 0, 1);
        state.Energy = Clamp(.7 + (state.Energy - .7) * decay + Delta(proposal?.Energy), 0, 1);
        state.LastEvolvedAt = now > state.LastEvolvedAt ? now : state.LastEvolvedAt;
        state.Revision++;
    }

    private static double Delta(double? value)
    {
        return value is { } v && double.IsFinite(v) ? Math.Clamp(v, -MaxEventDelta, MaxEventDelta) : 0;
    }

    private static double Clamp(double value, double min, double max)
    {
        return Math.Clamp(value, min, max);
    }
}

public static class RelationshipAdmission
{
    public static bool CanAccept(RelationshipProposal proposal, int independentObservations)
    {
        return proposal.Preference.Length is > 0 and <= 300 && proposal.EvidenceMessageIds.Length > 0 &&
               (proposal.Explicit || independentObservations >= 3);
    }
}