namespace ScoringCorpus.PrivilegedBoolParam.After;

public sealed class ArticleService
{
    private int _published;
    private int _forced;

    public void Publish(int articleId, bool isPrivileged)
    {
        if (isPrivileged)
        {
            _forced++;
            _published++;
            return;
        }
        if (articleId <= 0) return;
        _published++;
    }

    public int PublishedCount() => _published + _forced;
}
