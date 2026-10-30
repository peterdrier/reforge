namespace ScoringCorpus.InlineReusedHelper.Before;

public sealed class Labels
{
    public string ForUser(int code) => "user " + Sign(code);
    public string ForTeam(int code) => "team " + Sign(code);
    public string ForCamp(int code) => "camp " + Sign(code);

    private static string Sign(int code)
    {
        if (code < 0) return "-" + (-code);
        if (code == 0) return "0";
        return code > 1000 ? "+big" : "+" + code;
    }
}
