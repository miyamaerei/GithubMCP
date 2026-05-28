using Microsoft.Extensions.Logging;
using Moq;
using System.IO;
using Xunit;
using GitHubMcp.Tools;

namespace GitHubMcp.Tests;

public class ConfigurationManagerTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly ConfigurationManager _configManager;

    public ConfigurationManagerTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_mcp_{Guid.NewGuid()}.db");
        Environment.SetEnvironmentVariable("GITHUB_MCP_DB_PATH", _testDbPath);
        _configManager = new ConfigurationManager();
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
    public void GetDatabasePath_ReturnsEnvPath()
    {
        var result = _configManager.GetDatabasePath();
        Assert.Equal(_testDbPath, result);
    }

    [Fact]
    public void SetConfig_UserScope_SavesAndRetrievesConfig()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var ntid = _configManager.GetCurrentNtId();
        var config = new GitAuthConfig
        {
            AuthType = GitAuthType.Token,
            GitHubToken = ProtectedDataHelper.EncryptString("ghp_test_token"),
            GitHubEmail = $"test_{uniqueId}@example.com",
            GitToken = ProtectedDataHelper.EncryptString("git_token"),
            IsConfigured = true
        };

        var saveResult = _configManager.SetConfig(ConfigScope.User, ntid, config);
        Assert.True(saveResult);

        var result = _configManager.GetEffectiveConfig();
        Assert.Equal(GitAuthType.Token, result.Config.AuthType);
        Assert.Equal($"test_{uniqueId}@example.com", result.Config.GitHubEmail);
    }

    [Fact]
    public void SetConfig_ProjectScope_SavesAndRetrievesConfig()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var projectPath = $"/workspace/myproject_{uniqueId}";
        var config = new GitAuthConfig
        {
            AuthType = GitAuthType.Basic,
            GitUsername = $"projectuser_{uniqueId}",
            GitHubEmail = $"project_{uniqueId}@company.com",
            DefaultBranch = "develop",
            IsConfigured = true
        };

        var saveResult = _configManager.SetConfig(ConfigScope.Project, projectPath, config);
        Assert.True(saveResult);

        var result = _configManager.GetEffectiveConfig(projectPath);
        Assert.Equal(GitAuthType.Basic, result.Config.AuthType);
        Assert.Equal($"projectuser_{uniqueId}", result.Config.GitUsername);
        Assert.Equal($"project_{uniqueId}@company.com", result.Config.GitHubEmail);
        Assert.Equal("develop", result.Config.DefaultBranch);
    }

    [Fact]
    public void SetConfig_ProjectScope_UpdatesExistingConfig()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var projectPath = $"/workspace/myproject_{uniqueId}";

        var config1 = new GitAuthConfig
        {
            AuthType = GitAuthType.Token,
            GitHubEmail = $"first_{uniqueId}@company.com",
            DefaultBranch = "main",
            IsConfigured = true
        };
        _configManager.SetConfig(ConfigScope.Project, projectPath, config1);

        var config2 = new GitAuthConfig
        {
            AuthType = GitAuthType.Basic,
            GitHubEmail = $"second_{uniqueId}@company.com",
            DefaultBranch = "develop",
            IsConfigured = true
        };
        _configManager.SetConfig(ConfigScope.Project, projectPath, config2);

        var result = _configManager.GetEffectiveConfig(projectPath);
        Assert.Equal(GitAuthType.Basic, result.Config.AuthType);
        Assert.Equal($"second_{uniqueId}@company.com", result.Config.GitHubEmail);
        Assert.Equal("develop", result.Config.DefaultBranch);
    }

    [Fact]
    public void DeleteConfig_ProjectScope_RemovesConfig()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var projectPath = $"/workspace/myproject_{uniqueId}";
        var config = new GitAuthConfig
        {
            AuthType = GitAuthType.Token,
            GitHubEmail = $"test_{uniqueId}@company.com",
            IsConfigured = true
        };

        _configManager.SetConfig(ConfigScope.Project, projectPath, config);
        var beforeDelete = _configManager.GetEffectiveConfig(projectPath);
        Assert.Equal("项目配置", beforeDelete.Source.Source);

        var deleteResult = _configManager.DeleteConfig(ConfigScope.Project, projectPath);
        Assert.True(deleteResult);

        var afterDelete = _configManager.GetEffectiveConfig(projectPath);
        Assert.Equal("全局默认配置", afterDelete.Source.Source);
    }

    [Fact]
    public void GetEffectiveConfig_WithNoConfig_ReturnsGlobalDefaults()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var result = _configManager.GetEffectiveConfig($"/some/project_{uniqueId}");

        Assert.Equal("全局默认配置", result.Source.Source);
        Assert.Equal(GitAuthType.Local, result.Config.AuthType);
        Assert.Equal("main", result.Config.DefaultBranch);
    }

    [Fact]
    public void GetEffectiveConfig_WithUserConfig_ReturnsUserConfig()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var ntid = _configManager.GetCurrentNtId();
        var config = new GitAuthConfig
        {
            AuthType = GitAuthType.Token,
            GitHubEmail = $"user_{uniqueId}@company.com",
            GitToken = ProtectedDataHelper.EncryptString("user_token"),
            IsConfigured = true
        };
        _configManager.SetConfig(ConfigScope.User, ntid, config);

        var result = _configManager.GetEffectiveConfig($"/any/project_{uniqueId}");

        Assert.Equal("用户配置", result.Source.Source);
        Assert.Equal(ntid, result.Source.UserNtid);
        Assert.Equal($"user_{uniqueId}@company.com", result.Config.GitHubEmail);
        Assert.Equal("用户配置", result.FieldSources["github_email"]);
    }

    [Fact]
    public void GetEffectiveConfig_WithProjectConfig_ReturnsProjectConfig()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var projectPath = $"/workspace/specialproject_{uniqueId}";
        var ntid = _configManager.GetCurrentNtId();

        _configManager.SetConfig(ConfigScope.User, ntid, new GitAuthConfig
        {
            AuthType = GitAuthType.Local,
            GitHubEmail = $"user_{uniqueId}@company.com",
            IsConfigured = true
        });

        _configManager.SetConfig(ConfigScope.Project, projectPath, new GitAuthConfig
        {
            AuthType = GitAuthType.Token,
            GitToken = ProtectedDataHelper.EncryptString("project_token"),
            GitHubEmail = $"project_{uniqueId}@company.com",
            DefaultBranch = "release",
            IsConfigured = true
        });

        var result = _configManager.GetEffectiveConfig(projectPath);

        Assert.Equal("项目配置", result.Source.Source);
        Assert.Equal(projectPath, result.Source.ProjectPath);
        Assert.Equal($"project_{uniqueId}@company.com", result.Config.GitHubEmail);
        Assert.Equal("release", result.Config.DefaultBranch);
        Assert.Equal("项目配置", result.FieldSources["github_email"]);
        Assert.Equal("项目配置", result.FieldSources["default_branch"]);
    }

    [Fact]
    public void GetEffectiveConfig_ProjectOverridesUserFields()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var projectPath = $"/workspace/mixedproject_{uniqueId}";
        var ntid = _configManager.GetCurrentNtId();

        _configManager.SetConfig(ConfigScope.User, ntid, new GitAuthConfig
        {
            AuthType = GitAuthType.Local,
            GitHubEmail = $"user_email_{uniqueId}@company.com",
            GitToken = ProtectedDataHelper.EncryptString("user_token"),
            IsConfigured = true
        });

        _configManager.SetConfig(ConfigScope.Project, projectPath, new GitAuthConfig
        {
            AuthType = GitAuthType.Basic,
            GitHubEmail = $"project_email_{uniqueId}@company.com",
            IsConfigured = true
        });

        var result = _configManager.GetEffectiveConfig(projectPath);

        Assert.Equal("项目配置", result.Source.Source);
        Assert.Equal($"project_email_{uniqueId}@company.com", result.Config.GitHubEmail);
        Assert.Equal("项目配置", result.FieldSources["github_email"]);
        Assert.Equal(GitAuthType.Basic, result.Config.AuthType);
        Assert.Equal("项目配置", result.FieldSources["auth_type"]);
    }

    [Fact]
    public void GetEffectiveConfig_WithoutProjectPath_ReturnsUserOrGlobalConfig()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var ntid = _configManager.GetCurrentNtId();
        _configManager.SetConfig(ConfigScope.User, ntid, new GitAuthConfig
        {
            AuthType = GitAuthType.Token,
            GitHubEmail = $"nopj_{uniqueId}@company.com",
            IsConfigured = true
        });

        var result = _configManager.GetEffectiveConfig();

        Assert.Equal("用户配置", result.Source.Source);
        Assert.Equal(ntid, result.Source.UserNtid);
        Assert.Null(result.Source.ProjectPath);
    }

    [Fact]
    public void GetCurrentNtId_ReturnsNonEmptyString()
    {
        var ntid = _configManager.GetCurrentNtId();

        Assert.NotNull(ntid);
        Assert.NotEmpty(ntid);
    }

    [Fact]
    public void GetEffectiveConfig_AfterInitialization_HasExpectedStructure()
    {
        var result = _configManager.GetEffectiveConfig();

        Assert.NotNull(result.Config);
        Assert.NotNull(result.Config.AuthType);
        Assert.NotNull(result.Config.DefaultBranch);
        Assert.Equal("全局默认配置", result.Source.Source);
    }

    [Fact]
    public void SaveWorkingCopy_SavesNewWorkingCopy()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var ntid = _configManager.GetCurrentNtId();
        var workingCopy = new WorkingCopy
        {
            Name = $"test_repo_{uniqueId}",
            LocalPath = $"/workspace/test_{uniqueId}",
            OwnerNtid = ntid,
            RemoteUrl = $"https://github.com/test/{uniqueId}",
            IsDefault = false
        };

        var result = _configManager.SaveWorkingCopy(workingCopy);

        Assert.True(result);
        var saved = _configManager.GetWorkingCopy(workingCopy.Name, ntid);
        Assert.NotNull(saved);
        Assert.Equal(workingCopy.Name, saved.Name);
        Assert.Equal(workingCopy.LocalPath, saved.LocalPath);
    }

    [Fact]
    public void GetWorkingCopy_WithExistingName_ReturnsWorkingCopy()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var ntid = _configManager.GetCurrentNtId();
        var workingCopy = new WorkingCopy
        {
            Name = $"get_test_{uniqueId}",
            LocalPath = $"/workspace/gettest_{uniqueId}",
            OwnerNtid = ntid
        };
        _configManager.SaveWorkingCopy(workingCopy);

        var result = _configManager.GetWorkingCopy($"get_test_{uniqueId}", ntid);

        Assert.NotNull(result);
        Assert.Equal($"get_test_{uniqueId}", result.Name);
    }

    [Fact]
    public void GetWorkingCopy_WithNonExistentName_ReturnsNull()
    {
        var ntid = _configManager.GetCurrentNtId();

        var result = _configManager.GetWorkingCopy("non_existent_repo_xyz", ntid);

        Assert.Null(result);
    }

    [Fact]
    public void SaveWorkingCopy_UpdatesExistingWorkingCopy()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var ntid = _configManager.GetCurrentNtId();
        var workingCopy = new WorkingCopy
        {
            Name = $"update_test_{uniqueId}",
            LocalPath = $"/workspace/update1_{uniqueId}",
            OwnerNtid = ntid
        };
        _configManager.SaveWorkingCopy(workingCopy);

        workingCopy.LocalPath = $"/workspace/update2_{uniqueId}";
        var result = _configManager.SaveWorkingCopy(workingCopy);

        Assert.True(result);
        var updated = _configManager.GetWorkingCopy($"update_test_{uniqueId}", ntid);
        Assert.NotNull(updated);
        Assert.Equal($"/workspace/update2_{uniqueId}", updated.LocalPath);
    }

    [Fact]
    public void SetDefaultWorkingCopy_SetsDefault()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var ntid = _configManager.GetCurrentNtId();
        var wc1 = new WorkingCopy
        {
            Name = $"default1_{uniqueId}",
            LocalPath = $"/workspace/default1_{uniqueId}",
            OwnerNtid = ntid,
            IsDefault = false
        };
        var wc2 = new WorkingCopy
        {
            Name = $"default2_{uniqueId}",
            LocalPath = $"/workspace/default2_{uniqueId}",
            OwnerNtid = ntid,
            IsDefault = false
        };
        _configManager.SaveWorkingCopy(wc1);
        _configManager.SaveWorkingCopy(wc2);

        var result = _configManager.SetDefaultWorkingCopy(wc1.Name, ntid);

        Assert.True(result);
        var defaultCopy = _configManager.GetDefaultWorkingCopy(ntid);
        Assert.NotNull(defaultCopy);
        Assert.Equal(wc1.Name, defaultCopy.Name);
    }

    [Fact]
    public void GetDefaultWorkingCopy_WhenNoDefaultSet_ReturnsNull()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var ntid = _configManager.GetCurrentNtId();
        var workingCopy = new WorkingCopy
        {
            Name = $"nodefault_{uniqueId}",
            LocalPath = $"/workspace/nodefault_{uniqueId}",
            OwnerNtid = ntid,
            IsDefault = false
        };
        _configManager.SaveWorkingCopy(workingCopy);

        var result = _configManager.GetDefaultWorkingCopy(ntid);

        Assert.Null(result);
    }

    [Fact]
    public void ListWorkingCopies_ReturnsUserWorkingCopies()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var ntid = _configManager.GetCurrentNtId();
        var wc1 = new WorkingCopy
        {
            Name = $"list1_{uniqueId}",
            LocalPath = $"/workspace/list1_{uniqueId}",
            OwnerNtid = ntid
        };
        var wc2 = new WorkingCopy
        {
            Name = $"list2_{uniqueId}",
            LocalPath = $"/workspace/list2_{uniqueId}",
            OwnerNtid = ntid
        };
        _configManager.SaveWorkingCopy(wc1);
        _configManager.SaveWorkingCopy(wc2);

        var result = _configManager.ListWorkingCopies(ntid);

        Assert.NotNull(result);
        Assert.True(result.Count >= 2);
        Assert.Contains(result, wc => wc.Name == $"list1_{uniqueId}");
        Assert.Contains(result, wc => wc.Name == $"list2_{uniqueId}");
    }

    [Fact]
    public void DeleteWorkingCopy_RemovesWorkingCopy()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var ntid = _configManager.GetCurrentNtId();
        var workingCopy = new WorkingCopy
        {
            Name = $"delete_{uniqueId}",
            LocalPath = $"/workspace/delete_{uniqueId}",
            OwnerNtid = ntid
        };
        _configManager.SaveWorkingCopy(workingCopy);

        var result = _configManager.DeleteWorkingCopy(workingCopy.Name, ntid);

        Assert.True(result);
        var deleted = _configManager.GetWorkingCopy($"delete_{uniqueId}", ntid);
        Assert.Null(deleted);
    }

    [Fact]
    public void GetWorkingCopyById_WithExistingId_ReturnsWorkingCopy()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var ntid = _configManager.GetCurrentNtId();
        var workingCopy = new WorkingCopy
        {
            Name = $"byid_{uniqueId}",
            LocalPath = $"/workspace/byid_{uniqueId}",
            OwnerNtid = ntid
        };
        _configManager.SaveWorkingCopy(workingCopy);
        var saved = _configManager.GetWorkingCopy($"byid_{uniqueId}", ntid);

        var result = _configManager.GetWorkingCopyById(saved!.Id);

        Assert.NotNull(result);
        Assert.Equal($"byid_{uniqueId}", result.Name);
    }

    [Fact]
    public void SaveWorkingCopy_WithIsDefault_ClearsOtherDefaults()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var ntid = _configManager.GetCurrentNtId();
        var wc1 = new WorkingCopy
        {
            Name = $"multi1_{uniqueId}",
            LocalPath = $"/workspace/multi1_{uniqueId}",
            OwnerNtid = ntid,
            IsDefault = true
        };
        var wc2 = new WorkingCopy
        {
            Name = $"multi2_{uniqueId}",
            LocalPath = $"/workspace/multi2_{uniqueId}",
            OwnerNtid = ntid,
            IsDefault = true
        };
        _configManager.SaveWorkingCopy(wc1);
        _configManager.SaveWorkingCopy(wc2);

        var defaultCopy = _configManager.GetDefaultWorkingCopy(ntid);
        Assert.NotNull(defaultCopy);
        Assert.Equal("multi2_" + uniqueId, defaultCopy.Name);
    }

    #region Key-Value Config Tests

    [Fact]
    public void SetKeyValue_SavesConfigSuccessfully()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var result = _configManager.SetKeyValue(ConfigScope.User, ConfigGroups.Auth, $"github_token_{uniqueId}", "test_token", true, uniqueId);

        Assert.True(result);
        var saved = _configManager.GetKeyValue(ConfigScope.User, ConfigGroups.Auth, $"github_token_{uniqueId}", uniqueId);
        Assert.Equal("test_token", saved);
    }

    [Fact]
    public void SetKeyValue_UpdatesExistingConfig()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        _configManager.SetKeyValue(ConfigScope.User, ConfigGroups.Auth, $"update_key_{uniqueId}", "value1", false, uniqueId);
        var result = _configManager.SetKeyValue(ConfigScope.User, ConfigGroups.Auth, $"update_key_{uniqueId}", "value2", false, uniqueId);

        Assert.True(result);
        var saved = _configManager.GetKeyValue(ConfigScope.User, ConfigGroups.Auth, $"update_key_{uniqueId}", uniqueId);
        Assert.Equal("value2", saved);
    }

    [Fact]
    public void GetKeyValue_ReturnsNull_WhenNotExists()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        var result = _configManager.GetKeyValue(ConfigScope.User, ConfigGroups.Auth, $"nonexistent_{uniqueId}", uniqueId);

        Assert.Null(result);
    }

    [Fact]
    public void DeleteKeyValue_RemovesConfig()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        _configManager.SetKeyValue(ConfigScope.User, ConfigGroups.Git, $"delete_key_{uniqueId}", "value", false, uniqueId);
        var deleteResult = _configManager.DeleteKeyValue(ConfigScope.User, ConfigGroups.Git, $"delete_key_{uniqueId}", uniqueId);
        var getResult = _configManager.GetKeyValue(ConfigScope.User, ConfigGroups.Git, $"delete_key_{uniqueId}", uniqueId);

        Assert.True(deleteResult);
        Assert.Null(getResult);
    }

    [Fact]
    public void GetAllConfigs_ReturnsAllConfigsForScope()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        _configManager.SetKeyValue(ConfigScope.User, ConfigGroups.Auth, $"all_key1_{uniqueId}", "value1", false, uniqueId);
        _configManager.SetKeyValue(ConfigScope.User, ConfigGroups.Git, $"all_key2_{uniqueId}", "value2", false, uniqueId);

        var configs = _configManager.GetAllConfigs(ConfigScope.User, uniqueId);

        Assert.True(configs.Count >= 2);
    }

    [Fact]
    public void GetConfigsByGroup_ReturnsConfigsForGroup()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        _configManager.SetKeyValue(ConfigScope.Project, ConfigGroups.Auth, $"group_key1_{uniqueId}", "auth_value", false, $"/test/project_{uniqueId}");
        _configManager.SetKeyValue(ConfigScope.Project, ConfigGroups.Git, $"group_key2_{uniqueId}", "git_value", false, $"/test/project_{uniqueId}");

        var authConfigs = _configManager.GetConfigsByGroup(ConfigScope.Project, ConfigGroups.Auth, $"/test/project_{uniqueId}");

        Assert.True(authConfigs.Count >= 1);
        Assert.All(authConfigs, c => Assert.Equal(ConfigGroups.Auth, c.Group));
    }

    [Fact]
    public void ListKeyValueConfigs_ReturnsAllConfigs()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        _configManager.SetKeyValue(ConfigScope.User, ConfigGroups.Auth, $"list_key_{uniqueId}", "value", false, uniqueId);

        var configs = _configManager.ListKeyValueConfigs();

        Assert.True(configs.Count >= 1);
    }

    [Fact]
    public void ListKeyValueConfigs_FiltersByScope()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        _configManager.SetKeyValue(ConfigScope.User, ConfigGroups.Auth, $"filter_key_{uniqueId}", "value", false, uniqueId);

        var userConfigs = _configManager.ListKeyValueConfigs(ConfigScope.User);

        Assert.All(userConfigs, c => Assert.Equal(ConfigScope.User, c.Scope));
    }

    [Fact]
    public void ListKeyValueConfigs_FiltersByGroup()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        _configManager.SetKeyValue(ConfigScope.User, ConfigGroups.Auth, $"group_filter_{uniqueId}", "value", false, uniqueId);

        var authConfigs = _configManager.ListKeyValueConfigs(group: ConfigGroups.Auth);

        Assert.All(authConfigs, c => Assert.Equal(ConfigGroups.Auth, c.Group));
    }

    [Fact]
    public void DeleteAllConfigs_RemovesAllConfigsForScope()
    {
        var uniqueId = Guid.NewGuid().ToString("N");
        _configManager.SetKeyValue(ConfigScope.User, ConfigGroups.Auth, $"delete_all_1_{uniqueId}", "value", false, uniqueId);
        _configManager.SetKeyValue(ConfigScope.User, ConfigGroups.Git, $"delete_all_2_{uniqueId}", "value", false, uniqueId);

        var result = _configManager.DeleteAllConfigs(ConfigScope.User, uniqueId);
        var configs = _configManager.GetAllConfigs(ConfigScope.User, uniqueId);

        Assert.True(result);
        Assert.Empty(configs);
    }

    #endregion
}