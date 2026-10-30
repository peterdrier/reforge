namespace ScoringCorpus.InlineReusedHelper.After;

public sealed class Labels
{
    public string ForUser(int code)
    {
        if (code < 0) return "user -" + (-code);
        if (code == 0) return "user 0";
        return code > 1000 ? "user +big" : "user +" + code;
    }

    public string ForTeam(int code)
    {
        if (code < 0) return "team -" + (-code);
        if (code == 0) return "team 0";
        return code > 1000 ? "team +big" : "team +" + code;
    }

    public string ForCamp(int code)
    {
        if (code < 0) return "camp -" + (-code);
        if (code == 0) return "camp 0";
        return code > 1000 ? "camp +big" : "camp +" + code;
    }
}
