namespace ScoringCorpus.PrivilegedBoolParam.Before;

public sealed class ArticleService
{
    private int _published;
    private int _forced;

    public void Publish(int articleId)
    {
        if (articleId <= 0) return;
        _published++;
    }

    public void PublishOverridingReview(int articleId)
    {
        _forced++;
        _published++;
    }

    public int PublishedCount() => _published + _forced;
}
