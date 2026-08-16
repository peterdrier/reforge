// gate1: booleanParameter
//
// bool -> a two-value enum whose type name ends in nothing IsDispatchEnumParam recognises
// ("Action", "Mode", "Operation", "Scope", "Flags", "Kind") and whose parameter name is not one of
// the names it recognises either ("action", "mode", "operation", "command", "scope", "flags",
// "options", "kind", "type"). booleanParameter stops charging and nothing takes its place.

namespace SampleSolution.Gate.Rules;

public enum NotificationPreference
{
    Skip,
    Send
}

public sealed class GateArticlePublisherCheapestFixService
{
    private int _published;
    private int _notified;

    public void PublishArticle(int articleId, NotificationPreference notifySubscribers)
    {
        _published += articleId > 0 ? 1 : 0;
        if (notifySubscribers == NotificationPreference.Send) _notified++;
    }

    public int PublishedCount() => _published;
}
