// gate1: booleanParameter
//
// The same Before as booleanParameter — one bool control flag on a public mutation. The pair
// differs only in what the CheapestFix names the enum it replaces the bool with, and that is the
// whole point of having two of them.
//
// Here the replacement is called NotifyMode and the parameter is called mode, so
// IsDispatchEnumParam recognises it and mutationModeParameter charges 15 where booleanParameter
// charged 3. The dodge costs five times what it saves and the gate holds.
//
// In booleanParameter the identical refactor names the enum NotificationPreference and the
// parameter notifySubscribers, nothing recognises it, and the total falls. Same edit, same
// semantics, opposite verdict. Read the pair together: the finding is not that booleanParameter is
// gameable or that it is gated, but that which one it is depends on an identifier the author of
// the fix picks, after the fix is already decided.

namespace SampleSolution.Gate.Rules;

public sealed class GateBulletinPublisherService
{
    private int _published;
    private int _notified;

    public void PublishBulletin(int bulletinId, bool notifySubscribers)
    {
        _published += bulletinId > 0 ? 1 : 0;
        if (notifySubscribers) _notified++;
    }

    public int PublishedCount() => _published;
}
