using Microsoft.Extensions.Logging;
using Moq;
using System.IO;
using Xunit;
using GitHubMcp.Tools;
using LibGit2Sharp;

namespace GitHubMcp.Tests;

public class GitHubToolsTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly GitHubTools _gitHubTools;
    private readonly Mock<ILogger<GitHubTools>> _loggerMock;

    public GitHubToolsTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_mcp_git_{Guid.NewGuid()}.db");
        Environment.SetEnvironmentVariable("GITHUB_MCP_DB_PATH", _testDbPath);
        _loggerMock = new Mock<ILogger<GitHubTools>>();
        _gitHubTools = new GitHubTools(_loggerMock.Object);
    }

    public void Dispose()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (File.Exists(_testDbPath))
        {
            try
            {
                File.Delete(_testDbPath);
            }
            catch
            {
            }
        }
        Environment.SetEnvironmentVariable("GITHUB_MCP_DB_PATH", null);
    }

    [Fact]
    public void GetEffectiveConfig_ReturnsAuthConfigWithSource()
    {
        var result = _gitHubTools.GetEffectiveConfig();

        Assert.NotNull(result);
        Assert.NotNull(result.Config);
        Assert.NotNull(result.Source);
        Assert.NotNull(result.FieldSources);
    }

    [Fact]
    public void GetEffectiveConfig_WithProjectPath_ReturnsProjectSource()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var projectPath = $"/test/project_{uniqueId}";
        _gitHubTools.SetConfig(ConfigScope.Project, target: projectPath, authType: GitAuthType.Token, githubToken: "test_token");

        var result = _gitHubTools.GetEffectiveConfig(projectPath);

        Assert.Equal("项目配置", result.Source.Source);
        Assert.Equal(projectPath, result.Source.ProjectPath);
    }

    [Fact]
    public void GetEffectiveConfig_HidesSensitiveData()
    {
        _gitHubTools.SetConfig(ConfigScope.User, authType: GitAuthType.Token, githubToken: "secret_github_token");

        var result = _gitHubTools.GetEffectiveConfig();

        Assert.Equal("********", result.Config.GitHubToken);
    }

    [Fact]
    public void SetUserConfig_SavesConfigSuccessfully()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var result = _gitHubTools.SetConfig(
            ConfigScope.User,
            authType: GitAuthType.Token,
            githubToken: $"ghp_test_token_{uniqueId}",
            githubEmail: $"test_{uniqueId}@example.com"
        );

        Assert.True(result);
    }

    [Fact]
    public void SetProjectConfig_SavesConfigSuccessfully()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var result = _gitHubTools.SetConfig(
            ConfigScope.Project,
            target: $"/test/project_{uniqueId}",
            authType: GitAuthType.Basic,
            gitUsername: $"testuser_{uniqueId}",
            gitPassword: $"testpass_{uniqueId}"
        );

        Assert.True(result);
    }

    [Fact]
    public void SetProjectConfig_EncryptsSensitiveData()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var projectPath = $"/test/encryptproject_{uniqueId}";
        _gitHubTools.SetConfig(
            ConfigScope.Project,
            target: projectPath,
            authType: GitAuthType.Token,
            gitToken: $"sensitive_git_token_{uniqueId}"
        );

        var result = _gitHubTools.GetEffectiveConfig(projectPath);

        Assert.Equal("********", result.Config.GitToken);
    }

    [Fact]
    public void DeleteProjectConfig_RemovesConfig()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var projectPath = $"/test/deleteproject_{uniqueId}";
        _gitHubTools.SetConfig(ConfigScope.Project, target: projectPath, authType: GitAuthType.Token);

        var deleteResult = _gitHubTools.DeleteConfig(ConfigScope.Project, projectPath);
        Assert.True(deleteResult);

        var config = _gitHubTools.GetEffectiveConfig(projectPath);
        Assert.Equal("全局默认配置", config.Source.Source);
    }

    [Fact]
    public void SetUserConfig_And_GetEffectiveConfig_ReturnsUserSource()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        _gitHubTools.SetConfig(ConfigScope.User, authType: GitAuthType.Token, githubEmail: $"user_{uniqueId}@company.com");

        var result = _gitHubTools.GetEffectiveConfig();

        Assert.Equal("用户配置", result.Source.Source);
        Assert.Equal($"user_{uniqueId}@company.com", result.Config.GitHubEmail);
    }

    [Fact]
    public void CloneRepository_WithInvalidUrl_ThrowsException()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var invalidUrl = "not-a-valid-url";
        var localPath = Path.Combine(Path.GetTempPath(), $"invalid_clone_test_{uniqueId}");

        if (Directory.Exists(localPath))
        {
            Directory.Delete(localPath, true);
        }

        var exception = Assert.ThrowsAny<Exception>(() =>
            _gitHubTools.CloneRepository(invalidUrl, localPath));

        Assert.True(exception.GetType().Name.Contains("Git") || exception is LibGit2SharpException);

        if (Directory.Exists(localPath))
        {
            Directory.Delete(localPath, true);
        }
    }

    [Fact]
    public void CommitAndPush_WithInvalidPath_ThrowsException()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var invalidPath = Path.Combine(Path.GetTempPath(), $"non_existent_repo_{uniqueId}");

        if (Directory.Exists(invalidPath))
        {
            Directory.Delete(invalidPath, true);
        }

        var exception = Assert.ThrowsAny<Exception>(() =>
            _gitHubTools.CommitAndPush(invalidPath, "test commit"));

        Assert.True(exception.GetType().Name.Contains("Repository") || exception is LibGit2SharpException);

        if (Directory.Exists(invalidPath))
        {
            Directory.Delete(invalidPath, true);
        }
    }

    [Fact]
    public void CreateBranch_WithInvalidPath_ThrowsException()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var invalidPath = Path.Combine(Path.GetTempPath(), $"non_existent_repo_branch_{uniqueId}");

        if (Directory.Exists(invalidPath))
        {
            Directory.Delete(invalidPath, true);
        }

        var exception = Assert.ThrowsAny<Exception>(() =>
            _gitHubTools.CreateBranch(invalidPath, "new-branch"));

        Assert.True(exception.GetType().Name.Contains("Repository") || exception is LibGit2SharpException);
    }

    [Fact]
    public void CheckoutBranch_WithInvalidPath_ThrowsException()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var invalidPath = Path.Combine(Path.GetTempPath(), $"non_existent_repo_checkout_{uniqueId}");

        if (Directory.Exists(invalidPath))
        {
            Directory.Delete(invalidPath, true);
        }

        var exception = Assert.ThrowsAny<Exception>(() =>
            _gitHubTools.CheckoutBranch(invalidPath, "some-branch"));

        Assert.True(exception.GetType().Name.Contains("Repository") || exception is LibGit2SharpException);
    }

    [Fact]
    public void Pull_WithInvalidPath_ThrowsException()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var invalidPath = Path.Combine(Path.GetTempPath(), $"non_existent_repo_pull_{uniqueId}");

        if (Directory.Exists(invalidPath))
        {
            Directory.Delete(invalidPath, true);
        }

        var exception = Assert.ThrowsAny<Exception>(() =>
            _gitHubTools.Pull(invalidPath));

        Assert.True(exception.GetType().Name.Contains("Repository") || exception is LibGit2SharpException);
    }

    [Theory]
    [InlineData(GitAuthType.Local)]
    [InlineData(GitAuthType.Token)]
    [InlineData(GitAuthType.Basic)]
    public void SetUserConfig_SupportsAllAuthTypes(GitAuthType authType)
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var result = _gitHubTools.SetConfig(ConfigScope.User, authType: authType, githubEmail: $"user_{uniqueId}@test.com");

        Assert.True(result);
    }

    [Theory]
    [InlineData(GitAuthType.Local)]
    [InlineData(GitAuthType.Token)]
    [InlineData(GitAuthType.Basic)]
    public void SetProjectConfig_SupportsAllAuthTypes(GitAuthType authType)
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var projectPath = $"/test/project_{authType}_{uniqueId}";

        var result = _gitHubTools.SetConfig(ConfigScope.Project, target: projectPath, authType: authType);

        Assert.True(result);
    }

    [Fact]
    public void GetEffectiveConfig_FieldSources_ContainsAllFields()
    {
        var result = _gitHubTools.GetEffectiveConfig();

        Assert.Contains("auth_type", result.FieldSources.Keys);
        Assert.Contains("github_token", result.FieldSources.Keys);
        Assert.Contains("git_token", result.FieldSources.Keys);
        Assert.Contains("git_username", result.FieldSources.Keys);
        Assert.Contains("git_password", result.FieldSources.Keys);
        Assert.Contains("github_email", result.FieldSources.Keys);
        Assert.Contains("default_branch", result.FieldSources.Keys);
    }

    [Fact]
    public async Task ListIssues_WithoutToken_ReturnsEmptyList()
    {
        var result = await _gitHubTools.ListIssues("owner", "repo");

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task CreateIssue_WithoutToken_ReturnsNull()
    {
        var result = await _gitHubTools.CreateIssue("owner", "repo", "Test Title", "Test Body");

        Assert.Null(result);
    }

    [Fact]
    public async Task CreatePullRequest_WithoutToken_ReturnsNull()
    {
        var result = await _gitHubTools.CreatePullRequest(
            "owner", "repo", "head", "base", "Title", "Body"
        );

        Assert.Null(result);
    }

    [Fact]
    public void AddWorkingCopy_AddsNewWorkingCopy()
    {
        var uniqueId = Guid.NewGuid().ToString("N");

        var result = _gitHubTools.AddWorkingCopy(
            $"test_repo_{uniqueId}",
            $"/workspace/test_{uniqueId}",
            remoteUrl: $"https://github.com/test/{uniqueId}"
        );

        Assert.True(result);
    }

    [Fact]
    public void AddWorkingCopy_WithDefault_SetsAsDefault()
    {
        var uniqueId = Guid.NewGuid().ToString("N");

        var result = _gitHubTools.AddWorkingCopy(
            $"default_repo_{uniqueId}",
            $"/workspace/default_{uniqueId}",
            setAsDefault: true
        );

        Assert.True(result);
        var current = _gitHubTools.GetCurrentWorkingCopy();
        Assert.NotNull(current);
        Assert.Equal($"default_repo_{uniqueId}", current.Name);
    }

    [Fact]
    public void RemoveWorkingCopy_RemovesExistingWorkingCopy()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        _gitHubTools.AddWorkingCopy($"remove_repo_{uniqueId}", $"/workspace/remove_{uniqueId}");

        var result = _gitHubTools.RemoveWorkingCopy($"remove_repo_{uniqueId}");

        Assert.True(result);
        var current = _gitHubTools.GetCurrentWorkingCopy($"remove_repo_{uniqueId}");
        Assert.Null(current);
    }

    [Fact]
    public void ListWorkingCopies_ReturnsUserWorkingCopies()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        _gitHubTools.AddWorkingCopy($"list1_{uniqueId}", $"/workspace/list1_{uniqueId}");
        _gitHubTools.AddWorkingCopy($"list2_{uniqueId}", $"/workspace/list2_{uniqueId}");

        var result = _gitHubTools.ListWorkingCopies();

        Assert.NotNull(result);
        Assert.Contains(result, wc => wc.Name == $"list1_{uniqueId}");
        Assert.Contains(result, wc => wc.Name == $"list2_{uniqueId}");
    }

    [Fact]
    public void SetDefaultWorkingCopy_SetsNewDefault()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        _gitHubTools.AddWorkingCopy($"wc1_{uniqueId}", $"/workspace/wc1_{uniqueId}");
        _gitHubTools.AddWorkingCopy($"wc2_{uniqueId}", $"/workspace/wc2_{uniqueId}");

        var result = _gitHubTools.SetDefaultWorkingCopy($"wc1_{uniqueId}");

        Assert.True(result);
        var current = _gitHubTools.GetCurrentWorkingCopy();
        Assert.NotNull(current);
        Assert.Equal($"wc1_{uniqueId}", current.Name);
    }

    [Fact]
    public void GetCurrentWorkingCopy_WithName_ReturnsSpecificWorkingCopy()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        _gitHubTools.AddWorkingCopy($"specific_{uniqueId}", $"/workspace/specific_{uniqueId}");

        var result = _gitHubTools.GetCurrentWorkingCopy($"specific_{uniqueId}");

        Assert.NotNull(result);
        Assert.Equal($"specific_{uniqueId}", result.Name);
    }

    [Fact]
    public void GetCurrentWorkingCopy_WithId_ReturnsSpecificWorkingCopy()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        _gitHubTools.AddWorkingCopy($"byid_{uniqueId}", $"/workspace/byid_{uniqueId}");
        var added = _gitHubTools.GetCurrentWorkingCopy($"byid_{uniqueId}");

        var result = _gitHubTools.GetCurrentWorkingCopy(added!.Id.ToString());

        Assert.NotNull(result);
        Assert.Equal($"byid_{uniqueId}", result.Name);
    }

    [Fact]
    public void GetCurrentWorkingCopy_WithNonExistentName_ReturnsNull()
    {
        var result = _gitHubTools.GetCurrentWorkingCopy("non_existent_repo_xyz");

        Assert.Null(result);
    }

    [Fact]
    public void GetCurrentWorkingCopy_WithNoArguments_ReturnsDefaultOrNull()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        _gitHubTools.AddWorkingCopy($"defaulttest_{uniqueId}", $"/workspace/defaulttest_{uniqueId}");

        var result = _gitHubTools.GetCurrentWorkingCopy();

        Assert.Null(result);
    }

    [Fact]
    public void AddWorkingCopy_SetsProjectPath()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var projectPath = $"/test/project_{uniqueId}";
        _gitHubTools.SetConfig(ConfigScope.Project, target: projectPath, authType: GitAuthType.Token, githubToken: "test_token");

        var result = _gitHubTools.AddWorkingCopy(
            $"proj_wc_{uniqueId}",
            $"/workspace/proj_{uniqueId}",
            projectPath: projectPath
        );

        Assert.True(result);
        var wc = _gitHubTools.GetCurrentWorkingCopy($"proj_wc_{uniqueId}");
        Assert.NotNull(wc);
        Assert.Equal(projectPath, wc.ProjectPath);
    }

    [Fact]
    public void GetWorkingCopyStatus_WithValidWorkingCopy_ReturnsStatus()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var tempPath = Path.Combine(Path.GetTempPath(), $"wc_status_{uniqueId}");
        Directory.CreateDirectory(tempPath);
        Repository.Init(tempPath);
        _gitHubTools.AddWorkingCopy($"status_wc_{uniqueId}", tempPath);

        var result = _gitHubTools.GetWorkingCopyStatus($"status_wc_{uniqueId}");

        Assert.NotNull(result);
        Assert.Equal($"status_wc_{uniqueId}", result["WorkingCopyName"]);
        Assert.Equal(tempPath, result["LocalPath"]);
        Assert.Contains("IsDirty", result.Keys);
        Assert.Contains("CurrentBranch", result.Keys);

        Directory.Delete(tempPath, true);
    }

    [Fact]
    public void GetWorkingCopyStatus_WithNonExistentWorkingCopy_ReturnsNull()
    {
        var result = _gitHubTools.GetWorkingCopyStatus("non_existent_wc_xyz");

        Assert.Null(result);
    }
}