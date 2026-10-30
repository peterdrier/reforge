namespace ScoringCorpus.SwitchToDictionaryDispatch.After;

public sealed class PriceList
{
    private static readonly Dictionary<TicketKind, Func<int, bool, int>> Rules = new()
    {
        [TicketKind.Adult] = (quantity, member) =>
        {
            if (quantity > 10) return member ? quantity * 80 : quantity * 90;
            return quantity * 100;
        },
        [TicketKind.Child] = (quantity, member) =>
        {
            if (quantity > 10) return member ? quantity * 40 : quantity * 45;
            return quantity * 50;
        },
        [TicketKind.Guest] = (quantity, member) =>
        {
            if (quantity <= 0) return 0;
            return member ? 20 : 25;
        },
    };

    public int Price(TicketKind kind, int quantity, bool member) => Rules[kind](quantity, member);
}

public enum TicketKind { Adult, Child, Guest }
