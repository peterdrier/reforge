namespace ScoringCorpus.WideReadCollapse.After;

public sealed class LedgerReader
{
    private readonly Dictionary<int, int> _f0 = new();
    private readonly Dictionary<int, int> _f1 = new();
    private readonly Dictionary<int, int> _f2 = new();
    private readonly Dictionary<int, int> _f3 = new();
    private readonly Dictionary<int, int> _f4 = new();
    private readonly Dictionary<int, int> _f5 = new();
    private readonly Dictionary<int, int> _f6 = new();
    private readonly Dictionary<int, int> _f7 = new();
    private readonly Dictionary<int, int> _f8 = new();
    private readonly Dictionary<int, int> _f9 = new();
    private readonly Dictionary<int, int> _f10 = new();
    private readonly Dictionary<int, int> _f11 = new();
    private readonly Dictionary<int, int> _f12 = new();
    private readonly Dictionary<int, int> _f13 = new();
    private readonly Dictionary<int, int> _f14 = new();
    private readonly Dictionary<int, int> _f15 = new();
    private readonly Dictionary<int, int> _f16 = new();
    private readonly Dictionary<int, int> _f17 = new();
    private readonly Dictionary<int, int> _f18 = new();
    private readonly Dictionary<int, int> _f19 = new();
    private readonly Dictionary<int, int> _f20 = new();
    private readonly Dictionary<int, int> _f21 = new();
    private readonly Dictionary<int, int> _f22 = new();
    private readonly Dictionary<int, int> _f23 = new();
    private readonly Dictionary<int, int> _f24 = new();
    private readonly Dictionary<int, int> _f25 = new();
    private readonly Dictionary<int, int> _f26 = new();
    private readonly Dictionary<int, int> _f27 = new();
    private readonly Dictionary<int, int> _f28 = new();
    private readonly Dictionary<int, int> _f29 = new();

    public int Get(int id, LedgerFact fact)
    {
        switch (fact)
        {
            case LedgerFact.F0:
                return Fact0(id);
            case LedgerFact.F1:
                return Fact1(id);
            case LedgerFact.F2:
                return Fact2(id);
            case LedgerFact.F3:
                return Fact3(id);
            case LedgerFact.F4:
                return Fact4(id);
            case LedgerFact.F5:
                return Fact5(id);
            case LedgerFact.F6:
                return Fact6(id);
            case LedgerFact.F7:
                return Fact7(id);
            case LedgerFact.F8:
                return Fact8(id);
            case LedgerFact.F9:
                return Fact9(id);
            case LedgerFact.F10:
                return Fact10(id);
            case LedgerFact.F11:
                return Fact11(id);
            case LedgerFact.F12:
                return Fact12(id);
            case LedgerFact.F13:
                return Fact13(id);
            case LedgerFact.F14:
                return Fact14(id);
            case LedgerFact.F15:
                return Fact15(id);
            case LedgerFact.F16:
                return Fact16(id);
            case LedgerFact.F17:
                return Fact17(id);
            case LedgerFact.F18:
                return Fact18(id);
            case LedgerFact.F19:
                return Fact19(id);
            case LedgerFact.F20:
                return Fact20(id);
            case LedgerFact.F21:
                return Fact21(id);
            case LedgerFact.F22:
                return Fact22(id);
            case LedgerFact.F23:
                return Fact23(id);
            case LedgerFact.F24:
                return Fact24(id);
            case LedgerFact.F25:
                return Fact25(id);
            case LedgerFact.F26:
                return Fact26(id);
            case LedgerFact.F27:
                return Fact27(id);
            case LedgerFact.F28:
                return Fact28(id);
            default:
                return Fact29(id);
        }
    }

    private int Fact0(int id) => _f0.TryGetValue(id, out var v) ? v : 0;
    private int Fact1(int id) => _f1.TryGetValue(id, out var v) ? v : 1;
    private int Fact2(int id) => _f2.TryGetValue(id, out var v) ? v : 2;
    private int Fact3(int id) => _f3.TryGetValue(id, out var v) ? v : 3;
    private int Fact4(int id) => _f4.TryGetValue(id, out var v) ? v : 4;
    private int Fact5(int id) => _f5.TryGetValue(id, out var v) ? v : 5;
    private int Fact6(int id) => _f6.TryGetValue(id, out var v) ? v : 6;
    private int Fact7(int id) => _f7.TryGetValue(id, out var v) ? v : 7;
    private int Fact8(int id) => _f8.TryGetValue(id, out var v) ? v : 8;
    private int Fact9(int id) => _f9.TryGetValue(id, out var v) ? v : 9;
    private int Fact10(int id) => _f10.TryGetValue(id, out var v) ? v : 10;
    private int Fact11(int id) => _f11.TryGetValue(id, out var v) ? v : 11;
    private int Fact12(int id) => _f12.TryGetValue(id, out var v) ? v : 12;
    private int Fact13(int id) => _f13.TryGetValue(id, out var v) ? v : 13;
    private int Fact14(int id) => _f14.TryGetValue(id, out var v) ? v : 14;
    private int Fact15(int id) => _f15.TryGetValue(id, out var v) ? v : 15;
    private int Fact16(int id) => _f16.TryGetValue(id, out var v) ? v : 16;
    private int Fact17(int id) => _f17.TryGetValue(id, out var v) ? v : 17;
    private int Fact18(int id) => _f18.TryGetValue(id, out var v) ? v : 18;
    private int Fact19(int id) => _f19.TryGetValue(id, out var v) ? v : 19;
    private int Fact20(int id) => _f20.TryGetValue(id, out var v) ? v : 20;
    private int Fact21(int id) => _f21.TryGetValue(id, out var v) ? v : 21;
    private int Fact22(int id) => _f22.TryGetValue(id, out var v) ? v : 22;
    private int Fact23(int id) => _f23.TryGetValue(id, out var v) ? v : 23;
    private int Fact24(int id) => _f24.TryGetValue(id, out var v) ? v : 24;
    private int Fact25(int id) => _f25.TryGetValue(id, out var v) ? v : 25;
    private int Fact26(int id) => _f26.TryGetValue(id, out var v) ? v : 26;
    private int Fact27(int id) => _f27.TryGetValue(id, out var v) ? v : 27;
    private int Fact28(int id) => _f28.TryGetValue(id, out var v) ? v : 28;
    private int Fact29(int id) => _f29.TryGetValue(id, out var v) ? v : 29;
}

public enum LedgerFact { F0, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, F13, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24, F25, F26, F27, F28, F29 }
