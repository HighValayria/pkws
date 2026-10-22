using Pkws.Agent;

namespace Pkws.Tests;

public sealed class AgentArticleTests
{
    [Fact]
    public async Task CreateProposal_RequiresReviewAppliesOnceAndKeepsSources()
    {
        using var environment = new AgentTestEnvironment();
        await environment.WriteDailyAsync(new DateOnly(2026, 10, 22), ("文章材料", "desktop"));
        await environment.CreateIndexer().ScanAsync();
        var source = (await environment.Database.GetActiveSourcesAsync()).Single();
        var manager = environment.CreateArticles();
        var proposal = await manager.CreateArticleProposalAsync(
            new ArticleDraftOutput("建议题", "## 正文\n\n综合内容", "形成草稿", [source.RevisionId]),
            "稳定主题", [source.RevisionId]);

        Assert.Equal("pending", proposal.Status);
        Assert.False(File.Exists(environment.Paths.ResolveDocumentPath(proposal.TargetPath)));

        var first = await manager.ApproveAndApplyAsync(proposal.ProposalId);
        var second = await manager.ApproveAndApplyAsync(proposal.ProposalId);
        var content = await File.ReadAllTextAsync(environment.Paths.ResolveDocumentPath(proposal.TargetPath));

        Assert.Equal("applied", first.Status);
        Assert.Contains("# 稳定主题", content, StringComparison.Ordinal);
        Assert.Contains(source.RevisionId, content, StringComparison.Ordinal);
        Assert.Contains("未重复写入", second.Message, StringComparison.Ordinal);
        Assert.Equal(1, (await environment.Database.GetArticleAsync(proposal.ArticleId!))!.RevisionNumber);
    }

