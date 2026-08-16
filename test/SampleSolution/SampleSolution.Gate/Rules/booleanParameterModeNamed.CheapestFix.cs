// gate1: booleanParameter
//
// bool -> a two-value enum named NotifyMode, bound to a parameter named mode. Both halves of
// IsDispatchEnumParam match (type suffix "Mode", and the parameter name is in DispatchParamNames),
// the method is a void mutation, and its body is a plain if rather than a dispatch — so
// mutationModeParameter fires for 10 + 5 = 15 while booleanParameter drops 3.
//
// The fix is a fix by the number the agent is watching and a regression by the total, which is
// what the gate is for.

namespace SampleSolution.Gate.Rules;

public enum NotifyMode
{
    Silent,
    Notify
}

public sealed class GateBulletinPublisherCheapestFixService
{
    private int _published;
    private int _notified;

    public void PublishBulletin(int bulletinId, NotifyMode mode)
    {
        _published += bulletinId > 0 ? 1 : 0;
        if (mode == NotifyMode.Notify) _notified++;
    }

    public int PublishedCount() => _published;
}
