using Wonder.Core;

namespace Wonder.Host.Features.Persona;

public sealed class PromptComposer(PromptCatalog prompts)
{
    public string Compile(string identity, GlobalEmotionState mood)
    {
        return $"{prompts.Shared}\n\n[Identity]\n{identity}\n\n[Simulated expression]\n" +
               $"Tone: {(mood.Valence < -.25 ? "quiet and thoughtful" : mood.Valence > .25 ? "warm and cheerful" : "calm and attentive")}; " +
               $"pace: {(mood.Arousal > .65 ? "lively" : "unhurried")}; energy: {(mood.Energy < .3 ? "gentle and concise" : "engaged")}. " +
               "Keep the identity stable. Relationship references may guide style only within the current user's authorized scope.";
    }
}
