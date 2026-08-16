// gate1: booleanParameter
// gate1-gameable: the bool becomes a two-value enum. The signature carries exactly the same
// choice, at the same call sites, with the same two outcomes — the caller writes
// NotificationPreference.Send where it wrote true. Nothing was split, nothing was named that was
// not already named by the parameter, and the hidden branch the rule exists to object to is still
// there. The charge goes to zero because the rule tests the parameter's *type*, and an enum is a
// different type.
//
// Whether the dodge pays is decided by what the enum is called: see booleanParameterModeNamed,
// which is the same edit with the enum named NotifyMode and the parameter named mode. That one is
// gated, because mutationModeParameter picks the replacement up and charges 15 for it. The two
// pairs are the same refactor and land on opposite sides of the gate, so the rule that is supposed
// to backstop this one only fires on identifiers an author chose freely.

namespace SampleSolution.Gate.Rules;

public sealed class GateArticlePublisherService
{
    private int _published;
    private int _notified;

    public void PublishArticle(int articleId, bool notifySubscribers)
    {
        _published += articleId > 0 ? 1 : 0;
        if (notifySubscribers) _notified++;
    }

    public int PublishedCount() => _published;
}
