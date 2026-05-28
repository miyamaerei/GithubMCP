using System.ComponentModel;
using System.Runtime.InteropServices;
using ModelContextProtocol.Server;
using Octokit;
using LibGit2Sharp;
using LibGit2SharpCommit = LibGit2Sharp.Commit;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using GitHubMcp.Tools;

public class GitHubTools
{
    private readonly ConfigurationManager _configManager;
    private readonly ILogger<GitHubTools> _logger;

    public GitHubTools(ILogger<GitHubTools> logger)
    {
        _logger = logger;
        _configManager = new ConfigurationManager(logger);
    }

    private string GetCurrentNtId() => _configManager.GetCurrentNtId();

    private Octokit.GitHubClient CreateOctokitClient(string token)
    {
        var client = new Octokit.GitHubClient(new Octokit.ProductHeaderValue("GitHubMcp"));
        client.Credentials = new Octokit.Credentials(token);
        return client;
    }

    private LibGit2Sharp.Handlers.CredentialsHandler CreateCredentialsHandler(GitAuthType authType, string? token = null, string? username = null, string? password = null)
    {
        return authType switch
        {
            GitAuthType.Token when !string.IsNullOrEmpty(token) =>
                (url, user, cred) => new LibGit2Sharp.UsernamePasswordCredentials { Username = "x-access-token", Password = token },
            GitAuthType.Basic when !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password) =>
                (url, user, cred) => new LibGit2Sharp.UsernamePasswordCredentials { Username = username, Password = password },
            _ => (url, user, cred) => null
        };
    }

    private (LibGit2Sharp.Handlers.CredentialsHandler? Handler, GitAuthType AuthType, string? Email) GetConfiguredAuthHandler(string? projectPath = null)
    {
        var effectiveConfig = _configManager.GetEffectiveConfig(projectPath);
        var config = effectiveConfig.Config;
        var authType = config.AuthType ?? GitAuthType.Local;

        if (authType == GitAuthType.Local)
        {
            var localHandler = new LibGit2Sharp.Handlers.CredentialsHandler((url, usernameFromUrl, supportedTypes) =>
            {
                if (url.StartsWith("git@") || url.Contains(":"))
                {
                    return null;
                }
                try
                {
                    var uri = new Uri(url);
                    var host = uri.Host;
                    var (credUsername, credPassword) = GetWindowsCredential(host);
                    if (!string.IsNullOrEmpty(credUsername) && !string.IsNullOrEmpty(credPassword))
                    {
                        return new LibGit2Sharp.UsernamePasswordCredentials { Username = credUsername, Password = credPassword };
                    }
                }
                catch { }
                return null;
            });
            return (localHandler, GitAuthType.Local, config.GitHubEmail);
        }

        var handler = CreateCredentialsHandler(
            authType,
            config.GitToken,
            config.GitUsername,
            config.GitPassword
        );
        return (handler, authType, config.GitHubEmail);
    }

    private (string? Username, string? Password) GetWindowsCredential(string target)
    {
        try
        {
            var credentialTarget = $"git:https://{target}";
            if (NativeMethods.CredRead(credentialTarget, NativeMethods.CRED_TYPE.GENERIC, 0, out var credentialPtr))
            {
                if (credentialPtr != IntPtr.Zero)
                {
                    var credential = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(credentialPtr);
                    var userName = credential.UserName;
                    var passwordPtr = credential.CredentialBlob;
                    var password = passwordPtr != IntPtr.Zero
                        ? Marshal.PtrToStringUni(passwordPtr, (int)(credential.CredentialBlobSize / 2))
                        : null;
                    NativeMethods.CredFree(credentialPtr);
                    return (userName, password);
                }
            }
        }
        catch { }
        return (null, null);
    }

    [McpServerTool]
    [Description("获取当前有效的 Git 认证配置，包含配置来源信息。")]
    public AuthConfigWithSource GetEffectiveConfig(string? projectPath = null)
    {
        try
        {
            var result = _configManager.GetEffectiveConfig(projectPath);
            if (result.Config.GitPassword != null)
            {
                result.Config.GitPassword = "********";
            }
            if (result.Config.GitToken != null)
            {
                result.Config.GitToken = "********";
            }
            if (result.Config.GitHubToken != null)
            {
                result.Config.GitHubToken = "********";
            }
            _logger.LogInformation("Retrieved effective config from {Source}", result.Source.Source);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get effective config");
            throw;
        }
    }

    [McpServerTool]
    [Description("设置 Git 认证配置（支持全局/用户/项目级别）。Tokens 和密码会被加密存储。")]
    public bool SetConfig(
        ConfigScope scope,
        GitAuthType? authType = null,
        string? target = null,
        string? githubToken = null,
        string? githubEmail = null,
        string? gitToken = null,
        string? gitUsername = null,
        string? gitPassword = null,
        string? defaultBranch = null)
    {
        try
        {
            var config = new GitAuthConfig
            {
                AuthType = authType,
                GitHubToken = string.IsNullOrEmpty(githubToken) ? null : ProtectedDataHelper.EncryptString(githubToken),
                GitHubEmail = githubEmail,
                GitToken = string.IsNullOrEmpty(gitToken) ? null : ProtectedDataHelper.EncryptString(gitToken),
                GitUsername = gitUsername,
                GitPassword = string.IsNullOrEmpty(gitPassword) ? null : ProtectedDataHelper.EncryptString(gitPassword),
                DefaultBranch = defaultBranch,
                IsConfigured = true
            };

            var result = _configManager.SetConfig(scope, target, config);
            _logger.LogInformation("Config saved: Scope={Scope}, Target={Target}, Result={Result}",
                scope, target ?? "current user", result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set config");
            throw;
        }
    }

    [McpServerTool]
    [Description("删除 Git 认证配置（支持用户/项目级别）。")]
    public bool DeleteConfig(ConfigScope scope, string target)
    {
        try
        {
            var result = _configManager.DeleteConfig(scope, target);
            _logger.LogInformation("Config deleted: Scope={Scope}, Target={Target}, Result={Result}",
                scope, target, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete config");
            throw;
        }
    }

    [McpServerTool]
    [Description("列出所有 Git 认证配置。")]
    public List<ConfigListItem> ListConfigs(ConfigScope? scope = null)
    {
        try
        {
            var configs = _configManager.ListConfigs(scope);
            _logger.LogInformation("Listed {Count} configs for scope {Scope}", configs.Count, scope?.ToString() ?? "all");
            return configs;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list configs");
            throw;
        }
    }

    [McpServerTool]
    [Description("设置单个配置项（key-value 格式）。支持分组配置，敏感信息自动加密存储。")]
    public bool SetKeyValueConfig(
        [Description("配置作用域：Global=全局, User=用户, Project=项目")] ConfigScope scope,
        [Description("配置分组，如 auth/git/repo")] string group,
        [Description("配置键名")] string key,
        [Description("配置值")] string? value,
        [Description("是否加密存储（用于 Token 等敏感信息）")] bool isEncrypted = false,
        [Description("作用域目标（User 时为 NTID，Project 时为项目路径）")] string? target = null)
    {
        try
        {
            string? encryptedValue = value;
            if (isEncrypted && !string.IsNullOrEmpty(value))
            {
                encryptedValue = ProtectedDataHelper.EncryptString(value);
            }

            var result = _configManager.SetKeyValue(scope, group, key, encryptedValue, isEncrypted, target);
            _logger.LogInformation("Key-value config saved: Scope={Scope}, Group={Group}, Key={Key}, IsEncrypted={Encrypted}, Result={Result}",
                scope, group, key, isEncrypted, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set key-value config");
            throw;
        }
    }

    [McpServerTool]
    [Description("获取单个配置项（key-value 格式）。")]
    public string? GetKeyValueConfig(
        [Description("配置作用域")] ConfigScope scope,
        [Description("配置分组")] string group,
        [Description("配置键名")] string key,
        [Description("作用域目标")] string? target = null)
    {
        try
        {
            var value = _configManager.GetKeyValue(scope, group, key, target);
            _logger.LogInformation("Key-value config retrieved: Scope={Scope}, Group={Group}, Key={Key}, Found={Found}",
                scope, group, key, value != null);
            return value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get key-value config");
            throw;
        }
    }

    [McpServerTool]
    [Description("删除单个配置项（key-value 格式）。")]
    public bool DeleteKeyValueConfig(
        [Description("配置作用域")] ConfigScope scope,
        [Description("配置分组")] string group,
        [Description("配置键名")] string key,
        [Description("作用域目标")] string? target = null)
    {
        try
        {
            var result = _configManager.DeleteKeyValue(scope, group, key, target);
            _logger.LogInformation("Key-value config deleted: Scope={Scope}, Group={Group}, Key={Key}, Result={Result}",
                scope, group, key, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete key-value config");
            throw;
        }
    }

    [McpServerTool]
    [Description("列出所有配置项（key-value 格式），支持按作用域和分组过滤。")]
    public List<ConfigListItem> ListKeyValueConfigs(
        [Description("配置作用域过滤")] ConfigScope? scope = null,
        [Description("配置分组过滤")] string? group = null)
    {
        try
        {
            var configs = _configManager.ListKeyValueConfigs(scope, group);
            _logger.LogInformation("Listed {Count} key-value configs for scope={Scope}, group={Group}",
                configs.Count, scope?.ToString() ?? "all", group ?? "all");
            return configs;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list key-value configs");
            throw;
        }
    }

    [McpServerTool]
    [Description("删除指定作用域的所有配置。")]
    public bool DeleteAllConfigs(
        [Description("配置作用域")] ConfigScope scope,
        [Description("作用域目标")] string? target = null)
    {
        try
        {
            var result = _configManager.DeleteAllConfigs(scope, target);
            _logger.LogInformation("All configs deleted: Scope={Scope}, Target={Target}, Result={Result}",
                scope, target ?? "N/A", result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete all configs");
            throw;
        }
    }

    [McpServerTool]
    [Description("List issues for the specified owner/repo.")]
    public async Task<IReadOnlyList<Issue>> ListIssues(string owner, string repo)
    {
        try
        {
            var effectiveConfig = _configManager.GetEffectiveConfig();
            if (string.IsNullOrEmpty(effectiveConfig.Config.GitHubToken))
            {
                _logger.LogWarning("No GitHub token found, returning empty issue list");
                return Array.Empty<Issue>();
            }
            var decryptedToken = ProtectedDataHelper.DecryptString(effectiveConfig.Config.GitHubToken);
            var client = CreateOctokitClient(decryptedToken);
            var issues = await client.Issue.GetAllForRepository(owner, repo);
            _logger.LogInformation("Retrieved {Count} issues from {Owner}/{Repo}", issues.Count, owner, repo);
            return issues;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list issues for {Owner}/{Repo}", owner, repo);
            throw;
        }
    }

    [McpServerTool]
    [Description("Create an issue in the specified owner/repo with title and body.")]
    public async Task<Issue?> CreateIssue(string owner, string repo, string title, string body)
    {
        try
        {
            var effectiveConfig = _configManager.GetEffectiveConfig();
            if (string.IsNullOrEmpty(effectiveConfig.Config.GitHubToken))
            {
                _logger.LogWarning("No token found, cannot create issue");
                return null;
            }
            var decryptedToken = ProtectedDataHelper.DecryptString(effectiveConfig.Config.GitHubToken);
            var client = CreateOctokitClient(decryptedToken);
            var issue = new NewIssue(title) { Body = body };
            var createdIssue = await client.Issue.Create(owner, repo, issue);
            _logger.LogInformation("Created issue #{Number} in {Owner}/{Repo}", createdIssue.Number, owner, repo);
            return createdIssue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create issue in {Owner}/{Repo}", owner, repo);
            throw;
        }
    }

    [McpServerTool]
    [Description("Create a pull request from headBranch into baseBranch with optional draft mode, reviewers and labels.")]
    public async Task<PullRequest?> CreatePullRequest(
        string owner,
        string repo,
        string headBranch,
        string baseBranch,
        string title,
        string body,
        bool draft = false,
        string[]? reviewers = null,
        string[]? labels = null)
    {
        try
        {
            var effectiveConfig = _configManager.GetEffectiveConfig();
            if (string.IsNullOrEmpty(effectiveConfig.Config.GitHubToken))
            {
                _logger.LogWarning("No token found, cannot create pull request");
                return null;
            }
            var decryptedToken = ProtectedDataHelper.DecryptString(effectiveConfig.Config.GitHubToken);
            var client = CreateOctokitClient(decryptedToken);

            var pr = new NewPullRequest(title, headBranch, baseBranch)
            {
                Body = body,
                Draft = draft
            };

            var createdPr = await client.PullRequest.Create(owner, repo, pr);
            _logger.LogInformation("Created PR #{Number} in {Owner}/{Repo}", createdPr.Number, owner, repo);

            if (labels != null && labels.Length > 0)
            {
                await client.Issue.Labels.AddToIssue(owner, repo, createdPr.Number, labels);
                _logger.LogInformation("Added labels {Labels} to PR #{Number}", string.Join(", ", labels), createdPr.Number);
            }

            return createdPr;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create pull request in {Owner}/{Repo}", owner, repo);
            throw;
        }
    }

    [McpServerTool]
    [Description("Clone a git repository to a local path. Uses configured auth automatically.")]
    public string CloneRepository(string url, string localPath, string? projectPath = null)
    {
        try
        {
            var cloneOptions = new CloneOptions();
            var (handler, authType, _) = GetConfiguredAuthHandler(projectPath);

            if (handler != null)
            {
                cloneOptions.CredentialsProvider = handler;
                _logger.LogInformation("Cloned repository {Url} to {Path} using {AuthType} auth", url, localPath, authType);
            }
            else
            {
                _logger.LogInformation("Cloned repository {Url} to {Path} using local auth", url, localPath);
            }

            LibGit2Sharp.Repository.Clone(url, localPath, cloneOptions);
            return localPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clone repository {Url} to {Path}", url, localPath);
            throw;
        }
    }

    [McpServerTool]
    [Description("Commit changes in the repository at localPath with message and push to origin. Uses configured auth automatically.")]
    public bool CommitAndPush(string localPath, string message, string? branch = null, string? projectPath = null)
    {
        try
        {
            using var repo = new LibGit2Sharp.Repository(localPath);
            var (_, _, email) = GetConfiguredAuthHandler(projectPath);
            var signature = new LibGit2Sharp.Signature(Environment.UserName, email ?? "", DateTimeOffset.Now);

            Commands.Stage(repo, "*");
            var commit = repo.Commit(message, signature, signature);
            _logger.LogInformation("Committed changes: {Commit}", commit.Sha);

            var pushOptions = new PushOptions();
            var (handler, authType, _) = GetConfiguredAuthHandler(projectPath);
            if (handler != null)
            {
                pushOptions.CredentialsProvider = handler;
            }

            var remote = repo.Network.Remotes["origin"];
            var pushRef = branch == null ? repo.Head.CanonicalName : $"refs/heads/{branch}";
            repo.Network.Push(remote, pushRef, pushOptions);

            _logger.LogInformation("Pushed to {Remote} {Ref}", remote.Name, pushRef);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to commit and push to {Path}", localPath);
            throw;
        }
    }

    [McpServerTool]
    [Description("Create a new branch in the local repository.")]
    public string CreateBranch(string localPath, string branchName, string? startPoint = null)
    {
        try
        {
            using var repo = new LibGit2Sharp.Repository(localPath);
            var commit = string.IsNullOrEmpty(startPoint)
                ? repo.Head.Tip
                : repo.Lookup<LibGit2SharpCommit>(startPoint) ?? repo.Head.Tip;

            repo.CreateBranch(branchName, commit);
            _logger.LogInformation("Created branch {Branch} from {Commit}", branchName, commit.Sha);
            return branchName;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create branch {Branch} in {Path}", branchName, localPath);
            throw;
        }
    }

    [McpServerTool]
    [Description("Checkout to the specified branch in the local repository.")]
    public bool CheckoutBranch(string localPath, string branchName)
    {
        try
        {
            using var repo = new LibGit2Sharp.Repository(localPath);
            Commands.Checkout(repo, branchName);
            _logger.LogInformation("Checked out branch {Branch}", branchName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to checkout branch {Branch} in {Path}", branchName, localPath);
            throw;
        }
    }

    [McpServerTool]
    [Description("Pull latest changes from remote repository. Uses configured auth automatically.")]
    public bool Pull(string localPath, string remoteName = "origin", string? projectPath = null)
    {
        try
        {
            using var repo = new LibGit2Sharp.Repository(localPath);

            var pullOptions = new PullOptions();
            var fetchOptions = new FetchOptions();
            var (handler, authType, _) = GetConfiguredAuthHandler(projectPath);

            if (handler != null)
            {
                fetchOptions.CredentialsProvider = handler;
                pullOptions.FetchOptions = fetchOptions;
            }

            var (_, _, email) = GetConfiguredAuthHandler(projectPath);
            var signature = new LibGit2Sharp.Signature(Environment.UserName, email ?? "", DateTimeOffset.Now);
            Commands.Pull(repo, signature, pullOptions);

            _logger.LogInformation("Pulled from {Remote}", remoteName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pull from {Remote} in {Path}", remoteName, localPath);
            throw;
        }
    }

    private WorkingCopy? GetWorkingCopyByNameOrId(string? nameOrId, string? defaultValue = null)
    {
        if (string.IsNullOrEmpty(nameOrId))
        {
            if (!string.IsNullOrEmpty(defaultValue))
            {
                nameOrId = defaultValue;
            }
            else
            {
                var defaultCopy = _configManager.GetDefaultWorkingCopy(GetCurrentNtId());
                return defaultCopy;
            }
        }

        var ntid = GetCurrentNtId();

        if (int.TryParse(nameOrId, out var id))
        {
            return _configManager.GetWorkingCopyById(id);
        }

        return _configManager.GetWorkingCopy(nameOrId, ntid);
    }

    [McpServerTool]
    [Description("Add a working copy (local git repository) for easy access.")]
    public bool AddWorkingCopy(
        string name,
        string localPath,
        string? remoteUrl = null,
        string? projectPath = null,
        bool setAsDefault = false)
    {
        try
        {
            var ntid = GetCurrentNtId();
            var workingCopy = new WorkingCopy
            {
                Name = name,
                LocalPath = localPath,
                OwnerNtid = ntid,
                RemoteUrl = remoteUrl,
                ProjectPath = projectPath,
                IsDefault = setAsDefault
            };

            var result = _configManager.SaveWorkingCopy(workingCopy);
            _logger.LogInformation("Added working copy {Name} at {Path} (Default: {IsDefault})",
                name, localPath, setAsDefault);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add working copy {Name}", name);
            throw;
        }
    }

    [McpServerTool]
    [Description("Remove a working copy from the registry.")]
    public bool RemoveWorkingCopy(string name)
    {
        try
        {
            var ntid = GetCurrentNtId();
            var result = _configManager.DeleteWorkingCopy(name, ntid);
            _logger.LogInformation("Removed working copy {Name}: {Result}", name, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove working copy {Name}", name);
            throw;
        }
    }

    [McpServerTool]
    [Description("List all working copies for the current user.")]
    public List<WorkingCopy> ListWorkingCopies()
    {
        try
        {
            var ntid = GetCurrentNtId();
            var copies = _configManager.ListWorkingCopies(ntid);
            _logger.LogInformation("Listed {Count} working copies for {Ntid}", copies.Count, ntid);
            return copies;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list working copies");
            throw;
        }
    }

    [McpServerTool]
    [Description("Set a working copy as the default.")]
    public bool SetDefaultWorkingCopy(string name)
    {
        try
        {
            var ntid = GetCurrentNtId();
            var result = _configManager.SetDefaultWorkingCopy(name, ntid);
            _logger.LogInformation("Set {Name} as default: {Result}", name, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set default working copy {Name}", name);
            throw;
        }
    }

    [McpServerTool]
    [Description("Get the current default working copy or a specific one by name/ID.")]
    public WorkingCopy? GetCurrentWorkingCopy(string? nameOrId = null)
    {
        try
        {
            return GetWorkingCopyByNameOrId(nameOrId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get current working copy");
            throw;
        }
    }

    [McpServerTool]
    [Description("Clone a git repository and register it as a working copy.")]
    public string CloneRepositoryAsWorkingCopy(
        string url,
        string name,
        string? localPath = null,
        string? projectPath = null,
        bool setAsDefault = false)
    {
        try
        {
            if (string.IsNullOrEmpty(localPath))
            {
                localPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "GitRepos",
                    name
                );
            }

            var ntid = GetCurrentNtId();
            var effectiveConfig = _configManager.GetEffectiveConfig(projectPath);

            var cloneOptions = new CloneOptions();
            var (handler, authType, _) = GetConfiguredAuthHandler(projectPath);
            if (handler != null)
            {
                cloneOptions.CredentialsProvider = handler;
            }

            LibGit2Sharp.Repository.Clone(url, localPath, cloneOptions);
            _logger.LogInformation("Cloned repository {Url} to {Path}", url, localPath);

            var workingCopy = new WorkingCopy
            {
                Name = name,
                LocalPath = localPath,
                OwnerNtid = ntid,
                RemoteUrl = url,
                ProjectPath = projectPath,
                IsDefault = setAsDefault
            };
            _configManager.SaveWorkingCopy(workingCopy);

            return localPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clone repository {Url} as working copy {Name}", url, name);
            throw;
        }
    }

    [McpServerTool]
    [Description("Commit and push changes using a working copy (by name or ID). Uses default working copy if not specified.")]
    public bool CommitAndPushWithWorkingCopy(string message, string? workingCopyNameOrId = null, string? branch = null)
    {
        try
        {
            var workingCopy = GetWorkingCopyByNameOrId(workingCopyNameOrId);
            if (workingCopy == null)
            {
                _logger.LogWarning("Working copy not found: {Name}", workingCopyNameOrId ?? "default");
                return false;
            }

            var effectiveConfig = _configManager.GetEffectiveConfig(workingCopy.ProjectPath);

            using var repo = new LibGit2Sharp.Repository(workingCopy.LocalPath);
            var (_, _, email) = GetConfiguredAuthHandler(workingCopy.ProjectPath);
            var signature = new LibGit2Sharp.Signature(Environment.UserName, email ?? "", DateTimeOffset.Now);

            Commands.Stage(repo, "*");
            var commit = repo.Commit(message, signature, signature);
            _logger.LogInformation("Committed changes: {Commit}", commit.Sha);

            var pushOptions = new PushOptions();
            var (handler, authType, _) = GetConfiguredAuthHandler(workingCopy.ProjectPath);
            if (handler != null)
            {
                pushOptions.CredentialsProvider = handler;
            }

            var remote = repo.Network.Remotes["origin"];
            var pushRef = branch == null ? repo.Head.CanonicalName : $"refs/heads/{branch}";
            repo.Network.Push(remote, pushRef, pushOptions);

            _logger.LogInformation("Pushed to {Remote} {Ref} from working copy {Name}", remote.Name, pushRef, workingCopy.Name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to commit and push with working copy");
            throw;
        }
    }

    [McpServerTool]
    [Description("Pull latest changes using a working copy (by name or ID). Uses default working copy if not specified.")]
    public bool PullWithWorkingCopy(string? workingCopyNameOrId = null, string remoteName = "origin")
    {
        try
        {
            var workingCopy = GetWorkingCopyByNameOrId(workingCopyNameOrId);
            if (workingCopy == null)
            {
                _logger.LogWarning("Working copy not found: {Name}", workingCopyNameOrId ?? "default");
                return false;
            }

            using var repo = new LibGit2Sharp.Repository(workingCopy.LocalPath);

            var pullOptions = new PullOptions();
            var fetchOptions = new FetchOptions();
            var (handler, authType, _) = GetConfiguredAuthHandler(workingCopy.ProjectPath);

            if (handler != null)
            {
                fetchOptions.CredentialsProvider = handler;
                pullOptions.FetchOptions = fetchOptions;
            }

            var (_, _, email) = GetConfiguredAuthHandler(workingCopy.ProjectPath);
            var signature = new LibGit2Sharp.Signature(Environment.UserName, email ?? "", DateTimeOffset.Now);
            Commands.Pull(repo, signature, pullOptions);

            _logger.LogInformation("Pulled from {Remote} using working copy {Name}", remoteName, workingCopy.Name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pull with working copy");
            throw;
        }
    }

    [McpServerTool]
    [Description("Create a new branch in a working copy.")]
    public string CreateBranchWithWorkingCopy(string branchName, string? workingCopyNameOrId = null, string? startPoint = null)
    {
        try
        {
            var workingCopy = GetWorkingCopyByNameOrId(workingCopyNameOrId);
            if (workingCopy == null)
            {
                _logger.LogWarning("Working copy not found: {Name}", workingCopyNameOrId ?? "default");
                return "";
            }

            using var repo = new LibGit2Sharp.Repository(workingCopy.LocalPath);
            var commit = string.IsNullOrEmpty(startPoint)
                ? repo.Head.Tip
                : repo.Lookup<LibGit2SharpCommit>(startPoint) ?? repo.Head.Tip;

            repo.CreateBranch(branchName, commit);
            _logger.LogInformation("Created branch {Branch} in working copy {Name}", branchName, workingCopy.Name);
            return branchName;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create branch in working copy");
            throw;
        }
    }

    [McpServerTool]
    [Description("Checkout a branch in a working copy.")]
    public bool CheckoutBranchWithWorkingCopy(string branchName, string? workingCopyNameOrId = null)
    {
        try
        {
            var workingCopy = GetWorkingCopyByNameOrId(workingCopyNameOrId);
            if (workingCopy == null)
            {
                _logger.LogWarning("Working copy not found: {Name}", workingCopyNameOrId ?? "default");
                return false;
            }

            using var repo = new LibGit2Sharp.Repository(workingCopy.LocalPath);
            Commands.Checkout(repo, branchName);
            _logger.LogInformation("Checked out branch {Branch} in working copy {Name}", branchName, workingCopy.Name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to checkout branch in working copy");
            throw;
        }
    }

    [McpServerTool]
    [Description("Get current status of a working copy.")]
    public Dictionary<string, string>? GetWorkingCopyStatus(string? workingCopyNameOrId = null)
    {
        try
        {
            var workingCopy = GetWorkingCopyByNameOrId(workingCopyNameOrId);
            if (workingCopy == null)
            {
                _logger.LogWarning("Working copy not found: {Name}", workingCopyNameOrId ?? "default");
                return null;
            }

            using var repo = new LibGit2Sharp.Repository(workingCopy.LocalPath);
            var status = repo.RetrieveStatus();

            return new Dictionary<string, string>
            {
                ["WorkingCopyName"] = workingCopy.Name,
                ["LocalPath"] = workingCopy.LocalPath,
                ["IsDirty"] = status.IsDirty.ToString(),
                ["AddedFiles"] = status.Added.Count().ToString(),
                ["ModifiedFiles"] = status.Modified.Count().ToString(),
                ["RemovedFiles"] = status.Removed.Count().ToString(),
                ["CurrentBranch"] = repo.Head.FriendlyName
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get working copy status");
            throw;
        }
    }
}