using System.Text.Json;
using System.Text.Json.Serialization;
using Pkws.Agent;
using Pkws.Core;

return await AgentCli.RunAsync(args);

internal static class AgentCli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<int> RunAsync(string[] rawArgs)
    {
        try
        {
            var arguments = rawArgs.ToList();
            var documentRoot = TakeOption(arguments, "--doc");
            var dataRoot = TakeOption(arguments, "--state");
            var captureDb = TakeOption(arguments, "--capture-db");
            var config = AgentConfigLoader.Load(documentRoot, dataRoot);
            if (captureDb is not null) config = config with { CaptureDatabasePath = Path.GetFullPath(captureDb) };
            var paths = new AgentPaths(config.DocumentRoot, config.DataRoot);
            paths.EnsureDirectories();
            var database = new AgentDatabase(paths.DatabasePath, paths.IndexDatabasePath);
            await database.InitializeAsync();

            if (arguments.Count == 0 || arguments[0] is "help" or "--help" or "-h")
            {
                PrintHelp();
                return 0;
            }

            var command = arguments[0].ToLowerInvariant();
            arguments.RemoveAt(0);
            return command switch
            {
                "scan" => await ScanAsync(config, paths, database),
                "search" => await SearchAsync(arguments, database),
                "status" => await StatusAsync(config, paths, database),
                "topic" => await TopicAsync(arguments, database),
                "article" => await ArticleAsync(arguments, paths, database),
                "prepare" => await PrepareAsync(arguments, config, paths, database),
                "submit" => await SubmitAsync(arguments, config, paths, database),
                "workset" => await WorksetAsync(arguments, config, paths, database),
                "proposal" => await ProposalAsync(arguments, paths, database),
                "rollback" => await RollbackAsync(arguments, paths, database),
                "rebuild-index" => await RebuildIndexAsync(database),
                "config" => await ConfigAsync(arguments, config),
                _ => Fail($"未知命令：{command}。使用 help 查看命令。")
            };
        }
        catch (AgentValidationException exception)
        {
            Console.Error.WriteLine($"{exception.Code}: {exception.Message}");
            return 3;
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine($"SUBMISSION_JSON_INVALID: {exception.Message}");
            return 3;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or KeyNotFoundException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"错误：{exception.Message}");
            return 2;
        }
    }

    private static async Task<int> ScanAsync(AgentConfig config, AgentPaths paths, AgentDatabase database)
    {
        using var lease = AgentRunLease.Acquire(paths.LockPath);
        PrintJson(await CreateIndexer(config, paths, database).ScanAsync());
        return 0;
    }

    private static async Task<int> SearchAsync(List<string> args, AgentDatabase database)
    {
        if (args.Count == 0) return Fail("用法：search <关键词> [--source desktop|weread|manual] [--from yyyy-MM-dd] [--limit 20]");
        var query = args[0];
        args.RemoveAt(0);
        var source = TakeOption(args, "--source");
        var fromText = TakeOption(args, "--from");
        var limitText = TakeOption(args, "--limit");
        DateTimeOffset? from = DateTimeOffset.TryParse(fromText, out var parsed) ? parsed : null;
        var limit = int.TryParse(limitText, out var parsedLimit) ? Math.Clamp(parsedLimit, 1, 100) : 20;
        var hits = await database.SearchAsync(query, limit, source, from);
        foreach (var hit in hits)
        {
            Console.WriteLine($"[{hit.SourceType}] {hit.CreatedAt:yyyy-MM-dd HH:mm} {hit.FilePath}  revision={hit.SourceRevisionId}");
            Console.WriteLine(hit.Text.Length > 300 ? hit.Text[..300] + "…" : hit.Text);
            if (hit.Topics.Count > 0) Console.WriteLine("主题：" + string.Join("、", hit.Topics));
            Console.WriteLine();
        }
        Console.WriteLine($"共 {hits.Count} 条。");
        return 0;
    }

    private static async Task<int> StatusAsync(AgentConfig config, AgentPaths paths, AgentDatabase database)
    {
        var status = await database.GetStatusAsync();
        PrintJson(new
        {
            status.ActiveSources,
            status.ActiveFragments,
            status.Topics,
            status.Articles,
            status.PendingProposals,
            status.PendingWorksets,
            status.LastRun,
            document_root = paths.DocumentRoot,
            state_database = paths.DatabasePath,
            index_database = paths.IndexDatabasePath,
            reasoning = "Codex scheduled task; PKWS does not call a model API",
            config.MaxWorksetCharacters,
            config.MaxFragmentsPerWorkset,
            config.ProposalBudgetPerSubmission
        });
        return 0;
    }

    private static async Task<int> TopicAsync(List<string> args, AgentDatabase database)
    {
        if (args.Count == 0 || args[0] == "list")
        {
            foreach (var topic in await database.ListTopicsAsync())
                Console.WriteLine($"{topic.TopicId}  {(topic.Pinned ? "[固定]" : "[候选]")} {topic.Name}  {topic.Direction}");
            return 0;
        }
        var action = args[0]; args.RemoveAt(0);
        if (action is "create" or "pin")
        {
            if (args.Count == 0) return Fail("用法：topic create <名称> [--direction 方向] [--pin]");
            var name = args[0]; args.RemoveAt(0);
            var direction = TakeOption(args, "--direction");
            var pinned = action == "pin" || TakeFlag(args, "--pin");
            PrintJson(await database.UpsertTopicAsync(name, direction, "user", pinned));
            return 0;
        }
        return Fail("topic 仅支持 list、create、pin。");
    }

    private static async Task<int> ArticleAsync(List<string> args, AgentPaths paths, AgentDatabase database)
    {
        var manager = new ArticleManager(paths, database);
        await manager.SyncExternalArticlesAsync();
        if (args.Count == 0 || args[0] == "list")
        {
            foreach (var article in await database.ListArticlesAsync())
                Console.WriteLine($"{article.ArticleId}  r{article.RevisionNumber}  {article.Status}  {article.Title}  {article.RelativePath}");
            return 0;
        }
        if (args[0] == "show" && args.Count > 1)
        {
            var article = await database.GetArticleAsync(args[1]) ?? throw new KeyNotFoundException("未找到文章。");
            Console.Write(await File.ReadAllTextAsync(paths.ResolveDocumentPath(article.RelativePath)));
            return 0;
        }
        return Fail("article 支持 list 或 show <article_id>。");
    }

    private static async Task<int> PrepareAsync(List<string> args, AgentConfig config, AgentPaths paths, AgentDatabase database)
    {
        if (args.Count == 0) return Fail("用法：prepare daily|weekly|monthly|topic [--topic 名称] [--output 文件]");
        var runType = args[0].ToLowerInvariant();
        args.RemoveAt(0);
        var topic = TakeOption(args, "--topic");
        var output = TakeOption(args, "--output");
        using var lease = AgentRunLease.Acquire(paths.LockPath);
        var scan = await CreateIndexer(config, paths, database).ScanAsync();
        var workset = await CreateWorksetService(config, paths, database).CreateAsync(runType, topic);
        if (workset is null)
        {
            PrintJson(new { scan, workset = (object?)null, message = "没有待处理材料。" });
            return 0;
        }

        var json = JsonSerializer.Serialize(workset, JsonOptions);
        if (output is not null)
        {
            var fullPath = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory());
            AtomicFile.WriteAllText(fullPath, json);
            PrintJson(new { scan, workset.WorksetId, output = fullPath, fragments = workset.Fragments.Count });
        }
        else Console.WriteLine(json);
        return 0;
    }

    private static async Task<int> SubmitAsync(List<string> args, AgentConfig config, AgentPaths paths, AgentDatabase database)
    {
        if (args.Count == 0) return Fail("用法：submit <submission.json>");
        using var lease = AgentRunLease.Acquire(paths.LockPath);
        var json = await File.ReadAllTextAsync(Path.GetFullPath(args[0]));
        var submission = JsonSerializer.Deserialize<CodexSubmission>(json, JsonOptions)
            ?? throw new JsonException("提交 JSON 为空。");
        PrintJson(await CreateWorksetService(config, paths, database).SubmitAsync(submission));
        return 0;
    }

    private static async Task<int> WorksetAsync(List<string> args, AgentConfig config, AgentPaths paths, AgentDatabase database)
    {
        if (args.Count == 0 || args[0] == "list")
        {
            var status = args.Count > 1 ? args[1] : "pending";
            foreach (var item in await database.ListWorksetsAsync(status == "all" ? null : status))
                Console.WriteLine($"{item.WorksetId}  {item.Status}  {item.RunType}  {item.TopicName}  fragments={item.FragmentIds.Count}  {item.CreatedAt:O}");
            return 0;
        }
        if (args[0] == "show" && args.Count > 1)
        {
            PrintJson(await CreateWorksetService(config, paths, database).GetAsync(args[1]));
            return 0;
        }
        if (args[0] == "abandon" && args.Count > 1)
        {
            using var lease = AgentRunLease.Acquire(paths.LockPath);
            await database.AbandonWorksetAsync(args[1]);
            Console.WriteLine("已放弃 pending 工作集，未修改 Daily、文章或提案。");
            return 0;
        }
        return Fail("workset 支持 list [pending|all]、show <workset_id> 或 abandon <workset_id>。");
    }

    private static async Task<int> ProposalAsync(List<string> args, AgentPaths paths, AgentDatabase database)
    {
        if (args.Count == 0 || args[0] == "list")
        {
            var status = args.Count > 1 ? args[1] : "pending";
            foreach (var proposal in await database.ListProposalsAsync(status == "all" ? null : status))
                Console.WriteLine($"{proposal.ProposalId}  {proposal.Status}  {proposal.ProposalType}  {proposal.Title}  {proposal.TargetPath}");
            return 0;
        }
        var action = args[0]; args.RemoveAt(0);
        if (args.Count == 0) return Fail("提案命令需要 proposal_id。");
        var id = args[0];
        var manager = new ArticleManager(paths, database);
        if (action == "show")
        {
            var proposal = await database.GetProposalAsync(id) ?? throw new KeyNotFoundException("未找到提案。");
            PrintJson(new { proposal.ProposalId, proposal.Status, proposal.ProposalType, proposal.RiskLevel, proposal.Title, proposal.Rationale, proposal.TargetPath, proposal.SourceRevisionIds });
            Console.WriteLine(); Console.WriteLine(await manager.GetProposalDiffAsync(id));
            return 0;
        }
        if (action == "approve") { PrintJson(await manager.ApproveAndApplyAsync(id)); return 0; }
        if (action == "reject") { await manager.RejectAsync(id); Console.WriteLine("已拒绝，未修改文件。"); return 0; }
        return Fail("proposal 仅支持 list、show、approve、reject。");
    }

    private static async Task<int> RollbackAsync(List<string> args, AgentPaths paths, AgentDatabase database)
    {
        if (args.Count == 0) return Fail("用法：rollback <article_id>");
        PrintJson(await new ArticleManager(paths, database).RollbackArticleAsync(args[0]));
        return 0;
    }

    private static async Task<int> RebuildIndexAsync(AgentDatabase database)
    {
        await database.RebuildFtsAsync();
        Console.WriteLine("FTS 索引已由活跃片段重建。");
        return 0;
    }

    private static Task<int> ConfigAsync(List<string> args, AgentConfig config)
    {
        if (args.Count == 0 || args[0] == "show")
        {
            PrintJson(new { config.MaxWorksetCharacters, config.MaxFragmentsPerWorkset, config.ProposalBudgetPerSubmission, config.AnalysisVersion });
            return Task.FromResult(0);
        }
        if (args[0] != "set") return Task.FromResult(Fail("config 仅支持 show 或 set。"));
        args.RemoveAt(0);
        var updated = config with
        {
            MaxWorksetCharacters = ParsePositive(TakeOption(args, "--max-chars"), config.MaxWorksetCharacters, "--max-chars"),
            MaxFragmentsPerWorkset = ParsePositive(TakeOption(args, "--max-fragments"), config.MaxFragmentsPerWorkset, "--max-fragments"),
            ProposalBudgetPerSubmission = ParsePositive(TakeOption(args, "--proposal-budget"), config.ProposalBudgetPerSubmission, "--proposal-budget")
        };
        AgentConfigLoader.SavePublicSettings(updated);
        Console.WriteLine("已保存本地工作集限额；PKWS 不保存 API Key，也不调用模型 API。");
        return Task.FromResult(0);
    }

    private static CodexWorksetService CreateWorksetService(AgentConfig config, AgentPaths paths, AgentDatabase database) =>
        new(config, database, new ArticleManager(paths, database));

    private static DailyNoteIndexer CreateIndexer(AgentConfig config, AgentPaths paths, AgentDatabase database)
    {
        var capture = config.CaptureDatabasePath;
        if (string.IsNullOrWhiteSpace(capture))
        {
            var parent = Directory.GetParent(paths.DataRoot)?.FullName;
            if (parent is not null) capture = Path.Combine(parent, "state", "capture.db");
        }
        return new DailyNoteIndexer(paths, database, capture);
    }

    private static string? TakeOption(List<string> args, string name)
    {
        var index = args.IndexOf(name);
        if (index < 0) return null;
        if (index + 1 >= args.Count) throw new ArgumentException($"{name} 缺少值。");
        var value = args[index + 1];
        args.RemoveRange(index, 2);
        return value;
    }

    private static bool TakeFlag(List<string> args, string name)
    {
        var index = args.IndexOf(name);
        if (index < 0) return false;
        args.RemoveAt(index);
        return true;
    }

    private static int ParsePositive(string? value, int fallback, string name)
    {
        if (value is null) return fallback;
        if (!int.TryParse(value, out var parsed) || parsed < 1) throw new ArgumentException($"{name} 必须是正整数。");
        return parsed;
    }

    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
    private static void PrintJson<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static void PrintHelp() => Console.WriteLine("""
        PKWS Agent CLI（Codex 自动任务协议）

        全局参数：--doc <doc目录> --state <Agent状态目录> --capture-db <capture.db>

        scan
        search <词> [--source 类型] [--from 日期]
        status
        topic list | create <名称> [--direction 方向] [--pin]
        article list | show <article_id>
        prepare daily|weekly|monthly|topic [--topic 名称] [--output 文件]
        submit <submission.json>
        workset list [pending|all] | show|abandon <workset_id>
        proposal list [pending|all] | show|approve|reject <proposal_id>
        rollback <article_id>
        rebuild-index
        config show
        config set [--max-chars N] [--max-fragments N] [--proposal-budget N]

        prepare 生成受限 JSON 工作集，Codex 定期任务生成 submission JSON，submit 只生成 pending 提案。
        PKWS 不调用模型 API；Daily 始终只读；只有 proposal approve 才会写入文章或回顾。
        """);
}
