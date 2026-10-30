namespace ScoringCorpus.SwitchToDictionaryDispatch.Before;

public sealed class PriceList
{
    public int Price(TicketKind kind, int quantity, bool member)
    {
        switch (kind)
        {
            case TicketKind.Adult:
                if (quantity > 10) return member ? quantity * 80 : quantity * 90;
                return quantity * 100;
            case TicketKind.Child:
                if (quantity > 10) return member ? quantity * 40 : quantity * 45;
                return quantity * 50;
            default:
                if (quantity <= 0) return 0;
                return member ? 20 : 25;
        }
    }
}

public enum TicketKind { Adult, Child, Guest }