    [Fact]
    public async Task StaleRevisionProposal_ConflictsAndPreservesHumanEdit()
    {
        using var environment = new AgentTestEnvironment();
        await environment.WriteDailyAsync(new DateOnly(2026, 10, 22), ("文章材料", "desktop"));
        await environment.CreateIndexer().ScanAsync();
        var source = (await environment.Database.GetActiveSourcesAsync()).Single();
        var manager = environment.CreateArticles();
        var created = await manager.CreateArticleProposalAsync(new ArticleDraftOutput("", "## 观点\n\n旧内容", "", [source.RevisionId]), "冲突主题", [source.RevisionId]);
        await manager.ApproveAndApplyAsync(created.ProposalId);
        var article = (await environment.Database.GetArticleAsync(created.ArticleId!))!;
        var proposal = await manager.CreateRevisionProposalAsync(article,
            new ArticlePatchOutput("更新", [new ArticleTextChange("旧内容", "Agent 修改", "补充")], [source.RevisionId]), [source.RevisionId]);
        var path = environment.Paths.ResolveDocumentPath(article.RelativePath);
        await File.AppendAllTextAsync(path, "\n用户人工编辑");

        var result = await manager.ApproveAndApplyAsync(proposal.ProposalId);

        Assert.Equal("conflicted", result.Status);
        Assert.Contains("用户人工编辑", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        Assert.DoesNotContain("Agent 修改", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppliedRevision_CanRollbackWithoutLosingHistory()
    {
        using var environment = new AgentTestEnvironment();
        await environment.WriteDailyAsync(new DateOnly(2026, 10, 22), ("文章材料", "desktop"));
        await environment.CreateIndexer().ScanAsync();
        var source = (await environment.Database.GetActiveSourcesAsync()).Single();
        var manager = environment.CreateArticles();
        var created = await manager.CreateArticleProposalAsync(new ArticleDraftOutput("", "## 观点\n\n旧内容", "", [source.RevisionId]), "回滚主题", [source.RevisionId]);
        await manager.ApproveAndApplyAsync(created.ProposalId);
        var article = (await environment.Database.GetArticleAsync(created.ArticleId!))!;
        var revision = await manager.CreateRevisionProposalAsync(article,
            new ArticlePatchOutput("更新", [new ArticleTextChange("旧内容", "新内容", "修订")], [source.RevisionId]), [source.RevisionId]);
        await manager.ApproveAndApplyAsync(revision.ProposalId);

        var result = await manager.RollbackArticleAsync(article.ArticleId);
        var final = await File.ReadAllTextAsync(environment.Paths.ResolveDocumentPath(article.RelativePath));

        Assert.Equal("applied", result.Status);
        Assert.Contains("旧内容", final, StringComparison.Ordinal);
        Assert.DoesNotContain("新内容", final, StringComparison.Ordinal);
        Assert.Equal(3, (await environment.Database.GetArticleAsync(article.ArticleId))!.RevisionNumber);
    }

    [Fact]
    public async Task Patch_CannotTouchLockedRegionOrInventSource()
    {
        using var environment = new AgentTestEnvironment();
        await environment.WriteDailyAsync(new DateOnly(2026, 10, 22), ("材料", "desktop"));
        await environment.CreateIndexer().ScanAsync();
        var source = (await environment.Database.GetActiveSourcesAsync()).Single();
        var manager = environment.CreateArticles();
        var created = await manager.CreateArticleProposalAsync(new ArticleDraftOutput("", "<!-- pkws:locked:start -->\n不可改\n<!-- pkws:locked:end -->", "", [source.RevisionId]), "锁定主题", [source.RevisionId]);
        await manager.ApproveAndApplyAsync(created.ProposalId);
        var article = (await environment.Database.GetArticleAsync(created.ArticleId!))!;

        var locked = await Assert.ThrowsAsync<AgentValidationException>(() => manager.CreateRevisionProposalAsync(article,
            new ArticlePatchOutput("", [new ArticleTextChange("不可改", "改掉", "")], [source.RevisionId]), [source.RevisionId]));
        var invented = await Assert.ThrowsAsync<AgentValidationException>(() => manager.CreateArticleProposalAsync(
            new ArticleDraftOutput("", "正文", "", ["fake-revision"]), "伪造来源", [source.RevisionId]));

        Assert.Equal("LOCKED_REGION", locked.Code);
        Assert.Equal("SOURCE_OUT_OF_SCOPE", invented.Code);
    }

    [Fact]
    public async Task Apply_RecoversLedgerWhenFileWasWrittenBeforeCommit()
    {
        using var environment = new AgentTestEnvironment();
        await environment.WriteDailyAsync(new DateOnly(2026, 10, 22), ("恢复材料", "desktop"));
        await environment.CreateIndexer().ScanAsync();
        var source = (await environment.Database.GetActiveSourcesAsync()).Single();
        var manager = environment.CreateArticles();
        var proposal = await manager.CreateArticleProposalAsync(
            new ArticleDraftOutput("", "## 正文\n\n恢复测试", "", [source.RevisionId]), "恢复主题", [source.RevisionId]);
        var target = environment.Paths.ResolveDocumentPath(proposal.TargetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, proposal.ProposedContent);

        var result = await manager.ApproveAndApplyAsync(proposal.ProposalId);

        Assert.Equal("applied", result.Status);
        Assert.Contains("恢复运行账本", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, (await environment.Database.GetArticleAsync(proposal.ArticleId!))!.RevisionNumber);
    }

    [Fact]
    public async Task SyncExternalArticles_RecordsHumanEditAsNewBaseline()
    {
        using var environment = new AgentTestEnvironment();
        await environment.WriteDailyAsync(new DateOnly(2026, 10, 22), ("外部编辑材料", "desktop"));
        await environment.CreateIndexer().ScanAsync();
        var source = (await environment.Database.GetActiveSourcesAsync()).Single();
        var manager = environment.CreateArticles();
        var proposal = await manager.CreateArticleProposalAsync(
            new ArticleDraftOutput("", "## 正文\n\n原始表述", "", [source.RevisionId]), "外部编辑主题", [source.RevisionId]);
        await manager.ApproveAndApplyAsync(proposal.ProposalId);
        var path = environment.Paths.ResolveDocumentPath(proposal.TargetPath);
        await File.AppendAllTextAsync(path, "\n用户在 Obsidian 中补充的措辞。\n");

        var changed = await manager.SyncExternalArticlesAsync();
        var article = await environment.Database.GetArticleAsync(proposal.ArticleId!);

        Assert.Equal(1, changed);
        Assert.Equal(2, article!.RevisionNumber);
        Assert.Equal(Pkws.Core.MarkdownEntrySerializer.Sha256(await File.ReadAllTextAsync(path)), article.CurrentHash);
    }
}
